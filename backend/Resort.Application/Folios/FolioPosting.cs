using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Reservations;
using Resort.Application.Tax;
using Resort.Domain.Folios;
using Resort.Domain.Organizations;
using Resort.Domain.Rates;
using Resort.Domain.Reservations;
using Resort.Domain.Tax;

namespace Resort.Application.Folios;

/// <summary>
/// The folio steps other modules take inside their own transaction (Sprint 8): open a booking's main folio when it is
/// confirmed, put the room nights on it at check-in (or night by night), take released nights back off when a guest
/// leaves early, and write reversal lines. The caller holds the reservation row lock.
///
/// Queries here filter by reservation id and ignore the tenant filter on purpose: the nightly job runs with no
/// signed-in user (so no organisation), and every caller has already loaded the reservation it passes in.
/// </summary>
public sealed class FolioPosting(IAppDbContext db, ICurrentUser caller, TimeProvider clock, TaxCalculator tax, Invoicing.FolioBilling billing)
{
    /// <summary>Statuses whose folio takes charges. A cancelled or no-show booking's folio is closed (refunds and
    /// cancellation fees come with payments, Sprint 10).</summary>
    public static readonly string[] Billable = [ReservationStatuses.Confirmed, ReservationStatuses.CheckedIn, ReservationStatuses.CheckedOut];

    /// <summary>The booking's main folio, opened now if it has none (idempotent).</summary>
    public async Task<Folio> EnsureMainFolioAsync(Reservation reservation, CancellationToken ct)
    {
        var folio = db.Folios.Local.FirstOrDefault(f => f.ReservationId == reservation.Id && f.IsPrimary)
                    ?? await db.Folios.IgnoreQueryFilters().SingleOrDefaultAsync(f => f.ReservationId == reservation.Id && f.IsPrimary, ct);
        if (folio is not null) return folio;
        folio = await OpenAsync(reservation, "Main", true, null, ct);
        await db.SaveChangesAsync(ct);
        return folio;
    }

    /// <summary>A new folio for the booking (not saved yet). Numbers come from the property's running counter.</summary>
    public async Task<Folio> OpenAsync(Reservation reservation, string name, bool primary, Guid? billTo, CancellationToken ct)
    {
        var n = (await db.Database.SqlQuery<long>(
            $"""
             INSERT INTO property_counters (property_id, name, last_value) VALUES ({reservation.PropertyId}, 'folio', 1)
             ON CONFLICT (property_id, name) DO UPDATE SET last_value = property_counters.last_value + 1
             RETURNING last_value AS "Value"
             """).ToListAsync(ct)).Single();
        var folio = new Folio
        {
            OrganizationId = reservation.OrganizationId,
            PropertyId = reservation.PropertyId,
            ReservationId = reservation.Id,
            Number = $"F{n:D5}",
            Name = name,
            IsPrimary = primary,
            BillToGuestId = billTo,
            Currency = reservation.Currency,
        };
        db.Folios.Add(folio);
        return folio;
    }

    /// <summary>The property's room charge posting mode (setting <see cref="RoomChargePosting.SettingKey"/>).</summary>
    public async Task<string> PostingModeAsync(Guid propertyId, CancellationToken ct)
    {
        var json = await db.PropertySettings.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.PropertyId == propertyId && s.Key == RoomChargePosting.SettingKey).Select(s => s.Value).SingleOrDefaultAsync(ct);
        if (json is null) return RoomChargePosting.AtCheckIn;
        using var doc = JsonDocument.Parse(json);
        var value = doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString()?.Trim().ToUpperInvariant() : null;
        return value is not null && RoomChargePosting.All.Contains(value) ? value : RoomChargePosting.AtCheckIn;
    }

    /// <summary>
    /// Puts the room nights that are due on the main folio: the whole stay (posting AT_CHECK_IN) or every night up to and
    /// including today (NIGHTLY). A night already charged is skipped (one ROOM_NIGHT charge per booking and night, also
    /// enforced by a unique index), so this can run any number of times. Returns how many nights were posted.
    /// </summary>
    public async Task<int> PostDueRoomNightsAsync(Property property, Reservation reservation, CancellationToken ct)
    {
        var today = property.LocalDate(clock.GetUtcNow());
        var mode = await PostingModeAsync(property.Id, ct);
        var upTo = mode == RoomChargePosting.Nightly ? Min(today.AddDays(1), reservation.CheckOut) : reservation.CheckOut;
        return await PostRoomNightsAsync(reservation, upTo, ct);
    }

    /// <summary>Charges every night from check-in up to (not including) <paramref name="upTo"/> that isn't charged yet:
    /// one line per night with the price of all the booking's rooms that night, as booked.</summary>
    public async Task<int> PostRoomNightsAsync(Reservation reservation, DateOnly upTo, CancellationToken ct)
    {
        if (upTo <= reservation.CheckIn) return 0;
        var folio = await EnsureMainFolioAsync(reservation, ct);
        var charged = (await db.FolioItems.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.ReservationId == reservation.Id && i.Source == FolioItemSources.RoomNight
                            && i.ItemType == FolioItemTypes.Charge && i.TransferredFromItemId == null)
                .Select(i => i.ServiceDate).ToListAsync(ct))
            .ToHashSet();
        var rows = await db.ReservationUnits.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.ReservationId == reservation.Id && u.SupersededAt == null).ToListAsync(ct);
        var rowIds = rows.Select(u => u.Id).ToList();
        var nights = await db.ReservationRateBreakdowns.IgnoreQueryFilters().AsNoTracking()
            .Where(n => rowIds.Contains(n.ReservationUnitId) && n.StayDate >= reservation.CheckIn && n.StayDate < upTo)
            .ToListAsync(ct);
        var unitIds = rows.Select(u => u.UnitId).Distinct().ToList();
        var units = await db.AccommodationUnits.IgnoreQueryFilters().AsNoTracking().Include(u => u.Beds)
            .Where(u => unitIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);

        var posted = 0;
        foreach (var night in nights.GroupBy(n => n.StayDate).Where(g => !charged.Contains(g.Key)).OrderBy(g => g.Key))
        {
            var places = night.Select(n => rows.Single(u => u.Id == n.ReservationUnitId))
                .OrderBy(u => units[u.UnitId].SortOrder).ThenBy(u => units[u.UnitId].Code, StringComparer.Ordinal)
                .Select(u => u.BedId is { } b ? $"{units[u.UnitId].Name} bed {units[u.UnitId].Beds.First(x => x.Id == b).Code}" : units[u.UnitId].Name)
                .Distinct().ToList();
            var price = night.Sum(n => n.Total);
            // GST slabs go by the value of one room (or bed) for one night, so each room of the night is a part
            await ChargeAsync(folio, new FolioItem
            {
                Category = ChargeCategories.Room,
                Description = Trim($"Room night: {string.Join(", ", places)}"),
                ServiceDate = night.Key,
                Quantity = 1,
                UnitPrice = price,
                Source = FolioItemSources.RoomNight,
            }, night.Select(n => new TaxablePart(n.Total, TaxMath.Round(n.Total / n.Units))).ToList(), ct);
            posted++;
        }
        if (posted > 0) await db.SaveChangesAsync(ct);
        return posted;
    }

    /// <summary>Reverses the room nights from <paramref name="from"/> on (the guest left early): every room night charge
    /// still counting for those nights, on whichever folio it is now.</summary>
    public async Task<int> ReverseRoomNightsFromAsync(Reservation reservation, DateOnly from, string reason, CancellationToken ct)
    {
        var live = await LiveChargesAsync(reservation.Id, ct);
        var released = live.Where(i => i.Source == FolioItemSources.RoomNight && i.ServiceDate >= from).ToList();
        if (released.Count == 0) return 0;
        var folios = await db.Folios.IgnoreQueryFilters().Where(f => f.ReservationId == reservation.Id).ToDictionaryAsync(f => f.Id, ct);
        foreach (var charge in released)
            await ReverseAsync(folios[charge.FolioId], charge, reason, ct);
        await db.SaveChangesAsync(ct);
        return released.Count;
    }

    /// <summary>Charges of a booking that still count: not reversed (a moved line counts on the folio it moved to).</summary>
    public async Task<List<FolioItem>> LiveChargesAsync(Guid reservationId, CancellationToken ct)
    {
        var items = await db.FolioItems.IgnoreQueryFilters().AsNoTracking().Where(i => i.ReservationId == reservationId).ToListAsync(ct);
        var reversed = items.Where(i => i.ReversesItemId != null).Select(i => i.ReversesItemId!.Value).ToHashSet();
        return items.Where(i => i.ItemType == FolioItemTypes.Charge && !reversed.Contains(i.Id)).ToList();
    }

    /// <summary>Adds a reversal line for <paramref name="charge"/> (same category, date and price; quantity, amount, tax
    /// and total negated) and its negated tax rows. Not saved.</summary>
    public async Task<FolioItem> ReverseAsync(Folio folio, FolioItem charge, string reason, CancellationToken ct)
    {
        await RequireNotInvoicedAsync(charge, ct);
        var (quantity, amount, tax, total) = FolioMath.Negate(charge);
        var reversal = new FolioItem
        {
            OrganizationId = folio.OrganizationId,
            PropertyId = folio.PropertyId,
            FolioId = folio.Id,
            ReservationId = folio.ReservationId,
            ItemType = FolioItemTypes.Reversal,
            Category = charge.Category,
            Description = Trim($"Reversal: {charge.Description}"),
            ServiceDate = charge.ServiceDate,
            Quantity = quantity,
            UnitPrice = charge.UnitPrice,
            Amount = amount,
            TaxAmount = tax,
            Total = total,
            Source = charge.Source,
            PackageId = charge.PackageId,
            GroupId = charge.GroupId,
            ReversesItemId = charge.Id,
            Reason = reason,
            PostedAt = clock.GetUtcNow(),
            PostedBy = caller.UserId,
        };
        db.FolioItems.Add(reversal);
        await CopyTaxesAsync(charge, reversal, -1, ct);
        return reversal;
    }

    /// <summary>
    /// Adds a new charge with its GST (Sprint 9): the tax engine finds the rule effective on the service date, and the
    /// rule id and rate are stored with the line (folio_item_taxes), so a later rule change never alters it.
    /// <paramref name="parts"/>: how the line splits for the slab (default: the whole amount at the price of one item).
    /// Refused when the property is registered and the category is taxed but no rule covers the day. Not saved.
    /// </summary>
    public async Task<FolioItem> ChargeAsync(Folio folio, FolioItem item, IReadOnlyList<TaxablePart>? parts, CancellationToken ct)
    {
        var amount = FolioMath.LineAmount(item.Quantity, item.UnitPrice);
        var outcome = await tax.CalculateAsync(folio.PropertyId, item.Category, null, item.ServiceDate,
            parts ?? [new TaxablePart(amount, item.UnitPrice)], null, ct);
        if (outcome.RuleMissing)
            throw new BusinessRuleAppException("TAX_RULE_MISSING",
                $"{outcome.NoTaxReason} Until then {ChargeCategories.Words(item.Category).ToLowerInvariant()} charges dated {QuoteEngine.Day(item.ServiceDate)} can't be posted.");
        item.TaxAmount = outcome.Total;
        Add(folio, item);
        foreach (var row in TaxCalculator.Rows(outcome, item, item.PostedAt)) db.FolioItemTaxes.Add(row);
        return item;
    }

    /// <summary>Adds a charge to <paramref name="folio"/>: fills the folio, booking, amount and total, who and when. Not saved.
    /// The tax is what <see cref="FolioItem.TaxAmount"/> already holds: use <see cref="ChargeAsync"/> for a new charge.</summary>
    public FolioItem Add(Folio folio, FolioItem item)
    {
        item.OrganizationId = folio.OrganizationId;
        item.PropertyId = folio.PropertyId;
        item.FolioId = folio.Id;
        item.ReservationId = folio.ReservationId;
        item.ItemType = FolioItemTypes.Charge;
        item.Amount = FolioMath.LineAmount(item.Quantity, item.UnitPrice);
        item.Total = item.Amount + item.TaxAmount;
        item.PostedAt = clock.GetUtcNow();
        item.PostedBy = caller.UserId;
        db.FolioItems.Add(item);
        return item;
    }

    /// <summary>Copies the tax rows of <paramref name="from"/> onto <paramref name="to"/> (sign −1 for a reversal).</summary>
    public async Task CopyTaxesAsync(FolioItem from, FolioItem to, int sign, CancellationToken ct)
    {
        var taxes = await db.FolioItemTaxes.IgnoreQueryFilters().AsNoTracking().Where(t => t.FolioItemId == from.Id).ToListAsync(ct);
        foreach (var t in taxes)
            db.FolioItemTaxes.Add(new FolioItemTax
            {
                OrganizationId = to.OrganizationId,
                PropertyId = to.PropertyId,
                FolioItemId = to.Id,
                TaxRuleId = t.TaxRuleId,
                TaxCode = t.TaxCode,
                Rate = t.Rate,
                TaxableValue = sign * t.TaxableValue,
                Amount = sign * t.Amount,
                CreatedAt = clock.GetUtcNow(),
            });
    }

    /// <summary>
    /// A charge on an invoice (or debit note) that stands, or one a credit note has reduced, is corrected only by a credit
    /// note (Sprint 12, brief §25): reversing or moving it on the folio would make the bill and the invoice disagree.
    /// </summary>
    public async Task RequireNotInvoicedAsync(FolioItem charge, CancellationToken ct, string? field = null)
    {
        string? message = null;
        if ((await billing.BilledAsync([charge.FolioId], ct)).TryGetValue(charge.Id, out var number))
            message = $"\"{charge.Description}\" is on invoice {number}, so it can't be reversed or moved. Issue a credit note on {number} instead.";
        else if (await db.FolioItems.IgnoreQueryFilters().AnyAsync(i => i.AdjustsItemId == charge.Id, ct))
            message = $"\"{charge.Description}\" was reduced by a credit note, so it can't be reversed or moved. Correct it with another credit note.";
        if (message is null) return;
        if (field is not null) throw ReservationService.Field(field, message);
        throw new BusinessRuleAppException("INVOICED", message);
    }

    /// <summary>What the booking's folios add up to: the sum of every line, before payments.</summary>
    public async Task<decimal> TotalAsync(Guid reservationId, CancellationToken ct) =>
        await db.FolioItems.IgnoreQueryFilters().Where(i => i.ReservationId == reservationId).SumAsync(i => (decimal?)i.Total, ct) ?? 0m;

    internal static string Trim(string s) => s.Length <= 200 ? s : s[..197] + "…";

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
}

/// <summary>What a reservation owes for the check-out rule: the sum of its folios (Sprint 8) minus everything paid for
/// it, on its folios or on account (Sprint 10).</summary>
public sealed class FolioBalances(FolioPosting posting, Payments.PaymentLedger ledger) : FrontDesk.IFolioBalances
{
    public async Task<decimal> BalanceAsync(Guid reservationId, CancellationToken ct) =>
        await posting.TotalAsync(reservationId, ct) - await ledger.PaidForReservationAsync(reservationId, ct);
}

/// <summary>Runs the nightly room charge posting for every checked-in booking (the job calls it every few minutes;
/// posting is idempotent). Each booking in its own transaction, under its row lock, as check-in takes.</summary>
public sealed class RoomChargeRun(IAppDbContext db, FolioPosting posting)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var ids = await db.Reservations.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.Status == ReservationStatuses.CheckedIn).Select(r => r.Id).ToListAsync(ct);
        var posted = 0;
        foreach (var id in ids)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM reservations WHERE id = {id} FOR UPDATE", ct);
            var reservation = await db.Reservations.IgnoreQueryFilters().SingleAsync(r => r.Id == id, ct);
            if (reservation.Status != ReservationStatuses.CheckedIn) continue;
            var property = await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == reservation.PropertyId, ct);
            posted += await posting.PostDueRoomNightsAsync(property, reservation, ct);
            await tx.CommitAsync(ct);
        }
        return posted;
    }
}
