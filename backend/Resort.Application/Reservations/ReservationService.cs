using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Resort.Application.Accommodation;
using Resort.Application.Common;
using Resort.Application.Folios;
using Resort.Contracts;
using Resort.Domain.Accommodation;
using Resort.Domain.Guests;
using Resort.Domain.Organizations;
using Resort.Domain.Rates;
using Resort.Domain.Reservations;

namespace Resort.Application.Reservations;

/// <summary>
/// Reservations for one property: create (idempotent), change dates/rooms/guests, confirm, cancel, no-show, notes.
/// The booking algorithm (docs/architecture/booking-algorithm.md):
///   1. claim the Idempotency-Key (INSERT … ON CONFLICT DO NOTHING; a retry waits for the first request, then replays it)
///   2. lock the rooms of the requested types (SELECT … ORDER BY id FOR UPDATE — fixed order, so no deadlocks)
///   3. load held reservations and active blocks, let <see cref="AvailabilityEngine"/> pick free rooms/beds
///   4. price every night from the rate grid (<see cref="QuoteEngine"/>), insert, store the response, COMMIT
/// The EXCLUDE constraints on reservation_units are the final guard: a 23P01 becomes 409 ROOM_UNAVAILABLE.
/// </summary>
public sealed class ReservationService(IAppDbContext db, ICurrentUser caller, AuditContext audit, TimeProvider clock, FolioPosting folios)
{
    public const int MaxNights = 180;

    public sealed record ListQuery(string? Q, string? Status, DateOnly? From, DateOnly? To, int Page, int PageSize);

    // ------------------------------------------------------------------ queries

    public async Task<PagedResult<ReservationListItemDto>> ListAsync(Guid propertyId, ListQuery q, CancellationToken ct)
    {
        var property = await LoadPropertyAsync(propertyId, ct);
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 200);

        var rows = db.Reservations.AsNoTracking().Where(r => r.PropertyId == propertyId);
        // Default: stays that haven't ended yet (today's departures included), so the list starts with what matters now.
        var from = q.From ?? (q.To is null && string.IsNullOrWhiteSpace(q.Q) ? property.LocalDate(clock.GetUtcNow()) : null);
        if (from is { } f) rows = rows.Where(r => r.CheckOut >= f);
        if (q.To is { } t) rows = rows.Where(r => r.CheckIn < t);
        if (!string.IsNullOrWhiteSpace(q.Status))
        {
            var statuses = q.Status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToUpperInvariant()).ToList();
            var unknown = statuses.FirstOrDefault(s => !ReservationStatuses.All.Contains(s));
            if (unknown is not null)
                throw Field("Status", $"'{unknown}' isn't a reservation status. Use {string.Join(", ", ReservationStatuses.All)}.");
            rows = rows.Where(r => statuses.Contains(r.Status));
        }
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var text = q.Q.Trim();
            var reference = text.ToUpperInvariant();
            var like = "%" + text.ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            var digits = new string(text.Where(char.IsDigit).ToArray());
            var guestIds = db.Guests.Where(g => EF.Functions.Like(g.SearchText, like)
                                                || (digits.Length >= 4 && g.PhoneDigits.Contains(digits))).Select(g => g.Id);
            rows = rows.Where(r => r.Reference == reference || guestIds.Contains(r.PrimaryGuestId));
        }

        var total = await rows.CountAsync(ct);
        var items = await rows.OrderBy(r => r.CheckIn).ThenBy(r => r.Reference)
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);

        var ids = items.Select(r => r.Id).ToList();
        var guestIdsOnPage = items.Select(r => r.PrimaryGuestId).Distinct().ToList();
        var guests = await db.Guests.AsNoTracking().Where(g => guestIdsOnPage.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, ct);
        var sources = await db.ReservationSources.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var units = await db.ReservationUnits.AsNoTracking()
            .Where(u => ids.Contains(u.ReservationId) && u.SupersededAt == null).ToListAsync(ct);
        var roomNames = await RoomNamesAsync(units.Select(u => u.UnitId), ct);

        return new PagedResult<ReservationListItemDto>(items.Select(r =>
        {
            var g = guests[r.PrimaryGuestId];
            return new ReservationListItemDto(r.Id, r.Reference, r.Status, r.PrimaryGuestId, g.FullName, g.Phone,
                r.CheckIn, r.CheckOut, r.Nights, r.Adults, r.Children,
                RoomsText(units.Where(u => u.ReservationId == r.Id), roomNames),
                sources.GetValueOrDefault(r.SourceId, ""), r.TotalAmount, r.Currency, r.CreatedAt);
        }).ToList(), page, size, total);
    }

    public async Task<ReservationDto> GetAsync(Guid propertyId, Guid id, CancellationToken ct) =>
        await ToDtoAsync(await LoadAsync(propertyId, id, ct), ct);

    // ------------------------------------------------------------------ create

    /// <returns>The reservation, and whether it is the stored response of an earlier request with the same key.</returns>
    public async Task<(ReservationDto Reservation, bool Replayed)> CreateAsync(
        Guid propertyId, string? idempotencyKey, CreateReservationRequest r, CancellationToken ct)
    {
        var key = Idempotency.RequireKey(idempotencyKey);
        var property = await LoadPropertyAsync(propertyId, ct);
        var orgId = caller.RequireOrganizationId();
        var scope = $"POST /api/v1/properties/{propertyId}/reservations";

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // 1. Claim the key. A concurrent request with the same key waits here until the first one commits or rolls back.
        if (await Idempotency.ClaimAsync(db, orgId, caller.UserId, scope, key, Idempotency.Hash(r), "booking", ct) is { } stored)
            return (JsonSerializer.Deserialize<ReservationDto>(stored, Idempotency.Json)!, true);

        // 2–4. Check, lock, allocate, price, insert.
        var reservation = await BookAsync(property, r, new HashSet<Guid>(), ct);

        var dto = await ToDtoAsync(reservation, ct);
        await Idempotency.CompleteAsync(db, orgId, scope, key, 201, dto, reservation.Id, ct);
        await tx.CommitAsync(ct);
        return (dto, false);
    }

    /// <summary>Steps 2–4 of the booking algorithm inside the caller's transaction: check what the request refers to,
    /// lock the rooms of its types, allocate (taking rooms in <paramref name="preferred"/> first), price and save.
    /// Shared by a booking and a walk-in (Sprint 7).</summary>
    internal async Task<Reservation> BookAsync(Property property, CreateReservationRequest r, IReadOnlySet<Guid> preferred, CancellationToken ct)
    {
        var propertyId = property.Id;
        var orgId = caller.RequireOrganizationId();
        var today = property.LocalDate(clock.GetUtcNow());
        if (r.CheckIn < today)
            throw Field(nameof(r.CheckIn), $"Check-in can't be before today ({QuoteEngine.Day(today)} at the property).");
        var status = string.IsNullOrWhiteSpace(r.Status) ? ReservationStatuses.Confirmed : r.Status.Trim().ToUpperInvariant();
        var source = await db.ReservationSources.SingleOrDefaultAsync(
                         s => s.Id == r.SourceId && s.IsActive && (s.OrganizationId == null || s.OrganizationId == orgId), ct)
                     ?? throw Field(nameof(r.SourceId), "That booking source doesn't exist. Reload the page and choose again.");
        var guests = await LoadGuestsAsync(r.GuestIds, ct);
        var plan = await ResolvePlanAsync(propertyId, r.RatePlanId, ct);
        var lines = await ResolveLinesAsync(propertyId, r.Rooms, ct);

        var priced = await AllocateAndPriceAsync(propertyId, plan, lines, r.CheckIn, r.CheckOut, null, preferred, ct);

        var reservation = new Reservation
        {
            OrganizationId = orgId,
            PropertyId = propertyId,
            Reference = await NextReferenceAsync(propertyId, ct),
            Status = status,
            SourceId = source.Id,
            PrimaryGuestId = guests[0].Id,
            RatePlanId = plan.Id,
            CheckIn = r.CheckIn,
            CheckOut = r.CheckOut,
            Adults = (short)lines.Sum(l => l.Adults),
            Children = (short)lines.Sum(l => l.Children),
            Currency = property.Currency,
            SpecialRequests = Clean(r.SpecialRequests),
            StatusChangedAt = clock.GetUtcNow(),
        };
        db.Reservations.Add(reservation);
        AddUnits(reservation, priced, plan.Id);
        foreach (var g in guests)
            db.ReservationGuests.Add(new ReservationGuest
            {
                OrganizationId = orgId,
                PropertyId = propertyId,
                ReservationId = reservation.Id,
                GuestId = g.Id,
            });
        AddHistory(reservation, null, status, null);
        if (Clean(r.Note) is { } note) AddNote(reservation, note);
        await db.SaveChangesAsync(ct);
        // A confirmed booking gets its bill (Sprint 8); a pending one when it is confirmed.
        if (status == ReservationStatuses.Confirmed) await folios.EnsureMainFolioAsync(reservation, ct);
        return reservation;
    }

    // ------------------------------------------------------------------ change dates / rooms / guests

    public async Task<ReservationDto> UpdateAsync(Guid propertyId, Guid id, UpdateReservationRequest r, CancellationToken ct)
    {
        var property = await LoadPropertyAsync(propertyId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var reservation = await LockAsync(propertyId, id, ct);
        if (!ReservationStatuses.Changeable.Contains(reservation.Status))
            throw new BusinessRuleAppException("RESERVATION_NOT_CHANGEABLE",
                $"This reservation is {ReservationStatuses.Words(reservation.Status)}, so its dates and rooms can't be changed.");
        ConcurrencyGuard.ExpectVersion(db, reservation, r.Version);
        var today = property.LocalDate(clock.GetUtcNow());
        if (r.CheckIn != reservation.CheckIn && r.CheckIn < today)
            throw Field(nameof(r.CheckIn), $"Check-in can't be moved before today ({QuoteEngine.Day(today)} at the property).");

        var plan = await db.RatePlans.SingleAsync(p => p.Id == reservation.RatePlanId, ct);
        if (!plan.IsActive)
            throw new BusinessRuleAppException("RATE_PLAN_INACTIVE",
                $"This booking's rate plan ({plan.Name}) is now inactive, so the stay can't be re-priced. Re-activate it in Settings → Rates.");
        var lines = await ResolveLinesAsync(propertyId, r.Rooms, ct);
        var current = await db.ReservationUnits.Where(u => u.ReservationId == id && u.SupersededAt == null).ToListAsync(ct);
        var preferred = current.Select(u => u.BedId ?? u.UnitId).ToHashSet();

        var priced = await AllocateAndPriceAsync(propertyId, plan, lines, r.CheckIn, r.CheckOut, id, preferred, ct);
        await ReplaceUnitsAsync(reservation, current, priced, plan.Id, ct);

        reservation.CheckIn = r.CheckIn;
        reservation.CheckOut = r.CheckOut;
        reservation.Adults = (short)lines.Sum(l => l.Adults);
        reservation.Children = (short)lines.Sum(l => l.Children);
        reservation.SpecialRequests = Clean(r.SpecialRequests);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ToDtoAsync(reservation, ct);
    }

    // ------------------------------------------------------------------ status changes

    public Task<ReservationDto> ConfirmAsync(Guid propertyId, Guid id, ReservationStatusRequest r, CancellationToken ct) =>
        MoveAsync(propertyId, id, ReservationStatuses.Confirmed, Clean(r.Reason), ct);

    public Task<ReservationDto> CancelAsync(Guid propertyId, Guid id, ReservationStatusRequest r, CancellationToken ct) =>
        MoveAsync(propertyId, id, ReservationStatuses.Cancelled,
            Clean(r.Reason) ?? throw Field(nameof(r.Reason), "Say why the booking is cancelled (at least 3 characters), e.g. Guest changed plans."), ct);

    public Task<ReservationDto> NoShowAsync(Guid propertyId, Guid id, ReservationStatusRequest r, CancellationToken ct) =>
        MoveAsync(propertyId, id, ReservationStatuses.NoShow,
            Clean(r.Reason) ?? throw Field(nameof(r.Reason), "Say what happened (at least 3 characters), e.g. Guest didn't arrive and isn't answering calls."), ct);

    private async Task<ReservationDto> MoveAsync(Guid propertyId, Guid id, string to, string? reason, CancellationToken ct)
    {
        if (reason is { Length: < 3 })
            throw Field(nameof(ReservationStatusRequest.Reason), "The reason must be at least 3 characters.");
        var property = await LoadPropertyAsync(propertyId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var reservation = await LockAsync(propertyId, id, ct);
        var from = reservation.Status;
        if (!ReservationStatuses.CanMove(from, to))
        {
            var next = ReservationStatuses.Transitions[from];
            throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION",
                $"This reservation is {ReservationStatuses.Words(from)}, so it can't be marked {ReservationStatuses.Words(to)}. " +
                (next.Length == 0 ? "No further status change is possible."
                    : $"From {ReservationStatuses.Words(from)} it can only become {string.Join(" or ", next.Select(ReservationStatuses.Words))}."),
                new { from, to, allowed = next });
        }
        if (to == ReservationStatuses.NoShow)
        {
            var today = property.LocalDate(clock.GetUtcNow());
            if (today < reservation.CheckIn)
                throw new BusinessRuleAppException("NO_SHOW_TOO_EARLY",
                    $"A booking can be marked no-show from its arrival day ({QuoteEngine.Day(reservation.CheckIn)}); " +
                    $"it is {QuoteEngine.Day(today)} at the property.");
        }

        var current = await db.ReservationUnits.Where(u => u.ReservationId == id && u.SupersededAt == null).ToListAsync(ct);
        var holds = ReservationStatuses.HoldsInventory(to);
        if (holds && !ReservationStatuses.HoldsInventory(from))
        {
            // An enquiry being booked: its rooms must still be free. Keep them if they are, otherwise pick others of
            // the same types (re-priced from today's grid).
            var plan = await db.RatePlans.SingleAsync(p => p.Id == reservation.RatePlanId, ct);
            var lines = await LinesOfAsync(current, ct);
            var preferred = current.Select(u => u.BedId ?? u.UnitId).ToHashSet();
            var priced = await AllocateAndPriceAsync(propertyId, plan, lines, reservation.CheckIn, reservation.CheckOut, id, preferred, ct,
                requirePrices: false);
            var same = priced.Select(p => (p.Allocation.UnitId, p.Allocation.BedId)).ToHashSet()
                .SetEquals(current.Select(u => (u.UnitId, u.BedId)));
            if (same)
                foreach (var u in current) u.HoldsInventory = true;
            else
            {
                if (priced.Any(p => p.Quote is null))
                    throw new BusinessRuleAppException("RATE_NOT_SET", priced.First(p => p.Quote is null).PriceProblem!);
                reservation.Status = to;   // so the new rows are created holding
                await ReplaceUnitsAsync(reservation, current, priced, plan.Id, ct);
            }
        }
        else if (!holds)
            foreach (var u in current) u.HoldsInventory = false;

        audit.Reason = reason;
        reservation.Status = to;
        reservation.StatusChangedAt = clock.GetUtcNow();
        AddHistory(reservation, from, to, reason);
        await db.SaveChangesAsync(ct);
        if (to == ReservationStatuses.Confirmed) await folios.EnsureMainFolioAsync(reservation, ct);
        await tx.CommitAsync(ct);
        return await ToDtoAsync(reservation, ct);
    }

    // ------------------------------------------------------------------ move to another room (Sprint 6)

    /// <summary>
    /// Moves one room (or dorm bed) of a booking to another for the same nights; the price stays as booked.
    /// A checked-in guest is moved from the front desk instead (<see cref="MoveRoomAsync"/> from today, Sprint 7).
    /// </summary>
    public async Task<ReservationDto> ReassignAsync(Guid propertyId, Guid id, ReassignRoomRequest r, CancellationToken ct)
    {
        var reason = Clean(r.Reason)!;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var reservation = await LockAsync(propertyId, id, ct);
        if (reservation.Status == ReservationStatuses.CheckedIn)
            throw new BusinessRuleAppException("RESERVATION_NOT_MOVABLE",
                $"{reservation.Reference} is checked in. Move an in-house guest from the front desk (In-house → Move room): " +
                "the move starts today and the nights already slept stay with the first room.");
        if (!ReservationStatuses.Changeable.Contains(reservation.Status))
            throw new BusinessRuleAppException("RESERVATION_NOT_MOVABLE",
                $"{reservation.Reference} is {ReservationStatuses.Words(reservation.Status)}, so its room can't be changed.");
        ConcurrencyGuard.ExpectVersion(db, reservation, r.Version);

        await MoveRoomAsync(reservation, r.ReservationUnitId, r.UnitId, r.BedId, reason, null, null, ct);
        await tx.CommitAsync(ct);
        return await ToDtoAsync(reservation, ct);
    }

    /// <summary>What a room move did: the row that was replaced, where the guest went, and from which night.</summary>
    internal sealed record RoomMove(ReservationUnit Old, AccommodationUnit Target, Bed? Bed, string SourceName, string TargetName, DateOnly From);

    /// <summary>
    /// Moves one current room (or dorm bed) row of a locked reservation to another room for the nights from
    /// <paramref name="from"/> (null = all its nights) to the row's end; the price stays as booked, night by night.
    /// Same locking as booking: the caller holds the reservation row; this locks the target room row (the lock a booking,
    /// a date change and a block on that room all take), then the availability check sees every committed booking and block.
    /// The old row is superseded (kept as history). Moving from a later night keeps the nights before it on the old
    /// room as a new row, so the stay reads right on the calendar. <paramref name="check"/> runs once the target is
    /// locked and known to be free (the front desk checks housekeeping there).
    /// </summary>
    internal async Task<RoomMove> MoveRoomAsync(Reservation reservation, Guid rowId, Guid unitId, Guid? bedId, string reason, DateOnly? from,
        Func<AccommodationUnit, Bed?, Task>? check, CancellationToken ct)
    {
        var propertyId = reservation.PropertyId;
        var orgId = caller.RequireOrganizationId();
        var row = await db.ReservationUnits.SingleOrDefaultAsync(u => u.Id == rowId && u.ReservationId == reservation.Id, ct);
        if (row is null || !row.IsCurrent)
            throw Field("ReservationUnitId", $"That room is no longer part of {reservation.Reference}. Reload and try again.");
        var moveFrom = from is { } f && f > row.StartDate ? f : row.StartDate;
        if (moveFrom >= row.EndDate)
            throw Field("ReservationUnitId",
                $"That room of {reservation.Reference} was booked {BlockService.NightsText(row.StartDate, row.EndDate)}, so there are no nights left to move.");

        // Lock the target room before reading anything about it (fixed order: reservation, then room).
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM accommodation_units WHERE id = {unitId} AND property_id = {propertyId} AND organization_id = {orgId} FOR UPDATE", ct);
        var target = await db.AccommodationUnits.Include(u => u.Type).Include(u => u.Beds)
                         .SingleOrDefaultAsync(u => u.Id == unitId && u.PropertyId == propertyId, ct)
                     ?? throw Field("UnitId", "That room doesn't exist at this property. Reload and choose again.");
        var source = await db.AccommodationUnits.AsNoTracking().Include(u => u.Beds).SingleAsync(u => u.Id == row.UnitId, ct);
        var sourceName = row.BedId is { } sb ? $"{source.Name} bed {source.Beds.First(b => b.Id == sb).Code}" : source.Name;

        var isBed = row.BedId is not null;
        if (!target.IsActive || !target.Type.IsActive)
            throw Field("UnitId", $"{target.Name} is inactive, so guests can't be moved into it.");
        if (isBed && target.Type.InventoryMode != InventoryModes.Bed)
            throw Field("UnitId", $"This guest has a dormitory bed; {target.Name} is a private room. Move them to a bed in a dormitory, " +
                                  "or change the booking's room type on the booking page.");
        if (!isBed && target.Type.InventoryMode == InventoryModes.Bed)
            throw Field("UnitId", $"{target.Name} is a dormitory, sold by the bed. Move this room booking to another room, " +
                                  "or change the booking's room type on the booking page.");
        Bed? bed = null;
        if (isBed)
        {
            if (bedId is null)
                throw Field("BedId", $"Choose the bed in {target.Name}.");
            bed = target.Beds.FirstOrDefault(b => b.Id == bedId)
                  ?? throw Field("BedId", $"That bed isn't in {target.Name}. Reload and choose again.");
            if (!bed.IsActive)
                throw Field("BedId", $"Bed {bed.Code} in {target.Name} is inactive, so guests can't be moved into it.");
        }
        else if (bedId is not null)
            throw Field("BedId", $"{target.Name} is a private room; it has no beds to choose.");
        if (row.UnitId == target.Id && row.BedId == bed?.Id)
            throw Field("UnitId", $"The guest is already in {sourceName}. Choose a different {(isBed ? "bed" : "room")}.");
        if (!isBed && row.Adults + row.Children > target.Type.MaxOccupancy)
            throw Field("UnitId",
                $"{target.Name} ({target.Type.Name}) sleeps at most {target.Type.MaxOccupancy}; this room has {row.Adults + row.Children} guests. " +
                "Choose a bigger room.");

        // Free for every night? The same rules the booking engine uses (held bookings and active blocks).
        var inventory = await InventoryLoader.UnitsAsync(db, propertyId, [target.AccommodationTypeId], ct);
        var unit = inventory.Single(u => u.UnitId == target.Id);
        var taken = await InventoryLoader.TakenAsync(db, [target.Id], moveFrom, row.EndDate, null, ct);
        var free = AvailabilityEngine.Free([unit], taken, moveFrom, row.EndDate);
        var isFree = isBed ? free.FreeBeds.Any(b => b.Bed.BedId == bed!.Id) : free.FreeRooms.Any(u => u.UnitId == target.Id);
        if (!isFree)
        {
            var clash = taken.FirstOrDefault(o => o.Overlaps(moveFrom, row.EndDate) && (o.BedId is null || o.BedId == bed?.Id));
            var what = isBed ? $"Bed {bed!.Code} in {target.Name}" : target.Name;
            throw new ConflictAppException("ROOM_UNAVAILABLE",
                $"{what} isn't free {BlockService.NightsText(moveFrom, row.EndDate)}: {clash?.Label ?? "it is taken"}. Choose another {(isBed ? "bed" : "room")}.",
                new { unitId = target.Id, bedId = bed?.Id });
        }
        if (check is not null) await check(target, bed);

        // Supersede the old row (it stops holding first), then insert the new rows with the same nights and prices.
        var now = clock.GetUtcNow();
        audit.Reason = reason;
        var holds = row.HoldsInventory;
        row.HoldsInventory = false;
        row.SupersededAt = now;
        await db.SaveChangesAsync(ct);

        var nights = await db.ReservationRateBreakdowns.AsNoTracking().Where(n => n.ReservationUnitId == row.Id).ToListAsync(ct);
        if (moveFrom > row.StartDate)
            AddRow(row, row.UnitId, row.BedId, row.AccommodationTypeId, row.StartDate, moveFrom, holds, nights);
        AddRow(row, target.Id, bed?.Id, target.AccommodationTypeId, moveFrom, row.EndDate, holds, nights);

        var targetName = bed is null ? target.Name : $"{target.Name} bed {bed.Code}";
        AddNote(reservation, moveFrom > row.StartDate
            ? $"Moved from {sourceName} to {targetName} from {QuoteEngine.Day(moveFrom)} ({BlockService.NightsText(moveFrom, row.EndDate)}). Reason: {reason}"
            : $"Moved from {sourceName} to {targetName} ({BlockService.NightsText(row.StartDate, row.EndDate)}). Reason: {reason}");
        reservation.UpdatedAt = now;   // a new version, so a screen showing the old rooms can't save over the move
        await db.SaveChangesAsync(ct);
        return new RoomMove(row, target, bed, sourceName, targetName, moveFrom);
    }

    /// <summary>A new current row copied from <paramref name="old"/> for [start, end) in a room (or bed), with the old
    /// row's prices for those nights.</summary>
    internal ReservationUnit AddRow(ReservationUnit old, Guid unitId, Guid? bedId, Guid typeId, DateOnly start, DateOnly end, bool holds,
        IEnumerable<ReservationRateBreakdown> nights)
    {
        var copy = new ReservationUnit
        {
            OrganizationId = old.OrganizationId,
            PropertyId = old.PropertyId,
            ReservationId = old.ReservationId,
            AccommodationTypeId = typeId,
            UnitId = unitId,
            BedId = bedId,
            StartDate = start,
            EndDate = end,
            Adults = old.Adults,
            Children = old.Children,
            HoldsInventory = holds,
        };
        db.ReservationUnits.Add(copy);
        foreach (var n in nights.Where(n => n.StayDate >= start && n.StayDate < end))
            db.ReservationRateBreakdowns.Add(new ReservationRateBreakdown
            {
                OrganizationId = n.OrganizationId,
                PropertyId = n.PropertyId,
                ReservationId = n.ReservationId,
                ReservationUnitId = copy.Id,
                RatePlanId = n.RatePlanId,
                StayDate = n.StayDate,
                Units = n.Units,
                UnitPrice = n.UnitPrice,
                BaseAmount = n.BaseAmount,
                ExtraAdults = n.ExtraAdults,
                ExtraAdultRate = n.ExtraAdultRate,
                ExtraAdultAmount = n.ExtraAdultAmount,
                ExtraChildren = n.ExtraChildren,
                ExtraChildRate = n.ExtraChildRate,
                ExtraChildAmount = n.ExtraChildAmount,
                Total = n.Total,
            });
        return copy;
    }

    // ------------------------------------------------------------------ notes

    public async Task<ReservationDto> AddNoteAsync(Guid propertyId, Guid id, AddReservationNoteRequest r, CancellationToken ct)
    {
        var reservation = await LoadAsync(propertyId, id, ct);
        AddNote(reservation, r.Note.Trim());
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(reservation, ct);
    }

    // ------------------------------------------------------------------ allocation and pricing

    /// <summary>A resolved room line plus its type.</summary>
    private sealed record Line(StayLine Stay, AccommodationType Type)
    {
        public int Adults => Stay.Adults;
        public int Children => Stay.Children;
    }

    private sealed record Priced(Allocation Allocation, QuoteResult? Quote, string? PriceProblem);

    private async Task<List<Line>> ResolveLinesAsync(Guid propertyId, IReadOnlyList<ReservationRoomRequest> rooms, CancellationToken ct)
    {
        var typeIds = rooms.Select(x => x.AccommodationTypeId).Distinct().ToList();
        var types = await db.AccommodationTypes.Where(t => t.PropertyId == propertyId && typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var unitIds = rooms.Where(x => x.UnitId != null).Select(x => x.UnitId!.Value).ToList();
        var units = await db.AccommodationUnits.Where(u => u.PropertyId == propertyId && unitIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);

        var lines = new List<Line>();
        for (var i = 0; i < rooms.Count; i++)
        {
            var x = rooms[i];
            if (!types.TryGetValue(x.AccommodationTypeId, out var type))
                throw Field($"Rooms[{i}].AccommodationTypeId", "That room type doesn't exist at this property. Reload the page and choose again.");
            if (!type.IsActive)
                throw Field($"Rooms[{i}].AccommodationTypeId", $"{type.Name} is inactive, so it can't be booked.");
            if (x.UnitId is { } uid)
            {
                if (!units.TryGetValue(uid, out var unit))
                    throw Field($"Rooms[{i}].UnitId", "That room doesn't exist at this property. Reload the page and choose again.");
                if (unit.AccommodationTypeId != type.Id)
                    throw Field($"Rooms[{i}].UnitId", $"{unit.Name} isn't a {type.Name}. Choose a {type.Name}, or change the room type.");
            }
            if (!type.IsBedBased && x.Adults + x.Children > type.MaxOccupancy)
                throw Field($"Rooms[{i}].Adults",
                    $"{type.Name} sleeps at most {type.MaxOccupancy} guest{(type.MaxOccupancy == 1 ? "" : "s")}; this room has {x.Adults + x.Children}. " +
                    "Add another room for the others.");
            lines.Add(new Line(new StayLine(type.Id, type.Name, type.InventoryMode, x.UnitId, x.Adults, x.Children), type));
        }
        return lines;
    }

    /// <summary>The current rows of a reservation as request lines: one per room; dorm beds grouped per dorm.</summary>
    private async Task<List<Line>> LinesOfAsync(List<ReservationUnit> current, CancellationToken ct)
    {
        var typeIds = current.Select(u => u.AccommodationTypeId).Distinct().ToList();
        var types = await db.AccommodationTypes.Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var lines = current.Where(u => u.BedId == null)
            .Select(u => new Line(new StayLine(u.AccommodationTypeId, types[u.AccommodationTypeId].Name, InventoryModes.Unit, null, u.Adults, u.Children),
                types[u.AccommodationTypeId]))
            .ToList();
        lines.AddRange(current.Where(u => u.BedId != null).GroupBy(u => (u.AccommodationTypeId, u.UnitId))
            .Select(g => new Line(new StayLine(g.Key.AccommodationTypeId, types[g.Key.AccommodationTypeId].Name, InventoryModes.Bed, g.Key.UnitId,
                g.Sum(u => u.Adults), g.Sum(u => u.Children)), types[g.Key.AccommodationTypeId])));
        return lines;
    }

    private async Task<List<Priced>> AllocateAndPriceAsync(
        Guid propertyId, RatePlan plan, List<Line> lines, DateOnly from, DateOnly to, Guid? excludeReservation,
        IReadOnlySet<Guid> preferred, CancellationToken ct, bool requirePrices = true)
    {
        var orgId = caller.RequireOrganizationId();
        var typeIds = lines.Select(l => l.Stay.TypeId).Distinct().OrderBy(x => x).ToArray();

        // Lock every room of the requested types, always in id order (so two bookings can't deadlock). A second booking
        // for the same types waits here until the first commits, then sees its rows.
        await db.Database.ExecuteSqlAsync(
            $"""
             SELECT 1 FROM accommodation_units
              WHERE organization_id = {orgId} AND property_id = {propertyId} AND accommodation_type_id = ANY({typeIds})
              ORDER BY id FOR UPDATE
             """, ct);

        var inventory = await InventoryLoader.UnitsAsync(db, propertyId, typeIds, ct);
        var taken = await InventoryLoader.TakenAsync(db, inventory.Select(u => u.UnitId).ToList(), from, to, excludeReservation, ct);

        var free = AvailabilityEngine.Free(inventory, taken, from, to);
        var stayText = BlockService.NightsText(from, to);
        var result = AvailabilityEngine.Allocate(lines.Select(l => l.Stay).ToList(), free, inventory, taken, from, to, stayText, preferred);
        if (!result.Ok)
            throw new ConflictAppException("ROOM_UNAVAILABLE", result.ProblemMessage!, new { line = result.ProblemLine });

        var grids = await db.RatePlanPrices.AsNoTracking()
            .Where(x => x.RatePlanId == plan.Id && typeIds.Contains(x.AccommodationTypeId) && x.StayDate >= from && x.StayDate < to)
            .ToListAsync(ct);
        var priced = new List<Priced>();
        foreach (var a in result.Allocations)
        {
            var type = lines[a.Line].Type;
            var grid = grids.Where(g => g.AccommodationTypeId == type.Id).ToDictionary(g => g.StayDate, g => g.ToNightRate());
            var quote = QuoteEngine.Price(new QuoteInput(plan.Name, type.Name, type.InventoryMode, type.BaseOccupancy, type.MaxOccupancy,
                from, to, a.Adults, a.Children), grid);
            if (quote.Problem is { } p)
            {
                if (requirePrices)
                    throw new BusinessRuleAppException(p.Code, p.Message, p.Dates.Count > 0 ? new { dates = p.Dates } : null);
                priced.Add(new Priced(a, null, p.Message));
            }
            else priced.Add(new Priced(a, quote, null));
        }
        return priced;
    }

    private void AddUnits(Reservation reservation, List<Priced> priced, Guid planId)
    {
        var holds = ReservationStatuses.HoldsInventory(reservation.Status);
        decimal total = 0;
        foreach (var p in priced)
        {
            var unit = new ReservationUnit
            {
                OrganizationId = reservation.OrganizationId,
                PropertyId = reservation.PropertyId,
                ReservationId = reservation.Id,
                AccommodationTypeId = p.Allocation.TypeId,
                UnitId = p.Allocation.UnitId,
                BedId = p.Allocation.BedId,
                StartDate = p.Quote!.Nights[0].Date,
                EndDate = p.Quote.Nights[^1].Date.AddDays(1),
                Adults = (short)p.Allocation.Adults,
                Children = (short)p.Allocation.Children,
                HoldsInventory = holds,
            };
            db.ReservationUnits.Add(unit);
            foreach (var n in p.Quote.Nights)
            {
                db.ReservationRateBreakdowns.Add(new ReservationRateBreakdown
                {
                    OrganizationId = reservation.OrganizationId,
                    PropertyId = reservation.PropertyId,
                    ReservationId = reservation.Id,
                    ReservationUnitId = unit.Id,
                    RatePlanId = planId,
                    StayDate = n.Date,
                    Units = (short)n.Units,
                    UnitPrice = n.UnitPrice,
                    BaseAmount = n.BaseAmount,
                    ExtraAdults = (short)n.ExtraAdults,
                    ExtraAdultRate = n.ExtraAdultRate,
                    ExtraAdultAmount = n.ExtraAdultAmount,
                    ExtraChildren = (short)n.ExtraChildren,
                    ExtraChildRate = n.ExtraChildRate,
                    ExtraChildAmount = n.ExtraChildAmount,
                    Total = n.Total,
                });
            }
            total += p.Quote.Total;
        }
        reservation.TotalAmount = total;
    }

    /// <summary>Supersedes the current rows (kept as history, no longer holding) and adds the new allocation. Saved in
    /// two steps so the old rows stop holding before the new ones are inserted (they may use the same rooms).</summary>
    private async Task ReplaceUnitsAsync(Reservation reservation, List<ReservationUnit> current, List<Priced> priced, Guid planId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        foreach (var u in current)
        {
            u.HoldsInventory = false;
            u.SupersededAt = now;
        }
        await db.SaveChangesAsync(ct);
        AddUnits(reservation, priced, planId);
    }

    // ------------------------------------------------------------------ helpers

    internal void AddHistory(Reservation r, string? from, string to, string? reason) =>
        db.ReservationStatusHistory.Add(new ReservationStatusChange
        {
            OrganizationId = r.OrganizationId,
            PropertyId = r.PropertyId,
            ReservationId = r.Id,
            FromStatus = from,
            ToStatus = to,
            Reason = reason,
            ChangedAt = clock.GetUtcNow(),
            ChangedBy = caller.UserId,
        });

    internal void AddNote(Reservation r, string note) =>
        db.ReservationNotes.Add(new ReservationNote
        {
            OrganizationId = r.OrganizationId,
            PropertyId = r.PropertyId,
            ReservationId = r.Id,
            Note = note,
            CreatedAt = clock.GetUtcNow(),
            CreatedBy = caller.UserId,
        });

    private async Task<string> NextReferenceAsync(Guid propertyId, CancellationToken ct)
    {
        var n = (await db.Database.SqlQuery<long>(
            $"""
             INSERT INTO property_counters (property_id, name, last_value) VALUES ({propertyId}, 'reservation', 1)
             ON CONFLICT (property_id, name) DO UPDATE SET last_value = property_counters.last_value + 1
             RETURNING last_value AS "Value"
             """).ToListAsync(ct)).Single();
        return $"R{n:D5}";
    }

    private async Task<List<Guest>> LoadGuestsAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var found = await db.Guests.Where(g => ids.Contains(g.Id)).ToDictionaryAsync(g => g.Id, ct);
        var missing = ids.Count(id => !found.ContainsKey(id));
        if (missing > 0)
            throw Field("GuestIds", missing == ids.Count && ids.Count == 1
                ? "That guest profile doesn't exist. Search for the guest again."
                : $"{missing} of the chosen guests don't exist. Search for them again.");
        foreach (var g in found.Values)
        {
            if (g.Status == GuestStatuses.Merged)
                throw Field("GuestIds", $"{g.FullName}'s profile was merged into another one. Search again and choose the remaining profile.");
            if (g.Status == GuestStatuses.Anonymised)
                throw Field("GuestIds", "One of the chosen profiles was anonymised, so it can't be booked. Add the guest again.");
        }
        return ids.Select(id => found[id]).ToList();
    }

    private async Task<RatePlan> ResolvePlanAsync(Guid propertyId, Guid? planId, CancellationToken ct)
    {
        if (planId is { } id)
        {
            var plan = await db.RatePlans.SingleOrDefaultAsync(p => p.Id == id && p.PropertyId == propertyId, ct)
                       ?? throw Field("RatePlanId", "That rate plan doesn't exist at this property. Reload the page and choose again.");
            if (!plan.IsActive) throw Field("RatePlanId", $"{plan.Name} is inactive, so it can't be used for new bookings.");
            return plan;
        }
        return await db.RatePlans.SingleOrDefaultAsync(p => p.PropertyId == propertyId && p.IsDefault, ct)
               ?? throw new BusinessRuleAppException("NO_DEFAULT_RATE_PLAN",
                   "This property has no default rate plan. Mark a plan as the default in Settings → Rates, or choose a plan.");
    }

    private async Task<Property> LoadPropertyAsync(Guid propertyId, CancellationToken ct) =>
        await db.Properties.SingleOrDefaultAsync(p => p.Id == propertyId, ct) ?? throw new NotFoundAppException("Property", propertyId);

    private async Task<Reservation> LoadAsync(Guid propertyId, Guid id, CancellationToken ct) =>
        await db.Reservations.SingleOrDefaultAsync(r => r.Id == id && r.PropertyId == propertyId, ct)
        ?? throw new NotFoundAppException("Reservation", id);

    /// <summary>Locks the reservation row for this transaction (status changes and edits of one booking serialise), then loads it.</summary>
    internal async Task<Reservation> LockAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        var orgId = caller.RequireOrganizationId();
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM reservations WHERE id = {id} AND property_id = {propertyId} AND organization_id = {orgId} FOR UPDATE", ct);
        return await LoadAsync(propertyId, id, ct);
    }

    internal static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    internal static ValidationException Field(string field, string message) => new([new ValidationFailure(field, message)]);

    internal async Task<Dictionary<Guid, (string Name, Dictionary<Guid, string> Beds)>> RoomNamesAsync(IEnumerable<Guid> unitIds, CancellationToken ct)
    {
        var ids = unitIds.Distinct().ToList();
        return await db.AccommodationUnits.AsNoTracking().Include(u => u.Beds).Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => (u.Name, u.Beds.ToDictionary(b => b.Id, b => b.Code)), ct);
    }

    /// <summary>"Room 103", "Room 103, Room 104", "Dormitory · 2 beds".</summary>
    internal static string RoomsText(IEnumerable<ReservationUnit> units, Dictionary<Guid, (string Name, Dictionary<Guid, string> Beds)> names) =>
        string.Join(", ", units.GroupBy(u => u.UnitId).Select(g =>
        {
            var name = names.TryGetValue(g.Key, out var n) ? n.Name : "?";
            var beds = g.Count(u => u.BedId != null);
            return beds == 0 ? name : $"{name} · {beds} bed{(beds == 1 ? "" : "s")}";
        }));

    internal async Task<ReservationDto> ToDtoAsync(Reservation r, CancellationToken ct)
    {
        var units = await db.ReservationUnits.AsNoTracking().Where(u => u.ReservationId == r.Id).ToListAsync(ct);
        var rooms = await db.AccommodationUnits.AsNoTracking().Include(u => u.Type).Include(u => u.Beds)
            .Where(u => units.Select(x => x.UnitId).Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
        var typeIds = units.Select(u => u.AccommodationTypeId).Distinct().ToList();
        var types = await db.AccommodationTypes.AsNoTracking().Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var currentIds = units.Where(u => u.IsCurrent).Select(u => u.Id).ToList();
        var nights = await db.ReservationRateBreakdowns.AsNoTracking().Where(b => b.ReservationId == r.Id).ToListAsync(ct);
        var guestRows = await db.ReservationGuests.AsNoTracking().Where(g => g.ReservationId == r.Id).ToListAsync(ct);
        var guestIds = guestRows.Select(g => g.GuestId).Append(r.PrimaryGuestId).Distinct().ToList();
        var guests = await db.Guests.AsNoTracking().Where(g => guestIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, ct);
        var notes = await db.ReservationNotes.AsNoTracking().Where(n => n.ReservationId == r.Id).OrderBy(n => n.CreatedAt).ToListAsync(ct);
        var history = await db.ReservationStatusHistory.AsNoTracking().Where(h => h.ReservationId == r.Id).OrderBy(h => h.ChangedAt).ToListAsync(ct);
        var source = await db.ReservationSources.AsNoTracking().Where(s => s.Id == r.SourceId).Select(s => s.Name).SingleAsync(ct);
        var plan = await db.RatePlans.AsNoTracking().SingleAsync(p => p.Id == r.RatePlanId, ct);
        var userIds = notes.Select(n => n.CreatedBy).Concat(history.Select(h => h.ChangedBy)).Append(r.CreatedBy)
            .Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
        var names = await db.Users.IgnoreQueryFilters().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        string? Name(Guid? id) => id is { } x && names.TryGetValue(x, out var n) ? n : null;

        var unitDtos = units
            .OrderByDescending(u => u.IsCurrent).ThenByDescending(u => u.CreatedAt)
            .ThenBy(u => rooms[u.UnitId].SortOrder).ThenBy(u => rooms[u.UnitId].Code, StringComparer.Ordinal)
            .ThenBy(u => u.BedId is { } b ? rooms[u.UnitId].Beds.First(x => x.Id == b).Code : "", StringComparer.Ordinal)
            .Select(u =>
            {
                var room = rooms[u.UnitId];
                var type = types[u.AccommodationTypeId];
                return new ReservationUnitDto(u.Id, type.Id, type.Name, type.InventoryMode, room.Id, room.Code, room.Name, u.BedId,
                    u.BedId is { } b ? room.Beds.First(x => x.Id == b).Code : null, u.StartDate, u.EndDate, u.Adults, u.Children,
                    u.HoldsInventory, u.IsCurrent, nights.Where(n => n.ReservationUnitId == u.Id).Sum(n => n.Total));
            }).ToList();
        var order = unitDtos.Select((u, i) => (u.Id, i)).ToDictionary(x => x.Id, x => x.i);

        return new ReservationDto(
            r.Id, r.Reference, r.Status, r.CheckIn, r.CheckOut, r.Nights, r.Adults, r.Children, r.SourceId, source,
            plan.Id, plan.Name, plan.MealPlan, r.TotalAmount, r.Currency, r.SpecialRequests,
            guestIds.OrderByDescending(id => id == r.PrimaryGuestId).ThenBy(id => guests[id].FirstName)
                .Select(id => new ReservationGuestDto(id, guests[id].FullName, guests[id].Phone, guests[id].Email, id == r.PrimaryGuestId))
                .ToList(),
            unitDtos,
            nights.Where(n => currentIds.Contains(n.ReservationUnitId))
                .OrderBy(n => order[n.ReservationUnitId]).ThenBy(n => n.StayDate)
                .Select(n => new ReservationNightDto(n.ReservationUnitId, n.StayDate, n.Units, n.UnitPrice, n.BaseAmount, n.ExtraAdults,
                    n.ExtraAdultRate, n.ExtraAdultAmount, n.ExtraChildren, n.ExtraChildRate, n.ExtraChildAmount, n.Total)).ToList(),
            notes.Select(n => new ReservationNoteDto(n.Id, n.Note, n.CreatedAt, Name(n.CreatedBy))).ToList(),
            history.Select(h => new ReservationStatusChangeDto(h.FromStatus, h.ToStatus, h.Reason, h.ChangedAt, Name(h.ChangedBy))).ToList(),
            ReservationStatuses.Transitions[r.Status],
            r.CreatedAt, Name(r.CreatedBy), r.Version, r.CheckedInAt, r.CheckedOutAt);
    }
}
