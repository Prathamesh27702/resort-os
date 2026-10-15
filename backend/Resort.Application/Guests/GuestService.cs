using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Resort.Application.Common;
using Resort.Contracts;
using Resort.Domain.Guests;

namespace Resort.Application.Guests;

public sealed class GuestOptions
{
    public const string Section = "Guests";
    /// <summary>How long ID documents are kept after they were captured (until Sprint 5: after upload; then after the
    /// guest's last check-out). Owner decision Q7 (issue #54) sets the real value; the purge itself stays off until approved.</summary>
    public int DocumentRetentionDays { get; set; } = 365;
}

/// <summary>
/// Guest profiles (brief §17, §59). Guests belong to the organisation and are shared by all its properties, so the
/// endpoints need the permission in at least one scope. Profiles are never deleted: duplicates are merged and
/// people who ask to be forgotten are anonymised, which keeps stays and invoices consistent for accounting.
/// </summary>
public sealed class GuestService(
    IAppDbContext db, ICurrentUser caller, CallerAccess access, AuditContext audit, IFileStore files, TimeProvider clock)
{
    public const string AnonymisedName = "Anonymised guest";

    // ------------------------------------------------------------------ list / search

    public async Task<PagedResult<GuestListItemDto>> ListAsync(string? q, bool includeInactive, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var rows = db.Guests.AsNoTracking();
        if (!includeInactive) rows = rows.Where(g => g.Status == GuestStatuses.Active);

        var term = GuestRules.Clean(q);
        if (term is not null)
        {
            var phoneDigits = PhoneDigitsOf(term);
            if (phoneDigits is not null)
                rows = rows.Where(g => EF.Functions.Like(g.PhoneDigits, "%" + Escape(phoneDigits) + "%"));
            else
            {
                var pattern = "%" + Escape(term.ToLowerInvariant()) + "%";
                rows = rows.Where(g => EF.Functions.Like(g.SearchText, pattern));
            }
        }

        var total = await rows.LongCountAsync(ct);
        var items = await rows
            .OrderBy(g => g.FirstName).ThenBy(g => g.LastName).ThenBy(g => g.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(g => new GuestListItemDto(
                g.Id,
                g.LastName == null ? g.FirstName : g.FirstName + " " + g.LastName,
                g.Phone, g.Email, g.Nationality,
                g.Addresses.Where(a => a.AddressType == "HOME").Select(a => a.City).FirstOrDefault(),
                g.CompanyName, g.IsVip, g.Status, g.CreatedAt))
            .ToListAsync(ct);
        return new PagedResult<GuestListItemDto>(items, page, pageSize, total);
    }

    /// <summary>A search term that looks like a phone number (digits, spaces, +, -, brackets; at least 4 digits)
    /// becomes the digits to look for. A full number typed the local way (098200 12345) is normalised first so it
    /// matches the stored +919820012345.</summary>
    internal static string? PhoneDigitsOf(string term)
    {
        if (!term.All(c => char.IsAsciiDigit(c) || c is '+' or ' ' or '-' or '(' or ')')) return null;
        var digits = new string(term.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length < 4) return null;
        var normalised = GuestRules.NormalizePhone(term);
        return normalised is null ? digits.TrimStart('0') is { Length: >= 4 } d ? d : digits : normalised[1..];
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    // ------------------------------------------------------------------ read

    public async Task<GuestDto> GetAsync(Guid id, CancellationToken ct) => await ToDtoAsync(await LoadAsync(id, ct), ct);

    public async Task<IReadOnlyList<GuestStayDto>> StaysAsync(Guid id, CancellationToken ct)
    {
        await LoadAsync(id, ct, track: false);
        // Every reservation the guest is on (as the booker or staying), newest first, across the organisation's properties.
        var reservationIds = db.ReservationGuests.Where(g => g.GuestId == id).Select(g => g.ReservationId);
        return await db.Reservations.AsNoTracking()
            .Where(r => r.PrimaryGuestId == id || reservationIds.Contains(r.Id))
            .Join(db.Properties, r => r.PropertyId, p => p.Id, (r, p) => new { r, p.Name })
            .OrderByDescending(x => x.r.CheckIn)
            .Select(x => new GuestStayDto(x.r.Id, x.r.PropertyId, x.Name, x.r.CheckIn, x.r.CheckOut, x.r.Status, x.r.Reference))
            .ToListAsync(ct);
    }

    // ------------------------------------------------------------------ duplicates

    public async Task<IReadOnlyList<PossibleDuplicateDto>> PossibleDuplicatesAsync(
        string? firstName, string? lastName, string? phone, string? email, Guid? excludeId, CancellationToken ct)
    {
        var p = GuestRules.NormalizePhone(phone);
        var e = GuestRules.NormalizeEmail(email);
        var f = GuestRules.Clean(firstName);
        var l = GuestRules.Clean(lastName);
        var byName = f is not null && l is not null;
        // search_text starts with lower(first_name || ' ' || last_name || ' '), so this is a case-insensitive
        // exact match on the full name that the trigram index can serve.
        var namePattern = byName ? Escape($"{f} {l} ".ToLowerInvariant()) + "%" : null;
        if (p is null && e is null && !byName && excludeId is null) return [];

        var candidates = await db.Guests.AsNoTracking()
            .Where(g => g.Status == GuestStatuses.Active && g.Id != excludeId)
            .Where(g => (p != null && g.Phone == p)
                     || (e != null && g.Email == e)
                     || (namePattern != null && g.LastName != null && EF.Functions.Like(g.SearchText, namePattern)))
            .OrderBy(g => g.FirstName).Take(20)
            .ToListAsync(ct);

        var result = candidates.Select(g =>
        {
            var reasons = new List<string>();
            if (p is not null && g.Phone == p) reasons.Add("Same phone");
            if (e is not null && g.Email == e) reasons.Add("Same email");
            if (byName && GuestRules.NameKey(g.FirstName, g.LastName) == GuestRules.NameKey(firstName, lastName)) reasons.Add("Same name");
            return new PossibleDuplicateDto(g.Id, g.FullName, g.Phone, g.Email, g.Nationality, reasons);
        }).Where(d => d.Reasons.Count > 0).ToList();

        // An existing profile can also share an ID document (same passport number) with another profile.
        if (excludeId is { } me)
        {
            var sharing = await db.GuestDocuments.AsNoTracking()
                .Where(d => d.GuestId == me && d.NumberHash != null && d.Status == DocumentStatuses.Active)
                .SelectMany(d => db.GuestDocuments.Where(o => o.NumberHash == d.NumberHash && o.GuestId != me && o.Status == DocumentStatuses.Active))
                .Select(o => o.GuestId).Distinct()
                .Join(db.Guests.Where(g => g.Status == GuestStatuses.Active), id => id, g => g.Id, (_, g) => g)
                .ToListAsync(ct);
            foreach (var g in sharing)
            {
                var existing = result.FindIndex(r => r.Id == g.Id);
                if (existing >= 0) result[existing] = result[existing] with { Reasons = [.. result[existing].Reasons, "Same ID document"] };
                else result.Add(new PossibleDuplicateDto(g.Id, g.FullName, g.Phone, g.Email, g.Nationality, ["Same ID document"]));
            }
        }
        return result;
    }

    // ------------------------------------------------------------------ create / update

    public async Task<GuestDto> CreateAsync(CreateGuestRequest r, CancellationToken ct)
    {
        if (!r.IgnoreDuplicates)
        {
            var dupes = await PossibleDuplicatesAsync(r.FirstName, r.LastName, r.Phone, r.Email, null, ct);
            if (dupes.Count > 0)
                throw new ConflictAppException("POSSIBLE_DUPLICATE",
                    "This guest may already have a profile. Open the existing one, or create a new profile anyway.", dupes);
        }

        var g = new Guest { OrganizationId = caller.RequireOrganizationId() };
        Apply(g, r.FirstName, r.LastName, r.Phone, r.Email, r.DateOfBirth, r.Nationality, r.CompanyName, r.Gstin, r.Notes, r.IsVip);
        SetAddress(g, r.Address);
        SetPreferences(g, r.Preferences);
        db.Guests.Add(g);
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(g, ct);
    }

    public async Task<GuestDto> UpdateAsync(Guid id, UpdateGuestRequest r, CancellationToken ct)
    {
        var g = await LoadAsync(id, ct);
        RequireActive(g);
        ConcurrencyGuard.ExpectVersion(db, g, r.Version);
        Apply(g, r.FirstName, r.LastName, r.Phone, r.Email, r.DateOfBirth, r.Nationality, r.CompanyName, r.Gstin, r.Notes, r.IsVip);
        SetAddress(g, r.Address);
        SetPreferences(g, r.Preferences);
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(g, ct);
    }

    private static void Apply(Guest g, string firstName, string? lastName, string? phone, string? email, DateOnly? dob,
        string? nationality, string? company, string? gstin, string? notes, bool isVip)
    {
        g.FirstName = GuestRules.Clean(firstName)!;
        g.LastName = GuestRules.Clean(lastName);
        g.Phone = GuestRules.NormalizePhone(phone);
        g.Email = GuestRules.NormalizeEmail(email);
        g.DateOfBirth = dob;
        g.Nationality = GuestRules.Clean(nationality)?.ToUpperInvariant();
        g.CompanyName = GuestRules.Clean(company);
        g.Gstin = GuestRules.Clean(gstin)?.ToUpperInvariant();
        g.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        g.IsVip = isVip;
    }

    private void SetAddress(Guest g, GuestAddressDto? a)
    {
        var home = g.Addresses.FirstOrDefault(x => x.AddressType == "HOME");
        var next = a is null ? null : new GuestAddress
        {
            Line1 = GuestRules.Clean(a.Line1),
            Line2 = GuestRules.Clean(a.Line2),
            City = GuestRules.Clean(a.City),
            State = GuestRules.Clean(a.State),
            StateCode = GuestRules.Clean(a.StateCode),
            PostalCode = GuestRules.Clean(a.PostalCode),
            CountryCode = (GuestRules.Clean(a.CountryCode) ?? "IN").ToUpperInvariant(),
        };
        if (next is null || next.IsEmpty)
        {
            if (home is not null) { g.Addresses.Remove(home); db.GuestAddresses.Remove(home); }
            return;
        }
        if (home is null)
        {
            home = new GuestAddress { OrganizationId = g.OrganizationId, GuestId = g.Id, AddressType = "HOME" };
            g.Addresses.Add(home);
        }
        home.Line1 = next.Line1; home.Line2 = next.Line2; home.City = next.City; home.State = next.State;
        home.StateCode = next.StateCode; home.PostalCode = next.PostalCode; home.CountryCode = next.CountryCode;
    }

    private void SetPreferences(Guest g, IReadOnlyList<GuestPreferenceDto>? prefs)
    {
        var wanted = (prefs ?? [])
            .Select(p => (Category: p.Category.Trim().ToUpperInvariant(), Value: GuestRules.Clean(p.Value) ?? ""))
            .Where(p => p.Value.Length > 0).Distinct().ToList();
        foreach (var old in g.Preferences.Where(p => !wanted.Contains((p.Category, p.Value))).ToList())
        {
            g.Preferences.Remove(old);
            db.GuestPreferences.Remove(old);
        }
        foreach (var (cat, val) in wanted.Where(w => !g.Preferences.Any(p => p.Category == w.Category && p.Value == w.Value)))
            g.Preferences.Add(new GuestPreference { OrganizationId = g.OrganizationId, GuestId = g.Id, Category = cat, Value = val });
    }

    // ------------------------------------------------------------------ consent

    public async Task<GuestDto> RecordConsentAsync(Guid id, RecordConsentRequest r, CancellationToken ct)
    {
        var g = await LoadAsync(id, ct);
        RequireActive(g);
        db.GuestConsents.Add(new GuestConsent
        {
            OrganizationId = g.OrganizationId,
            GuestId = g.Id,
            Purpose = r.Purpose,
            Granted = r.Granted,
            Source = r.Source,
            RecordedAt = clock.GetUtcNow(),
            RecordedBy = caller.UserId,
        });
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(g, ct);
    }

    // ------------------------------------------------------------------ anonymise (right to be forgotten)

    /// <summary>
    /// Removes every piece of personal data from the profile — and from any profiles merged into it — and purges
    /// its ID documents and scans. The row stays (status ANONYMISED) so stays, invoices and reports still add up.
    /// The audit trail never held the values (personal fields are masked there), so nothing remains to scrub.
    /// </summary>
    public async Task<GuestDto> AnonymiseAsync(Guid id, AnonymiseGuestRequest r, CancellationToken ct)
    {
        var g = await LoadAsync(id, ct);
        if (g.Status == GuestStatuses.Anonymised)
            throw new BusinessRuleAppException("ALREADY_ANONYMISED", "This profile has already been anonymised.");
        if (g.Status == GuestStatuses.Merged)
            throw new BusinessRuleAppException("GUEST_MERGED", "This profile was merged into another one. Anonymise that profile instead.");

        audit.Reason = r.Reason.Trim();
        var now = clock.GetUtcNow();
        var merged = await db.Guests.Include(x => x.Addresses).Include(x => x.Preferences)
            .Where(x => x.MergedIntoId == g.Id).ToListAsync(ct);
        var everyone = merged.Prepend(g).ToList();
        var ids = everyone.Select(x => x.Id).ToList();

        var docs = await db.GuestDocuments.Where(d => ids.Contains(d.GuestId) && d.Status == DocumentStatuses.Active).ToListAsync(ct);
        var blobs = docs.Where(d => d.StorageKey is not null).Select(d => d.StorageKey!).ToList();
        foreach (var d in docs) d.Purge(now);

        foreach (var x in everyone)
        {
            x.FirstName = AnonymisedName;
            x.LastName = null; x.Phone = null; x.Email = null; x.DateOfBirth = null;
            x.Notes = null; x.CompanyName = null; x.Gstin = null; x.IsVip = false;
            foreach (var a in x.Addresses.ToList()) { x.Addresses.Remove(a); db.GuestAddresses.Remove(a); }
            foreach (var p in x.Preferences.ToList()) { x.Preferences.Remove(p); db.GuestPreferences.Remove(p); }
        }
        g.Status = GuestStatuses.Anonymised;
        g.AnonymisedAt = now;
        g.AnonymisedBy = caller.UserId;
        await db.SaveChangesAsync(ct);

        // Files go after the commit: if the commit had failed, the scans would still be needed.
        foreach (var key in blobs) await files.DeleteAsync(key, ct);
        return await ToDtoAsync(g, ct);
    }

    // ------------------------------------------------------------------ merge

    /// <summary>
    /// Folds the duplicate into the survivor: documents, consents, preferences and a missing address move across,
    /// empty fields on the survivor are filled from the duplicate, and the duplicate becomes MERGED (hidden from
    /// search, pointing at the survivor). Reservations will follow the same rule from Sprint 5.
    /// </summary>
    public async Task<GuestDto> MergeAsync(MergeGuestsRequest r, CancellationToken ct)
    {
        if (r.SurvivorId == r.DuplicateId)
            throw new BusinessRuleAppException("SAME_GUEST", "Choose two different profiles to merge.");
        var survivor = await LoadAsync(r.SurvivorId, ct);
        var dup = await LoadAsync(r.DuplicateId, ct);
        RequireActive(survivor);
        RequireActive(dup);
        ConcurrencyGuard.ExpectVersion(db, survivor, r.SurvivorVersion);
        ConcurrencyGuard.ExpectVersion(db, dup, r.DuplicateVersion);
        audit.Reason = r.Reason.Trim();

        survivor.LastName ??= dup.LastName;
        survivor.Phone ??= dup.Phone;
        survivor.Email ??= dup.Email;
        survivor.DateOfBirth ??= dup.DateOfBirth;
        survivor.Nationality ??= dup.Nationality;
        survivor.CompanyName ??= dup.CompanyName;
        survivor.Gstin ??= dup.Gstin;
        survivor.IsVip |= dup.IsVip;
        if (dup.Notes is not null && dup.Notes != survivor.Notes)
        {
            var combined = survivor.Notes is null ? dup.Notes : survivor.Notes + "\n\n" + dup.Notes;
            survivor.Notes = combined.Length <= 2000 ? combined : combined[..2000];
        }

        // Re-pointing the foreign key moves a row; EF fixes up both collections when it detects the change.
        foreach (var a in dup.Addresses.ToList())
        {
            if (survivor.Addresses.Any(s => s.AddressType == a.AddressType)) db.GuestAddresses.Remove(a);
            else a.GuestId = survivor.Id;
        }
        foreach (var p in dup.Preferences.ToList())
        {
            if (survivor.Preferences.Any(s => s.Category == p.Category && s.Value == p.Value)) db.GuestPreferences.Remove(p);
            else p.GuestId = survivor.Id;
        }
        foreach (var d in await db.GuestDocuments.Where(d => d.GuestId == dup.Id).ToListAsync(ct)) d.GuestId = survivor.Id;
        foreach (var c in await db.GuestConsents.Where(c => c.GuestId == dup.Id).ToListAsync(ct)) c.GuestId = survivor.Id;
        foreach (var earlier in await db.Guests.Where(x => x.MergedIntoId == dup.Id).ToListAsync(ct)) earlier.MergedIntoId = survivor.Id;
        // Bookings follow the person: the survivor becomes the booker, and replaces the duplicate on guest lists.
        foreach (var res in await db.Reservations.Where(x => x.PrimaryGuestId == dup.Id).ToListAsync(ct))
            res.PrimaryGuestId = survivor.Id;
        var onBoth = await db.ReservationGuests.Where(x => x.GuestId == survivor.Id).Select(x => x.ReservationId).ToListAsync(ct);
        foreach (var rg in await db.ReservationGuests.Where(x => x.GuestId == dup.Id).ToListAsync(ct))
        {
            if (onBoth.Contains(rg.ReservationId)) db.ReservationGuests.Remove(rg);
            else rg.GuestId = survivor.Id;
        }

        dup.Status = GuestStatuses.Merged;
        dup.MergedIntoId = survivor.Id;
        dup.MergedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(survivor, ct);
    }

    // ------------------------------------------------------------------ helpers

    internal async Task<Guest> LoadAsync(Guid id, CancellationToken ct, bool track = true)
    {
        var q = db.Guests.Include(g => g.Addresses).Include(g => g.Preferences).AsQueryable();
        if (!track) q = q.AsNoTracking();
        return await q.FirstOrDefaultAsync(g => g.Id == id, ct) ?? throw new NotFoundAppException("Guest", id);
    }

    private static void RequireActive(Guest g)
    {
        if (g.Status == GuestStatuses.Merged)
            throw new BusinessRuleAppException("GUEST_MERGED", "This profile was merged into another one and can't be changed.");
        if (g.Status == GuestStatuses.Anonymised)
            throw new BusinessRuleAppException("GUEST_ANONYMISED", "This profile has been anonymised and can't be changed.");
    }

    private async Task<GuestDto> ToDtoAsync(Guest g, CancellationToken ct)
    {
        var canSeeDocs = (await access.GetAsync(ct)).HasAnywhere(Perm.GuestDocumentRead);
        int? docCount = canSeeDocs
            ? await db.GuestDocuments.CountAsync(d => d.GuestId == g.Id && d.Status == DocumentStatuses.Active, ct)
            : null;
        var consents = await db.GuestConsents.AsNoTracking().Where(c => c.GuestId == g.Id)
            .GroupBy(c => c.Purpose)
            .Select(grp => grp.OrderByDescending(c => c.RecordedAt).ThenByDescending(c => c.Id).First())
            .ToListAsync(ct);
        var userIds = consents.Where(c => c.RecordedBy != null).Select(c => c.RecordedBy!.Value).Distinct().ToList();
        var names = await db.Users.IgnoreQueryFilters().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        var home = g.Addresses.FirstOrDefault(a => a.AddressType == "HOME");
        return new GuestDto(
            g.Id, g.FirstName, g.LastName, g.FullName, g.Phone, g.Email, g.DateOfBirth, g.Nationality, g.CompanyName, g.Gstin,
            g.Notes, g.IsVip, g.Status, g.MergedIntoId, g.AnonymisedAt,
            home is null ? null : new GuestAddressDto(home.Line1, home.Line2, home.City, home.State, home.StateCode, home.PostalCode, home.CountryCode),
            g.Preferences.OrderBy(p => p.Category).ThenBy(p => p.Value).Select(p => new GuestPreferenceDto(p.Category, p.Value)).ToList(),
            consents.OrderBy(c => c.Purpose).Select(c => new GuestConsentDto(c.Purpose, c.Granted, c.Source, c.RecordedAt,
                c.RecordedBy is { } u && names.TryGetValue(u, out var n) ? n : null)).ToList(),
            docCount, g.CreatedAt, g.UpdatedAt, g.Version);
    }
}
