using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Reservations;
using Resort.Application.Validation;
using Resort.Contracts;
using Resort.Domain.Folios;
using Resort.Domain.Organizations;
using Resort.Domain.Rates;
using Resort.Domain.Reservations;
using Resort.Domain.Tax;

namespace Resort.Application.Folios;

/// <summary>
/// Folios (Sprint 8): the running bill of a stay. Staff post charges (restaurant, transport, activities …, or a package
/// split into its components), managers reverse a charge with a reason, and reception moves lines to another folio to
/// split the bill. Nothing is ever edited or deleted: every correction is a new line (brief §85).
///
/// Every change locks the booking's reservation row first (the lock check-in, check-out and the nightly room charge run
/// take), so a charge can't land between the balance check and the check-out, and two reversals of the same line can't
/// both pass.
/// </summary>
public sealed class FolioService(
    IAppDbContext db, ICurrentUser caller, AuditContext audit, TimeProvider clock, ReservationService reservations, FolioPosting posting)
{
    public const int MaxQuantity = 9999;
    public const int MaxPackageQuantity = 50;
    private static readonly string[] TaxCodeOrder = [TaxCodes.Cgst, TaxCodes.Sgst, TaxCodes.Igst, TaxCodes.Cess];

    public sealed record ListQuery(string? Q, string? Show, int Page, int PageSize);

    // ------------------------------------------------------------------ queries

    /// <summary>Folios of the property. Show: in-house (default: guests checked in now), open (also confirmed arrivals)
    /// or all. Q matches the folio number, the booking reference or the guest (name, phone).</summary>
    public async Task<PagedResult<FolioListItemDto>> ListAsync(Guid propertyId, ListQuery q, CancellationToken ct)
    {
        await LoadPropertyAsync(propertyId, ct);
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 200);
        var show = string.IsNullOrWhiteSpace(q.Show) ? "in-house" : q.Show.Trim().ToLowerInvariant();
        string[] statuses = show switch
        {
            "in-house" => [ReservationStatuses.CheckedIn],
            "open" => [ReservationStatuses.Confirmed, ReservationStatuses.CheckedIn],
            "all" => ReservationStatuses.All,
            _ => throw ReservationService.Field("Show", $"'{q.Show}' isn't a folio filter. Use in-house, open or all."),
        };

        var rows = from f in db.Folios.AsNoTracking()
                   join r in db.Reservations.AsNoTracking() on f.ReservationId equals r.Id
                   where f.PropertyId == propertyId && statuses.Contains(r.Status)
                   select new { Folio = f, Reservation = r };
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var text = q.Q.Trim();
            var code = text.ToUpperInvariant();
            var like = "%" + text.ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            var digits = new string(text.Where(char.IsDigit).ToArray());
            var guestIds = db.Guests.Where(g => EF.Functions.Like(g.SearchText, like)
                                                || (digits.Length >= 4 && g.PhoneDigits.Contains(digits))).Select(g => g.Id);
            rows = rows.Where(x => x.Folio.Number == code || x.Reservation.Reference == code || guestIds.Contains(x.Reservation.PrimaryGuestId));
        }

        var total = await rows.CountAsync(ct);
        var pageRows = await rows.OrderBy(x => x.Reservation.CheckIn).ThenBy(x => x.Reservation.Reference)
            .ThenByDescending(x => x.Folio.IsPrimary).ThenBy(x => x.Folio.Number)
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<FolioListItemDto>(await ListItemsAsync(pageRows.Select(x => (x.Folio, x.Reservation)).ToList(), ct), page, size, total);
    }

    /// <summary>Every folio of one booking, the main one first.</summary>
    public async Task<IReadOnlyList<FolioListItemDto>> ForReservationAsync(Guid propertyId, Guid reservationId, CancellationToken ct)
    {
        var reservation = await db.Reservations.AsNoTracking().SingleOrDefaultAsync(r => r.Id == reservationId && r.PropertyId == propertyId, ct)
                          ?? throw new NotFoundAppException("Reservation", reservationId);
        var folios = await db.Folios.AsNoTracking().Where(f => f.ReservationId == reservationId)
            .OrderByDescending(f => f.IsPrimary).ThenBy(f => f.Number).ToListAsync(ct);
        return await ListItemsAsync(folios.Select(f => (f, reservation)).ToList(), ct);
    }

    public async Task<FolioDto> GetAsync(Guid propertyId, Guid id, CancellationToken ct) =>
        await ToDtoAsync(await LoadAsync(propertyId, id, ct), ct);

    // ------------------------------------------------------------------ post a charge

    /// <summary>
    /// Posts a charge, or a package as one line per component. Under an Idempotency-Key: a retry with the same key and
    /// body returns the folio as it was after the first post, without charging twice.
    /// </summary>
    public async Task<(FolioDto Folio, bool Replayed)> PostChargeAsync(Guid propertyId, Guid id, string? idempotencyKey, PostChargeRequest r,
        CancellationToken ct)
    {
        var key = Idempotency.RequireKey(idempotencyKey);
        var property = await LoadPropertyAsync(propertyId, ct);
        var orgId = caller.RequireOrganizationId();
        var scope = $"POST /api/v1/properties/{propertyId}/folios/{id}/charges";

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await Idempotency.ClaimAsync(db, orgId, caller.UserId, scope, key, Idempotency.Hash(r), "charge", ct) is { } stored)
            return (JsonSerializer.Deserialize<FolioDto>(stored, Idempotency.Json)!, true);

        var (folio, reservation) = await LockAsync(propertyId, id, ct);
        RequireBillable(folio, reservation);
        var today = property.LocalDate(clock.GetUtcNow());
        var day = r.ServiceDate ?? today;
        if (day > today)
            throw ReservationService.Field(nameof(r.ServiceDate),
                $"A charge can't be dated after today ({QuoteEngine.Day(today)} at the property). Post it on the day of the service.");
        if (day < reservation.CheckIn.AddDays(-30))
            throw ReservationService.Field(nameof(r.ServiceDate),
                $"{reservation.Reference} arrives on {QuoteEngine.Day(reservation.CheckIn)}; a charge can be dated at most 30 days before that.");

        if (r.PackageId is { } packageId)
        {
            var package = await db.Packages.AsNoTracking().Include(p => p.Components)
                              .SingleOrDefaultAsync(p => p.Id == packageId && p.PropertyId == propertyId, ct)
                          ?? throw ReservationService.Field(nameof(r.PackageId), "That package doesn't exist at this property. Reload and choose again.");
            if (!package.IsActive)
                throw ReservationService.Field(nameof(r.PackageId), $"{package.Name} is switched off, so it can't be sold. Turn it back on in Settings → Packages.");
            var quantity = (int)r.Quantity;
            var parts = package.Components.Where(c => c.Amount > 0).OrderBy(c => c.SortOrder).ThenBy(c => c.Category, StringComparer.Ordinal).ToList();
            if (package.Price * quantity > Money.Max)
                throw ReservationService.Field(nameof(r.Quantity), $"{quantity} × {Money.Rupees(package.Price)} is more than {Money.Rupees(Money.Max)}; post fewer at a time.");
            var group = Guid.CreateVersion7();
            for (var i = 0; i < parts.Count; i++)
                // the room part of a package is taxed on its value for one night
                await posting.ChargeAsync(folio, new FolioItem
                {
                    Category = parts[i].Category,
                    Description = FolioPosting.Trim($"{package.Name}: {parts[i].Description}"),
                    ServiceDate = day,
                    Quantity = quantity,
                    UnitPrice = parts[i].Amount,
                    Source = FolioItemSources.Package,
                    PackageId = package.Id,
                    GroupId = group,
                }, [new TaxablePart(parts[i].Amount * quantity,
                    parts[i].Category == ChargeCategories.Room ? TaxMath.Round(parts[i].Amount / Math.Max((short)1, package.Nights)) : parts[i].Amount)], ct);
        }
        else
        {
            if (FolioMath.LineAmount(r.Quantity, r.UnitPrice) > Money.Max)
                throw ReservationService.Field(nameof(r.UnitPrice),
                    $"{r.Quantity:0.###} × {Money.Rupees(r.UnitPrice)} is more than {Money.Rupees(Money.Max)}; split it into smaller charges.");
            await posting.ChargeAsync(folio, new FolioItem
            {
                Category = r.Category!.Trim().ToUpperInvariant(),
                Description = r.Description!.Trim(),
                ServiceDate = day,
                Quantity = r.Quantity,
                UnitPrice = r.UnitPrice,
                Source = FolioItemSources.Manual,
            }, null, ct);
        }
        await db.SaveChangesAsync(ct);

        var dto = await ToDtoAsync(folio, ct);
        await Idempotency.CompleteAsync(db, orgId, scope, key, 201, dto, folio.Id, ct);
        await tx.CommitAsync(ct);
        return (dto, false);
    }

    // ------------------------------------------------------------------ reverse a charge

    /// <summary>Cancels a charge with a reversal line (needs folio.adjust, checked by the endpoint, and a reason). The
    /// charge stays on the bill, marked reversed; both lines are in the audit trail.</summary>
    public async Task<FolioDto> ReverseAsync(Guid propertyId, Guid id, Guid itemId, ReverseChargeRequest r, CancellationToken ct)
    {
        var reason = ReservationService.Clean(r.Reason)!;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (folio, _) = await LockAsync(propertyId, id, ct);
        var item = await db.FolioItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId && i.FolioId == folio.Id, ct)
                   ?? throw new NotFoundAppException("Folio line", itemId);
        if (item.ItemType == FolioItemTypes.Reversal)
            throw new BusinessRuleAppException("NOT_REVERSIBLE",
                $"\"{item.Description}\" is itself a reversal, so it can't be reversed. Post the charge again instead.");
        await RequireNotReversedAsync(item, ct);

        audit.Reason = reason;
        await posting.ReverseAsync(folio, item, reason, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ToDtoAsync(folio, ct);
    }

    // ------------------------------------------------------------------ split the bill

    /// <summary>
    /// Moves lines to another folio of the same booking (an existing one, or a new one opened here): each line is
    /// reversed on this folio ("Moved to F00012") and posted again on the other with a link back, so the total of the
    /// booking doesn't change and both bills show what happened.
    /// </summary>
    public async Task<SplitFolioResultDto> SplitAsync(Guid propertyId, Guid id, SplitFolioRequest r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (from, reservation) = await LockAsync(propertyId, id, ct);
        RequireBillable(from, reservation);

        Folio to;
        if (r.TargetFolioId is { } targetId)
        {
            if (targetId == from.Id)
                throw ReservationService.Field(nameof(r.TargetFolioId), $"The lines are already on {from.Number}. Choose another folio, or open a new one.");
            to = await db.Folios.SingleOrDefaultAsync(f => f.Id == targetId && f.ReservationId == reservation.Id, ct)
                 ?? throw ReservationService.Field(nameof(r.TargetFolioId),
                     $"That folio isn't one of {reservation.Reference}'s. Lines can only move between the folios of the same booking.");
        }
        else
        {
            var name = ReservationService.Clean(r.NewFolioName)!;
            if (r.BillToGuestId is { } guestId && !await db.Guests.AnyAsync(g => g.Id == guestId, ct))
                throw ReservationService.Field(nameof(r.BillToGuestId), "That guest doesn't exist. Search for them again.");
            to = await posting.OpenAsync(reservation, name, false, r.BillToGuestId, ct);
            await db.SaveChangesAsync(ct);
        }

        var ids = r.ItemIds.Distinct().ToList();
        var items = await db.FolioItems.AsNoTracking().Where(i => ids.Contains(i.Id) && i.FolioId == from.Id).ToListAsync(ct);
        if (items.Count != ids.Count)
            throw ReservationService.Field(nameof(r.ItemIds), $"Some of the chosen lines aren't on {from.Number}. Reload the folio and choose again.");
        if (items.FirstOrDefault(i => i.ItemType == FolioItemTypes.Reversal) is { } rev)
            throw ReservationService.Field(nameof(r.ItemIds), $"\"{rev.Description}\" is a reversal; only charges can be moved.");
        foreach (var item in items) await RequireNotReversedAsync(item, ct, nameof(r.ItemIds));

        var reason = $"Moved to {to.Number} ({to.Name})";
        audit.Reason = reason;
        foreach (var item in items.OrderBy(i => i.ServiceDate).ThenBy(i => i.PostedAt))
        {
            await posting.ReverseAsync(from, item, reason, ct);
            var copy = new FolioItem
            {
                Category = item.Category,
                Description = item.Description,
                ServiceDate = item.ServiceDate,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice,
                TaxAmount = item.TaxAmount,
                Source = item.Source,
                PackageId = item.PackageId,
                GroupId = item.GroupId,
                TransferredFromItemId = item.Id,
            };
            posting.Add(to, copy);
            await posting.CopyTaxesAsync(item, copy, 1, ct);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new SplitFolioResultDto(await ToDtoAsync(from, ct), await ToDtoAsync(to, ct));
    }

    // ------------------------------------------------------------------ helpers

    private async Task RequireNotReversedAsync(FolioItem item, CancellationToken ct, string? field = null)
    {
        var by = await db.FolioItems.AsNoTracking().SingleOrDefaultAsync(i => i.ReversesItemId == item.Id, ct);
        if (by is null) return;
        var moved = await db.FolioItems.AsNoTracking().Where(i => i.TransferredFromItemId == item.Id)
            .Join(db.Folios, i => i.FolioId, f => f.Id, (i, f) => f.Number).SingleOrDefaultAsync(ct);
        var who = by.PostedBy is { } uid
            ? await db.Users.IgnoreQueryFilters().Where(u => u.Id == uid).Select(u => u.FullName).SingleOrDefaultAsync(ct)
            : null;
        var property = await db.Properties.AsNoTracking().SingleAsync(p => p.Id == item.PropertyId, ct);
        var local = TimeZoneInfo.ConvertTime(by.PostedAt, TimeZoneInfo.FindSystemTimeZoneById(property.Timezone));
        var when = $"at {local:HH:mm} on {QuoteEngine.Day(DateOnly.FromDateTime(local.DateTime))}{(who is null ? "" : $" by {who}")}";
        var message = moved is not null
            ? $"\"{item.Description}\" was moved to {moved} {when}. Open {moved} to change it."
            : $"\"{item.Description}\" was already reversed {when} (reason: {by.Reason}). Reload to see it.";
        if (field is not null) throw ReservationService.Field(field, message);
        throw new ConflictAppException("ALREADY_REVERSED", message, new { reversedBy = by.Id });
    }

    private static void RequireBillable(Folio folio, Reservation reservation)
    {
        if (!FolioPosting.Billable.Contains(reservation.Status))
            throw new BusinessRuleAppException("FOLIO_CLOSED", CantPost(folio, reservation)!);
    }

    private static string? CantPost(Folio folio, Reservation reservation) =>
        FolioPosting.Billable.Contains(reservation.Status) ? null
            : $"{reservation.Reference} is {ReservationStatuses.Words(reservation.Status)}, so nothing can be charged to {folio.Number}.";

    /// <summary>Locks the folio's booking (the same lock check-in, check-out and booking changes take), then loads both.</summary>
    private async Task<(Folio Folio, Reservation Reservation)> LockAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        var reservationId = await db.Folios.AsNoTracking().Where(f => f.Id == id && f.PropertyId == propertyId).Select(f => (Guid?)f.ReservationId)
                                .SingleOrDefaultAsync(ct)
                            ?? throw new NotFoundAppException("Folio", id);
        var reservation = await reservations.LockAsync(propertyId, reservationId, ct);
        var folio = await db.Folios.SingleAsync(f => f.Id == id, ct);
        return (folio, reservation);
    }

    private async Task<Folio> LoadAsync(Guid propertyId, Guid id, CancellationToken ct) =>
        await db.Folios.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id && f.PropertyId == propertyId, ct)
        ?? throw new NotFoundAppException("Folio", id);

    private async Task<Property> LoadPropertyAsync(Guid propertyId, CancellationToken ct) =>
        await db.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == propertyId, ct) ?? throw new NotFoundAppException("Property", propertyId);

    private async Task<List<FolioListItemDto>> ListItemsAsync(List<(Folio Folio, Reservation Reservation)> rows, CancellationToken ct)
    {
        var folioIds = rows.Select(x => x.Folio.Id).ToList();
        var sums = await db.FolioItems.AsNoTracking().Where(i => folioIds.Contains(i.FolioId))
            .GroupBy(i => i.FolioId).Select(g => new { g.Key, Total = g.Sum(i => i.Total), Lines = g.Count() })
            .ToDictionaryAsync(x => x.Key, ct);
        var reservationIds = rows.Select(x => x.Reservation.Id).Distinct().ToList();
        var units = await db.ReservationUnits.AsNoTracking()
            .Where(u => reservationIds.Contains(u.ReservationId) && u.SupersededAt == null).ToListAsync(ct);
        var roomNames = await reservations.RoomNamesAsync(units.Select(u => u.UnitId), ct);
        var guestIds = rows.Select(x => x.Reservation.PrimaryGuestId).Distinct().ToList();
        var guests = await db.Guests.AsNoTracking().Where(g => guestIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.FullName, ct);
        return rows.Select(x =>
        {
            var (f, r) = x;
            var s = sums.GetValueOrDefault(f.Id);
            var total = s?.Total ?? 0m;
            return new FolioListItemDto(f.Id, f.Number, f.Name, f.IsPrimary, r.Id, r.Reference, r.Status, guests.GetValueOrDefault(r.PrimaryGuestId, ""),
                r.CheckIn, r.CheckOut, ReservationService.RoomsText(units.Where(u => u.ReservationId == r.Id && u.EndDate == r.CheckOut), roomNames),
                s?.Lines ?? 0, total, total, f.Currency);
        }).ToList();
    }

    private async Task<FolioDto> ToDtoAsync(Folio folio, CancellationToken ct)
    {
        var reservation = await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == folio.ReservationId, ct);
        var all = await db.FolioItems.AsNoTracking().Where(i => i.ReservationId == reservation.Id).ToListAsync(ct);
        var folios = await db.Folios.AsNoTracking().Where(f => f.ReservationId == reservation.Id).ToListAsync(ct);
        var numbers = folios.ToDictionary(f => f.Id, f => f.Number);
        var items = all.Where(i => i.FolioId == folio.Id).OrderBy(i => i.ServiceDate).ThenBy(i => i.PostedAt).ThenBy(i => i.Id).ToList();
        var reversalOf = all.Where(i => i.ReversesItemId != null).ToDictionary(i => i.ReversesItemId!.Value);
        var movedTo = all.Where(i => i.TransferredFromItemId != null).ToDictionary(i => i.TransferredFromItemId!.Value, i => numbers[i.FolioId]);
        var byId = all.ToDictionary(i => i.Id);
        var packageIds = items.Where(i => i.PackageId != null).Select(i => i.PackageId!.Value).Distinct().ToList();
        var packages = await db.Packages.AsNoTracking().Where(p => packageIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var userIds = items.Where(i => i.PostedBy != null).Select(i => i.PostedBy!.Value).Distinct().ToList();
        var users = await db.Users.IgnoreQueryFilters().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        var guestIds = new[] { reservation.PrimaryGuestId, folio.BillToGuestId ?? reservation.PrimaryGuestId }.Distinct().ToList();
        var guests = await db.Guests.AsNoTracking().Where(g => guestIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.FullName, ct);
        var units = await db.ReservationUnits.AsNoTracking()
            .Where(u => u.ReservationId == reservation.Id && u.SupersededAt == null && u.EndDate == reservation.CheckOut).ToListAsync(ct);
        var rooms = ReservationService.RoomsText(units, await reservations.RoomNamesAsync(units.Select(u => u.UnitId), ct));

        var itemIds = items.Select(i => i.Id).ToList();
        var taxes = (await db.FolioItemTaxes.AsNoTracking().Where(t => itemIds.Contains(t.FolioItemId)).ToListAsync(ct))
            .OrderBy(t => t.Rate).ThenBy(t => Array.IndexOf(TaxCodeOrder, t.TaxCode))
            .ToLookup(t => t.FolioItemId, t => new FolioItemTaxDto(t.TaxCode, t.Rate, t.TaxableValue, t.Amount, t.TaxRuleId));
        var lines = items.Select(i =>
        {
            var reversal = reversalOf.GetValueOrDefault(i.Id);
            return new FolioItemDto(i.Id, i.ItemType, i.Category, i.Description, i.ServiceDate, i.Quantity, i.UnitPrice, i.Amount, i.TaxAmount,
                i.Total, i.Source, i.PackageId, i.PackageId is { } p ? packages.GetValueOrDefault(p) : null, i.GroupId, i.ReversesItemId,
                reversal?.Id, i.Reason, reversal?.Reason,
                i.TransferredFromItemId is { } src && byId.TryGetValue(src, out var o) ? numbers[o.FolioId] : null,
                movedTo.GetValueOrDefault(i.Id),
                i.PostedAt, i.PostedBy is { } u ? users.GetValueOrDefault(u) : null,
                i.ItemType == FolioItemTypes.Charge && reversal is null, taxes[i.Id].ToList());
        }).ToList();
        var categories = ChargeCategories.All
            .Select(c => new FolioCategoryTotalDto(c, items.Where(i => i.Category == c).Sum(i => i.Total)))
            .Where(c => items.Any(i => i.Category == c.Category)).ToList();
        var total = items.Sum(i => i.Total);
        var summaries = folios.OrderByDescending(f => f.IsPrimary).ThenBy(f => f.Number)
            .Select(f =>
            {
                var t = all.Where(i => i.FolioId == f.Id).Sum(i => i.Total);
                return new FolioSummaryDto(f.Id, f.Number, f.Name, f.IsPrimary, t, t);
            }).ToList();

        return new FolioDto(folio.Id, folio.Number, folio.Name, folio.IsPrimary, reservation.Id, reservation.Reference, reservation.Status,
            reservation.PrimaryGuestId, guests.GetValueOrDefault(reservation.PrimaryGuestId, ""), folio.BillToGuestId,
            folio.BillToGuestId is { } b ? guests.GetValueOrDefault(b) : null, reservation.CheckIn, reservation.CheckOut, rooms, folio.Currency,
            lines, categories,
            Charges: items.Where(i => i.ItemType == FolioItemTypes.Charge).Sum(i => i.Total),
            Reversals: items.Where(i => i.ItemType == FolioItemTypes.Reversal).Sum(i => i.Total),
            Tax: items.Sum(i => i.TaxAmount), Total: total, Payments: 0m, Balance: total,
            summaries, CanPost: CantPost(folio, reservation) is null, CantPostReason: CantPost(folio, reservation));
    }
}
