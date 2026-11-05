using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Resort.Application.Activities;
using Resort.Application.Common;
using Resort.Application.Folios;
using Resort.Application.Reservations;
using Resort.Application.Validation;
using Resort.Contracts;
using Resort.Domain.Folios;
using Resort.Domain.Organizations;
using Resort.Domain.Reservations;
using Resort.Domain.Transport;

namespace Resort.Application.Transport;

/// <summary>
/// Seats on the beach shuttle and running the trip (Sprint 21, brief §47, §73).
/// <list type="bullet">
/// <item>Booking takes the trip's row lock first (SELECT … FOR UPDATE, as Sprint 5 locks rooms and Sprint 20 activity
/// sessions), then counts the seats left with fresh data, so two people booking the last seat queue and the second is
/// told it is full. The database keeps <c>seats_booked</c> from the passengers by a trigger and refuses it above capacity
/// (CHECK) if anything skipped the lock. A retried click replays the first booking (Idempotency-Key).</item>
/// <item>A staying guest's seats go on the stay's main folio as one TRANSPORT line (locks: trip, then reservation, as every
/// folio writer does). Free trips and complimentary seats (with the reason) charge nothing.</item>
/// <item>The driver (or a manager) ticks passengers as they board and departs the trip; anyone still not on board then is a
/// no-show and keeps the charge. Cancelling a passenger before departure, or the whole trip, reverses the charge.</item>
/// </list>
/// </summary>
public sealed class TransportBookingService(IAppDbContext db, ICurrentUser caller, CallerAccess access, AuditContext audit, TimeProvider clock,
    FolioPosting posting, TransportService transport, ActivityService activities)
{
    // ------------------------------------------------------------------ booking

    public async Task<(TripPassengerDto Passenger, bool Replayed)> BookAsync(Guid propertyId, string? idempotencyKey, BookTransportRequest r,
        CancellationToken ct)
    {
        var key = Idempotency.RequireKey(idempotencyKey);
        var property = await transport.RequirePropertyAsync(propertyId, ct);
        var orgId = caller.RequireOrganizationId();
        var scope = $"POST /api/v1/properties/{propertyId}/transport/bookings";
        var now = clock.GetUtcNow();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await Idempotency.ClaimAsync(db, orgId, caller.UserId, scope, key, Idempotency.Hash(r), "transport booking", ct) is { } stored)
            return (JsonSerializer.Deserialize<TripPassengerDto>(stored, Idempotency.Json)!, true);

        // 1. The trip's lock: everyone booking this trip waits here, so the count below is the truth.
        var trip = await transport.LockTripAsync(propertyId, r.TripId, ct);
        var route = await db.TransportRoutes.AsNoTracking().SingleAsync(x => x.Id == trip.RouteId, ct);
        var label = TransportService.Label(route.Name, trip, property);
        if (trip.Status == TripStatuses.Cancelled)
            throw new BusinessRuleAppException("TRIP_CANCELLED", $"{label} was cancelled ({trip.CancelReason}). Choose another trip.");
        if (trip.Status != TripStatuses.Scheduled || now >= trip.DepartsAt)
            throw new BusinessRuleAppException("TRIP_DEPARTED", $"{label} has already left, so nobody more can be booked on it. Choose a later trip.");
        // 2. The seats, counted under the lock.
        if (r.Seats > trip.SeatsLeft)
            throw new ConflictAppException("TRIP_FULL", trip.SeatsLeft == 0
                ? $"{label} is full ({trip.SeatsBooked} of {trip.Capacity} seats booked). Choose another trip."
                : $"{label} has {(trip.SeatsLeft == 1 ? "1 seat" : $"{trip.SeatsLeft} seats")} left and this booking is for {r.Seats}. Book fewer seats or choose another trip.",
                new { seatsLeft = trip.SeatsLeft });

        // 3. Who, and how it is paid.
        Reservation? stay = null;
        string guestName;
        if (r.ReservationId is { } rid)
        {
            stay = await LockStayAsync(propertyId, rid, ct);
            if (stay.Status is not (ReservationStatuses.CheckedIn or ReservationStatuses.Confirmed))
                throw ReservationService.Field(nameof(r.ReservationId),
                    $"{stay.Reference} is {ReservationStatuses.Words(stay.Status).ToLowerInvariant()}, so it can't be booked on the shuttle. Choose a stay that is confirmed or checked in.");
            if (trip.TripDate < stay.CheckIn || trip.TripDate > stay.CheckOut)
                throw ReservationService.Field(nameof(r.ReservationId),
                    $"{stay.Reference} stays from {TransportService.Short(stay.CheckIn)} to {TransportService.Short(stay.CheckOut)}, so it can't be booked on {TransportService.Short(trip.TripDate)}.");
            guestName = ReservationService.Clean(r.GuestName)
                        ?? await db.Guests.AsNoTracking().Where(g => g.Id == stay.PrimaryGuestId).Select(g => g.FullName).SingleAsync(ct);
        }
        else
        {
            guestName = ReservationService.Clean(r.GuestName)
                        ?? throw ReservationService.Field(nameof(r.GuestName), "Choose the guest's stay, or type the name of the person travelling.");
        }
        var amount = r.Seats * trip.Price;
        var settlement = r.Complimentary ? TransportSettlements.Complimentary
            : amount == 0 ? TransportSettlements.Free
            : stay is not null ? TransportSettlements.RoomCharge
            : throw ReservationService.Field(nameof(r.ReservationId),
                $"A seat on {route.Name} costs {Money.Rupees(trip.Price)}. Choose the guest's stay so it goes on their bill, or make it complimentary with the reason.");

        var p = new TripPassenger
        {
            OrganizationId = property.OrganizationId,
            PropertyId = propertyId,
            Code = await Purchasing.PurchasingService.NextNumberAsync(db, propertyId, "trip_passenger", "TP", ct),
            TripId = trip.Id,
            ReservationId = stay?.Id,
            GuestId = stay?.PrimaryGuestId,
            GuestName = guestName,
            Seats = (short)r.Seats,
            UnitPrice = trip.Price,
            Amount = amount,
            Settlement = settlement,
            ComplimentaryReason = r.Complimentary ? r.ComplimentaryReason!.Trim() : null,
            Note = ReservationService.Clean(r.Note),
        };
        // 4. The charge on the stay's bill (saved first: the passenger points at it).
        if (settlement == TransportSettlements.RoomCharge)
        {
            var folio = await posting.EnsureMainFolioAsync(stay!, ct);
            var item = await posting.ChargeAsync(folio, new FolioItem
            {
                Category = ChargeCategories.Transport,
                Description = FolioPosting.Trim($"{route.Name}, {TransportService.Short(trip.TripDate)} {TransportService.Hm(trip.DepartsAt, TransportService.Tz(property))} ({p.Code})"),
                ServiceDate = trip.TripDate,
                Quantity = r.Seats,
                UnitPrice = trip.Price,
                Source = FolioItemSources.Transport,
            }, null, ct);
            await db.SaveChangesAsync(ct);
            p.FolioId = folio.Id;
            p.FolioItemId = item.Id;
        }
        db.TripPassengers.Add(p);
        audit.Reason = $"{p.Code}: {guestName} × {r.Seats} on {label}" +
                       (settlement == TransportSettlements.RoomCharge ? $", {Money.Rupees(amount)} on {stay!.Reference}" : $" ({settlement.ToLowerInvariant()})");
        await db.SaveChangesAsync(ct);

        var dto = (await DtosAsync(property, [p], ct))[0];
        await Idempotency.CompleteAsync(db, orgId, scope, key, 201, dto, p.Id, ct);
        await tx.CommitAsync(ct);
        return (dto, false);
    }

    // ------------------------------------------------------------------ cancelling

    /// <summary>Cancels one passenger before the trip leaves: the charge comes off the bill.</summary>
    public async Task<TripPassengerDto> CancelAsync(Guid propertyId, Guid id, CancelPassengerRequest r, CancellationToken ct)
    {
        var property = await transport.RequirePropertyAsync(propertyId, ct);
        var tripId = await TripOfAsync(propertyId, id, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var trip = await transport.LockTripAsync(propertyId, tripId, ct);
        var p = await db.TripPassengers.SingleAsync(x => x.Id == id, ct);
        if (r.Version is { } v && p.Version != v)
            throw new ConflictAppException("CONCURRENCY_CONFLICT", $"Someone else changed {p.Code} a moment ago. Reload to see it, then try again.");
        var label = await transport.LabelAsync(trip, property, ct);
        if (p.Status == PassengerStatuses.Cancelled)
            throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION", $"{p.Code} is already cancelled ({p.CancelReason}).");
        if (trip.Status != TripStatuses.Scheduled || clock.GetUtcNow() >= trip.DepartsAt)
            throw new BusinessRuleAppException("TRIP_DEPARTED", $"{label} has already left, so {p.Code} can't be cancelled now.");
        if (p.Status == PassengerStatuses.Boarded)
            throw new BusinessRuleAppException("PASSENGER_ON_BOARD", $"{p.GuestName} ({p.Code}) is ticked as on board. Untick them first if they got off, then cancel.");

        var reversed = await RefundAsync(propertyId, [p], r.Reason.Trim(), ct);
        Cancel(p, r.Reason, reversed.GetValueOrDefault(p.Id));
        audit.Reason = $"{p.Code} cancelled: {r.Reason.Trim()}" + (reversed.ContainsKey(p.Id) ? $"; {Money.Rupees(p.Amount)} reversed" : "");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await DtosAsync(property, [await db.TripPassengers.AsNoTracking().SingleAsync(x => x.Id == id, ct)], ct))[0];
    }

    /// <summary>Cancels a trip before it leaves (a breakdown, the road closed): every passenger is cancelled and their charge
    /// reversed on the bill.</summary>
    public async Task<CancelTripResultDto> CancelTripAsync(Guid propertyId, Guid id, CancelTripRequest r, CancellationToken ct)
    {
        var property = await transport.RequirePropertyAsync(propertyId, ct);
        var reason = r.Reason.Trim();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var trip = await transport.LockTripAsync(propertyId, id, ct);
        var label = await transport.LabelAsync(trip, property, ct);
        if (r.Version is { } v && trip.Version != v)
            throw new ConflictAppException("CONCURRENCY_CONFLICT", $"{label} changed a moment ago (someone booked or changed it). Reload to see it, then try again.");
        if (trip.Status == TripStatuses.Cancelled)
            throw new BusinessRuleAppException("TRIP_CANCELLED", $"{label} is already cancelled ({trip.CancelReason}).");
        if (trip.Status != TripStatuses.Scheduled)
            throw new BusinessRuleAppException("TRIP_DEPARTED", $"{label} has already left, so it can't be cancelled.");

        trip.Status = TripStatuses.Cancelled;
        trip.CancelReason = reason;
        trip.CancelledAt = clock.GetUtcNow();
        trip.CancelledBy = caller.RequireUserId();
        await db.SaveChangesAsync(ct);

        var live = await db.TripPassengers.Where(x => x.TripId == id && x.Status != PassengerStatuses.Cancelled).OrderBy(x => x.Code).ToListAsync(ct);
        var reversed = await RefundAsync(propertyId, live, $"Trip cancelled: {reason}", ct);
        foreach (var p in live)
        {
            p.BoardedAt = null;
            p.BoardedBy = null;
            Cancel(p, reason, reversed.GetValueOrDefault(p.Id));
        }
        var refunded = live.Where(p => reversed.ContainsKey(p.Id)).Sum(p => p.Amount);
        audit.Reason = $"{label} cancelled: {reason}. {reversed.Count} refunded ({Money.Rupees(refunded)}).";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var fresh = await db.VehicleTrips.AsNoTracking().SingleAsync(x => x.Id == id, ct);
        var after = await db.TripPassengers.AsNoTracking().Where(x => x.TripId == id).OrderBy(x => x.Code).ToListAsync(ct);
        return new CancelTripResultDto((await transport.TripDtosAsync(property, [fresh], ct))[0], reversed.Count, refunded, live.Count - reversed.Count,
            await DtosAsync(property, after, ct));
    }

    // ------------------------------------------------------------------ running the trip

    /// <summary>The driver ticks a passenger as on board (or unticks them) before the trip leaves.</summary>
    public async Task<TripPassengerDto> BoardAsync(Guid propertyId, Guid id, bool boarded, TripStepRequest r, CancellationToken ct)
    {
        var property = await transport.RequirePropertyAsync(propertyId, ct);
        var tripId = await TripOfAsync(propertyId, id, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var trip = await transport.LockTripAsync(propertyId, tripId, ct);
        await RequireOperatorAsync(trip, property, ct);
        var p = await db.TripPassengers.SingleAsync(x => x.Id == id, ct);
        if (r.Version is { } v && p.Version != v)
            throw new ConflictAppException("CONCURRENCY_CONFLICT", $"Someone else changed {p.Code} a moment ago. Reload to see it, then try again.");
        var label = await transport.LabelAsync(trip, property, ct);
        if (trip.Status != TripStatuses.Scheduled)
            throw new BusinessRuleAppException("TRIP_DEPARTED", $"{label} is {TripStatuses.Words(trip.Status).ToLowerInvariant()}, so boarding can't change now.");
        var want = boarded ? PassengerStatuses.Boarded : PassengerStatuses.Booked;
        if (p.Status == want)
        {
            // A double tap: nothing to do.
        }
        else if (p.Status is PassengerStatuses.Cancelled or PassengerStatuses.NoShow)
            throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION", $"{p.GuestName} ({p.Code}) is {PassengerStatuses.Words(p.Status).ToLowerInvariant()}, so they can't be ticked.");
        else
        {
            p.Status = want;
            p.BoardedAt = boarded ? clock.GetUtcNow() : null;
            p.BoardedBy = boarded ? caller.RequireUserId() : null;
            audit.Reason = $"{p.Code} {p.GuestName} {(boarded ? "on board" : "not on board after all")}: {label}";
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return (await DtosAsync(property, [await db.TripPassengers.AsNoTracking().SingleAsync(x => x.Id == id, ct)], ct))[0];
    }

    /// <summary>The trip leaves: everyone not ticked as on board becomes a no-show (their charge stays). Allowed from the
    /// trip's day.</summary>
    public async Task<TripDetailDto> DepartAsync(Guid propertyId, Guid id, TripStepRequest r, CancellationToken ct)
    {
        var property = await transport.RequirePropertyAsync(propertyId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var trip = await transport.LockTripAsync(propertyId, id, ct);
        await RequireOperatorAsync(trip, property, ct);
        var label = await transport.LabelAsync(trip, property, ct);
        if (r.Version is { } v && trip.Version != v)
            throw new ConflictAppException("CONCURRENCY_CONFLICT", $"{label} changed a moment ago (someone booked or boarded). Reload to see it, then try again.");
        if (trip.Status != TripStatuses.Scheduled)
            throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION", $"{label} is {TripStatuses.Words(trip.Status).ToLowerInvariant()}, so it can't leave.");
        var today = property.LocalDate(clock.GetUtcNow());
        if (today < trip.TripDate)
            throw new BusinessRuleAppException("TRIP_NOT_TODAY", $"{label} is on {TransportService.Short(trip.TripDate)}, so it can't leave today.");
        var now = clock.GetUtcNow();
        trip.Status = TripStatuses.Departed;
        trip.DepartedAt = now;
        trip.DepartedBy = caller.RequireUserId();
        var waiting = await db.TripPassengers.Where(x => x.TripId == id && x.Status == PassengerStatuses.Booked).ToListAsync(ct);
        foreach (var p in waiting) p.Status = PassengerStatuses.NoShow;
        audit.Reason = $"{label} left" + (waiting.Count > 0 ? $"; no-show: {string.Join(", ", waiting.OrderBy(x => x.Code).Select(x => $"{x.Code} {x.GuestName}"))}" : "");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await transport.TripAsync(propertyId, id, this, ct);
    }

    /// <summary>The trip has arrived.</summary>
    public async Task<TripDetailDto> CompleteAsync(Guid propertyId, Guid id, TripStepRequest r, CancellationToken ct)
    {
        var property = await transport.RequirePropertyAsync(propertyId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var trip = await transport.LockTripAsync(propertyId, id, ct);
        await RequireOperatorAsync(trip, property, ct);
        var label = await transport.LabelAsync(trip, property, ct);
        if (trip.Status != TripStatuses.Departed)
            throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION", trip.Status == TripStatuses.Scheduled
                ? $"{label} hasn't left yet. Mark it as left first."
                : $"{label} is {TripStatuses.Words(trip.Status).ToLowerInvariant()}, so it can't be completed.");
        trip.Status = TripStatuses.Completed;
        trip.CompletedAt = clock.GetUtcNow();
        trip.CompletedBy = caller.RequireUserId();
        audit.Reason = $"{label} arrived";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await transport.TripAsync(propertyId, id, this, ct);
    }

    /// <summary>A driver runs only their own trips; a manager any trip.</summary>
    private async Task RequireOperatorAsync(VehicleTrip trip, Property property, CancellationToken ct)
    {
        var a = await access.GetAsync(ct);
        if (TransportService.CanOperate(a, caller.UserId, trip)) return;
        var label = await transport.LabelAsync(trip, property, ct);
        var driver = trip.DriverUserId is { } d ? (await transport.UserNamesAsync([d], ct)).GetValueOrDefault(d) : null;
        throw new ForbiddenAppException(driver is null
            ? $"{label} has no driver yet, so only a transport manager can run it."
            : $"{label} is {driver}'s trip, so only {driver} or a transport manager can run it.");
    }

    // ------------------------------------------------------------------ helpers

    private async Task<Guid> TripOfAsync(Guid propertyId, Guid passengerId, CancellationToken ct) =>
        await db.TripPassengers.AsNoTracking().Where(p => p.Id == passengerId && p.PropertyId == propertyId).Select(p => (Guid?)p.TripId)
            .SingleOrDefaultAsync(ct)
        ?? throw new NotFoundAppException("That passenger doesn't exist at this property. Reload the page.");

    /// <summary>Reverses the room charge of each passenger (under the stays' locks) and saves the reversals. Returns the
    /// reversal line per passenger; one with nothing on the bill isn't in it. A charge someone already reversed on the folio
    /// by hand isn't reversed twice: that reversal is used.</summary>
    private async Task<Dictionary<Guid, Guid>> RefundAsync(Guid propertyId, IReadOnlyList<TripPassenger> passengers, string reason, CancellationToken ct)
    {
        var charged = passengers.Where(p => p.Settlement == TransportSettlements.RoomCharge && p.FolioItemId is not null).ToList();
        var result = new Dictionary<Guid, Guid>();
        if (charged.Count == 0) return result;
        var stayIds = charged.Select(p => p.ReservationId!.Value).Distinct().Order().ToArray();
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM reservations WHERE id = ANY({stayIds}) AND property_id = {propertyId} ORDER BY id FOR UPDATE", ct);
        var itemIds = charged.Select(p => p.FolioItemId!.Value).ToList();
        var items = await db.FolioItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var already = await db.FolioItems.AsNoTracking().Where(i => i.ReversesItemId != null && itemIds.Contains(i.ReversesItemId.Value))
            .ToDictionaryAsync(i => i.ReversesItemId!.Value, i => i.Id, ct);
        var folioIds = items.Values.Select(i => i.FolioId).Distinct().ToList();
        var folios = await db.Folios.Where(f => folioIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);
        foreach (var p in charged)
        {
            var charge = items[p.FolioItemId!.Value];
            if (already.TryGetValue(charge.Id, out var existing)) { result[p.Id] = existing; continue; }
            var reversal = await posting.ReverseAsync(folios[charge.FolioId], charge, FolioPosting.Trim($"{p.Code}: {reason}"), ct);
            result[p.Id] = reversal.Id;
        }
        await db.SaveChangesAsync(ct);
        return result;
    }

    private void Cancel(TripPassenger p, string reason, Guid reversal)
    {
        p.Status = PassengerStatuses.Cancelled;
        p.CancelReason = reason.Trim();
        p.CancelledAt = clock.GetUtcNow();
        p.CancelledBy = caller.RequireUserId();
        p.ReversalItemId = reversal == Guid.Empty ? null : reversal;
        p.Refund = p.ReversalItemId is not null ? TransportRefunds.Reversed : TransportRefunds.None;
    }

    private async Task<Reservation> LockStayAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM reservations WHERE id = {id} AND property_id = {propertyId} FOR UPDATE", ct);
        return await db.Reservations.SingleOrDefaultAsync(x => x.Id == id && x.PropertyId == propertyId, ct)
               ?? throw ReservationService.Field("ReservationId", "That stay isn't at this property. Reload and choose the guest again.");
    }

    // ------------------------------------------------------------------ reading

    /// <summary>Stays that can be booked on the shuttle on a date (the same list as for activities).</summary>
    public Task<IReadOnlyList<ActivityStayDto>> StaysAsync(Guid propertyId, DateOnly? date, CancellationToken ct) => activities.StaysAsync(propertyId, date, ct);

    /// <summary>Seats booked for a stay (the reservation page uses it).</summary>
    public async Task<IReadOnlyList<TripPassengerDto>> ForStayAsync(Guid propertyId, Guid reservationId, CancellationToken ct)
    {
        var property = await transport.RequirePropertyAsync(propertyId, ct);
        var list = await db.TripPassengers.AsNoTracking().Where(p => p.PropertyId == propertyId && p.ReservationId == reservationId)
            .OrderByDescending(p => p.CreatedAt).Take(100).ToListAsync(ct);
        return await DtosAsync(property, list, ct);
    }

    public async Task<TripPassengerDto> GetAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        var property = await transport.RequirePropertyAsync(propertyId, ct);
        var p = await db.TripPassengers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.PropertyId == propertyId, ct)
                ?? throw new NotFoundAppException("That passenger doesn't exist at this property. Reload the page.");
        return (await DtosAsync(property, [p], ct))[0];
    }

    internal async Task<List<TripPassengerDto>> DtosAsync(Property property, IReadOnlyCollection<TripPassenger> list, CancellationToken ct)
    {
        if (list.Count == 0) return [];
        var tripIds = list.Select(p => p.TripId).Distinct().ToList();
        var trips = await db.VehicleTrips.AsNoTracking().Where(t => tripIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var routes = await db.TransportRoutes.AsNoTracking().Where(x => x.PropertyId == property.Id).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var stayIds = list.Select(p => p.ReservationId).OfType<Guid>().Distinct().ToList();
        var refs = await db.Reservations.AsNoTracking().Where(r => stayIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Reference, ct);
        var rooms = await activities.RoomsTextAsync(stayIds, ct);
        var folioIds = list.Select(p => p.FolioId).OfType<Guid>().Distinct().ToList();
        var folios = await db.Folios.AsNoTracking().Where(f => folioIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Number, ct);
        var users = await transport.UserNamesAsync(list.Select(p => p.CreatedBy).Concat(list.Select(p => p.BoardedBy)), ct);
        var a = await access.GetAsync(ct);
        var canCancel = a.Has(Perm.TransportBookingCreate, property.Id);
        var now = clock.GetUtcNow();
        var tz = TransportService.Tz(property);
        return list.Select(p =>
        {
            var t = trips[p.TripId];
            var open = t.Status == TripStatuses.Scheduled;
            var operate = TransportService.CanOperate(a, caller.UserId, t);
            var actions = new List<string>();
            if (p.Status == PassengerStatuses.Booked && open && now < t.DepartsAt && canCancel) actions.Add("cancel");
            if (p.Status == PassengerStatuses.Booked && open && operate) actions.Add("board");
            if (p.Status == PassengerStatuses.Boarded && open && operate) actions.Add("unboard");
            return new TripPassengerDto(p.Id, p.Code, p.TripId, routes.GetValueOrDefault(t.RouteId, ""), t.TripDate, TransportService.Hm(t.DepartsAt, tz),
                p.ReservationId, p.ReservationId is { } r ? refs.GetValueOrDefault(r) : null, p.ReservationId is { } r2 ? rooms.GetValueOrDefault(r2) : null,
                p.GuestName, p.Seats, p.UnitPrice, p.Amount, p.Settlement, p.ComplimentaryReason, p.FolioId is { } f ? folios.GetValueOrDefault(f) : null,
                p.Note, p.Status, p.BoardedAt, p.BoardedBy is { } bb ? users.GetValueOrDefault(bb) : null, p.CancelReason, p.CancelledAt, p.Refund,
                actions, p.CreatedAt, p.CreatedBy is { } u ? users.GetValueOrDefault(u) : null, p.Version);
        }).ToList();
    }
}
