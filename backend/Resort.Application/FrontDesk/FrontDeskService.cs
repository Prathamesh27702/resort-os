using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Resort.Application.Accommodation;
using Resort.Application.Common;
using Resort.Application.Guests;
using Resort.Application.Reservations;
using Resort.Application.Validation;
using Resort.Contracts;
using Resort.Domain.Accommodation;
using Resort.Domain.FrontDesk;
using Resort.Domain.Organizations;
using Resort.Domain.Rates;
using Resort.Domain.Reservations;

namespace Resort.Application.FrontDesk;

/// <summary>
/// Reception (Sprint 7): today's arrivals, departures and in-house guests; assigning rooms; check-in; check-out; walk-ins;
/// the reception dashboard; and the cleaning state of rooms that check-in and check-out depend on.
///
/// "Today" is always the property's business date (<see cref="Property.LocalDate"/> of the injected clock), never the
/// server's. Every change locks the reservation row first (as booking does), so two people checking in the same guest
/// at once can't both succeed: the second waits, then sees the guest checked in.
/// </summary>
public sealed class FrontDeskService(
    IAppDbContext db, ICurrentUser caller, AuditContext audit, TimeProvider clock, ReservationService reservations, GuestService guests,
    CallerAccess access, IFolioBalances folios)
{
    public const string AllowCheckoutWithBalance = "frontdesk.allow_checkout_with_balance";
    public const string AllowCheckinWhenNotReady = "frontdesk.allow_checkin_when_not_ready";

    private static readonly string[] Arriving = [ReservationStatuses.Pending, ReservationStatuses.Confirmed];

    // ------------------------------------------------------------------ lists

    /// <summary>Bookings arriving on <paramref name="date"/> (default today), checked in or not. On today's list also the
    /// late arrivals: bookings due on an earlier day whose guest hasn't come yet and whose stay hasn't ended.</summary>
    public async Task<FrontDeskListDto> ArrivalsAsync(Guid propertyId, DateOnly? date, CancellationToken ct)
    {
        var property = await LoadPropertyAsync(propertyId, ct);
        var today = Today(property);
        var day = date ?? today;
        var rows = await db.Reservations.AsNoTracking()
            .Where(r => r.PropertyId == propertyId && (
                (r.CheckIn == day && (Arriving.Contains(r.Status) || r.Status == ReservationStatuses.CheckedIn
                                      || (r.Status == ReservationStatuses.CheckedOut && r.CheckedInAt != null)))
                || (day == today && r.CheckIn < today && r.CheckOut > today && Arriving.Contains(r.Status))))
            .ToListAsync(ct);
        var items = await StaysAsync(rows, today, ct);
        return new FrontDeskListDto(day, today, items
            .OrderBy(s => s.Status is ReservationStatuses.CheckedIn or ReservationStatuses.CheckedOut)
            .ThenByDescending(s => s.Late).ThenBy(s => s.GuestName, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Guests leaving on <paramref name="date"/> (default today), checked out or not. On today's list also the
    /// overdue ones: still checked in after their departure day.</summary>
    public async Task<FrontDeskListDto> DeparturesAsync(Guid propertyId, DateOnly? date, CancellationToken ct)
    {
        var property = await LoadPropertyAsync(propertyId, ct);
        var today = Today(property);
        var day = date ?? today;
        var rows = await db.Reservations.AsNoTracking()
            .Where(r => r.PropertyId == propertyId && (
                (r.CheckOut == day && (r.Status == ReservationStatuses.CheckedIn || r.Status == ReservationStatuses.CheckedOut))
                || (day == today && r.CheckOut < today && r.Status == ReservationStatuses.CheckedIn)))
            .ToListAsync(ct);
        var items = await StaysAsync(rows, today, ct);
        return new FrontDeskListDto(day, today, items
            .OrderBy(s => s.Status == ReservationStatuses.CheckedOut).ThenByDescending(s => s.Overdue)
            .ThenBy(s => s.Rooms.FirstOrDefault()?.UnitCode, StringComparer.Ordinal).ToList());
    }

    /// <summary>Everyone checked in now, by room.</summary>
    public async Task<FrontDeskListDto> InHouseAsync(Guid propertyId, CancellationToken ct)
    {
        var property = await LoadPropertyAsync(propertyId, ct);
        var today = Today(property);
        var rows = await db.Reservations.AsNoTracking()
            .Where(r => r.PropertyId == propertyId && r.Status == ReservationStatuses.CheckedIn).ToListAsync(ct);
        var items = await StaysAsync(rows, today, ct);
        return new FrontDeskListDto(today, today, items
            .OrderBy(s => s.Rooms.FirstOrDefault()?.UnitCode, StringComparer.Ordinal).ThenBy(s => s.Reference).ToList());
    }

    // ------------------------------------------------------------------ reception dashboard (brief §54)

    public async Task<FrontDeskDashboardDto> DashboardAsync(Guid propertyId, CancellationToken ct)
    {
        var property = await LoadPropertyAsync(propertyId, ct);
        var today = Today(property);
        var arrivals = (await ArrivalsAsync(propertyId, today, ct)).Items;
        var departures = (await DeparturesAsync(propertyId, today, ct)).Items;
        var inHouse = (await InHouseAsync(propertyId, ct)).Items;

        var units = await db.AccommodationUnits.AsNoTracking().Include(u => u.Type).Include(u => u.Beds)
            .Where(u => u.PropertyId == propertyId).ToListAsync(ct);
        var unitIds = units.Select(u => u.Id).ToList();
        var housekeeping = await HousekeepingAsync(unitIds, ct);
        var occupying = await OccupyingAsync(unitIds, ct);
        var blocks = await db.UnitBlocks.AsNoTracking()
            .Where(b => unitIds.Contains(b.UnitId) && b.Status == BlockStatuses.Active && b.StartDate <= today && today < b.EndDate)
            .ToListAsync(ct);
        // bookings that arrive today (or are late) and haven't checked in: their rooms are "arriving"
        var arrivingIds = arrivals.Where(a => Arriving.Contains(a.Status)).Select(a => a.ReservationId).ToList();
        var arrivingRows = await db.ReservationUnits.AsNoTracking()
            .Where(u => arrivingIds.Contains(u.ReservationId) && u.SupersededAt == null && u.StartDate <= today && today < u.EndDate)
            .ToListAsync(ct);
        var arrivingBy = arrivals.ToDictionary(a => a.ReservationId);

        var board = new List<FrontDeskBoardRoomDto>();
        foreach (var u in units.OrderBy(u => u.SortOrder).ThenBy(u => u.Code, StringComparer.Ordinal))
        {
            List<(Bed? Bed, bool Active)> places = u.Type.InventoryMode == InventoryModes.Bed
                ? u.Beds.OrderBy(b => b.SortOrder).ThenBy(b => b.Code, StringComparer.Ordinal).Select(b => ((Bed?)b, b.IsActive)).ToList()
                : [(null, true)];
            foreach (var (bed, bedActive) in places)
            {
                var hk = housekeeping.GetValueOrDefault((u.Id, bed?.Id), HousekeepingStatuses.Ready);
                var occ = occupying.FirstOrDefault(o => o.UnitId == u.Id && o.BedId == bed?.Id);
                var arriving = arrivingRows.FirstOrDefault(r => r.UnitId == u.Id && r.BedId == bed?.Id);
                var blocked = blocks.Any(b => b.UnitId == u.Id && (b.BedId == null || b.BedId == bed?.Id));
                string state; Guid? resId = null; string? reference = null, guest = null; DateOnly? until = null;
                if (!u.IsActive || !u.Type.IsActive || !bedActive) state = "INACTIVE";
                else if (occ is not null) { state = "OCCUPIED"; (resId, reference, guest, until) = (occ.ReservationId, occ.Reference, occ.GuestName, occ.CheckOut); }
                else if (blocked) state = "BLOCKED";
                else if (arriving is not null && arrivingBy.TryGetValue(arriving.ReservationId, out var a))
                { state = "ARRIVING"; (resId, reference, guest, until) = (a.ReservationId, a.Reference, a.GuestName, a.CheckOut); }
                else state = "VACANT";
                board.Add(new FrontDeskBoardRoomDto(u.Id, u.Code, u.Name, bed?.Id, bed?.Code, u.Type.Name, state, hk, resId, reference, guest, until));
            }
        }
        var active = board.Where(b => b.Occupancy != "INACTIVE").ToList();
        return new FrontDeskDashboardDto(today,
            arrivals.Count, arrivals.Count(a => a.Status is ReservationStatuses.CheckedIn or ReservationStatuses.CheckedOut),
            departures.Count, departures.Count(d => d.Status == ReservationStatuses.CheckedOut), departures.Count(d => d.Overdue),
            inHouse.Count, inHouse.Sum(s => s.Adults + s.Children),
            active.Count, active.Count(b => b.Occupancy == "OCCUPIED"),
            active.Count(b => b.Occupancy is "VACANT" or "ARRIVING" && b.Housekeeping == HousekeepingStatuses.Ready),
            active.Count(b => b.Housekeeping != HousekeepingStatuses.Ready), active.Count(b => b.Occupancy == "BLOCKED"),
            board);
    }

    // ------------------------------------------------------------------ rooms a booking's room can go to

    /// <summary>Rooms (or beds, for a dorm guest) the room <paramref name="reservationUnitId"/> of a booking could be
    /// assigned to, for the nights it would take (from today for a guest already checked in): free or not, fits or
    /// not, and the cleaning state. Ready, free rooms come first.</summary>
    public async Task<IReadOnlyList<RoomOptionDto>> RoomOptionsAsync(Guid propertyId, Guid id, Guid reservationUnitId, CancellationToken ct)
    {
        var property = await LoadPropertyAsync(propertyId, ct);
        var today = Today(property);
        var reservation = await db.Reservations.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id && r.PropertyId == propertyId, ct)
                          ?? throw new NotFoundAppException("Reservation", id);
        var row = await db.ReservationUnits.AsNoTracking().SingleOrDefaultAsync(u => u.Id == reservationUnitId && u.ReservationId == id, ct);
        if (row is null || !row.IsCurrent)
            throw ReservationService.Field("ReservationUnitId", $"That room is no longer part of {reservation.Reference}. Reload and try again.");
        var from = reservation.Status == ReservationStatuses.CheckedIn && today > row.StartDate ? today : row.StartDate;
        if (from >= row.EndDate) return [];

        var isBed = row.BedId is not null;
        var inventory = (await InventoryLoader.UnitsAsync(db, propertyId, null, ct))
            .Where(u => u.IsActive && (u.Mode == InventoryModes.Bed) == isBed).ToList();
        var unitIds = inventory.Select(u => u.UnitId).ToList();
        var taken = await InventoryLoader.TakenAsync(db, unitIds, from, row.EndDate, null, ct);
        var free = AvailabilityEngine.Free(inventory, taken, from, row.EndDate);
        var housekeeping = await HousekeepingAsync(unitIds, ct);
        var occupying = await OccupyingAsync(unitIds, ct);
        var types = await db.AccommodationTypes.AsNoTracking().Where(t => t.PropertyId == propertyId).ToDictionaryAsync(t => t.Id, ct);
        var nights = BlockService.NightsText(from, row.EndDate);

        var options = new List<(RoomOptionDto Option, int Sort, string Code)>();
        foreach (var u in inventory)
        {
            var type = types[u.TypeId];
            List<InventoryBed?> places = isBed ? u.Beds.Where(b => b.IsActive).Select(b => (InventoryBed?)b).ToList() : [null];
            foreach (var bed in places)
            {
                if (u.UnitId == row.UnitId && bed?.BedId == row.BedId) continue;
                var isFree = isBed ? free.FreeBeds.Any(b => b.Bed.BedId == bed!.BedId) : free.FreeRooms.Any(r => r.UnitId == u.UnitId);
                var fits = isBed || row.Adults + row.Children <= type.MaxOccupancy;
                var hk = housekeeping.GetValueOrDefault((u.UnitId, bed?.BedId), HousekeepingStatuses.Ready);
                var occ = occupying.FirstOrDefault(o => o.UnitId == u.UnitId && o.BedId == bed?.BedId && o.ReservationId != id);
                var clash = taken.FirstOrDefault(o => o.UnitId == u.UnitId && o.Overlaps(from, row.EndDate) && (o.BedId is null || o.BedId == bed?.BedId));
                var problem = !isFree ? $"Not free {nights}: {clash?.Label ?? "it is taken"}."
                    : !fits ? $"Sleeps at most {type.MaxOccupancy}; this room has {row.Adults + row.Children} guests."
                    : occ is not null && from <= today ? $"Still occupied by {occ.Reference} ({occ.GuestName}), due out {QuoteEngine.Day(occ.CheckOut)}."
                    : hk != HousekeepingStatuses.Ready ? $"{(bed is null ? "Room" : "Bed")} is {HousekeepingStatuses.Words(hk)}, not ready."
                    : null;
                options.Add((new RoomOptionDto(u.UnitId, u.Code, u.Name, bed?.BedId, bed?.Code, u.TypeId, type.Name, hk, isFree, fits, problem),
                    u.SortOrder, u.Code + "/" + bed?.Code));
            }
        }
        return options.OrderBy(o => o.Option.Problem is not null).ThenBy(o => !o.Option.Free || !o.Option.Fits)
            .ThenBy(o => o.Option.AccommodationTypeId != row.AccommodationTypeId).ThenBy(o => o.Sort).ThenBy(o => o.Code, StringComparer.Ordinal)
            .Select(o => o.Option).ToList();
    }

    // ------------------------------------------------------------------ assign a room

    /// <summary>
    /// Puts one room (or bed) of a booking in a chosen room. Before check-in it moves all of that room's nights (the price
    /// stays as booked). For a guest already checked in it moves them from today: the nights already slept stay with the
    /// old room, which becomes DIRTY. The target must be READY (or override with a reason) and not still occupied.
    /// </summary>
    public async Task<ReservationDto> AssignRoomAsync(Guid propertyId, Guid id, AssignRoomRequest r, CancellationToken ct)
    {
        var property = await LoadPropertyAsync(propertyId, ct);
        var today = Today(property);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var reservation = await reservations.LockAsync(propertyId, id, ct);
        var inHouse = reservation.Status == ReservationStatuses.CheckedIn;
        if (!inHouse && !ReservationStatuses.Changeable.Contains(reservation.Status))
            throw new BusinessRuleAppException("RESERVATION_NOT_MOVABLE",
                $"{reservation.Reference} is {ReservationStatuses.Words(reservation.Status)}, so its room can't be changed.");
        ConcurrencyGuard.ExpectVersion(db, reservation, r.Version);
        var reason = ReservationService.Clean(r.Reason);
        if (inHouse && reason is null)
            throw ReservationService.Field(nameof(r.Reason), "Say why the guest is moving (at least 3 characters), e.g. AC not working in 102.");
        if (reason is { Length: < 3 })
            throw ReservationService.Field(nameof(r.Reason), "The reason must be at least 3 characters.");

        // In house, or arriving today: the room must be ready to walk into.
        var walkInto = inHouse || reservation.CheckIn <= today;
        var moved = await reservations.MoveRoomAsync(reservation, r.ReservationUnitId, r.UnitId, r.BedId,
            reason ?? "Room assigned at the front desk", inHouse ? today : null,
            async (target, bed) =>
            {
                if (!walkInto) return;
                await RequireNotOccupiedAsync(reservation, target.Id, bed?.Id, target.Name, bed, ct);
                var hk = await HousekeepingOfAsync(target.Id, bed?.Id, ct);
                if (hk == HousekeepingStatuses.Ready) return;
                var name = bed is null ? target.Name : $"Bed {bed.Code} in {target.Name}";
                if (!r.OverrideHousekeeping)
                    throw new ConflictAppException("HOUSEKEEPING_NOT_READY",
                        $"{name} is {HousekeepingStatuses.Words(hk)}, not ready. Choose a ready room, ask housekeeping to mark it ready, " +
                        "or assign it anyway with a reason.", new { unitId = target.Id, bedId = bed?.Id, housekeeping = hk });
                await RequireOverrideAllowedAsync(propertyId, ct);
                if (reason is null)
                    throw ReservationService.Field(nameof(r.Reason), $"{name} isn't ready; say why the guest goes there anyway (at least 3 characters).");
            }, ct);

        // The guest has used the old room: it needs cleaning.
        if (inHouse)
            await SetHousekeepingCoreAsync(reservation.OrganizationId, propertyId, moved.Old.UnitId, moved.Old.BedId, HousekeepingStatuses.Dirty,
                HousekeepingSources.RoomMove, reservation.Id, $"Guest moved to {moved.TargetName}", ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await reservations.ToDtoAsync(reservation, ct);
    }

    // ------------------------------------------------------------------ check-in

    public async Task<ReservationDto> CheckInAsync(Guid propertyId, Guid id, CheckInRequest r, CancellationToken ct)
    {
        var property = await LoadPropertyAsync(propertyId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var reservation = await reservations.LockAsync(propertyId, id, ct);
        await CheckInCoreAsync(property, reservation, r.Version, r.OverrideHousekeeping, r.OverrideReason, ct);
        await tx.CommitAsync(ct);
        return await reservations.ToDtoAsync(reservation, ct);
    }

    /// <summary>The check-in rules, on a reservation the caller has locked, inside the caller's transaction.</summary>
    private async Task CheckInCoreAsync(Property property, Reservation reservation, uint? version, bool overrideHk, string? overrideReason,
        CancellationToken ct)
    {
        var today = Today(property);
        if (reservation.Status == ReservationStatuses.CheckedIn)
            throw new ConflictAppException("ALREADY_CHECKED_IN",
                $"{reservation.Reference} is already checked in{await WhoAndWhenAsync(reservation, ReservationStatuses.CheckedIn, property, ct)}. Reload to see it.");
        if (!Arriving.Contains(reservation.Status))
            throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION",
                $"{reservation.Reference} is {ReservationStatuses.Words(reservation.Status)}, so it can't be checked in.",
                new { from = reservation.Status, to = ReservationStatuses.CheckedIn });
        if (version is { } v) ConcurrencyGuard.ExpectVersion(db, reservation, v);
        if (FrontDeskRules.CheckInDateProblem(reservation.Reference, reservation.CheckIn, reservation.CheckOut, today) is { } dateProblem)
            throw new BusinessRuleAppException("CHECK_IN_DATE", dateProblem);

        // Lock the booking's rooms (id order, as booking does) so a housekeeping change or another check-in into the same
        // room waits for this one, then check each one.
        var rows = await db.ReservationUnits.Where(u => u.ReservationId == reservation.Id && u.SupersededAt == null).ToListAsync(ct);
        var unitIds = rows.Select(u => u.UnitId).Distinct().OrderBy(x => x).ToArray();
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM accommodation_units WHERE id = ANY({unitIds}) ORDER BY id FOR UPDATE", ct);
        var names = await PlaceNamesAsync(unitIds, ct);
        var housekeeping = await HousekeepingAsync(unitIds, ct);
        foreach (var occ in (await OccupyingAsync(unitIds, ct)).Where(o => o.ReservationId != reservation.Id))
            if (rows.Any(u => u.UnitId == occ.UnitId && u.BedId == occ.BedId))
                throw new ConflictAppException("ROOM_OCCUPIED",
                    $"{names[(occ.UnitId, occ.BedId)]} is still occupied: {occ.Reference} ({occ.GuestName}) was due out {QuoteEngine.Day(occ.CheckOut)} " +
                    "and hasn't checked out. Check them out first, or assign this guest another room.",
                    new { unitId = occ.UnitId, bedId = occ.BedId, reference = occ.Reference });

        var notReady = rows.Select(u => (u.UnitId, u.BedId)).Distinct()
            .Select(k => (Key: k, Status: housekeeping.GetValueOrDefault(k, HousekeepingStatuses.Ready)))
            .Where(x => x.Status != HousekeepingStatuses.Ready).ToList();
        string? reason = null;
        if (notReady.Count > 0)
        {
            var list = string.Join(", ", notReady.Select(x => $"{names[x.Key]} is {HousekeepingStatuses.Words(x.Status)}"));
            if (!overrideHk)
                throw new ConflictAppException("HOUSEKEEPING_NOT_READY",
                    $"{list}, not ready. Ask housekeeping to mark it ready, assign a ready room, or check in anyway with a reason.",
                    new { rooms = notReady.Select(x => new { unitId = x.Key.UnitId, bedId = x.Key.BedId, housekeeping = x.Status }) });
            await RequireOverrideAllowedAsync(property.Id, ct);
            reason = ReservationService.Clean(overrideReason);
            if (reason is null || reason.Length < 3)
                throw ReservationService.Field("OverrideReason", $"{list}. Say why the guest goes in anyway (at least 3 characters), e.g. Guest agreed to wait while it is cleaned.");
            reservations.AddNote(reservation, $"Checked in although {list}. Reason: {reason}");
        }

        var now = clock.GetUtcNow();
        if (reservation.Status == ReservationStatuses.Pending)
        {
            // The status graph has no PENDING → CHECKED_IN step: confirm first (its own history row), then check in.
            reservations.AddHistory(reservation, ReservationStatuses.Pending, ReservationStatuses.Confirmed, "Confirmed at check-in");
            reservation.Status = ReservationStatuses.Confirmed;
            await db.SaveChangesAsync(ct);
        }
        audit.Reason = reason;
        reservations.AddHistory(reservation, ReservationStatuses.Confirmed, ReservationStatuses.CheckedIn, reason);
        reservation.Status = ReservationStatuses.CheckedIn;
        reservation.StatusChangedAt = now;
        reservation.CheckedInAt = now;
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ check-out

    public async Task<ReservationDto> CheckOutAsync(Guid propertyId, Guid id, CheckOutRequest r, CancellationToken ct)
    {
        var property = await LoadPropertyAsync(propertyId, ct);
        var today = Today(property);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var reservation = await reservations.LockAsync(propertyId, id, ct);
        if (reservation.Status == ReservationStatuses.CheckedOut)
            throw new ConflictAppException("ALREADY_CHECKED_OUT",
                $"{reservation.Reference} is already checked out{await WhoAndWhenAsync(reservation, ReservationStatuses.CheckedOut, property, ct)}. Reload to see it.");
        if (reservation.Status != ReservationStatuses.CheckedIn)
            throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION",
                $"{reservation.Reference} is {ReservationStatuses.Words(reservation.Status)}, not checked in, so it can't be checked out.",
                new { from = reservation.Status, to = ReservationStatuses.CheckedOut });
        if (r.Version is { } v) ConcurrencyGuard.ExpectVersion(db, reservation, v);

        var balance = await folios.BalanceAsync(reservation.Id, ct);
        if (balance > 0 && !await SettingAsync(propertyId, AllowCheckoutWithBalance, false, ct))
            throw new BusinessRuleAppException("BALANCE_DUE",
                $"{reservation.Reference} still owes {Money.Rupees(balance)}. Take the payment before check-out, or ask a manager to allow " +
                $"check-out with a balance (property setting {AllowCheckoutWithBalance}).", new { balance });

        var reason = ReservationService.Clean(r.Reason);
        var departure = FrontDeskRules.DepartureOnCheckOut(reservation.CheckIn, reservation.CheckOut, today);
        var rows = await db.ReservationUnits.Where(u => u.ReservationId == reservation.Id && u.SupersededAt == null).ToListAsync(ct);
        var now = clock.GetUtcNow();
        if (departure < reservation.CheckOut)
        {
            if (reason is null || reason.Length < 3)
                throw ReservationService.Field(nameof(r.Reason),
                    $"The guest is booked until {QuoteEngine.Day(reservation.CheckOut)}. Say why they are leaving early (at least 3 characters), e.g. Family emergency.");
            // Release the nights from the new departure day: rows after it end, rows across it are cut there (the nights
            // before stay, with their prices).
            var before = reservation.TotalAmount;
            var currentIds = rows.Select(u => u.Id).ToHashSet();
            var nights = await db.ReservationRateBreakdowns.AsNoTracking()
                .Where(n => n.ReservationId == reservation.Id).ToListAsync(ct);
            var cut = rows.Where(u => u.EndDate > departure).ToList();
            foreach (var u in cut) { u.HoldsInventory = false; u.SupersededAt = now; }
            await db.SaveChangesAsync(ct);
            rows.RemoveAll(cut.Contains);
            foreach (var u in cut.Where(u => u.StartDate < departure))
                rows.Add(reservations.AddRow(u, u.UnitId, u.BedId, u.AccommodationTypeId, u.StartDate, departure, false,
                    nights.Where(n => n.ReservationUnitId == u.Id)));
            reservation.TotalAmount = nights.Where(n => currentIds.Contains(n.ReservationUnitId) && n.StayDate < departure).Sum(n => n.Total);
            reservations.AddNote(reservation,
                $"Left early on {QuoteEngine.Day(departure)} instead of {QuoteEngine.Day(reservation.CheckOut)}; " +
                $"{BlockService.NightsText(departure, reservation.CheckOut)} released ({Money.Rupees(before - reservation.TotalAmount)} less). Reason: {reason}");
            reservation.CheckOut = departure;
        }
        else if (reason is { Length: < 3 })
            throw ReservationService.Field(nameof(r.Reason), "The reason must be at least 3 characters.");

        foreach (var u in rows) u.HoldsInventory = false;
        audit.Reason = reason;
        reservations.AddHistory(reservation, ReservationStatuses.CheckedIn, ReservationStatuses.CheckedOut, reason);
        reservation.Status = ReservationStatuses.CheckedOut;
        reservation.StatusChangedAt = now;
        reservation.CheckedOutAt = now;

        // The rooms the guest leaves need cleaning: this is the event Sprint 13 turns into a housekeeping task.
        foreach (var (unitId, bedId) in rows.Where(u => u.EndDate == reservation.CheckOut).Select(u => (u.UnitId, u.BedId)).Distinct())
            await SetHousekeepingCoreAsync(reservation.OrganizationId, propertyId, unitId, bedId, HousekeepingStatuses.Dirty,
                HousekeepingSources.CheckOut, reservation.Id, $"Checked out of {reservation.Reference}", ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await reservations.ToDtoAsync(reservation, ct);
    }

    // ------------------------------------------------------------------ walk-in

    /// <summary>
    /// A guest with no booking: the profile (found or new), a booking from today, and check-in, in one transaction under
    /// one Idempotency-Key (a retry returns the same stay; a failure leaves nothing behind). Free rooms that are READY are
    /// allocated first, so the check-in normally needs no override.
    /// </summary>
    public async Task<(ReservationDto Reservation, bool Replayed)> WalkInAsync(Guid propertyId, string? idempotencyKey, WalkInRequest r, CancellationToken ct)
    {
        var key = Idempotency.RequireKey(idempotencyKey);
        var property = await LoadPropertyAsync(propertyId, ct);
        var orgId = caller.RequireOrganizationId();
        await access.RequireAsync(Perm.ReservationCreate, propertyId, ct);
        if (r.NewGuest is not null) await access.RequireAsync(Perm.GuestCreate, propertyId, ct);
        var today = Today(property);
        if (r.CheckOut <= today)
            throw ReservationService.Field(nameof(r.CheckOut), $"Check-out must be after today ({QuoteEngine.Day(today)} at the property).");
        if (r.CheckOut.DayNumber - today.DayNumber > ReservationService.MaxNights)
            throw ReservationService.Field(nameof(r.CheckOut), $"A stay can be at most {ReservationService.MaxNights} nights.");
        var scope = $"POST /api/v1/properties/{propertyId}/walk-ins";

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await Idempotency.ClaimAsync(db, orgId, caller.UserId, scope, key, Idempotency.Hash(r), "walk-in", ct) is { } stored)
            return (JsonSerializer.Deserialize<ReservationDto>(stored, Idempotency.Json)!, true);

        var guestId = r.GuestId ?? (await guests.CreateAsync(r.NewGuest!, ct)).Id;
        var walkIn = await db.ReservationSources.Where(s => s.Code == "WALK_IN" && s.IsActive && (s.OrganizationId == null || s.OrganizationId == orgId))
                         .OrderBy(s => s.OrganizationId == null).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct)
                     ?? throw new BusinessRuleAppException("NO_WALK_IN_SOURCE",
                         "The Walk-in booking source is switched off. Turn it back on in Settings → Rates → Sources.");

        // Prefer rooms and beds that are ready to walk into, then let the booking engine allocate as usual.
        var notReady = await db.UnitHousekeepingStatus.AsNoTracking()
            .Where(h => h.PropertyId == propertyId && h.Status != HousekeepingStatuses.Ready).Select(h => h.BedId ?? h.UnitId).ToListAsync(ct);
        var all = await db.AccommodationUnits.AsNoTracking().Include(u => u.Beds).Where(u => u.PropertyId == propertyId).ToListAsync(ct);
        var preferred = all.SelectMany(u => u.Beds.Select(b => b.Id).Append(u.Id)).Except(notReady).ToHashSet();

        var booking = new CreateReservationRequest(today, r.CheckOut, r.Rooms, [guestId], walkIn, r.RatePlanId, ReservationStatuses.Confirmed,
            r.SpecialRequests, r.Note);
        var reservation = await reservations.BookAsync(property, booking, preferred, ct);
        await CheckInCoreAsync(property, reservation, null, r.OverrideHousekeeping, r.OverrideReason, ct);

        var dto = await reservations.ToDtoAsync(reservation, ct);
        await Idempotency.CompleteAsync(db, orgId, scope, key, 201, dto, reservation.Id, ct);
        await tx.CommitAsync(ct);
        return (dto, false);
    }

    // ------------------------------------------------------------------ housekeeping state

    /// <summary>Sets a room's (or dorm bed's) cleaning state by hand, e.g. housekeeping reports Room 101 ready.</summary>
    public async Task<FrontDeskBoardRoomDto> SetHousekeepingAsync(Guid propertyId, Guid unitId, SetHousekeepingRequest r, CancellationToken ct)
    {
        var orgId = caller.RequireOrganizationId();
        var status = r.Status.Trim().ToUpperInvariant();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM accommodation_units WHERE id = {unitId} AND property_id = {propertyId} AND organization_id = {orgId} FOR UPDATE", ct);
        var unit = await db.AccommodationUnits.AsNoTracking().Include(u => u.Type).Include(u => u.Beds)
                       .SingleOrDefaultAsync(u => u.Id == unitId && u.PropertyId == propertyId, ct)
                   ?? throw new NotFoundAppException("Room", unitId);
        Bed? bed = null;
        if (unit.Type.InventoryMode == InventoryModes.Bed)
        {
            if (r.BedId is null) throw ReservationService.Field(nameof(r.BedId), $"{unit.Name} is a dormitory; choose the bed.");
            bed = unit.Beds.FirstOrDefault(b => b.Id == r.BedId)
                  ?? throw ReservationService.Field(nameof(r.BedId), $"That bed isn't in {unit.Name}. Reload and choose again.");
        }
        else if (r.BedId is not null)
            throw ReservationService.Field(nameof(r.BedId), $"{unit.Name} is a private room; it has no beds to choose.");

        audit.Reason = ReservationService.Clean(r.Reason);
        await SetHousekeepingCoreAsync(orgId, propertyId, unit.Id, bed?.Id, status, HousekeepingSources.Manual, null, audit.Reason, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new FrontDeskBoardRoomDto(unit.Id, unit.Code, unit.Name, bed?.Id, bed?.Code, unit.Type.Name, "", status, null, null, null, null);
    }

    /// <summary>Upserts the cleaning state and appends the change. A check-out is always recorded (it is the event that
    /// becomes a cleaning task in Sprint 13); other changes only when the state actually changes.</summary>
    private async Task SetHousekeepingCoreAsync(Guid orgId, Guid propertyId, Guid unitId, Guid? bedId, string status, string source,
        Guid? reservationId, string? reason, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var row = await db.UnitHousekeepingStatus.SingleOrDefaultAsync(h => h.UnitId == unitId && h.BedId == bedId, ct);
        var from = row?.Status ?? HousekeepingStatuses.Ready;
        if (from == status && source != HousekeepingSources.CheckOut) return;
        if (row is null)
        {
            row = new UnitHousekeepingStatus { OrganizationId = orgId, PropertyId = propertyId, UnitId = unitId, BedId = bedId };
            db.UnitHousekeepingStatus.Add(row);
        }
        row.Status = status;
        row.Reason = reason;
        row.ChangedAt = now;
        row.ChangedBy = caller.UserId;
        db.UnitHousekeepingHistory.Add(new UnitHousekeepingChange
        {
            OrganizationId = orgId,
            PropertyId = propertyId,
            UnitId = unitId,
            BedId = bedId,
            FromStatus = from,
            ToStatus = status,
            Source = source,
            ReservationId = reservationId,
            Reason = reason,
            ChangedAt = now,
            ChangedBy = caller.UserId,
        });
    }

    // ------------------------------------------------------------------ read helpers

    /// <summary>A guest still checked in in a room (or bed): the current rows of CHECKED_IN bookings that end on the
    /// booking's departure day (after a mid-stay move, only the room they moved to). Overdue guests included.</summary>
    private sealed record Occupant(Guid UnitId, Guid? BedId, Guid ReservationId, string Reference, string GuestName, DateOnly CheckOut);

    private async Task<List<Occupant>> OccupyingAsync(IReadOnlyCollection<Guid> unitIds, CancellationToken ct)
    {
        var rows = await db.ReservationUnits.AsNoTracking()
            .Where(u => unitIds.Contains(u.UnitId) && u.SupersededAt == null)
            .Join(db.Reservations.Where(r => r.Status == ReservationStatuses.CheckedIn), u => u.ReservationId, r => r.Id,
                (u, r) => new { u.UnitId, u.BedId, u.EndDate, r.Id, r.Reference, r.CheckOut, r.PrimaryGuestId })
            .Where(x => x.EndDate == x.CheckOut)
            .ToListAsync(ct);
        var guestIds = rows.Select(x => x.PrimaryGuestId).Distinct().ToList();
        var names = await db.Guests.AsNoTracking().Where(g => guestIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.FullName, ct);
        return rows.Select(x => new Occupant(x.UnitId, x.BedId, x.Id, x.Reference, names.GetValueOrDefault(x.PrimaryGuestId, ""), x.CheckOut)).ToList();
    }

    private async Task<Dictionary<(Guid UnitId, Guid? BedId), string>> HousekeepingAsync(IReadOnlyCollection<Guid> unitIds, CancellationToken ct) =>
        (await db.UnitHousekeepingStatus.AsNoTracking().Where(h => unitIds.Contains(h.UnitId)).ToListAsync(ct))
        .ToDictionary(h => (h.UnitId, h.BedId), h => h.Status);

    private async Task<string> HousekeepingOfAsync(Guid unitId, Guid? bedId, CancellationToken ct) =>
        await db.UnitHousekeepingStatus.AsNoTracking().Where(h => h.UnitId == unitId && h.BedId == bedId).Select(h => h.Status)
            .SingleOrDefaultAsync(ct) ?? HousekeepingStatuses.Ready;

    /// <summary>"Room 101", "Dormitory bed B02", keyed by room and bed.</summary>
    private async Task<Dictionary<(Guid UnitId, Guid? BedId), string>> PlaceNamesAsync(IReadOnlyCollection<Guid> unitIds, CancellationToken ct)
    {
        var units = await db.AccommodationUnits.AsNoTracking().Include(u => u.Beds).Where(u => unitIds.Contains(u.Id)).ToListAsync(ct);
        var names = new Dictionary<(Guid, Guid?), string>();
        foreach (var u in units)
        {
            names[(u.Id, null)] = u.Name;
            foreach (var b in u.Beds) names[(u.Id, b.Id)] = $"{u.Name} bed {b.Code}";
        }
        return names;
    }

    private async Task RequireNotOccupiedAsync(Reservation reservation, Guid unitId, Guid? bedId, string unitName, Bed? bed, CancellationToken ct)
    {
        var occ = (await OccupyingAsync([unitId], ct)).FirstOrDefault(o => o.BedId == bedId && o.ReservationId != reservation.Id);
        if (occ is not null)
            throw new ConflictAppException("ROOM_OCCUPIED",
                $"{(bed is null ? unitName : $"Bed {bed.Code} in {unitName}")} is still occupied: {occ.Reference} ({occ.GuestName}) is due out " +
                $"{QuoteEngine.Day(occ.CheckOut)} and hasn't checked out. Choose another room.",
                new { unitId, bedId, reference = occ.Reference });
    }

    private async Task RequireOverrideAllowedAsync(Guid propertyId, CancellationToken ct)
    {
        if (!await SettingAsync(propertyId, AllowCheckinWhenNotReady, true, ct))
            throw new BusinessRuleAppException("OVERRIDE_NOT_ALLOWED",
                $"This property doesn't allow guests into rooms that aren't ready (property setting {AllowCheckinWhenNotReady}). " +
                "Ask housekeeping to mark the room ready, or choose a ready room.");
    }

    /// <summary>A boolean property setting, or <paramref name="fallback"/> when it isn't set.</summary>
    private async Task<bool> SettingAsync(Guid propertyId, string key, bool fallback, CancellationToken ct)
    {
        var json = await db.PropertySettings.AsNoTracking().Where(s => s.PropertyId == propertyId && s.Key == key).Select(s => s.Value)
            .SingleOrDefaultAsync(ct);
        if (json is null) return fallback;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Object when doc.RootElement.TryGetProperty("enabled", out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False => e.GetBoolean(),
            _ => fallback,
        };
    }

    /// <summary>" at 14:05 by Asha Rao" (property time), from the status history.</summary>
    private async Task<string> WhoAndWhenAsync(Reservation reservation, string status, Property property, CancellationToken ct)
    {
        var h = await db.ReservationStatusHistory.AsNoTracking().Where(x => x.ReservationId == reservation.Id && x.ToStatus == status)
            .OrderByDescending(x => x.ChangedAt).FirstOrDefaultAsync(ct);
        if (h is null) return "";
        var who = h.ChangedBy is { } uid
            ? await db.Users.IgnoreQueryFilters().Where(u => u.Id == uid).Select(u => u.FullName).SingleOrDefaultAsync(ct)
            : null;
        var local = TimeZoneInfo.ConvertTime(h.ChangedAt, TimeZoneInfo.FindSystemTimeZoneById(property.Timezone));
        return $" (at {local:HH:mm} on {QuoteEngine.Day(DateOnly.FromDateTime(local.DateTime))}{(who is null ? "" : $" by {who}")})";
    }

    /// <summary>The list rows: guest, rooms with their cleaning state, and what would stop a check-in.</summary>
    private async Task<List<FrontDeskStayDto>> StaysAsync(List<Reservation> list, DateOnly today, CancellationToken ct)
    {
        var ids = list.Select(r => r.Id).ToList();
        var rows = await db.ReservationUnits.AsNoTracking().Where(u => ids.Contains(u.ReservationId) && u.SupersededAt == null).ToListAsync(ct);
        var unitIds = rows.Select(u => u.UnitId).Distinct().ToList();
        var units = await db.AccommodationUnits.AsNoTracking().Include(u => u.Type).Include(u => u.Beds)
            .Where(u => unitIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
        var housekeeping = await HousekeepingAsync(unitIds, ct);
        var occupying = await OccupyingAsync(unitIds, ct);
        var guestIds = list.Select(r => r.PrimaryGuestId).Distinct().ToList();
        var guestRows = await db.Guests.AsNoTracking().Where(g => guestIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, ct);
        var sources = await db.ReservationSources.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var balances = new Dictionary<Guid, decimal>();
        foreach (var r in list) balances[r.Id] = await folios.BalanceAsync(r.Id, ct);

        return list.Select(r =>
        {
            var g = guestRows[r.PrimaryGuestId];
            // the rooms the guest is (or will be) in: the current rows that reach the departure day
            var mine = rows.Where(u => u.ReservationId == r.Id && u.EndDate == r.CheckOut)
                .OrderBy(u => units[u.UnitId].SortOrder).ThenBy(u => units[u.UnitId].Code, StringComparer.Ordinal).ToList();
            var rooms = mine.Select(u =>
            {
                var unit = units[u.UnitId];
                var occ = occupying.FirstOrDefault(o => o.UnitId == u.UnitId && o.BedId == u.BedId && o.ReservationId != r.Id);
                return new FrontDeskRoomDto(u.Id, unit.Id, unit.Code, unit.Name, u.BedId, u.BedId is { } b ? unit.Beds.First(x => x.Id == b).Code : null,
                    unit.Type.Name, u.StartDate, u.EndDate, housekeeping.GetValueOrDefault((u.UnitId, u.BedId), HousekeepingStatuses.Ready), occ?.Reference);
            }).ToList();
            var arriving = Arriving.Contains(r.Status);
            string? problem = null;
            if (arriving)
            {
                problem = FrontDeskRules.CheckInDateProblem(r.Reference, r.CheckIn, r.CheckOut, today);
                if (problem is null && rooms.FirstOrDefault(x => x.OccupiedBy is not null) is { } busy)
                    problem = $"{RoomName(busy)} is still occupied by {busy.OccupiedBy}, which hasn't checked out.";
                if (problem is null && rooms.FirstOrDefault(x => x.Housekeeping != HousekeepingStatuses.Ready) is { } dirty)
                    problem = $"{RoomName(dirty)} is {HousekeepingStatuses.Words(dirty.Housekeeping)}, not ready.";
            }
            return new FrontDeskStayDto(r.Id, r.Reference, r.Status, g.Id, g.FullName, g.Phone, g.IsVip, r.CheckIn, r.CheckOut, r.Nights,
                r.Adults, r.Children, rooms, sources.GetValueOrDefault(r.SourceId, ""), r.TotalAmount, balances[r.Id], r.Currency,
                r.SpecialRequests, r.CheckedInAt, r.CheckedOutAt,
                Late: arriving && r.CheckIn < today, Overdue: r.Status == ReservationStatuses.CheckedIn && r.CheckOut < today,
                problem, r.Version);
        }).ToList();
    }

    private static string RoomName(FrontDeskRoomDto r) => r.BedCode is null ? r.UnitName : $"{r.UnitName} bed {r.BedCode}";

    private DateOnly Today(Property property) => property.LocalDate(clock.GetUtcNow());

    private async Task<Property> LoadPropertyAsync(Guid propertyId, CancellationToken ct) =>
        await db.Properties.SingleOrDefaultAsync(p => p.Id == propertyId, ct) ?? throw new NotFoundAppException("Property", propertyId);
}
