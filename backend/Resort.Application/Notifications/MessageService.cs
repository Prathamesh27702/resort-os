using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Reservations;
using Resort.Contracts;
using Resort.Domain.Notifications;
using Resort.Domain.Organizations;

namespace Resort.Application.Notifications;

/// <summary>
/// The Messages page and the staff bell (Sprint 24): every message queued at a property with its delivery and, when it
/// failed or wasn't sent, why; sending a failed one again; the templates' wording (edited without a release); the scheduled
/// jobs (on/off, time, Run now) and what they did; and each user's own notifications. message.read sees, message.send
/// sends again and runs jobs, integration.manage changes templates and jobs.
/// </summary>
public sealed partial class MessageService(IAppDbContext db, ICurrentUser caller, CallerAccess access, TimeProvider clock, Outbox outbox,
    OutboxDispatcher dispatcher, NotificationJobs jobs, IWhatsAppClient whatsapp, IEmailClient email)
{
    private const int MaxPageSize = 100;

    // ------------------------------------------------------------------ setup

    public async Task<MessagingSetupDto> SetupAsync(Guid propertyId, CancellationToken ct)
    {
        var p = await PropertyAsync(propertyId, ct);
        await outbox.EnsureAsync(p, ct);
        var a = await access.GetAsync(ct);
        var counts = await db.OutboxMessages.AsNoTracking().Where(m => m.PropertyId == propertyId).GroupBy(m => m.Status)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return new MessagingSetupDto(whatsapp.Mode, email.Mode, a.Has(Perm.MessageSend, propertyId), a.Has(Perm.IntegrationManage, propertyId),
            await jobs.PreArrivalDaysAsync(p, ct), counts);
    }

    // ------------------------------------------------------------------ messages

    /// <summary>Messages newest first. Status: one status, or PROBLEM (failed or not sent). Q: the recipient's name or the
    /// booking reference.</summary>
    public async Task<PagedResult<MessageListItemDto>> ListAsync(Guid propertyId, string? status, string? channel, Guid? reservationId, string? q,
        int page, int pageSize, CancellationToken ct)
    {
        await PropertyAsync(propertyId, ct);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var query = db.OutboxMessages.AsNoTracking().Where(m => m.PropertyId == propertyId);
        if (Clean(status)?.ToUpperInvariant() is { } st)
            query = st == "PROBLEM" ? query.Where(m => m.Status == MessageStatuses.Failed || m.Status == MessageStatuses.Skipped)
                : MessageStatuses.All.Contains(st) ? query.Where(m => m.Status == st)
                : throw ReservationService.Field("Status", $"Show messages by status: {string.Join(", ", MessageStatuses.All)} or PROBLEM.");
        if (Clean(channel)?.ToUpperInvariant() is { } ch)
            query = MessageChannels.All.Contains(ch) ? query.Where(m => m.Channel == ch)
                : throw ReservationService.Field("Channel", "The channel is WHATSAPP, EMAIL or IN_APP.");
        if (reservationId is { } rid) query = query.Where(m => m.ReservationId == rid);
        if (Clean(q) is { } text)
        {
            var like = "%" + text.ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            var refs = db.Reservations.Where(r => r.PropertyId == propertyId && EF.Functions.Like(r.Reference.ToLowerInvariant(), like)).Select(r => (Guid?)r.Id);
            query = query.Where(m => EF.Functions.Like(m.RecipientName.ToLowerInvariant(), like) || refs.Contains(m.ReservationId));
        }
        var total = await query.LongCountAsync(ct);
        var rows = await query.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<MessageListItemDto>(await ItemsAsync(propertyId, rows, ct), page, pageSize, total);
    }

    public async Task<MessageDto> GetAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        await PropertyAsync(propertyId, ct);
        var m = await LoadAsync(propertyId, id, ct);
        var summary = (await ItemsAsync(propertyId, [m], ct))[0];
        var wa = await db.WhatsAppMessages.AsNoTracking().SingleOrDefaultAsync(w => w.OutboxMessageId == m.Id, ct);
        var jobName = m.JobRunId is { } run
            ? await db.JobRuns.AsNoTracking().Where(r => r.Id == run).Select(r => r.Job).SingleOrDefaultAsync(ct) is { } j
                ? NotificationCatalog.Jobs.SingleOrDefault(d => d.Code == j)?.Name : null
            : null;
        var steps = new List<MessageStepDto> { new(m.CreatedAt, MessageStatuses.Queued, m.Source switch
        {
            MessageSources.Job => $"Queued by the job {jobName ?? "(a scheduled job)"}",
            MessageSources.Manual => "Queued by hand",
            _ => "Queued when the booking was confirmed",
        }) };
        if (m.Status == MessageStatuses.Skipped) steps.Add(new(m.CreatedAt, m.Status, "Not sent: " + m.LastError));
        if (m.SentAt is { } s) steps.Add(new(s, MessageStatuses.Sent, m.Channel switch
        {
            MessageChannels.WhatsApp => "Accepted by WhatsApp",
            MessageChannels.Email => "Accepted by the email provider",
            _ => "In the bell",
        }));
        if (m.DeliveredAt is { } d) steps.Add(new(d, MessageStatuses.Delivered, "Delivered to the phone"));
        if (m.ReadAt is { } r) steps.Add(new(r, MessageStatuses.Read, m.Channel == MessageChannels.InApp ? "Opened" : "Read"));
        if (m.FailedAt is { } f) steps.Add(new(f, MessageStatuses.Failed, "Failed: " + m.LastError));
        var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(m.Params) ?? [];
        return new MessageDto(summary, m.Subject, m.Body, m.ProviderTemplate, m.Language, parameters, m.ProviderMessageId, m.NextAttemptAt,
            wa?.PricingCategory, jobName, steps.OrderBy(x => x.At).ToList(),
            whatsapp.Mode == ProviderModes.Fake && m.Channel == MessageChannels.WhatsApp && wa is not null && await CanAsync(Perm.MessageSend, propertyId, ct));
    }

    /// <summary>Sends a FAILED message again, now (its tries start again from 1).</summary>
    public async Task<MessageDto> RetryAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        await PropertyAsync(propertyId, ct);
        var m = await db.OutboxMessages.SingleOrDefaultAsync(x => x.Id == id && x.PropertyId == propertyId, ct)
                ?? throw new NotFoundAppException("That message doesn't exist at this property. Reload the list.");
        if (m.Status != MessageStatuses.Failed)
            throw new BusinessRuleAppException("MESSAGE_NOT_FAILED", m.Status == MessageStatuses.Skipped
                ? $"This message was never sent: {m.LastError} Once that is fixed, the next message goes; this one stays as it was."
                : $"This message is {MessageStatuses.Words(m.Status).ToLowerInvariant()}, so there is nothing to send again.");
        var now = clock.GetUtcNow();
        m.Status = MessageStatuses.Queued;
        m.Attempts = 0;
        m.NextAttemptAt = now;
        m.FailedAt = null;
        m.LastError = null;
        await db.SaveChangesAsync(ct);
        await dispatcher.DispatchOneAsync(m.Id, ct);
        return await GetAsync(propertyId, id, ct);
    }

    /// <summary>Sends what is queued and due now, instead of waiting for the dispatcher (at most 100).</summary>
    public async Task<int> DispatchNowAsync(Guid propertyId, CancellationToken ct)
    {
        await PropertyAsync(propertyId, ct);
        return await dispatcher.DispatchAsync(100, ct);
    }

    // ------------------------------------------------------------------ templates

    public async Task<IReadOnlyList<MessageTemplateDto>> TemplatesAsync(Guid propertyId, CancellationToken ct)
    {
        var p = await PropertyAsync(propertyId, ct);
        await outbox.EnsureAsync(p, ct);
        var rows = await db.MessageTemplates.AsNoTracking().Where(t => t.PropertyId == propertyId).ToListAsync(ct);
        var order = NotificationCatalog.Templates.Select((t, i) => (t.Code, t.Channel, i)).ToDictionary(x => (x.Code, x.Channel), x => x.i);
        return rows.OrderBy(t => order.GetValueOrDefault((t.Code, t.Channel), 999)).Select(ToDto).ToList();
    }

    public async Task<MessageTemplateDto> SaveTemplateAsync(Guid propertyId, Guid id, SaveMessageTemplateRequest r, CancellationToken ct)
    {
        await PropertyAsync(propertyId, ct);
        var t = await db.MessageTemplates.SingleOrDefaultAsync(x => x.Id == id && x.PropertyId == propertyId, ct)
                ?? throw new NotFoundAppException("That template doesn't exist at this property. Reload the page.");
        if (r.Version is { } v) ConcurrencyGuard.ExpectVersion(db, t, v);
        var known = NotificationCatalog.Variables[t.Code].Select(x => x.Name).ToList();
        var body = r.Body.Replace("\r\n", "\n").Trim();
        var wa = t.Channel == MessageChannels.WhatsApp;
        if (MessageText.Unknown(body, known) is { Count: > 0 } badBody)
            throw ReservationService.Field(nameof(r.Body), Unknown(badBody, known));
        if (MessageText.Placeholders(body).Count == 0 && t.Channel != MessageChannels.InApp)
            throw ReservationService.Field(nameof(r.Body), "Use at least {{guest_name}} or another variable, so the message says who and what it is about.");
        if (wa)
        {
            if (body.Length > 1024)
                throw ReservationService.Field(nameof(r.Body), $"A WhatsApp template's text is at most 1,024 characters; this is {body.Length:N0}.");
            var name = Clean(r.ProviderTemplate)
                       ?? throw ReservationService.Field(nameof(r.ProviderTemplate),
                           "Give the name of the template approved in WhatsApp Manager, e.g. booking_confirmation.");
            if (!TemplateName().IsMatch(name))
                throw ReservationService.Field(nameof(r.ProviderTemplate),
                    "A WhatsApp template's name is lower-case letters, digits and _ only, exactly as in WhatsApp Manager, e.g. booking_confirmation.");
            t.ProviderTemplate = name;
        }
        else
        {
            var subject = Clean(r.Subject);
            if (subject is null || subject.Length is < 2 or > 150)
                throw ReservationService.Field(nameof(r.Subject), t.Channel == MessageChannels.Email
                    ? "Give the email a subject, 2–150 characters, e.g. Your booking {{reference}} is confirmed."
                    : "Give the notification a title, 2–150 characters.");
            if (MessageText.Unknown(subject, known) is { Count: > 0 } badSubject)
                throw ReservationService.Field(nameof(r.Subject), Unknown(badSubject, known));
            t.Subject = subject;
        }
        var language = Clean(r.Language) ?? "en";
        if (!Language().IsMatch(language))
            throw ReservationService.Field(nameof(r.Language), "The language is WhatsApp's code for it, e.g. en, en_US, hi or mr.");
        t.Language = language;
        t.Body = body;
        t.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return ToDto(t);
    }

    private static string Unknown(IReadOnlyList<string> bad, IReadOnlyList<string> known) =>
        $"{string.Join(", ", bad.Select(b => "{{" + b + "}}"))} {(bad.Count == 1 ? "isn't something" : "aren't things")} this message can fill in. " +
        $"It can use: {string.Join(", ", known.Select(k => "{{" + k + "}}"))}.";

    private static MessageTemplateDto ToDto(MessageTemplate t) =>
        new(t.Id, t.Code, t.Channel, t.Name, NotificationCatalog.MessageWhen.GetValueOrDefault(t.Code, ""), t.ProviderTemplate, t.Language, t.Subject,
            t.Body, t.IsActive, NotificationCatalog.Variables[t.Code].Select(v => new MessageVariableDto(v.Name, v.Description, v.Sample)).ToList(),
            t.UpdatedAt, t.Version);

    // ------------------------------------------------------------------ jobs

    public async Task<JobsDto> JobsAsync(Guid propertyId, CancellationToken ct)
    {
        var p = await PropertyAsync(propertyId, ct);
        await outbox.EnsureAsync(p, ct);
        var rows = await db.ScheduledJobs.AsNoTracking().Where(j => j.PropertyId == propertyId).ToListAsync(ct);
        var days = await jobs.PreArrivalDaysAsync(p, ct);
        var list = NotificationCatalog.Jobs.Select(d => rows.Single(r => r.Job == d.Code)).Select(j => JobDto(j, days)).ToList();
        return new JobsDto(list, await RunsAsync(propertyId, null, 30, ct));
    }

    public async Task<ScheduledJobDto> SaveJobAsync(Guid propertyId, string job, SaveScheduledJobRequest r, CancellationToken ct)
    {
        var p = await PropertyAsync(propertyId, ct);
        NotificationCatalog.Job(job);
        await outbox.EnsureAsync(p, ct);
        var j = await db.ScheduledJobs.SingleAsync(x => x.PropertyId == propertyId && x.Job == job, ct);
        if (r.Version is { } v) ConcurrencyGuard.ExpectVersion(db, j, v);
        if (r.RunAt.Second != 0 || r.RunAt.Millisecond != 0)
            throw ReservationService.Field(nameof(r.RunAt), "Give the time in hours and minutes, e.g. 10:00.");
        j.Enabled = r.Enabled;
        j.RunAt = r.RunAt;
        j.NextRunAt = JobClock.Next(p, r.RunAt, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return JobDto(j, await jobs.PreArrivalDaysAsync(p, ct));
    }

    public async Task<JobRunResultDto> RunJobAsync(Guid propertyId, string job, CancellationToken ct)
    {
        var p = await PropertyAsync(propertyId, ct);
        NotificationCatalog.Job(job);
        return await jobs.RunAsync(p, job, caller.RequireUserId(), ct);
    }

    public async Task<IReadOnlyList<JobRunDto>> RunsAsync(Guid propertyId, string? job, int limit, CancellationToken ct)
    {
        var q = db.JobRuns.AsNoTracking().Where(r => r.PropertyId == propertyId);
        if (Clean(job) is { } j) q = q.Where(r => r.Job == j);
        var rows = await q.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).Take(Math.Clamp(limit, 1, 200)).ToListAsync(ct);
        var resIds = rows.Where(r => r.ReservationId != null).Select(r => r.ReservationId!.Value).Distinct().ToList();
        var refs = await db.Reservations.AsNoTracking().Where(r => resIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Reference, ct);
        var userIds = rows.Where(r => r.RunBy != null).Select(r => r.RunBy!.Value).Distinct().ToList();
        var names = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        return rows.Select(r => new JobRunDto(r.Id, r.Job, NotificationCatalog.Jobs.SingleOrDefault(d => d.Code == r.Job)?.Name ?? r.Job, r.BusinessKey,
            r.ReservationId, r.ReservationId is { } rid ? refs.GetValueOrDefault(rid) : null, r.TriggeredBy,
            r.RunBy is { } u ? names.GetValueOrDefault(u) : null, r.Outcome, r.CreatedAt)).ToList();
    }

    private static ScheduledJobDto JobDto(ScheduledJob j, int preArrivalDays)
    {
        var d = NotificationCatalog.Job(j.Job);
        var description = j.Job == JobCodes.PreArrival ? d.Description.Replace("in 3 days", $"in {preArrivalDays} days") : d.Description;
        return new ScheduledJobDto(j.Job, d.Name, description, j.Enabled, j.RunAt, j.NextRunAt, j.LastStartedAt, j.LastFinishedAt, j.LastOutcome,
            j.LastError, j.Version);
    }

    // ------------------------------------------------------------------ the bell

    public async Task<NotificationListDto> MineAsync(bool unreadOnly, CancellationToken ct)
    {
        var me = caller.RequireUserId();
        var q = db.Notifications.AsNoTracking().Where(n => n.UserId == me);
        if (unreadOnly) q = q.Where(n => n.ReadAt == null);
        var rows = await q.OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id).Take(30).ToListAsync(ct);
        var unread = await db.Notifications.CountAsync(n => n.UserId == me && n.ReadAt == null, ct);
        return new NotificationListDto(rows.Select(n => new NotificationDto(n.Id, n.Title, n.Body, n.Link, n.CreatedAt, n.ReadAt)).ToList(), unread);
    }

    /// <summary>Marks one notification (or, with null, all of them) read; its message counts as read too.</summary>
    public async Task<NotificationListDto> ReadAsync(Guid? id, CancellationToken ct)
    {
        var me = caller.RequireUserId();
        var now = clock.GetUtcNow();
        var rows = await db.Notifications.Where(n => n.UserId == me && n.ReadAt == null && (id == null || n.Id == id)).ToListAsync(ct);
        if (id is { } one && rows.Count == 0 && !await db.Notifications.AnyAsync(n => n.Id == one && n.UserId == me, ct))
            throw new NotFoundAppException("That notification isn't yours or doesn't exist. Reload the page.");
        foreach (var n in rows) n.ReadAt = now;
        var outboxIds = rows.Where(n => n.OutboxMessageId != null).Select(n => n.OutboxMessageId!.Value).ToList();
        foreach (var m in await db.OutboxMessages.IgnoreQueryFilters().Where(m => outboxIds.Contains(m.Id) && m.Status == MessageStatuses.Sent).ToListAsync(ct))
        {
            m.Status = MessageStatuses.Read;
            m.ReadAt = now;
        }
        await db.SaveChangesAsync(ct);
        return await MineAsync(false, ct);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<List<MessageListItemDto>> ItemsAsync(Guid propertyId, List<OutboxMessage> rows, CancellationToken ct)
    {
        var resIds = rows.Where(m => m.ReservationId != null).Select(m => m.ReservationId!.Value).Distinct().ToList();
        var refs = await db.Reservations.AsNoTracking().Where(r => resIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Reference, ct);
        var canSend = await CanAsync(Perm.MessageSend, propertyId, ct);
        return rows.Select(m => new MessageListItemDto(m.Id, m.CreatedAt, m.TemplateCode, NotificationCatalog.MessageNames.GetValueOrDefault(m.TemplateCode, m.TemplateCode),
            m.Channel, m.RecipientType, m.RecipientName, m.Channel == MessageChannels.InApp ? "Bell" : MessageText.Mask(m.ToAddress), m.ReservationId,
            m.ReservationId is { } rid ? refs.GetValueOrDefault(rid) : null, m.Status, MessageStatuses.Words(m.Status), m.LastError, m.Attempts,
            m.SentAt, m.DeliveredAt, m.ReadAt, m.FailedAt, m.Source, canSend && m.Status == MessageStatuses.Failed)).ToList();
    }

    private async Task<bool> CanAsync(string permission, Guid propertyId, CancellationToken ct) => (await access.GetAsync(ct)).Has(permission, propertyId);

    private async Task<OutboxMessage> LoadAsync(Guid propertyId, Guid id, CancellationToken ct) =>
        await db.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.PropertyId == propertyId, ct)
        ?? throw new NotFoundAppException("That message doesn't exist at this property. Reload the list.");

    private async Task<Property> PropertyAsync(Guid propertyId, CancellationToken ct) =>
        await db.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == propertyId, ct) ?? throw new NotFoundAppException("Property", propertyId);

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    [GeneratedRegex("^[a-z0-9_]{1,512}$")]
    private static partial Regex TemplateName();

    [GeneratedRegex("^[a-z]{2,3}(_[A-Z]{2})?$")]
    private static partial Regex Language();
}
