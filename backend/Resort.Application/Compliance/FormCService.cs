using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Notifications;
using Resort.Contracts;
using Resort.Domain.Audit;
using Resort.Domain.Compliance;
using Resort.Domain.Guests;
using Resort.Domain.Notifications;
using Resort.Domain.Organizations;
using Resort.Domain.Rates;
using Resort.Domain.Reservations;

namespace Resort.Application.Compliance;

/// <summary>
/// Form C (Sprint 30). A hotel must report every foreign guest to the FRRO within 24 hours of arrival, on the Form C page
/// of indianfrro.gov.in. The portal has no public API, so the app does everything around it: at check-in it opens a sheet
/// for each foreign guest of the stay (nationality set and not IN), fills in what the profile, the passport and visa
/// documents and the booking already say, shows what is still missing and how many hours are left, lets staff copy each
/// value into the portal, and records who filed it and under which acknowledgement number. Staff who check guests in get a
/// bell alert at check-in and a daily reminder while a sheet is still open.
///
/// The sheet holds passport and visa numbers, so it is stored like an ID document: one AES-256-GCM JSON blob per record,
/// bound to the record's id. Opening a sheet is written to the audit trail as a sensitive read, and anonymising the guest
/// empties it.
/// </summary>
public sealed class FormCService(IAppDbContext db, ICurrentUser caller, IFieldEncryptor crypto, Outbox outbox, TimeProvider clock)
{
    public const string PortalUrl = "https://indianfrro.gov.in/frro/FormC";

    // ------------------------------------------------------------------ opening sheets

    /// <summary>Opens a sheet for each foreign guest of a stay that has none yet. Called inside the check-in transaction.
    /// Returns the guests it opened one for.</summary>
    public async Task<IReadOnlyList<string>> OpenForStayAsync(Property p, Reservation r, DateTimeOffset arrivedAt, CancellationToken ct)
    {
        var guestIds = await db.ReservationGuests.IgnoreQueryFilters().Where(g => g.ReservationId == r.Id).Select(g => g.GuestId).ToListAsync(ct);
        guestIds.Add(r.PrimaryGuestId);
        var foreign = await db.Guests.IgnoreQueryFilters().AsNoTracking()
            .Where(g => guestIds.Contains(g.Id) && g.Status == GuestStatuses.Active && g.Nationality != null && g.Nationality != "IN")
            .OrderBy(g => g.FirstName).ThenBy(g => g.LastName).ToListAsync(ct);
        if (foreign.Count == 0) return [];
        var have = await db.FormCRecords.IgnoreQueryFilters().Where(f => f.ReservationId == r.Id).Select(f => f.GuestId).ToListAsync(ct);
        var opened = new List<string>();
        foreach (var g in foreign.Where(g => !have.Contains(g.Id)))
        {
            db.FormCRecords.Add(new FormCRecord
            {
                OrganizationId = p.OrganizationId,
                PropertyId = p.Id,
                ReservationId = r.Id,
                GuestId = g.Id,
                ArrivedAt = arrivedAt,
                DueAt = arrivedAt.AddHours(FormCFields.HoursToFile),
            });
            opened.Add(g.FullName);
        }
        if (opened.Count == 0) return opened;
        await db.SaveChangesAsync(ct);

        // tell the people who check guests in, once per stay
        var due = TimeZoneInfo.ConvertTime(arrivedAt.AddHours(FormCFields.HoursToFile), TimeZoneInfo.FindSystemTimeZoneById(p.Timezone));
        var values = new Dictionary<string, string>
        {
            ["property_name"] = p.Name,
            ["count"] = opened.Count.ToString(CultureInfo.InvariantCulture),
            ["guests"] = $"{string.Join(", ", opened)} ({r.Reference}), to file by {due.ToString("ddd d MMM, h:mm tt", CultureInfo.InvariantCulture)}",
        };
        await outbox.StaffAsync(p, MessageCodes.FormCReminder, Perm.FrontDeskCheckIn, $"{MessageCodes.FormCReminder}:stay:{r.Id:N}", values,
            "/form-c", MessageSources.Event, null, ct);
        return opened;
    }

    /// <summary>Opens the sheets missing for guests in house now: stays checked in before Sprint 30, and guests whose
    /// nationality was set after check-in. Safe to call on every list load (one cheap query when nothing is missing).</summary>
    public async Task EnsureInHouseAsync(Property p, CancellationToken ct)
    {
        var missing = await db.Database.SqlQuery<Guid>(
            $"""
             SELECT DISTINCT r.id AS "Value"
               FROM reservations r
               JOIN LATERAL (SELECT r.primary_guest_id AS guest_id
                             UNION SELECT rg.guest_id FROM reservation_guests rg WHERE rg.reservation_id = r.id) s ON true
               JOIN guests g ON g.id = s.guest_id
              WHERE r.property_id = {p.Id} AND r.status = {ReservationStatuses.CheckedIn}
                AND g.status = {GuestStatuses.Active} AND g.nationality IS NOT NULL AND g.nationality <> 'IN'
                AND NOT EXISTS (SELECT 1 FROM form_c_records f WHERE f.reservation_id = r.id AND f.guest_id = g.id)
             """).ToListAsync(ct);
        if (missing.Count == 0) return;
        var stays = await db.Reservations.IgnoreQueryFilters().AsNoTracking().Where(r => missing.Contains(r.Id)).ToListAsync(ct);
        foreach (var r in stays)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({$"formc:{r.Id}"}))", ct);
            await OpenForStayAsync(p, r, r.CheckedInAt ?? clock.GetUtcNow(), ct);
            await tx.CommitAsync(ct);
        }
    }

    // ------------------------------------------------------------------ reading

    /// <summary>Status: PENDING (default), SUBMITTED, NOT_REQUIRED or ALL. Pending ones first by deadline; the rest newest
    /// first, at most 200.</summary>
    public async Task<FormCListDto> ListAsync(Guid propertyId, string? status, CancellationToken ct)
    {
        var p = await LoadPropertyAsync(propertyId, ct);
        await EnsureInHouseAsync(p, ct);
        var want = string.IsNullOrWhiteSpace(status) ? FormCStatuses.Pending : status.Trim().ToUpperInvariant();
        if (want is not ("ALL" or FormCStatuses.Pending or FormCStatuses.Submitted or FormCStatuses.NotRequired))
            throw Field("status", "Choose which sheets to list: PENDING, SUBMITTED, NOT_REQUIRED or ALL.");
        var q = db.FormCRecords.AsNoTracking().Where(f => f.PropertyId == propertyId);
        if (want != "ALL") q = q.Where(f => f.Status == want);
        var rows = await q.OrderByDescending(f => f.ArrivedAt).Take(200).ToListAsync(ct);
        var now = clock.GetUtcNow();
        var ctx = await ContextAsync(p, rows, ct);
        var items = rows.Select(f =>
            {
                var c = ctx[f.Id];
                var missing = f.Status == FormCStatuses.Pending ? FormCFields.Missing(Values(f, c).ToDictionary(v => v.Key, v => v.Value.Value ?? "")).Count : 0;
                return new FormCListItemDto(f.Id, f.Status, f.GuestId, c.Guest.FullName, c.Guest.Nationality, f.ReservationId, c.Stay.Reference, c.Room,
                    c.Stay.CheckOut, f.ArrivedAt, f.DueAt, HoursLeft(f, now), IsOverdue(f, now), missing, f.FrroReference, f.SubmittedAt,
                    f.SubmittedBy is { } u ? c.Users.GetValueOrDefault(u) : null, f.NotRequiredReason);
            })
            .OrderBy(i => i.Status == FormCStatuses.Pending ? 0 : 1).ThenBy(i => i.Status == FormCStatuses.Pending ? i.DueAt : DateTimeOffset.MaxValue)
            .ThenByDescending(i => i.ArrivedAt).ToList();
        var (pending, overdue) = await CountsAsync(propertyId, now, ct);
        return new FormCListDto(items, pending, overdue, FormCFields.HoursToFile, PortalUrl);
    }

    /// <summary>How many sheets are still to file at the property, and how many of them are past their 24 hours.</summary>
    public async Task<(int Pending, int Overdue)> CountsAsync(Guid propertyId, DateTimeOffset now, CancellationToken ct)
    {
        var due = await db.FormCRecords.AsNoTracking().Where(f => f.PropertyId == propertyId && f.Status == FormCStatuses.Pending)
            .Select(f => f.DueAt).ToListAsync(ct);
        return (due.Count, due.Count(d => d <= now));
    }

    /// <summary>The sheet with every value. Audited as a sensitive read (it shows the passport and visa numbers).</summary>
    public async Task<FormCSheetDto> GetAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        var p = await LoadPropertyAsync(propertyId, ct);
        var f = await db.FormCRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.PropertyId == propertyId, ct)
                ?? throw new NotFoundAppException("That Form C doesn't exist at this property. Reload the list.");
        var dto = await SheetAsync(p, f, ct);
        db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = f.OrganizationId,
            UserId = caller.UserId,
            Action = AuditActions.SensitiveRead,
            EntityType = nameof(FormCRecord),
            EntityId = f.Id.ToString(),
            Changes = JsonSerializer.Serialize(new Dictionary<string, object?> { ["viewed"] = "sheet", ["guestId"] = f.GuestId }),
            CorrelationId = caller.CorrelationId,
            RequestId = caller.RequestId,
            UserAgent = caller.UserAgent is { Length: > 500 } ua ? ua[..500] : caller.UserAgent,
            IpAddress = caller.IpAddress,
            OccurredAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
        return dto;
    }

    // ------------------------------------------------------------------ changing

    public async Task<FormCSheetDto> SaveAsync(Guid propertyId, Guid id, SaveFormCRequest r, CancellationToken ct)
    {
        var p = await LoadPropertyAsync(propertyId, ct);
        var f = await LoadAsync(propertyId, id, ct);
        ConcurrencyGuard.ExpectVersion(db, f, r.Version);
        if (f.Status != FormCStatuses.Pending)
            throw new BusinessRuleAppException("FORM_C_CLOSED",
                $"This Form C is {FormCStatuses.Words(f.Status)}, so its sheet can't be changed. Reopen it first if something on it was wrong.");

        var failures = new List<ValidationFailure>();
        var typed = Typed(f);
        foreach (var (key, raw) in r.Values)
        {
            var field = FormCFields.All.SingleOrDefault(x => x.Key == key);
            if (field is null)
            {
                failures.Add(new ValidationFailure($"Values.{key}", $"Form C has no field called {key}."));
                continue;
            }
            var value = GuestRules.Clean(raw);
            if (value is null) { typed.Remove(key); continue; }
            if (Problem(field, value) is { } why) failures.Add(new ValidationFailure($"Values.{key}", why));
            else typed[key] = Normal(field, value);
        }
        if (failures.Count > 0) throw new ValidationException(failures);
        Store(f, typed);
        await db.SaveChangesAsync(ct);
        return await SheetAsync(p, f, ct);
    }

    /// <summary>Filed on the portal. Refused while a required field is empty: the portal refuses the form too, and a sheet
    /// marked filed with gaps is what an inspection finds.</summary>
    public async Task<FormCSheetDto> SubmitAsync(Guid propertyId, Guid id, SubmitFormCRequest r, CancellationToken ct)
    {
        var p = await LoadPropertyAsync(propertyId, ct);
        var f = await LoadAsync(propertyId, id, ct);
        ConcurrencyGuard.ExpectVersion(db, f, r.Version);
        if (f.Status == FormCStatuses.Submitted)
            throw new ConflictAppException("FORM_C_ALREADY_FILED", "This Form C is already marked as filed. Reload to see who filed it.");
        if (f.Status == FormCStatuses.NotRequired)
            throw new BusinessRuleAppException("FORM_C_CLOSED", "This Form C was marked as not needed. Reopen it first.");
        var reference = GuestRules.Clean(r.FrroReference);
        if (reference is { Length: < 3 or > 40 })
            throw Field("FrroReference", "The acknowledgement number from the FRRO portal has 3 to 40 characters. Leave it empty if the portal gave none.");
        var sheet = await SheetAsync(p, f, ct);
        if (sheet.Missing.Count > 0)
            throw new BusinessRuleAppException("FORM_C_INCOMPLETE",
                $"{sheet.Missing.Count} required field{(sheet.Missing.Count == 1 ? " is" : "s are")} still empty: {string.Join("; ", sheet.Missing)}. " +
                "Fill them in and save, then mark the form as filed.", new { missing = sheet.Missing });
        f.Status = FormCStatuses.Submitted;
        f.FrroReference = reference;
        f.SubmittedAt = clock.GetUtcNow();
        f.SubmittedBy = caller.UserId;
        await db.SaveChangesAsync(ct);
        return await SheetAsync(p, f, ct);
    }

    public async Task<FormCSheetDto> NotRequiredAsync(Guid propertyId, Guid id, FormCNotRequiredRequest r, CancellationToken ct)
    {
        var p = await LoadPropertyAsync(propertyId, ct);
        var f = await LoadAsync(propertyId, id, ct);
        ConcurrencyGuard.ExpectVersion(db, f, r.Version);
        if (f.Status != FormCStatuses.Pending)
            throw new BusinessRuleAppException("FORM_C_CLOSED", $"This Form C is already {FormCStatuses.Words(f.Status)}. Reopen it first.");
        var reason = GuestRules.Clean(r.Reason);
        if (reason is null || reason.Length is < 3 or > 300)
            throw Field("Reason", "Say why no Form C is needed (3 to 300 characters), e.g. Indian citizen, nationality was entered wrongly.");
        f.Status = FormCStatuses.NotRequired;
        f.NotRequiredReason = reason;
        await db.SaveChangesAsync(ct);
        return await SheetAsync(p, f, ct);
    }

    public async Task<FormCSheetDto> ReopenAsync(Guid propertyId, Guid id, ReopenFormCRequest r, CancellationToken ct)
    {
        var p = await LoadPropertyAsync(propertyId, ct);
        var f = await LoadAsync(propertyId, id, ct);
        ConcurrencyGuard.ExpectVersion(db, f, r.Version);
        if (f.Status == FormCStatuses.Pending)
            throw new ConflictAppException("FORM_C_OPEN", "This Form C is already open. Reload to see it.");
        f.Status = FormCStatuses.Pending;
        f.SubmittedAt = null;
        f.SubmittedBy = null;
        f.FrroReference = null;
        f.NotRequiredReason = null;
        await db.SaveChangesAsync(ct);
        return await SheetAsync(p, f, ct);
    }

    // ------------------------------------------------------------------ the register

    /// <summary>The foreign guest register as CSV: every Form C whose guest arrived in the period, oldest first. The
    /// passport shows its last four characters only.</summary>
    public async Task<(string FileName, byte[] Content)> RegisterCsvAsync(Guid propertyId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var p = await LoadPropertyAsync(propertyId, ct);
        if (to < from) throw Field("to", $"The period ends {QuoteEngine.Day(to)}, before it starts ({QuoteEngine.Day(from)}). Swap the dates.");
        if (to.DayNumber - from.DayNumber > 366) throw Field("to", "The register covers at most 366 days at a time. Choose a shorter period.");
        var start = p.StartOfDay(from);
        var end = p.StartOfDay(to.AddDays(1));
        var rows = await db.FormCRecords.AsNoTracking().Where(f => f.PropertyId == propertyId && f.ArrivedAt >= start && f.ArrivedAt < end)
            .OrderBy(f => f.ArrivedAt).ToListAsync(ct);
        var ctx = await ContextAsync(p, rows, ct);
        var tz = TimeZoneInfo.FindSystemTimeZoneById(p.Timezone);
        string Local(DateTimeOffset? t) => t is { } x ? TimeZoneInfo.ConvertTime(x, tz).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";
        var sb = new StringBuilder("﻿");
        sb.AppendLine("Guest,Nationality,Passport (last 4),Booking,Room,Arrived,Leaves,Form C,To file by,Filed on,Filed by,FRRO number,Note");
        foreach (var f in rows)
        {
            var c = ctx[f.Id];
            var values = Values(f, c);
            var passport = values["passportNumber"].Value is { Length: > 0 } n ? "•••• " + GuestRules.Last4(GuestRules.NormalizeIdNumber(n)) : "";
            sb.AppendLine(string.Join(",", new[]
            {
                c.Guest.FullName, values["nationality"].Value ?? "", passport, c.Stay.Reference, c.Room ?? "", Local(f.ArrivedAt),
                c.Stay.CheckOut.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                f.Status == FormCStatuses.Submitted ? "Filed" : f.Status == FormCStatuses.NotRequired ? "Not needed" : "To file",
                Local(f.DueAt), Local(f.SubmittedAt), f.SubmittedBy is { } u ? c.Users.GetValueOrDefault(u) ?? "" : "", f.FrroReference ?? "",
                f.NotRequiredReason ?? "",
            }.Select(Csv)));
        }
        return ($"form-c-register-{p.Code.ToLowerInvariant()}-{from:yyyyMMdd}-{to:yyyyMMdd}.csv", Encoding.UTF8.GetBytes(sb.ToString()));
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Ctx(Guest Guest, Reservation Stay, string? Room, Property Property, GuestDocument? Passport, GuestDocument? Visa,
        Dictionary<Guid, string> Users);

    private async Task<Dictionary<Guid, Ctx>> ContextAsync(Property p, IReadOnlyList<FormCRecord> rows, CancellationToken ct)
    {
        var guestIds = rows.Select(r => r.GuestId).Distinct().ToList();
        var stayIds = rows.Select(r => r.ReservationId).Distinct().ToList();
        var guests = await db.Guests.AsNoTracking().Include(g => g.Addresses).Where(g => guestIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, ct);
        var stays = await db.Reservations.AsNoTracking().Where(r => stayIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var units = await db.ReservationUnits.AsNoTracking().Where(u => stayIds.Contains(u.ReservationId) && u.SupersededAt == null)
            .Join(db.AccommodationUnits, u => u.UnitId, a => a.Id, (u, a) => new { u.ReservationId, a.Name }).ToListAsync(ct);
        var docs = await db.GuestDocuments.AsNoTracking()
            .Where(d => guestIds.Contains(d.GuestId) && d.Status == DocumentStatuses.Active && (d.DocumentType == DocumentTypes.Passport || d.DocumentType == DocumentTypes.Visa))
            .OrderByDescending(d => d.CreatedAt).ToListAsync(ct);
        var userIds = rows.Where(r => r.SubmittedBy != null).Select(r => r.SubmittedBy!.Value).Distinct().ToList();
        var users = userIds.Count == 0 ? []
            : await db.Users.IgnoreQueryFilters().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        return rows.ToDictionary(r => r.Id, r => new Ctx(guests[r.GuestId], stays[r.ReservationId],
            string.Join(", ", units.Where(u => u.ReservationId == r.ReservationId).Select(u => u.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal)) is { Length: > 0 } room ? room : null,
            p, docs.FirstOrDefault(d => d.GuestId == r.GuestId && d.DocumentType == DocumentTypes.Passport),
            docs.FirstOrDefault(d => d.GuestId == r.GuestId && d.DocumentType == DocumentTypes.Visa), users));
    }

    private async Task<FormCSheetDto> SheetAsync(Property p, FormCRecord f, CancellationToken ct)
    {
        var c = (await ContextAsync(p, [f], ct))[f.Id];
        var values = Values(f, c);
        var now = clock.GetUtcNow();
        var fields = FormCFields.All.Select(d => new FormCFieldDto(d.Key, d.Section, d.Label, d.Required, d.Hint, d.Kind, d.MaxLength,
            values[d.Key].Value, values[d.Key].FromProfile)).ToList();
        var missing = f.Status == FormCStatuses.Pending ? FormCFields.Missing(values.ToDictionary(v => v.Key, v => v.Value.Value ?? "")) : [];
        return new FormCSheetDto(f.Id, f.Status, f.GuestId, c.Guest.FullName, c.Guest.Nationality, f.ReservationId, c.Stay.Reference, c.Room,
            c.Stay.CheckIn, c.Stay.CheckOut, f.ArrivedAt, f.DueAt, HoursLeft(f, now), IsOverdue(f, now), fields, missing, f.FrroReference,
            f.SubmittedAt, f.SubmittedBy is { } u ? c.Users.GetValueOrDefault(u) : null, f.NotRequiredReason, PortalUrl, f.Version);
    }

    /// <summary>Every field's value: what staff typed on the sheet, else what the profile, documents and booking say.</summary>
    private Dictionary<string, (string? Value, bool FromProfile)> Values(FormCRecord f, Ctx c)
    {
        var typed = Typed(f);
        var known = Known(f, c);
        return FormCFields.All.ToDictionary(d => d.Key, d =>
            typed.TryGetValue(d.Key, out var t) ? (t, false) : known.TryGetValue(d.Key, out var k) && !string.IsNullOrWhiteSpace(k) ? ((string?)k, true) : (null, false));
    }

    private Dictionary<string, string> Known(FormCRecord f, Ctx c)
    {
        var g = c.Guest;
        var p = c.Property;
        var tz = TimeZoneInfo.FindSystemTimeZoneById(p.Timezone);
        var arrived = TimeZoneInfo.ConvertTime(f.ArrivedAt, tz);
        var known = new Dictionary<string, string>
        {
            ["givenName"] = g.FirstName,
            ["dateOfArrivalHere"] = arrived.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["timeOfArrivalHere"] = arrived.ToString("HH:mm", CultureInfo.InvariantCulture),
            ["intendedDurationDays"] = Math.Max(1, c.Stay.CheckOut.DayNumber - c.Stay.CheckIn.DayNumber).ToString(CultureInfo.InvariantCulture),
            ["addressInIndia"] = string.Join(", ", new[] { p.Name, p.AddressLine1, p.AddressLine2, p.City, p.StateName, p.PostalCode }.Where(x => !string.IsNullOrWhiteSpace(x))),
        };
        if (g.LastName is { } last) known["surname"] = last;
        if (g.DateOfBirth is { } dob) known["dateOfBirth"] = dob.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (g.Nationality is { } nat) known["nationality"] = CountryName(nat);
        var home = g.Addresses.Where(a => !a.IsEmpty && a.CountryCode != "IN").OrderBy(a => a.AddressType == "HOME" ? 0 : 1).FirstOrDefault();
        if (home is not null)
            known["addressAbroad"] = string.Join(", ", new[] { home.Line1, home.Line2, home.City, home.State, home.PostalCode, CountryName(home.CountryCode) }
                .Where(x => !string.IsNullOrWhiteSpace(x)));
        if (GuestRules.NormalizePhone(g.Phone) is { } phone)
            known[phone.StartsWith("+91", StringComparison.Ordinal) ? "phoneInIndia" : "phoneAbroad"] = phone;
        if (c.Passport is { } pass)
        {
            if (pass.NumberEncrypted is { } n) known["passportNumber"] = crypto.DecryptString(n, $"guest_document:{pass.Id:N}:number");
            if (pass.ExpiresOn is { } e) known["passportExpiry"] = e.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (Details(pass).GetValueOrDefault("visaNumber") is { Length: > 0 } v) known["visaNumber"] = v;
        }
        if (c.Visa is { } visa)
        {
            if (visa.NumberEncrypted is { } n) known["visaNumber"] = crypto.DecryptString(n, $"guest_document:{visa.Id:N}:number");
            else if (Details(visa).GetValueOrDefault("visaNumber") is { Length: > 0 } v) known["visaNumber"] = v;
            if (visa.ExpiresOn is { } e) known["visaExpiry"] = e.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        return known;
    }

    private Dictionary<string, string> Details(GuestDocument d) =>
        d.DetailsEncrypted is null ? []
            : JsonSerializer.Deserialize<Dictionary<string, string>>(crypto.DecryptString(d.DetailsEncrypted, $"guest_document:{d.Id:N}:details")) ?? [];

    private Dictionary<string, string> Typed(FormCRecord f) =>
        f.DetailsEncrypted is null ? []
            : JsonSerializer.Deserialize<Dictionary<string, string>>(crypto.DecryptString(f.DetailsEncrypted, Context(f))) ?? [];

    private void Store(FormCRecord f, Dictionary<string, string> typed)
    {
        if (typed.Count == 0)
        {
            f.DetailsEncrypted = null;
            f.KeyId = null;
            return;
        }
        f.DetailsEncrypted = crypto.EncryptString(JsonSerializer.Serialize(typed.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value)), Context(f));
        f.KeyId = crypto.ActiveKeyId;
    }

    private static string Context(FormCRecord f) => $"form_c:{f.Id:N}:details";

    /// <summary>Why a typed value can't be right, or null.</summary>
    internal static string? Problem(FormCField field, string value)
    {
        switch (field.Kind)
        {
            case "date":
                if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    return $"{field.Label} must be a date, e.g. 14 Nov 2026.";
                if (d.Year is < 1900 or > 2100) return $"{field.Label}: {d.Year} can't be right. Check the year.";
                return null;
            case "time":
                return TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? null
                    : $"{field.Label} must be a time on the 24-hour clock, e.g. 14:30.";
            case "number":
                return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 3650 ? null
                    : $"{field.Label} must be a whole number of days from 1 to 3650.";
            case "sex":
                return value.ToUpperInvariant() is "M" or "F" or "X" or "MALE" or "FEMALE" or "OTHER" ? null : $"{field.Label}: choose Male, Female or Other.";
            case "yesno":
                return value.ToUpperInvariant() is "YES" or "NO" ? null : $"{field.Label}: choose Yes or No.";
            default:
                return value.Length > field.MaxLength ? $"{field.Label} can have at most {field.MaxLength} characters; this has {value.Length}." : null;
        }
    }

    internal static string Normal(FormCField field, string value) => field.Kind switch
    {
        "sex" => value.ToUpperInvariant() switch { "M" or "MALE" => "Male", "F" or "FEMALE" => "Female", _ => "Other" },
        "yesno" => string.Equals(value, "YES", StringComparison.OrdinalIgnoreCase) ? "Yes" : "No",
        _ when field.Key is "passportNumber" or "visaNumber" => value.ToUpperInvariant(),
        _ => value,
    };

    internal static string CountryName(string code)
    {
        try { return new RegionInfo(code).EnglishName; }
        catch (ArgumentException) { return code; }
    }

    private static double HoursLeft(FormCRecord f, DateTimeOffset now) => Math.Round((f.DueAt - now).TotalHours, 1);
    private static bool IsOverdue(FormCRecord f, DateTimeOffset now) => f.Status == FormCStatuses.Pending && f.DueAt <= now;

    private static string Csv(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
        : s.Length > 0 && s[0] is '=' or '+' or '-' or '@' ? "'" + s : s;

    private static ValidationException Field(string field, string message) => new([new ValidationFailure(field, message)]);

    private async Task<FormCRecord> LoadAsync(Guid propertyId, Guid id, CancellationToken ct) =>
        await db.FormCRecords.SingleOrDefaultAsync(x => x.Id == id && x.PropertyId == propertyId, ct)
        ?? throw new NotFoundAppException("That Form C doesn't exist at this property. Reload the list.");

    private async Task<Property> LoadPropertyAsync(Guid propertyId, CancellationToken ct) =>
        await db.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == propertyId, ct) ?? throw new NotFoundAppException("Property", propertyId);
}
