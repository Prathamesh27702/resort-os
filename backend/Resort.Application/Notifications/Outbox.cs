using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Resort.Application.Common;
using Resort.Domain.Guests;
using Resort.Domain.Notifications;
using Resort.Domain.Organizations;
using Resort.Domain.Rates;
using Resort.Domain.Reservations;

namespace Resort.Application.Notifications;

/// <summary>What happened to one message being queued: QUEUED, SKIPPED (with the reason) or ALREADY (that message was
/// queued before, so nothing was written).</summary>
public sealed record Enqueued(string Channel, string Recipient, string Result, string? Reason = null);

/// <summary>
/// The outbox (Sprint 24, the transactional outbox pattern): code that needs a message sent calls this inside its own
/// transaction. Each message to one person on one channel is one row in outbox_messages, already filled in from its
/// template, inserted with ON CONFLICT (property, dedupe key) DO NOTHING, so the same message written twice (a job that runs
/// again, two API instances, a retried request) is one row. Nothing is sent here: if the business change rolls back, so
/// does the message; once it commits, <see cref="OutboxDispatcher"/> sends it.
///
/// A message that can't be sent at all is still written, as SKIPPED with the reason (no WhatsApp number, the guest opted
/// out of booking messages, the template is turned off), so staff can see why a guest didn't get it. All reads ignore the
/// tenant filter and name the property explicitly, because jobs and webhooks run with nobody signed in.
/// </summary>
public sealed class Outbox(IAppDbContext db, TimeProvider clock, IOptions<NotificationOptions> options)
{
    // ------------------------------------------------------------------ defaults

    /// <summary>Creates the property's templates and scheduled jobs with their defaults where missing (idempotent: ON
    /// CONFLICT DO NOTHING, so two callers at once are fine).</summary>
    public async Task EnsureAsync(Property p, CancellationToken ct)
    {
        var templates = await db.MessageTemplates.IgnoreQueryFilters().CountAsync(t => t.PropertyId == p.Id, ct);
        var jobs = await db.ScheduledJobs.IgnoreQueryFilters().CountAsync(j => j.PropertyId == p.Id, ct);
        if (templates >= NotificationCatalog.Templates.Length && jobs >= NotificationCatalog.Jobs.Length) return;
        var now = clock.GetUtcNow();
        foreach (var t in NotificationCatalog.Templates)
            await db.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO message_templates (id, organization_id, property_id, code, channel, name, provider_template, language, subject, body)
                 VALUES ({Guid.CreateVersion7()}, {p.OrganizationId}, {p.Id}, {t.Code}, {t.Channel}, {t.Name}, {t.ProviderTemplate}, 'en', {t.Subject}, {t.Body})
                 ON CONFLICT (property_id, code, channel) DO NOTHING
                 """, ct);
        foreach (var j in NotificationCatalog.Jobs)
            await db.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO scheduled_jobs (id, organization_id, property_id, job, enabled, run_at, next_run_at)
                 VALUES ({Guid.CreateVersion7()}, {p.OrganizationId}, {p.Id}, {j.Code}, true, {j.DefaultRunAt}, {JobClock.Next(p, j.DefaultRunAt, now)})
                 ON CONFLICT (property_id, job) DO NOTHING
                 """, ct);
    }

    // ------------------------------------------------------------------ guests

    /// <summary>A booking was confirmed: its confirmation, once per booking and channel.</summary>
    public async Task<IReadOnlyList<Enqueued>> BookingConfirmedAsync(Reservation r, CancellationToken ct)
    {
        var p = await PropertyAsync(r.PropertyId, ct);
        return await GuestAsync(p, r, MessageCodes.BookingConfirmation, $"{MessageCodes.BookingConfirmation}:{r.Id:N}", MessageSources.Event, null,
            null, ct);
    }

    /// <summary>Queues message <paramref name="code"/> on each of its channels to the booking's main guest. The dedupe key
    /// is <paramref name="key"/>:CHANNEL.</summary>
    public async Task<IReadOnlyList<Enqueued>> GuestAsync(Property p, Reservation r, string code, string key, string source, Guid? jobRunId,
        IReadOnlyDictionary<string, string>? extra, CancellationToken ct)
    {
        await EnsureAsync(p, ct);
        var guest = await db.Guests.IgnoreQueryFilters().AsNoTracking().SingleAsync(g => g.Id == r.PrimaryGuestId, ct);
        var values = new Dictionary<string, string>(await StayAsync(p, r, guest, ct));
        foreach (var (k, v) in extra ?? new Dictionary<string, string>()) values[k] = v;
        var optedOut = (await db.GuestConsents.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.GuestId == guest.Id && (c.Purpose == ConsentPurposes.ServiceWhatsApp || c.Purpose == ConsentPurposes.ServiceEmail))
                .ToListAsync(ct))
            .GroupBy(c => c.Purpose)
            .Select(g => g.OrderByDescending(c => c.RecordedAt).ThenByDescending(c => c.Id).First())
            .ToList();
        var templates = await TemplatesAsync(p.Id, code, ct);
        var results = new List<Enqueued>();
        foreach (var t in templates.Where(t => t.Channel != MessageChannels.InApp))
        {
            var wa = t.Channel == MessageChannels.WhatsApp;
            var address = wa ? GuestRules.NormalizePhone(guest.Phone) : string.IsNullOrWhiteSpace(guest.Email) ? null : guest.Email.Trim();
            var consent = optedOut.SingleOrDefault(c => c.Purpose == (wa ? ConsentPurposes.ServiceWhatsApp : ConsentPurposes.ServiceEmail));
            var reason = !guest.IsActive ? "The guest's details were removed (merged or anonymised), so nothing was sent."
                : !t.IsActive ? $"The {MessageChannels.Words(t.Channel)} template \"{t.Name}\" is turned off on the Messages page."
                : address is null ? wa ? $"{guest.FullName} has no mobile number that can get WhatsApp. Add it on the guest's profile."
                    : $"{guest.FullName} has no email address. Add it on the guest's profile."
                : consent is { Granted: false } ? $"{guest.FullName} asked not to get booking messages on {MessageChannels.Words(t.Channel)} " +
                                                  $"(recorded {QuoteEngine.Day(p.LocalDate(consent.RecordedAt))})."
                : null;
            results.Add(await InsertAsync(p, t, $"{key}:{t.Channel}", RecipientTypes.Guest, guest.Id, null, r.Id, guest.FullName,
                reason is null ? address : null, values, null, source, jobRunId, reason, ct));
        }
        return results;
    }

    // ------------------------------------------------------------------ staff

    /// <summary>Queues message <paramref name="code"/> on each of its staff channels (the bell, email) to every active user
    /// who has <paramref name="permission"/> at the property. The dedupe key is <paramref name="key"/>:USER:CHANNEL.</summary>
    public async Task<IReadOnlyList<Enqueued>> StaffAsync(Property p, string code, string permission, string key, IReadOnlyDictionary<string, string> values,
        string? link, string source, Guid? jobRunId, CancellationToken ct)
    {
        await EnsureAsync(p, ct);
        var people = await db.Database.SqlQuery<StaffRow>(
            $"""
             SELECT DISTINCT u.id AS "Id", u.full_name AS "Name", u.email AS "Email"
               FROM users u
               JOIN user_roles ur ON ur.user_id = u.id
               JOIN role_permissions rp ON rp.role_id = ur.role_id
              WHERE u.organization_id = {p.OrganizationId} AND u.is_active AND u.deleted_at IS NULL
                AND (ur.property_id IS NULL OR ur.property_id = {p.Id}) AND rp.permission_code = {permission}
             """).ToListAsync(ct);
        var templates = await TemplatesAsync(p.Id, code, ct);
        var results = new List<Enqueued>();
        foreach (var u in people.OrderBy(u => u.Name, StringComparer.Ordinal))
            foreach (var t in templates.Where(t => t.Channel != MessageChannels.WhatsApp))
            {
                var reason = !t.IsActive ? $"The {MessageChannels.Words(t.Channel)} template \"{t.Name}\" is turned off on the Messages page." : null;
                var address = t.Channel == MessageChannels.Email ? u.Email : null;
                results.Add(await InsertAsync(p, t, $"{key}:{u.Id:N}:{t.Channel}", RecipientTypes.User, null, u.Id, null, u.Name,
                    reason is null ? address : null, values, link, source, jobRunId, reason, ct));
            }
        return results;
    }

    private sealed record StaffRow(Guid Id, string Name, string Email);

    // ------------------------------------------------------------------ writing a row

    private async Task<Enqueued> InsertAsync(Property p, MessageTemplate t, string key, string recipientType, Guid? guestId, Guid? userId,
        Guid? reservationId, string name, string? address, IReadOnlyDictionary<string, string> values, string? link, string source, Guid? jobRunId,
        string? skipped, CancellationToken ct)
    {
        var wa = t.Channel == MessageChannels.WhatsApp;
        var body = MessageText.Fill(t.Body, values);
        var subject = t.Subject is null ? null : Cut(MessageText.Fill(t.Subject, values), 150);
        var parameters = wa
            ? JsonSerializer.Serialize(MessageText.Placeholders(t.Body).ToDictionary(v => v, v => MessageText.WhatsAppParameter(values.GetValueOrDefault(v, "")))) : "{}";
        var status = skipped is null ? MessageStatuses.Queued : MessageStatuses.Skipped;
        var now = clock.GetUtcNow();
        var id = await db.Database.SqlQuery<Guid>(
            $"""
             INSERT INTO outbox_messages (id, organization_id, property_id, dedupe_key, template_code, channel, recipient_type, guest_id, user_id,
                    reservation_id, recipient_name, to_address, provider_template, language, subject, body, params, link, source, job_run_id,
                    status, last_error, next_attempt_at, created_at, updated_at)
             VALUES ({Guid.CreateVersion7()}, {p.OrganizationId}, {p.Id}, {Cut(key, 200)}, {t.Code}, {t.Channel}, {recipientType}, {guestId}, {userId},
                    {reservationId}, {Cut(name, 200)}, {address}, {t.ProviderTemplate}, {(wa ? t.Language : null)}, {subject}, {Cut(body, 4000)},
                    {parameters}::jsonb, {link}, {source}, {jobRunId}, {status}, {skipped}, {now}, {now}, {now})
             ON CONFLICT (property_id, dedupe_key) DO NOTHING
             RETURNING id AS "Value"
             """).ToListAsync(ct);
        return new Enqueued(t.Channel, name, id.Count == 0 ? "ALREADY" : status, skipped);
    }

    // ------------------------------------------------------------------ helpers

    public async Task<Property> PropertyAsync(Guid propertyId, CancellationToken ct) =>
        await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == propertyId, ct);

    private Task<List<MessageTemplate>> TemplatesAsync(Guid propertyId, string code, CancellationToken ct) =>
        db.MessageTemplates.IgnoreQueryFilters().AsNoTracking().Where(t => t.PropertyId == propertyId && t.Code == code)
            .OrderBy(t => t.Channel).ToListAsync(ct);

    /// <summary>The values a guest message about a stay can use (<see cref="NotificationCatalog.Variables"/>).</summary>
    public async Task<IReadOnlyDictionary<string, string>> StayAsync(Property p, Reservation r, Guest guest, CancellationToken ct)
    {
        var rooms = await db.ReservationUnits.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.ReservationId == r.Id && u.SupersededAt == null)
            .Join(db.AccommodationTypes.IgnoreQueryFilters(), u => u.AccommodationTypeId, t => t.Id, (u, t) => new { t.Name, t.InventoryMode, u.UnitId, u.BedId })
            .ToListAsync(ct);
        var roomText = string.Join(", ", rooms.GroupBy(x => x.Name).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Select(x => x.BedId ?? x.UnitId).Distinct().Count()} × {g.Key}"));
        return new Dictionary<string, string>
        {
            ["guest_name"] = guest.FirstName,
            ["property_name"] = p.Name,
            ["reference"] = r.Reference,
            ["check_in"] = QuoteEngine.Day(r.CheckIn),
            ["check_out"] = QuoteEngine.Day(r.CheckOut),
            ["nights"] = MessageText.Nights(r.Nights),
            ["rooms"] = roomText.Length == 0 ? "your room" : roomText,
            ["check_in_time"] = MessageText.Time(p.CheckInTime),
            ["check_out_time"] = MessageText.Time(p.CheckOutTime),
            ["property_phone"] = string.IsNullOrWhiteSpace(p.Phone) ? "the front desk" : p.Phone,
        };
    }

    /// <summary>The address of the web app, for links in messages.</summary>
    public string PublicUrl(string path) => options.Value.PublicUrl.TrimEnd('/') + path;

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

/// <summary>When a daily job runs next: today at its local time at the property if that is still ahead, otherwise
/// tomorrow (a day where the clocks change has the time it has). Pure.</summary>
public static class JobClock
{
    public static DateTimeOffset Next(Property p, TimeOnly runAt, DateTimeOffset now)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(p.Timezone);
        var today = p.LocalDate(now);
        for (var d = today; ; d = d.AddDays(1))
        {
            var local = d.ToDateTime(runAt);
            var at = new DateTimeOffset(local, tz.GetUtcOffset(local)).ToUniversalTime();
            if (at > now) return at;
        }
    }
}
