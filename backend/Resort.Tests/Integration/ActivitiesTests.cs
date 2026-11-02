using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Resort.Contracts;
using Resort.Domain.Employees;
using Resort.Tests.Support;
using static Resort.Tests.Support.BookingKit;

namespace Resort.Tests.Integration;

/// <summary>Sprint 20 — activities and equipment: kayaking at 7am with 6 seats can't be oversold (also when two people book
/// the last seat at the same moment, and when SQL skips the service), the price goes on the guest's bill and comes off when
/// the booking is cancelled, the weather cancels a session (refunds and moves), and life jackets are issued for a booking
/// and come back, one damaged. Each test uses a property of its own; sessions are tomorrow (real clock) unless the clock
/// is pinned. Real API, real PostgreSQL.</summary>
public class ActivitiesTests(FrontDeskFactory app) : IClassFixture<FrontDeskFactory>
{
    private static readonly TimeZoneInfo Ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
    private static DateOnly Tomorrow => Today.AddDays(1);

    // ------------------------------------------------------------------ acceptance: kayaking at 7am with 6 seats can't be oversold

    [Fact]
    public async Task Kayaking_at_7am_with_6_seats_cannot_be_oversold_and_the_price_goes_on_the_guests_bill()
    {
        var s = await SetupAsync();
        var kayak = await SessionAsync(s, s.Kayaking, Tomorrow, 7);
        Assert.Equal((6, 0, 6, 800m, "07:00", "08:30"), (kayak.Capacity, kayak.SeatsBooked, kayak.SeatsLeft, kayak.Price, kayak.StartTime, kayak.EndTime));
        Assert.Contains("book", kayak.Actions);

        // Four for the Raos (on their bill), two for the Menons.
        var raos = await BookOkAsync(s.Desk, s, new BookActivityRequest(kayak.Id, 4, s.Stay1.Id, null));
        Assert.Equal(("AB00001", "ROOM_CHARGE", 3200m, "BOOKED", "Asha Rao"), (raos.Code, raos.Settlement, raos.Amount, raos.Status, raos.GuestName));
        Assert.NotNull(raos.FolioNumber);
        Assert.Equal(["cancel", "equipment"], raos.Actions);
        var menons = await BookOkAsync(s.Desk, s, new BookActivityRequest(kayak.Id, 2, s.Stay2.Id, null));
        Assert.Equal(6L, await ScalarAsync($"SELECT seats_booked::bigint FROM activity_schedules WHERE id = '{kayak.Id}'"));

        // Full: the seventh is refused with the reason, and the day board shows no seats left and no "book".
        var seventh = await Book(s.Desk, s, new BookActivityRequest(kayak.Id, 1, s.Stay1.Id, null));
        Assert.Equal(HttpStatusCode.Conflict, seventh.StatusCode);
        Assert.Equal(("ACTIVITY_FULL", $"Kayaking at 07:00 on {Short(Tomorrow)} is full (6 of 6 seats booked). Choose another session."),
            await FrontDeskTests.ProblemAsync(seventh));
        var day = await OkAsync<ActivityDayDto>(await s.Desk.GetAsync($"{Url(s.Pid)}/activities/sessions?date={Tomorrow:yyyy-MM-dd}"));
        var row = day.Sessions.Single(x => x.Id == kayak.Id);
        Assert.Equal((6, 0), (row.SeatsBooked, row.SeatsLeft));
        Assert.DoesNotContain("book", row.Actions);

        // Fewer seats than asked: says how many are left.
        await OkAsync<ActivityBookingDto>(await s.Desk.PostAsync($"{Url(s.Pid)}/activities/bookings/{menons.Id}/cancel",
            new CancelActivityBookingRequest("Guest changed plans", menons.Version)));
        var three = await Book(s.Desk, s, new BookActivityRequest(kayak.Id, 3, s.Stay2.Id, null));
        Assert.Equal($"Kayaking at 07:00 on {Short(Tomorrow)} has 2 seats left and this booking is for 3. Book fewer seats or choose another session.",
            (await FrontDeskTests.ProblemAsync(three)).Message);

        // The bill: one ACTIVITY line of 4 × ₹800 for the Raos; the Menons' line and its reversal cancel out.
        Assert.Equal(3200m, await ScalarAsync($"SELECT sum(total) FROM folio_items WHERE reservation_id = '{s.Stay1.Id}' AND source = 'ACTIVITY'"));
        Assert.Equal($"Kayaking, {Short(Tomorrow)} 07:00 (AB00001)",
            await ScalarAsync($"SELECT description FROM folio_items WHERE reservation_id = '{s.Stay1.Id}' AND source = 'ACTIVITY'"));
        Assert.Equal((4m, 800m, "ACTIVITY"), await FirstAsync(
            $"SELECT quantity, unit_price, category FROM folio_items WHERE reservation_id = '{s.Stay1.Id}' AND source = 'ACTIVITY'"));
        Assert.Equal(0m, await ScalarAsync($"SELECT sum(total) FROM folio_items WHERE reservation_id = '{s.Stay2.Id}' AND source = 'ACTIVITY'"));
    }

    // ------------------------------------------------------------------ required: last kayak seat booked twice → one wins

    [Fact]
    public async Task The_last_kayak_seat_booked_twice_at_the_same_moment_goes_to_exactly_one_of_them_10_times()
    {
        var s = await SetupAsync();
        for (var round = 0; round < 10; round++)
        {
            var kayak = await SessionAsync(s, s.Kayaking, Tomorrow, 6 + round);
            await BookOkAsync(s.Desk, s, new BookActivityRequest(kayak.Id, 5, s.Stay1.Id, null));
            var gate = new Barrier(2);
            var tasks = new[] { (s.Desk, s.Stay1.Id), (s.Manager, s.Stay2.Id) }.Select(x => Task.Run(async () =>
            {
                gate.SignalAndWait();
                return await Book(x.Item1, s, new BookActivityRequest(kayak.Id, 1, x.Item2, null));
            })).ToArray();
            var results = await Task.WhenAll(tasks);
            Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], results.Select(r => r.StatusCode).Order());
            Assert.Equal(6L, await ScalarAsync($"SELECT seats_booked::bigint FROM activity_schedules WHERE id = '{kayak.Id}'"));
        }
        // seats_booked is always the bookings' sum, and never above the seats.
        Assert.Equal(0L, await ScalarAsync($"""
            SELECT count(*) FROM activity_schedules s
            WHERE s.property_id = '{s.Pid}' AND (s.seats_booked > s.capacity
               OR s.seats_booked <> (SELECT COALESCE(sum(seats), 0) FROM activity_bookings b WHERE b.schedule_id = s.id AND b.status = 'BOOKED'))
            """));
    }

    [Fact]
    public async Task The_database_refuses_a_booking_over_the_seats_or_on_a_cancelled_session_even_without_the_service()
    {
        var s = await SetupAsync();
        var kayak = await SessionAsync(s, s.Kayaking, Tomorrow, 7);
        var b = await BookOkAsync(s.Desk, s, new BookActivityRequest(kayak.Id, 6, s.Stay1.Id, null));
        string Insert(Guid session, string code) => $"""
            INSERT INTO activity_bookings (organization_id, property_id, code, schedule_id, activity_id, guest_name, seats, unit_price, amount, settlement,
                complimentary_reason)
            SELECT organization_id, property_id, '{code}', id, activity_id, 'Sneaky', 1, 0, 0, 'COMPLIMENTARY', 'Test'
            FROM activity_schedules WHERE id = '{session}'
            """;
        var full = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(Insert(kayak.Id, "AB09999")));
        Assert.Equal(("23514", "ck_activity_schedules_capacity"), (full.SqlState, full.ConstraintName));

        await OkAsync<WeatherCancelResultDto>(await s.Manager.PostAsync($"{Url(s.Pid)}/activities/sessions/{kayak.Id}/weather-cancel",
            new WeatherCancelRequest("Rough sea, red flag")));
        Assert.Equal(0L, await ScalarAsync($"SELECT seats_booked::bigint FROM activity_schedules WHERE id = '{kayak.Id}'"));
        var closed = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(Insert(kayak.Id, "AB09998")));
        Assert.Equal("ck_activity_bookings_session_open", closed.ConstraintName);
        // A booking keeps its session and seats; a cancelled one stays cancelled.
        var moved = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"UPDATE activity_bookings SET status = 'BOOKED', cancel_kind = NULL, cancel_reason = NULL, cancelled_at = NULL, refund = NULL, reversal_item_id = NULL WHERE id = '{b.Id}'"));
        Assert.Equal("ck_activity_bookings_fixed", moved.ConstraintName);
    }

    // ------------------------------------------------------------------ cancelling and the weather

    [Fact]
    public async Task A_booking_cancelled_before_the_start_comes_off_the_bill_and_after_the_start_it_cannot_be_cancelled()
    {
        var s = await SetupAsync();
        var kayak = await SessionAsync(s, s.Kayaking, Tomorrow, 7);
        var b = await BookOkAsync(s.Desk, s, new BookActivityRequest(kayak.Id, 2, s.Stay1.Id, null, Note: "Beginners"));
        Assert.Equal("Say why it is cancelled (3–300 characters), e.g. Guest changed plans.",
            await FieldErrorAsync(await s.Desk.PostAsync($"{Url(s.Pid)}/activities/bookings/{b.Id}/cancel", new CancelActivityBookingRequest(" ")), "Reason"));
        var cancelled = await OkAsync<ActivityBookingDto>(await s.Desk.PostAsync($"{Url(s.Pid)}/activities/bookings/{b.Id}/cancel",
            new CancelActivityBookingRequest("Guest changed plans", b.Version)));
        Assert.Equal(("CANCELLED", "GUEST", "REVERSED"), (cancelled.Status, cancelled.CancelKind, cancelled.Refund));
        Assert.Empty(cancelled.Actions);
        Assert.Equal(0m, await ScalarAsync($"SELECT sum(total) FROM folio_items WHERE reservation_id = '{s.Stay1.Id}' AND source = 'ACTIVITY'"));
        Assert.Equal($"AB00001: Guest changed plans", await ScalarAsync($"SELECT reason FROM folio_items WHERE reservation_id = '{s.Stay1.Id}' AND item_type = 'REVERSAL'"));
        var again = await s.Desk.PostAsync($"{Url(s.Pid)}/activities/bookings/{b.Id}/cancel", new CancelActivityBookingRequest("Twice"));
        Assert.Equal(("INVALID_STATUS_TRANSITION", "AB00001 is already cancelled (Guest changed plans)."), await FrontDeskTests.ProblemAsync(again));

        // Once it has started: no booking, no cancelling.
        var other = await BookOkAsync(s.Desk, s, new BookActivityRequest(kayak.Id, 1, s.Stay2.Id, null));
        Pin(Tomorrow, 7, 5);
        var late = await s.Desk.PostAsync($"{Url(s.Pid)}/activities/bookings/{other.Id}/cancel", new CancelActivityBookingRequest("Overslept"));
        Assert.Equal(("SESSION_STARTED", $"Kayaking at 07:00 on {Short(Tomorrow)} has already started, so AB00002 can't be cancelled now."),
            await FrontDeskTests.ProblemAsync(late));
        var tooLate = await Book(s.Desk, s, new BookActivityRequest(kayak.Id, 1, s.Stay2.Id, null));
        Assert.Equal("SESSION_STARTED", (await FrontDeskTests.ProblemAsync(tooLate)).Code);
        app.Clock.At = null;
    }

    [Fact]
    public async Task The_weather_cancels_a_session_charges_are_refunded_on_the_bill_or_moved_with_the_booking_and_reserved_kit_is_released()
    {
        var s = await SetupAsync();
        var seven = await SessionAsync(s, s.Kayaking, Tomorrow, 7);
        var nine = await SessionAsync(s, s.Kayaking, Tomorrow, 9);
        var raos = await BookOkAsync(s.Desk, s, new BookActivityRequest(seven.Id, 2, s.Stay1.Id, null));
        var menons = await BookOkAsync(s.Desk, s, new BookActivityRequest(seven.Id, 3, s.Stay2.Id, null));
        var comp = await BookOkAsync(s.Desk, s, new BookActivityRequest(seven.Id, 1, null, "Chef Ravi", Complimentary: true, ComplimentaryReason: "Staff guide"));
        Assert.Equal(("COMPLIMENTARY", 800m), (comp.Settlement, comp.Amount));
        await BookOkAsync(s.Desk, s, new BookActivityRequest(nine.Id, 4, s.Stay1.Id, null));    // 2 seats left at nine
        // Life jackets put aside for the Raos.
        await IssueOkAsync(s, new IssueEquipmentRequest(raos.Id, null, null, null, s.LifeJacket, 2, ReserveOnly: true));
        Assert.Equal(2L, await ScalarAsync($"SELECT count(*) FROM equipment WHERE property_id = '{s.Pid}' AND status = 'RESERVED'"));

        // The desk can't cancel a session; the moves are checked first (the Menons need 3 seats at nine, there are 2).
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Desk.PostAsync($"{Url(s.Pid)}/activities/sessions/{seven.Id}/weather-cancel",
            new WeatherCancelRequest("Storm"))).StatusCode);
        var detail = await OkAsync<ActivitySessionDetailDto>(await s.Manager.GetAsync($"{Url(s.Pid)}/activities/sessions/{seven.Id}"));
        Assert.Equal([nine.Id], detail.MoveTargets.Select(t => t.Id));
        Assert.Equal(["AB00001", "AB00002", "AB00003"], detail.Bookings.Select(b => b.Code));
        Assert.Equal($"Kayaking at 09:00 on {Short(Tomorrow)} has 2 seats left and the bookings moving there need 3. Move fewer there, or refund the rest.",
            await FieldErrorAsync(await s.Manager.PostAsync($"{Url(s.Pid)}/activities/sessions/{seven.Id}/weather-cancel",
                new WeatherCancelRequest("Rough sea, red flag", [new MoveBookingRequest(menons.Id, nine.Id)])), "Moves"));

        // The Raos move to nine with their charge; the Menons are refunded; the complimentary one had nothing to refund.
        var result = await OkAsync<WeatherCancelResultDto>(await s.Manager.PostAsync($"{Url(s.Pid)}/activities/sessions/{seven.Id}/weather-cancel",
            new WeatherCancelRequest("Rough sea, red flag", [new MoveBookingRequest(raos.Id, nine.Id)], detail.Session.Version)));
        Assert.Equal(("CANCELLED", "Rough sea, red flag", 0), (result.Session.Status, result.Session.CancelReason, result.Session.SeatsBooked));
        Assert.Equal((1, 2400m, 1, 1), (result.Refunded, result.AmountRefunded, result.Moved, result.NothingToRefund));
        Assert.Equal([("AB00001", "MOVED", "AB00005"), ("AB00002", "REVERSED", null), ("AB00003", "NONE", null)],
            result.Bookings.Select(b => (b.Code, b.Refund!, b.MovedToCode)));
        Assert.All(result.Bookings, b => Assert.Equal(("CANCELLED", "WEATHER"), (b.Status, b.CancelKind!)));

        var moved = await OkAsync<ActivityBookingDto>(await s.Desk.GetAsync($"{Url(s.Pid)}/activities/bookings/{result.Bookings[0].Id}"));
        var atNine = (await OkAsync<ActivitySessionDetailDto>(await s.Desk.GetAsync($"{Url(s.Pid)}/activities/sessions/{nine.Id}"))).Bookings;
        var copy = atNine.Single(b => b.Code == "AB00005");
        Assert.Equal(("BOOKED", "AB00001", 2, 1600m, "09:00"), (copy.Status, copy.MovedFromCode!, copy.Seats, copy.Amount, copy.StartTime));
        Assert.Equal("CANCELLED", moved.Status);
        Assert.Equal(6L, await ScalarAsync($"SELECT seats_booked::bigint FROM activity_schedules WHERE id = '{nine.Id}'"));

        // The bill: the Raos keep one ₹1,600 line (now for the 9 o'clock); the Menons' ₹2,400 is reversed.
        Assert.Equal(1600m, await ScalarAsync($"SELECT sum(total) FROM folio_items WHERE reservation_id = '{s.Stay1.Id}' AND source = 'ACTIVITY' AND description LIKE '%AB00001%'"));
        Assert.Equal(0m, await ScalarAsync($"SELECT sum(total) FROM folio_items WHERE reservation_id = '{s.Stay2.Id}' AND source = 'ACTIVITY'"));
        Assert.Equal("AB00002: Weather: Rough sea, red flag", await ScalarAsync($"SELECT reason FROM folio_items WHERE reservation_id = '{s.Stay2.Id}' AND item_type = 'REVERSAL'"));
        // The reserved life jackets are back on the shelf; nothing more can be booked on the cancelled session.
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM equipment WHERE property_id = '{s.Pid}' AND status = 'RESERVED'"));
        var onCancelled = await Book(s.Desk, s, new BookActivityRequest(seven.Id, 1, s.Stay1.Id, null));
        Assert.Equal(("SESSION_CANCELLED", $"Kayaking at 07:00 on {Short(Tomorrow)} was cancelled (Rough sea, red flag). Choose another session."),
            await FrontDeskTests.ProblemAsync(onCancelled));
        var twice = await s.Manager.PostAsync($"{Url(s.Pid)}/activities/sessions/{seven.Id}/weather-cancel", new WeatherCancelRequest("Again"));
        Assert.Equal("SESSION_CANCELLED", (await FrontDeskTests.ProblemAsync(twice)).Code);
    }

    // ------------------------------------------------------------------ equipment: the review demo

    [Fact]
    public async Task Life_jackets_are_issued_for_a_booking_and_one_comes_back_damaged_then_goes_through_maintenance()
    {
        var s = await SetupAsync();
        var kayak = await SessionAsync(s, s.Kayaking, Tomorrow, 7);
        var b = await BookOkAsync(s.Desk, s, new BookActivityRequest(kayak.Id, 2, s.Stay1.Id, null));

        // Two life jackets for the booking: the first two available by tag, due back when the session ends.
        var issued = await IssueOkAsync(s, new IssueEquipmentRequest(b.Id, null, null, null, s.LifeJacket, 2));
        Assert.Equal([("LJ-01", "ISSUED", "Asha Rao", "AB00001"), ("LJ-02", "ISSUED", "Asha Rao", "AB00001")],
            issued.Select(x => (x.Tag, x.Status, x.IssuedTo, x.BookingCode!)));
        Assert.All(issued, x => Assert.Equal(kayak.EndsAt, x.DueBackAt));
        var booking = await OkAsync<ActivityBookingDto>(await s.Desk.GetAsync($"{Url(s.Pid)}/activities/bookings/{b.Id}"));
        Assert.Equal(["LJ-01", "LJ-02"], booking.Equipment.Select(x => x.Tag));

        // The same jacket can't go to someone else; asking for more than are free says how many are.
        var lj1 = issued[0].EquipmentId;
        var clash = await s.Desk.PostAsync($"{Url(s.Pid)}/equipment/issue", new IssueEquipmentRequest(null, null, "Walk-in", [lj1]));
        Assert.Equal(("EQUIPMENT_UNAVAILABLE", "LJ-01 is issued (Asha Rao). Choose other items."), await FrontDeskTests.ProblemAsync(clash));
        var tooMany = await s.Desk.PostAsync($"{Url(s.Pid)}/equipment/issue", new IssueEquipmentRequest(null, null, "Walk-in", null, s.LifeJacket, 3));
        Assert.Equal("Only 2 of life jacket are available and 3 were asked for.", (await FrontDeskTests.ProblemAsync(tooMany)).Message);

        // Back: LJ-01 fine, LJ-02 damaged (the note is required).
        await OkAsync<EquipmentAssignmentDto>(await s.Desk.PostAsync($"{Url(s.Pid)}/equipment/assignments/{issued[0].Id}/return",
            new ReturnEquipmentRequest("GOOD", null, issued[0].Version)));
        Assert.Equal("Say what happened (3–300 characters), e.g. Buckle broken.",
            await FieldErrorAsync(await s.Desk.PostAsync($"{Url(s.Pid)}/equipment/assignments/{issued[1].Id}/return", new ReturnEquipmentRequest("DAMAGED", null)), "Note"));
        var damaged = await OkAsync<EquipmentAssignmentDto>(await s.Desk.PostAsync($"{Url(s.Pid)}/equipment/assignments/{issued[1].Id}/return",
            new ReturnEquipmentRequest("DAMAGED", "Strap torn")));
        Assert.Equal(("RETURNED", "DAMAGED", "Strap torn"), (damaged.Status, damaged.ReturnCondition!, damaged.ReturnNote!));
        var again = await s.Desk.PostAsync($"{Url(s.Pid)}/equipment/assignments/{issued[1].Id}/return", new ReturnEquipmentRequest("GOOD", null));
        Assert.Equal(("INVALID_STATUS_TRANSITION", "LJ-02 was already returned by Asha Rao."), await FrontDeskTests.ProblemAsync(again));

        var board = await OkAsync<EquipmentBoardDto>(await s.Desk.GetAsync($"{Url(s.Pid)}/equipment"));
        var lj = board.Types.Single(t => t.Code == "LJ");
        Assert.Equal((4, 3, 1), (lj.Total, lj.Available, lj.ByStatus["DAMAGED"]));
        Assert.Empty(board.Out);
        var lj2 = board.Items.Single(i => i.Tag == "LJ-02");
        Assert.Equal(("DAMAGED", "Strap torn"), (lj2.Status, lj2.StatusNote!));
        Assert.Equal(["MAINTENANCE", "AVAILABLE", "RETIRED", "LOST"], lj2.Moves);

        // Damaged can't be issued; the desk can't change statuses; a manager sends it to maintenance and back.
        var broken = await s.Desk.PostAsync($"{Url(s.Pid)}/equipment/issue", new IssueEquipmentRequest(null, null, "Walk-in", [lj2.Id]));
        Assert.Equal("LJ-02 is damaged. Choose other items.", (await FrontDeskTests.ProblemAsync(broken)).Message);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Desk.PostAsync($"{Url(s.Pid)}/equipment/items/{lj2.Id}/status",
            new ChangeEquipmentStatusRequest("MAINTENANCE", "Strap to the tailor"))).StatusCode);
        var m = await OkAsync<EquipmentItemDto>(await s.Manager.PostAsync($"{Url(s.Pid)}/equipment/items/{lj2.Id}/status",
            new ChangeEquipmentStatusRequest("MAINTENANCE", "Strap to the tailor", lj2.Version)));
        Assert.Equal("MAINTENANCE", m.Status);
        Assert.Equal("LJ-02 is in maintenance; it can be set to available, damaged, retired.",
            await FieldErrorAsync(await s.Manager.PostAsync($"{Url(s.Pid)}/equipment/items/{lj2.Id}/status", new ChangeEquipmentStatusRequest("LOST", "Gone")), "Status"));
        var back = await OkAsync<EquipmentItemDto>(await s.Manager.PostAsync($"{Url(s.Pid)}/equipment/items/{lj2.Id}/status",
            new ChangeEquipmentStatusRequest("AVAILABLE", null)));
        Assert.Equal(("AVAILABLE", null), (back.Status, back.StatusNote));
        var history = await OkAsync<EquipmentItemHistoryDto>(await s.Desk.GetAsync($"{Url(s.Pid)}/equipment/items/{lj2.Id}"));
        Assert.Equal([("RETURNED", "DAMAGED")], history.Assignments.Select(x => (x.Status, x.ReturnCondition!)));

        // An issued item changes only by being returned; the database refuses an impossible move and a second holder.
        var out1 = (await IssueOkAsync(s, new IssueEquipmentRequest(null, null, "Walk-in", [lj1])))[0];
        var held = await s.Manager.PostAsync($"{Url(s.Pid)}/equipment/items/{lj1}/status", new ChangeEquipmentStatusRequest("LOST", "Not found"));
        Assert.Equal(("EQUIPMENT_OUT", "LJ-01 is issued to Walk-in. Take it back first (Return, and say if it is damaged or lost)."),
            await FrontDeskTests.ProblemAsync(held));
        var lost = await OkAsync<EquipmentAssignmentDto>(await s.Desk.PostAsync($"{Url(s.Pid)}/equipment/assignments/{out1.Id}/return",
            new ReturnEquipmentRequest("LOST", "Dropped in the sea")));
        Assert.Equal(("LOST", "LOST"), (lost.Status, lost.ReturnCondition!));
        var jump = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"UPDATE equipment SET status = 'ISSUED' WHERE id = '{lj1}'"));
        Assert.Equal("ck_equipment_status_transition", jump.ConstraintName);
        var twoHolders = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"""
            INSERT INTO equipment_assignments (organization_id, property_id, equipment_id, issued_to, status, issued_at)
            SELECT organization_id, property_id, id, 'One', 'ISSUED', now() FROM equipment WHERE property_id = '{s.Pid}' AND tag = 'LJ-03';
            INSERT INTO equipment_assignments (organization_id, property_id, equipment_id, issued_to, status, issued_at)
            SELECT organization_id, property_id, id, 'Two', 'ISSUED', now() FROM equipment WHERE property_id = '{s.Pid}' AND tag = 'LJ-03';
            """));
        Assert.Equal("ux_equipment_assignments_open", twoHolders.ConstraintName);
    }

    // ------------------------------------------------------------------ who can book what, and the reasons

    [Fact]
    public async Task Bookings_need_a_stay_for_a_paid_session_the_stays_dates_and_permission_and_a_retry_replays()
    {
        var s = await SetupAsync();
        var kayak = await SessionAsync(s, s.Kayaking, Tomorrow, 7);
        var late = await SessionAsync(s, s.Kayaking, Today.AddDays(5), 7);
        var yoga = await SessionAsync(s, s.Yoga, Tomorrow, 6);
        Assert.Equal(0m, yoga.Price);

        Assert.Equal($"Kayaking costs ₹800 a person. Choose the guest's stay so it goes on their bill, or make it complimentary with the reason.",
            await FieldErrorAsync(await Book(s.Desk, s, new BookActivityRequest(kayak.Id, 1, null, "Day visitor")), "ReservationId"));
        Assert.Equal($"{s.Stay1.Reference} stays from {Short(Today)} to {Short(Today.AddDays(3))}, so it can't be booked on {Short(Today.AddDays(5))}.",
            await FieldErrorAsync(await Book(s.Desk, s, new BookActivityRequest(late.Id, 1, s.Stay1.Id, null)), "ReservationId"));
        Assert.Equal("Say why it is complimentary (3–200 characters), e.g. Honeymoon welcome.",
            await FieldErrorAsync(await Book(s.Desk, s, new BookActivityRequest(kayak.Id, 1, null, "Day visitor", Complimentary: true)), "ComplimentaryReason"));
        Assert.Equal("Book 1–50 seats.", await FieldErrorAsync(await Book(s.Desk, s, new BookActivityRequest(kayak.Id, 0, s.Stay1.Id, null)), "Seats"));
        var noKey = await s.Desk.PostAsync($"{Url(s.Pid)}/activities/bookings", new BookActivityRequest(kayak.Id, 1, s.Stay1.Id, null));
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);

        // Free yoga for a visitor by name; a housekeeper can't book.
        var visitor = await BookOkAsync(s.Desk, s, new BookActivityRequest(yoga.Id, 1, null, "Day visitor"));
        Assert.Equal(("FREE", 0m, null), (visitor.Settlement, visitor.Amount, visitor.FolioNumber));
        Assert.Equal(HttpStatusCode.Forbidden, (await Book(s.Housekeeper, s, new BookActivityRequest(yoga.Id, 1, null, "Me"))).StatusCode);

        // A retried click: the same key replays the first booking instead of booking twice.
        var key = "act-" + Guid.NewGuid();
        var first = await BookOkAsync(s.Desk, s, new BookActivityRequest(kayak.Id, 2, s.Stay1.Id, null), key);
        var retry = await Book(s.Desk, s, new BookActivityRequest(kayak.Id, 2, s.Stay1.Id, null), key);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.True(retry.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(first.Id, (await retry.Content.ReadFromJsonAsync<ActivityBookingDto>())!.Id);
        Assert.Equal(2L, await ScalarAsync($"SELECT seats_booked::bigint FROM activity_schedules WHERE id = '{kayak.Id}'"));

        // Stays offered for tomorrow: both checked-in stays.
        var stays = await OkAsync<List<ActivityStayDto>>(await s.Desk.GetAsync($"{Url(s.Pid)}/activities/stays?date={Tomorrow:yyyy-MM-dd}"));
        Assert.Equal([s.Stay1.Reference, s.Stay2.Reference], stays.Select(x => x.Reference).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Sessions_are_put_on_for_a_range_skipping_taken_times_and_their_seats_never_go_below_what_is_booked()
    {
        var s = await SetupAsync();
        var first = await OkAsync<CreateSessionsResultDto>(await s.Manager.PostAsync($"{Url(s.Pid)}/activities/sessions",
            new CreateSessionsRequest(s.Kayaking, Tomorrow, Tomorrow.AddDays(2), [new TimeOnly(7, 0), new TimeOnly(16, 0)])));
        Assert.Equal((6, 0), (first.Created, first.Skipped.Count));
        var again = await OkAsync<CreateSessionsResultDto>(await s.Manager.PostAsync($"{Url(s.Pid)}/activities/sessions",
            new CreateSessionsRequest(s.Kayaking, Tomorrow, Tomorrow, [new TimeOnly(7, 0), new TimeOnly(10, 0)], 4, 900m)));
        Assert.Equal(1, again.Created);
        Assert.Equal([$"{Short(Tomorrow)} 07:00: already on"], again.Skipped);
        Assert.Equal($"Sessions can start from today ({Day(Today)}).",
            await FieldErrorAsync(await s.Manager.PostAsync($"{Url(s.Pid)}/activities/sessions",
                new CreateSessionsRequest(s.Kayaking, Today.AddDays(-1), Today, [new TimeOnly(7, 0)])), "FromDate"));
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Desk.PostAsync($"{Url(s.Pid)}/activities/sessions",
            new CreateSessionsRequest(s.Kayaking, Tomorrow, Tomorrow, [new TimeOnly(8, 0)]))).StatusCode);

        var day = await OkAsync<ActivityDayDto>(await s.Desk.GetAsync($"{Url(s.Pid)}/activities/sessions?date={Tomorrow:yyyy-MM-dd}"));
        Assert.Equal([("07:00", 6, 800m), ("10:00", 4, 900m), ("16:00", 6, 800m)], day.Sessions.Select(x => (x.StartTime, x.Capacity, x.Price)));
        var seven = day.Sessions[0];
        await BookOkAsync(s.Desk, s, new BookActivityRequest(seven.Id, 3, s.Stay1.Id, null));
        Assert.Equal("3 seats are already booked, so it can't have fewer than 3. Cancel bookings first to make it smaller.",
            await FieldErrorAsync(await s.Manager.PutAsync($"{Url(s.Pid)}/activities/sessions/{seven.Id}", new UpdateSessionRequest(2, 800m, null)), "Capacity"));
        var bigger = await OkAsync<ActivitySessionDto>(await s.Manager.PutAsync($"{Url(s.Pid)}/activities/sessions/{seven.Id}",
            new UpdateSessionRequest(8, 850m, "Two more kayaks hired")));
        Assert.Equal((8, 3, 5, 850m), (bigger.Capacity, bigger.SeatsBooked, bigger.SeatsLeft, bigger.Price));

        // Activities: codes and names are unique, with the reason under the field.
        Assert.Equal("Kayaking already has the code KAYAK. Choose another code.",
            await FieldErrorAsync(await s.Owner.PostAsync($"{Url(s.Pid)}/activities",
                new SaveActivityRequest("kayak", "Sea kayaking", null, 60, 4, 500m, null, true, null)), "Code"));
        Assert.Equal("An activity lasts 15 minutes to 12 hours (15–720 minutes).",
            await FieldErrorAsync(await s.Owner.PostAsync($"{Url(s.Pid)}/activities",
                new SaveActivityRequest("SUP", "Paddle board", null, 5, 4, 500m, null, true, null)), "DurationMinutes"));
    }

    // ------------------------------------------------------------------ setup

    private sealed record Setup(Guid Pid, TestClient Owner, TestClient Manager, TestClient Desk, TestClient Housekeeper, ReservationDto Stay1,
        ReservationDto Stay2, Guid Kayaking, Guid Yoga, Guid LifeJacket);

    /// <summary>A property of its own with two checked-in stays (today to three days ahead), kayaking (6 seats, ₹800, 90 minutes,
    /// a life jacket each), free sunrise yoga, and four life jackets LJ-01…LJ-04.</summary>
    private async Task<Setup> SetupAsync()
    {
        app.Clock.At = null;
        var owner = await app.SignInOwnerAsync();
        var code = "A" + Suffix();
        var res = await owner.PostAsync("/api/v1/properties", new CreatePropertyRequest(code, $"Kayak Shack {code}", null, "RESORT", "Asia/Kolkata", "INR",
            null, null, null, null, null, "30", null, "IN", null, null, new TimeOnly(14, 0), new TimeOnly(11, 0), null, null));
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        var pid = (await res.Content.ReadFromJsonAsync<PropertyDto>())!.Id;
        var manager = await StaffAsync("ACTIVITY_MANAGER", pid);
        var desk = await StaffAsync("RECEPTIONIST", pid);
        var housekeeper = await StaffAsync("HOUSEKEEPER", pid);

        var stay = await BookingKit.SetupAsync(owner, pid, rooms: 2);
        var source = await WalkInAsync(owner, pid);
        var asha = await CreateGuestAsync(owner, "Asha", "Rao");
        var vikram = await CreateGuestAsync(owner, "Vikram", "Menon");
        var stay1 = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, asha.Id, source, Today, Today.AddDays(3), adults: 2)));
        var stay2 = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, vikram.Id, source, Today, Today.AddDays(3), adults: 2)));
        await FrontDeskTests.OkAsync(await owner.PostAsync(FrontDeskTests.CheckInUrl(pid, stay1), new CheckInRequest(null)));
        await FrontDeskTests.OkAsync(await owner.PostAsync(FrontDeskTests.CheckInUrl(pid, stay2), new CheckInRequest(null)));

        var lj = await OkAsync<EquipmentTypeDto>(await owner.PostAsync($"{Url(pid)}/equipment/types", new SaveEquipmentTypeRequest("LJ", "Life jacket")),
            HttpStatusCode.Created);
        var items = await OkAsync<List<EquipmentItemDto>>(await owner.PostAsync($"{Url(pid)}/equipment/items", new AddEquipmentRequest(lj.Id, null, "LJ", 4)),
            HttpStatusCode.Created);
        Assert.Equal(["LJ-01", "LJ-02", "LJ-03", "LJ-04"], items.Select(i => i.Tag));
        var kayak = await OkAsync<ActivityDto>(await owner.PostAsync($"{Url(pid)}/activities",
            new SaveActivityRequest("KAYAK", "Kayaking", "Mangrove paddle", 90, 6, 800m, "Boat jetty", true, lj.Id)), HttpStatusCode.Created);
        var yoga = await OkAsync<ActivityDto>(await owner.PostAsync($"{Url(pid)}/activities",
            new SaveActivityRequest("YOGA", "Sunrise yoga", null, 60, 20, 0m, "Beach deck", true, null)), HttpStatusCode.Created);
        return new Setup(pid, owner, manager, desk, housekeeper, stay1, stay2, kayak.Id, yoga.Id, lj.Id);
    }

    private async Task<TestClient> StaffAsync(string role, Guid pid)
    {
        var (email, pw) = await app.CreateUserAsync(role, pid);
        return await app.SignInAsync(email, pw);
    }

    /// <summary>One session of an activity at a whole hour (local), and its DTO from the day's board.</summary>
    private static async Task<ActivitySessionDto> SessionAsync(Setup s, Guid activityId, DateOnly day, int hour)
    {
        var made = await OkAsync<CreateSessionsResultDto>(await s.Owner.PostAsync($"{Url(s.Pid)}/activities/sessions",
            new CreateSessionsRequest(activityId, day, day, [new TimeOnly(hour, 0)])));
        Assert.Equal(1, made.Created);
        var board = await OkAsync<ActivityDayDto>(await s.Owner.GetAsync($"{Url(s.Pid)}/activities/sessions?date={day:yyyy-MM-dd}"));
        return board.Sessions.Single(x => x.ActivityId == activityId && x.StartTime == $"{hour:00}:00");
    }

    private static Task<HttpResponseMessage> Book(TestClient c, Setup s, BookActivityRequest body, string? key = null) =>
        c.PostAsync($"{Url(s.Pid)}/activities/bookings", body, new Dictionary<string, string> { ["Idempotency-Key"] = key ?? "act-" + Guid.NewGuid() });

    private static async Task<ActivityBookingDto> BookOkAsync(TestClient c, Setup s, BookActivityRequest body, string? key = null) =>
        await OkAsync<ActivityBookingDto>(await Book(c, s, body, key), HttpStatusCode.Created);

    private static async Task<List<EquipmentAssignmentDto>> IssueOkAsync(Setup s, IssueEquipmentRequest body) =>
        await OkAsync<List<EquipmentAssignmentDto>>(await s.Desk.PostAsync($"{Url(s.Pid)}/equipment/issue", body), HttpStatusCode.Created);

    /// <summary>Stands the clock at a local time at the property.</summary>
    private void Pin(DateOnly day, int h, int m) => app.Clock.At = RosterMath.Instants(day, new TimeOnly(h, m), new TimeOnly(h, m).AddMinutes(1), Ist).Start;

    private static string Url(Guid pid) => $"/api/v1/properties/{pid}";
    private static string Day(DateOnly d) => Resort.Domain.Rates.QuoteEngine.Day(d);
    private static string Short(DateOnly d) => d.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<T> OkAsync<T>(HttpResponseMessage res, HttpStatusCode status = HttpStatusCode.OK)
    {
        Assert.True(res.StatusCode == status, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }

    private async Task<(decimal, decimal, string)> FirstAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        return (r.GetDecimal(0), r.GetDecimal(1), r.GetString(2));
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
