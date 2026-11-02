using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Folios;
using Resort.Application.Reservations;
using Resort.Contracts;
using Resort.Domain.Activities;
using Resort.Domain.Folios;
using Resort.Domain.Organizations;
using Resort.Domain.Reservations;

namespace Resort.Application.Activities;

/// <summary>
/// Activity bookings (Sprint 20, brief §48).
/// <list type="bullet">
/// <item>Booking takes the session's row lock first (SELECT … FOR UPDATE, as Sprint 5 locks rooms), then counts the seats
/// left with fresh data, so two people booking the last kayak seat queue and the second is told it is full. The database
/// keeps <c>seats_booked</c> from the bookings by a trigger and refuses it above capacity (CHECK) if anything skipped the
/// lock. A retried click replays the first booking (Idempotency-Key).</item>
/// <item>A staying guest's booking goes on the stay's main folio as one ACTIVITY line (locks: session, then reservation,
/// as every folio writer does). Free sessions and complimentary bookings (with the reason) charge nothing.</item>
/// <item>Cancelling a booking before its session starts reverses its charge. A weather cancellation cancels the session
/// and every booking on it: each is refunded on the bill, or moved to another session of the same activity with its
/// charge (the seats there are checked under the same locks). Equipment reserved for a cancelled booking is released.</item>
/// </list>
/// </summary>
public sealed class ActivityBookingService(IAppDbContext db, ICurrentUser caller, CallerAccess access, AuditContext audit, TimeProvider clock,
    FolioPosting posting, ActivityService activities, EquipmentService equipment)
{
    // ------------------------------------------------------------------ booking

    public async Task<(ActivityBookingDto Booking, bool Replayed)> BookAsync(Guid propertyId, string? idempotencyKey, BookActivityRequest r,
        CancellationToken ct)
    {
        var key = Idempotency.RequireKey(idempotencyKey);
        var property = await activities.RequirePropertyAsync(propertyId, ct);
        var orgId = caller.RequireOrganizationId();
        var scope = $"POST /api/v1/properties/{propertyId}/activities/bookings";
        var now = clock.GetUtcNow();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await Idempotency.ClaimAsync(db, orgId, caller.UserId, scope, key, Idempotency.Hash(r), "activity booking", ct) is { } stored)
            return (JsonSerializer.Deserialize<ActivityBookingDto>(stored, Idempotency.Json)!, true);

        // 1. The session's lock: everyone booking this session waits here, so the count below is the truth.
        var session = await activities.LockSessionAsync(propertyId, r.SessionId, ct);
        var activity = await db.Activities.AsNoTracking().SingleAsync(a => a.Id == session.ActivityId, ct);
        var label = ActivityService.Label(activity, session, property);
        if (session.Status != ActivitySessionStatuses.Scheduled)
            throw new BusinessRuleAppException("SESSION_CANCELLED", $"{label} was cancelled ({session.CancelReason}). Choose another session.");
        if (now >= session.StartsAt)
            throw new BusinessRuleAppException("SESSION_STARTED", $"{label} has already started, so nobody more can be booked on it. Choose a later session.");
        // 2. The seats, counted under the lock.
        if (r.Seats > session.SeatsLeft)
            throw new ConflictAppException("ACTIVITY_FULL", session.SeatsLeft == 0
                ? $"{label} is full ({session.SeatsBooked} of {session.Capacity} seats booked). Choose another session."
                : $"{label} has {(session.SeatsLeft == 1 ? "1 seat" : $"{session.SeatsLeft} seats")} left and this booking is for {r.Seats}. Book fewer seats or choose another session.",
                new { seatsLeft = session.SeatsLeft });

        // 3. Who, and how it is paid.
        Reservation? stay = null;
        string guestName;
        if (r.ReservationId is { } rid)
        {
            stay = await LockStayAsync(propertyId, rid, ct);
            if (stay.Status is not (ReservationStatuses.CheckedIn or ReservationStatuses.Confirmed))
                throw ReservationService.Field(nameof(r.ReservationId),
                    $"{stay.Reference} is {ReservationStatuses.Words(stay.Status).ToLowerInvariant()}, so it can't be booked on activities. Choose a stay that is confirmed or checked in.");
            if (session.SessionDate < stay.CheckIn || session.SessionDate > stay.CheckOut)
                throw ReservationService.Field(nameof(r.ReservationId),
                    $"{stay.Reference} stays from {ActivityService.Short(stay.CheckIn)} to {ActivityService.Short(stay.CheckOut)}, so it can't be booked on {ActivityService.Short(session.SessionDate)}.");
            guestName = ReservationService.Clean(r.GuestName)
                        ?? await db.Guests.AsNoTracking().Where(g => g.Id == stay.PrimaryGuestId).Select(g => g.FullName).SingleAsync(ct);
        }
        else
        {
            guestName = ReservationService.Clean(r.GuestName)
                        ?? throw ReservationService.Field(nameof(r.GuestName), "Choose the guest's stay, or type the name of the person booked.");
        }
        var amount = r.Seats * session.Price;
        var settlement = r.Complimentary ? ActivitySettlements.Complimentary
            : amount == 0 ? ActivitySettlements.Free
            : stay is not null ? ActivitySettlements.RoomCharge
            : throw ReservationService.Field(nameof(r.ReservationId),
                $"{activity.Name} costs {ActivityService.Rupees(session.Price)} a person. Choose the guest's stay so it goes on their bill, or make it complimentary with the reason.");

        var booking = new ActivityBooking
        {
            OrganizationId = property.OrganizationId,
            PropertyId = propertyId,
            Code = await Purchasing.PurchasingService.NextNumberAsync(db, propertyId, "activity_booking", "AB", ct),
            ScheduleId = session.Id,
            ActivityId = activity.Id,
            ReservationId = stay?.Id,
            GuestId = stay?.PrimaryGuestId,
            GuestName = guestName,
            Seats = (short)r.Seats,
            UnitPrice = session.Price,
            Amount = amount,
            Settlement = settlement,
            ComplimentaryReason = r.Complimentary ? r.ComplimentaryReason!.Trim() : null,
            Note = ReservationService.Clean(r.Note),
        };
        // 4. The charge on the stay's bill (saved first: the booking points at it).
        if (settlement == ActivitySettlements.RoomCharge)
        {
            var folio = await posting.EnsureMainFolioAsync(stay!, ct);
            var item = await posting.ChargeAsync(folio, new FolioItem
            {
                Category = ChargeCategories.Activity,
                Description = FolioPosting.Trim($"{activity.Name}, {ActivityService.Short(session.SessionDate)} {ActivityService.Hm(session, property)} ({booking.Code})"),
                ServiceDate = session.SessionDate,
                Quantity = r.Seats,
                UnitPrice = session.Price,
                Source = FolioItemSources.Activity,
            }, null, ct);
            await db.SaveChangesAsync(ct);
            booking.FolioId = folio.Id;
            booking.FolioItemId = item.Id;
        }
        db.ActivityBookings.Add(booking);
        audit.Reason = $"{booking.Code}: {guestName} × {r.Seats} on {label}" +
                       (settlement == ActivitySettlements.RoomCharge ? $", {ActivityService.Rupees(amount)} on {stay!.Reference}" : $" ({settlement.ToLowerInvariant()})");
        await db.SaveChangesAsync(ct);

        var dto = (await DtosAsync(property, [booking], ct))[0];
        await Idempotency.CompleteAsync(db, orgId, scope, key, 201, dto, booking.Id, ct);
        await tx.CommitAsync(ct);
        return (dto, false);
    }

    // ------------------------------------------------------------------ cancelling

    /// <summary>Cancels one booking before its session starts: the charge comes off the bill and reserved equipment is released.</summary>
    public async Task<ActivityBookingDto> CancelAsync(Guid propertyId, Guid id, CancelActivityBookingRequest r, CancellationToken ct)
    {
        var property = await activities.RequirePropertyAsync(propertyId, ct);
        var sessionId = await db.ActivityBookings.AsNoTracking().Where(b => b.Id == id && b.PropertyId == propertyId).Select(b => (Guid?)b.ScheduleId)
                            .SingleOrDefaultAsync(ct)
                        ?? throw new NotFoundAppException("That activity booking doesn't exist at this property. Reload the page.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var session = await activities.LockSessionAsync(propertyId, sessionId, ct);
        var booking = await db.ActivityBookings.SingleAsync(b => b.Id == id, ct);
        if (r.Version is { } v && booking.Version != v)
            throw new ConflictAppException("CONCURRENCY_CONFLICT", $"Someone else changed {booking.Code} a moment ago. Reload to see it, then try again.");
        var activity = await db.Activities.AsNoTracking().SingleAsync(a => a.Id == session.ActivityId, ct);
        if (booking.Status != ActivityBookingStatuses.Booked)
            throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION", $"{booking.Code} is already cancelled ({booking.CancelReason}).");
        if (clock.GetUtcNow() >= session.StartsAt)
            throw new BusinessRuleAppException("SESSION_STARTED",
                $"{ActivityService.Label(activity, session, property)} has already started, so {booking.Code} can't be cancelled now.");

        var reversed = await RefundAsync(propertyId, [booking], r.Reason.Trim(), ct);
        Cancel(booking, ActivityCancelKinds.Guest, r.Reason, Reversal(reversed, booking.Id));
        await equipment.ReleaseForBookingsAsync([booking.Id], ct);
        audit.Reason = $"{booking.Code} cancelled: {r.Reason.Trim()}" + (reversed.ContainsKey(booking.Id) ? $"; {ActivityService.Rupees(booking.Amount)} reversed" : "");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await DtosAsync(property, [await db.ActivityBookings.AsNoTracking().SingleAsync(b => b.Id == id, ct)], ct))[0];
    }

    /// <summary>
    /// The weather stops a session: the session is cancelled with the reason, and every booking on it is cancelled too.
    /// A booking in <see cref="WeatherCancelRequest.Moves"/> moves to the session named there (same activity, not started,
    /// seats checked) and keeps its charge; every other booking's charge is reversed on the bill.
    /// </summary>
    public async Task<WeatherCancelResultDto> WeatherCancelAsync(Guid propertyId, Guid id, WeatherCancelRequest r, CancellationToken ct)
    {
        var property = await activities.RequirePropertyAsync(propertyId, ct);
        var now = clock.GetUtcNow();
        var reason = r.Reason.Trim();
        var moves = (r.Moves ?? []).ToList();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Lock this session and every session bookings move to, in one fixed order (no deadlocks).
        var lockIds = moves.Select(m => m.ToSessionId).Append(id).Distinct().ToArray();
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM activity_schedules WHERE id = ANY({lockIds}) AND property_id = {propertyId} ORDER BY id FOR UPDATE", ct);
        var session = await db.ActivitySchedules.SingleOrDefaultAsync(s => s.Id == id && s.PropertyId == propertyId, ct)
                      ?? throw new NotFoundAppException("That session doesn't exist at this property. Reload the page.");
        var activity = await db.Activities.AsNoTracking().SingleAsync(a => a.Id == session.ActivityId, ct);
        var label = ActivityService.Label(activity, session, property);
        if (r.Version is { } v && session.Version != v)
            throw new ConflictAppException("CONCURRENCY_CONFLICT", $"{label} changed a moment ago (someone booked or changed it). Reload to see it, then try again.");
        if (session.Status != ActivitySessionStatuses.Scheduled)
            throw new BusinessRuleAppException("SESSION_CANCELLED", $"{label} is already cancelled ({session.CancelReason}).");
        if (now >= session.EndsAt)
            throw new BusinessRuleAppException("SESSION_OVER", $"{label} is over, so it can't be cancelled.");

        var bookings = await db.ActivityBookings.Where(b => b.ScheduleId == id && b.Status == ActivityBookingStatuses.Booked)
            .OrderBy(b => b.Code).ToListAsync(ct);
        var moveTo = new Dictionary<Guid, ActivitySchedule>();
        if (moves.Count > 0)
        {
            var targetIds = moves.Select(m => m.ToSessionId).Distinct().ToList();
            var targets = await db.ActivitySchedules.Where(s => targetIds.Contains(s.Id) && s.PropertyId == propertyId).ToDictionaryAsync(s => s.Id, ct);
            var stayIds = bookings.Select(b => b.ReservationId).OfType<Guid>().Distinct().ToList();
            var stays = await db.Reservations.AsNoTracking().Where(x => stayIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
            foreach (var m in moves)
            {
                var b = bookings.FirstOrDefault(x => x.Id == m.BookingId)
                        ?? throw ReservationService.Field("Moves", "One of the bookings to move isn't booked on this session any more. Reload and choose again.");
                if (moveTo.ContainsKey(b.Id)) throw ReservationService.Field("Moves", $"{b.Code} is listed twice. Choose one session for it.");
                if (!targets.TryGetValue(m.ToSessionId, out var t) || t.Id == session.Id || t.ActivityId != session.ActivityId)
                    throw ReservationService.Field("Moves", $"{b.Code} can only move to another session of {activity.Name}.");
                var tl = ActivityService.Label(activity, t, property);
                if (t.Status != ActivitySessionStatuses.Scheduled)
                    throw ReservationService.Field("Moves", $"{tl} was cancelled too, so {b.Code} can't move there. Choose another session or refund it.");
                if (now >= t.StartsAt)
                    throw ReservationService.Field("Moves", $"{tl} has already started, so {b.Code} can't move there. Choose a later session or refund it.");
                if (b.ReservationId is { } sid && stays.TryGetValue(sid, out var stay) && (t.SessionDate < stay.CheckIn || t.SessionDate > stay.CheckOut))
                    throw ReservationService.Field("Moves",
                        $"{b.GuestName} ({b.Code}) leaves on {ActivityService.Short(stay.CheckOut)}, so they can't move to {tl}. Refund it instead.");
                moveTo[b.Id] = t;
            }
            foreach (var g in moveTo.GroupBy(x => x.Value.Id))
            {
                var t = g.First().Value;
                var need = bookings.Where(b => moveTo.ContainsKey(b.Id) && moveTo[b.Id].Id == t.Id).Sum(b => b.Seats);
                if (need > t.SeatsLeft)
                    throw ReservationService.Field("Moves",
                        $"{ActivityService.Label(activity, t, property)} has {(t.SeatsLeft == 1 ? "1 seat" : $"{t.SeatsLeft} seats")} left and the bookings moving there need {need}. Move fewer there, or refund the rest.");
            }
        }

        // 1. The session itself (saved first: the bookings' trigger changes its row next).
        session.Status = ActivitySessionStatuses.Cancelled;
        session.CancelReason = reason;
        session.CancelledAt = now;
        session.CancelledBy = caller.RequireUserId();
        await db.SaveChangesAsync(ct);

        // 2. Refunds on the bill for everyone who doesn't move.
        var refunding = bookings.Where(b => !moveTo.ContainsKey(b.Id)).ToList();
        var reversed = await RefundAsync(propertyId, refunding, $"Weather: {reason}", ct);

        // 3. Cancel every booking; a moving one points at its new booking, inserted after (the charge is on one live booking at a time).
        var moved = new List<ActivityBooking>();
        foreach (var b in bookings)
        {
            if (moveTo.TryGetValue(b.Id, out var t))
            {
                var copy = new ActivityBooking
                {
                    OrganizationId = b.OrganizationId,
                    PropertyId = b.PropertyId,
                    Code = await Purchasing.PurchasingService.NextNumberAsync(db, propertyId, "activity_booking", "AB", ct),
                    ScheduleId = t.Id,
                    ActivityId = b.ActivityId,
                    ReservationId = b.ReservationId,
                    GuestId = b.GuestId,
                    GuestName = b.GuestName,
                    Seats = b.Seats,
                    UnitPrice = b.UnitPrice,
                    Amount = b.Amount,
                    Settlement = b.Settlement,
                    ComplimentaryReason = b.ComplimentaryReason,
                    FolioId = b.FolioId,
                    FolioItemId = b.FolioItemId,
                    Note = b.Note,
                    MovedFromBookingId = b.Id,
                };
                Cancel(b, ActivityCancelKinds.Weather, reason, null, copy.Id);
                moved.Add(copy);
            }
            else Cancel(b, ActivityCancelKinds.Weather, reason, Reversal(reversed, b.Id));
        }
        await equipment.ReleaseForBookingsAsync(bookings.Select(b => b.Id).ToList(), ct);
        await db.SaveChangesAsync(ct);
        foreach (var c in moved) db.ActivityBookings.Add(c);
        var refundedAmount = refunding.Where(b => reversed.ContainsKey(b.Id)).Sum(b => b.Amount);
        audit.Reason = $"{label} cancelled for the weather: {reason}. {reversed.Count} refunded ({ActivityService.Rupees(refundedAmount)}), {moved.Count} moved.";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var after = await db.ActivityBookings.AsNoTracking().Where(b => b.ScheduleId == id).OrderBy(b => b.Code).ToListAsync(ct);
        var fresh = await db.ActivitySchedules.AsNoTracking().SingleAsync(s => s.Id == id, ct);
        return new WeatherCancelResultDto((await activities.SessionDtosAsync(property, [fresh], ct))[0], reversed.Count, refundedAmount, moved.Count,
            refunding.Count - reversed.Count, await DtosAsync(property, after, ct));
    }

    /// <summary>Reverses the room charge of each booking (under the stays' locks) and saves the reversals. Returns the reversal
    /// line per booking; a booking with nothing on the bill isn't in it. A charge someone already reversed on the folio by
    /// hand isn't reversed twice: that reversal is used.</summary>
    private async Task<Dictionary<Guid, Guid>> RefundAsync(Guid propertyId, IReadOnlyList<ActivityBooking> bookings, string reason, CancellationToken ct)
    {
        var charged = bookings.Where(b => b.Settlement == ActivitySettlements.RoomCharge && b.FolioItemId is not null).ToList();
        var result = new Dictionary<Guid, Guid>();
        if (charged.Count == 0) return result;
        var stayIds = charged.Select(b => b.ReservationId!.Value).Distinct().Order().ToArray();
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM reservations WHERE id = ANY({stayIds}) AND property_id = {propertyId} ORDER BY id FOR UPDATE", ct);
        var itemIds = charged.Select(b => b.FolioItemId!.Value).ToList();
        var items = await db.FolioItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var already = await db.FolioItems.AsNoTracking().Where(i => i.ReversesItemId != null && itemIds.Contains(i.ReversesItemId.Value))
            .ToDictionaryAsync(i => i.ReversesItemId!.Value, i => i.Id, ct);
        var folioIds = items.Values.Select(i => i.FolioId).Distinct().ToList();
        var folios = await db.Folios.Where(f => folioIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);
        foreach (var b in charged)
        {
            var charge = items[b.FolioItemId!.Value];
            if (already.TryGetValue(charge.Id, out var existing)) { result[b.Id] = existing; continue; }
            var reversal = await posting.ReverseAsync(folios[charge.FolioId], charge, FolioPosting.Trim($"{b.Code}: {reason}"), ct);
            result[b.Id] = reversal.Id;
        }
        await db.SaveChangesAsync(ct);
        return result;
    }

    private static Guid? Reversal(Dictionary<Guid, Guid> reversed, Guid bookingId) => reversed.TryGetValue(bookingId, out var id) ? id : null;

    private void Cancel(ActivityBooking b, string kind, string reason, Guid? reversal, Guid? movedTo = null)
    {
        b.Status = ActivityBookingStatuses.Cancelled;
        b.CancelKind = kind;
        b.CancelReason = reason.Trim();
        b.CancelledAt = clock.GetUtcNow();
        b.CancelledBy = caller.RequireUserId();
        b.ReversalItemId = reversal;
        b.MovedToBookingId = movedTo;
        b.Refund = movedTo is not null ? ActivityRefunds.Moved : reversal is not null ? ActivityRefunds.Reversed : ActivityRefunds.None;
    }

    private async Task<Reservation> LockStayAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM reservations WHERE id = {id} AND property_id = {propertyId} FOR UPDATE", ct);
        return await db.Reservations.SingleOrDefaultAsync(x => x.Id == id && x.PropertyId == propertyId, ct)
               ?? throw ReservationService.Field("ReservationId", "That stay isn't at this property. Reload and choose the guest again.");
    }

    // ------------------------------------------------------------------ reading

    /// <summary>Bookings of a stay (the reservation page and the equipment dialog use it).</summary>
    public async Task<IReadOnlyList<ActivityBookingDto>> ForStayAsync(Guid propertyId, Guid reservationId, CancellationToken ct)
    {
        var property = await activities.RequirePropertyAsync(propertyId, ct);
        var list = await db.ActivityBookings.AsNoTracking().Where(b => b.PropertyId == propertyId && b.ReservationId == reservationId)
            .OrderByDescending(b => b.CreatedAt).Take(100).ToListAsync(ct);
        return await DtosAsync(property, list, ct);
    }

    public async Task<ActivityBookingDto> GetAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        var property = await activities.RequirePropertyAsync(propertyId, ct);
        var b = await db.ActivityBookings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.PropertyId == propertyId, ct)
                ?? throw new NotFoundAppException("That activity booking doesn't exist at this property. Reload the page.");
        return (await DtosAsync(property, [b], ct))[0];
    }

    internal async Task<List<ActivityBookingDto>> DtosAsync(Property property, IReadOnlyCollection<ActivityBooking> list, CancellationToken ct)
    {
        if (list.Count == 0) return [];
        var sessionIds = list.Select(b => b.ScheduleId).Distinct().ToList();
        var sessions = await db.ActivitySchedules.AsNoTracking().Where(s => sessionIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var activityIds = list.Select(b => b.ActivityId).Distinct().ToList();
        var acts = await db.Activities.AsNoTracking().Where(a => activityIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var stayIds = list.Select(b => b.ReservationId).OfType<Guid>().Distinct().ToList();
        var refs = await db.Reservations.AsNoTracking().Where(r => stayIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Reference, ct);
        var rooms = await activities.RoomsTextAsync(stayIds, ct);
        var folioIds = list.Select(b => b.FolioId).OfType<Guid>().Distinct().ToList();
        var folios = await db.Folios.AsNoTracking().Where(f => folioIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Number, ct);
        var linked = list.SelectMany(b => new[] { b.MovedToBookingId, b.MovedFromBookingId }).OfType<Guid>().Distinct().ToList();
        var codes = await db.ActivityBookings.AsNoTracking().Where(b => linked.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Code, ct);
        var users = await activities.UserNamesAsync(list.Select(b => b.CreatedBy), ct);
        var kit = await equipment.ForBookingsAsync(list.Select(b => b.Id).ToList(), ct);
        var a = await access.GetAsync(ct);
        var canCancel = a.Has(Perm.ActivityBookingCreate, property.Id);
        var canIssue = a.Has(Perm.EquipmentIssue, property.Id);
        var now = clock.GetUtcNow();
        var tz = ActivityService.Tz(property);
        return list.Select(b =>
        {
            var s = sessions[b.ScheduleId];
            var actions = new List<string>();
            if (b.Status == ActivityBookingStatuses.Booked && now < s.StartsAt && canCancel) actions.Add("cancel");
            if (b.Status == ActivityBookingStatuses.Booked && now < s.EndsAt && canIssue) actions.Add("equipment");
            return new ActivityBookingDto(b.Id, b.Code, b.ScheduleId, acts[b.ActivityId], s.SessionDate, ActivityService.Hm(s.StartsAt, tz), b.ReservationId,
                b.ReservationId is { } r ? refs.GetValueOrDefault(r) : null, b.ReservationId is { } r2 ? rooms.GetValueOrDefault(r2) : null, b.GuestName, b.Seats,
                b.UnitPrice, b.Amount, b.Settlement, b.ComplimentaryReason, b.FolioId is { } f ? folios.GetValueOrDefault(f) : null, b.Note, b.Status,
                b.CancelKind, b.CancelReason, b.CancelledAt, b.Refund, b.MovedToBookingId is { } mt ? codes.GetValueOrDefault(mt) : null,
                b.MovedFromBookingId is { } mf ? codes.GetValueOrDefault(mf) : null, kit.GetValueOrDefault(b.Id) ?? [], actions, b.CreatedAt,
                b.CreatedBy is { } u ? users.GetValueOrDefault(u) : null, b.Version);
        }).ToList();
    }
}
