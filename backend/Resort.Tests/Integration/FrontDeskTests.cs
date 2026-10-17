using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Resort.Contracts;
using Resort.Tests.Support;
using static Resort.Tests.Support.BookingKit;

namespace Resort.Tests.Integration;

/// <summary>Sprint 7 — the front desk: arrivals, check-in, in-house, check-out, walk-ins, room assignment and the
/// housekeeping state they depend on. Real API, real PostgreSQL. Every test builds its own rooms.</summary>
public class FrontDeskTests(FrontDeskFactory app) : IClassFixture<FrontDeskFactory>
{
    // ------------------------------------------------------------------ concurrency (required test)

    [Fact]
    public async Task Two_receptionists_checking_in_the_same_booking_at_the_same_moment_exactly_one_succeeds_20_times()
    {
        var (owner, pid) = await OwnerAsync();
        var (email, pw) = await app.CreateUserAsync("RECEPTIONIST", pid);
        var desk = await app.SignInAsync(email, pw);
        var guest = await CreateGuestAsync(owner);
        var source = await WalkInAsync(owner, pid);

        for (var run = 0; run < 20; run++)
        {
            var stay = await SetupAsync(owner, pid, rooms: 1);
            var booked = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, Today, Today.AddDays(2))));

            using var barrier = new Barrier(2);
            var a = Task.Run(() => { barrier.SignalAndWait(); return owner.PostAsync(CheckInUrl(pid, booked), new CheckInRequest(booked.Version)); });
            var b = Task.Run(() => { barrier.SignalAndWait(); return desk.PostAsync(CheckInUrl(pid, booked), new CheckInRequest(booked.Version)); });
            var results = await Task.WhenAll(a, b);

            Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
            var loser = Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
            var (code, message) = await ProblemAsync(loser);
            // the second waited on the first's row lock, then saw the guest checked in (or the version moved on)
            Assert.Contains(code, new[] { "ALREADY_CHECKED_IN", "CONCURRENCY_CONFLICT" });
            if (code == "ALREADY_CHECKED_IN") Assert.Matches($"^{booked.Reference} is already checked in \\(at [0-9]{{2}}:[0-9]{{2}} on .+ by .+\\)", message);
            Assert.Equal(1L, await ScalarAsync(
                $"SELECT count(*) FROM reservation_status_history WHERE reservation_id = '{booked.Id}' AND to_status = 'CHECKED_IN'"));
        }
    }

    // ------------------------------------------------------------------ the review demo: arrivals → assign → check in → in-house

    [Fact]
    public async Task Todays_arrival_is_assigned_a_room_checked_in_and_listed_in_house()
    {
        var (owner, pid) = await OwnerAsync();
        var (email, pw) = await app.CreateUserAsync("RECEPTIONIST", pid);
        var desk = await app.SignInAsync(email, pw);
        var stay = await SetupAsync(owner, pid, rooms: 2, price: 4000m);
        var guest = await CreateGuestAsync(owner, "Meera", "Nair");
        var booked = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, await WalkInAsync(owner, pid), Today, Today.AddDays(2), adults: 2)));

        var arrivals = await desk.GetJsonAsync<FrontDeskListDto>($"/api/v1/properties/{pid}/front-desk/arrivals");
        Assert.Equal(Today, arrivals.Date);
        Assert.Equal(Today, arrivals.Today);
        var row = Assert.Single(arrivals.Items, i => i.ReservationId == booked.Id);
        Assert.Equal("Meera Nair", row.GuestName);
        Assert.Equal("CONFIRMED", row.Status);
        Assert.Null(row.CheckInProblem);
        var room = Assert.Single(row.Rooms);
        Assert.Equal(stay.Rooms[0].Id, room.UnitId);
        Assert.Equal("READY", room.Housekeeping);

        // assign the other room (the options list it as free and ready)
        var options = await desk.GetJsonAsync<List<RoomOptionDto>>(
            $"/api/v1/properties/{pid}/reservations/{booked.Id}/room-options?reservationUnitId={room.ReservationUnitId}");
        var other = Assert.Single(options, o => o.UnitId == stay.Rooms[1].Id);
        Assert.True(other.Free && other.Fits);
        Assert.Null(other.Problem);
        Assert.DoesNotContain(options, o => o.UnitId == stay.Rooms[0].Id);
        var assigned = await OkAsync(await desk.PostAsync($"/api/v1/properties/{pid}/reservations/{booked.Id}/assign-room",
            new AssignRoomRequest(room.ReservationUnitId, stay.Rooms[1].Id, null, null, booked.Version)));
        Assert.Equal(stay.Rooms[1].Id, assigned.Units.Single(u => u.Current).UnitId);
        Assert.Equal(booked.TotalAmount, assigned.TotalAmount);
        Assert.Contains(assigned.Notes, n => n.Note.Contains("Reason: Room assigned at the front desk"));

        // check in
        var inHouse = await OkAsync(await desk.PostAsync(CheckInUrl(pid, booked), new CheckInRequest(assigned.Version)));
        Assert.Equal("CHECKED_IN", inHouse.Status);
        Assert.NotNull(inHouse.CheckedInAt);
        Assert.Equal(new[] { "CHECKED_OUT" }, inHouse.NextStatuses);
        var last = inHouse.History[^1];
        Assert.Equal(("CONFIRMED", "CHECKED_IN"), (last.FromStatus, last.ToStatus));
        Assert.Equal("RECEPTIONIST user", last.ChangedByName);

        var list = await desk.GetJsonAsync<FrontDeskListDto>($"/api/v1/properties/{pid}/front-desk/in-house");
        var mine = Assert.Single(list.Items, i => i.ReservationId == booked.Id);
        Assert.Equal(stay.Rooms[1].Id, Assert.Single(mine.Rooms).UnitId);
        Assert.False(mine.Overdue);
        // still on today's arrivals, now done
        arrivals = await desk.GetJsonAsync<FrontDeskListDto>($"/api/v1/properties/{pid}/front-desk/arrivals");
        Assert.Equal("CHECKED_IN", arrivals.Items.Single(i => i.ReservationId == booked.Id).Status);

        // the audit trail has the check-in
        Assert.True((long)(await ScalarAsync(
            $"SELECT count(*) FROM audit_logs WHERE entity_id = '{booked.Id}' AND changes::text LIKE '%CHECKED_IN%'"))! >= 1);
    }

    [Fact]
    public async Task A_pending_booking_is_confirmed_on_the_way_in_and_tomorrows_arrival_cant_check_in_today()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 2);
        var guest = await CreateGuestAsync(owner);
        var source = await WalkInAsync(owner, pid);
        var pending = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, Today, Today.AddDays(1)) with { Status = "PENDING" }));

        var inHouse = await OkAsync(await owner.PostAsync(CheckInUrl(pid, pending), new CheckInRequest(null)));
        Assert.Equal(new[] { "PENDING", "CONFIRMED", "CHECKED_IN" }, inHouse.History.Select(h => h.ToStatus));
        Assert.Equal("Confirmed at check-in", inHouse.History[1].Reason);

        var tomorrow = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, Today.AddDays(1), Today.AddDays(3))));
        var res = await owner.PostAsync(CheckInUrl(pid, tomorrow), new CheckInRequest(null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var (code, message) = await ProblemAsync(res);
        Assert.Equal("CHECK_IN_DATE", code);
        Assert.Equal($"{tomorrow.Reference} arrives on {Day(Today.AddDays(1))}; it is {Day(Today)} at the property. " +
                     "Change the arrival date on the booking to check the guest in today.", message);

        var cancelled = await OkAsync(await owner.PostAsync($"/api/v1/properties/{pid}/reservations/{tomorrow.Id}/cancel",
            new ReservationStatusRequest("Guest changed plans")));
        var (code2, message2) = await ProblemAsync(await owner.PostAsync(CheckInUrl(pid, cancelled), new CheckInRequest(null)));
        Assert.Equal("INVALID_STATUS_TRANSITION", code2);
        Assert.Equal($"{cancelled.Reference} is cancelled, so it can't be checked in.", message2);
    }

    // ------------------------------------------------------------------ housekeeping READY before check-in

    [Fact]
    public async Task Check_in_needs_a_ready_room_or_an_override_with_a_reason_and_the_property_can_switch_overrides_off()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 1);
        var guest = await CreateGuestAsync(owner);
        var booked = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, await WalkInAsync(owner, pid), Today, Today.AddDays(1))));
        var roomName = stay.Rooms[0].Name;
        await SetHousekeepingAsync(owner, pid, stay.Rooms[0].Id, "DIRTY");

        var arrivals = await owner.GetJsonAsync<FrontDeskListDto>($"/api/v1/properties/{pid}/front-desk/arrivals");
        Assert.Equal($"{roomName} is dirty, not ready.", arrivals.Items.Single(i => i.ReservationId == booked.Id).CheckInProblem);

        var refused = await owner.PostAsync(CheckInUrl(pid, booked), new CheckInRequest(null));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var (code, message) = await ProblemAsync(refused);
        Assert.Equal("HOUSEKEEPING_NOT_READY", code);
        Assert.Equal($"{roomName} is dirty, not ready. Ask housekeeping to mark it ready, assign a ready room, or check in anyway with a reason.", message);

        // an override needs a reason, under its field
        Assert.Equal($"{roomName} is dirty. Say why the guest goes in anyway (at least 3 characters), e.g. Guest agreed to wait while it is cleaned.",
            await FieldErrorAsync(await owner.PostAsync(CheckInUrl(pid, booked), new CheckInRequest(null, true, " ")), "OverrideReason"));

        // the property can switch overrides off
        await PutSettingAsync(owner, pid, "frontdesk.allow_checkin_when_not_ready", false);
        var off = await owner.PostAsync(CheckInUrl(pid, booked), new CheckInRequest(null, true, "Guest is tired"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, off.StatusCode);
        Assert.Equal("OVERRIDE_NOT_ALLOWED", (await ProblemAsync(off)).Code);
        await PutSettingAsync(owner, pid, "frontdesk.allow_checkin_when_not_ready", true);

        var checkedIn = await OkAsync(await owner.PostAsync(CheckInUrl(pid, booked), new CheckInRequest(null, true, "Guest agreed to wait")));
        Assert.Equal("CHECKED_IN", checkedIn.Status);
        Assert.Equal("Guest agreed to wait", checkedIn.History[^1].Reason);
        Assert.Contains(checkedIn.Notes, n => n.Note == $"Checked in although {roomName} is dirty. Reason: Guest agreed to wait");

        // marking a room ready by hand is recorded
        await SetHousekeepingAsync(owner, pid, stay.Rooms[0].Id, "READY", "Cleaned after check-in");
        Assert.Equal("MANUAL", await ScalarAsync(
            $"SELECT source FROM unit_housekeeping_history WHERE unit_id = '{stay.Rooms[0].Id}' AND to_status = 'READY'"));
    }

    // ------------------------------------------------------------------ check-out

    [Fact]
    public async Task Check_out_marks_the_room_dirty_records_the_event_for_housekeeping_and_cant_happen_twice()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 1);
        var guest = await CreateGuestAsync(owner);
        var booked = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, await WalkInAsync(owner, pid), Today, Today.AddDays(1))));

        var notIn = await owner.PostAsync(CheckOutUrl(pid, booked), new CheckOutRequest(null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notIn.StatusCode);
        Assert.Equal($"{booked.Reference} is confirmed, not checked in, so it can't be checked out.", (await ProblemAsync(notIn)).Message);

        await OkAsync(await owner.PostAsync(CheckInUrl(pid, booked), new CheckInRequest(null)));
        var departures = await owner.GetJsonAsync<FrontDeskListDto>($"/api/v1/properties/{pid}/front-desk/departures?date={Today.AddDays(1):yyyy-MM-dd}");
        Assert.Contains(departures.Items, i => i.ReservationId == booked.Id && i.Status == "CHECKED_IN");

        var res = await owner.PostAsync(CheckOutUrl(pid, booked), new CheckOutRequest(null));
        var left = await OkAsync(res);
        Assert.Equal("CHECKED_OUT", left.Status);
        Assert.NotNull(left.CheckedOutAt);
        Assert.Equal(Today.AddDays(1), left.CheckOut);   // a one-night stay: nothing to release
        Assert.All(left.Units.Where(u => u.Current), u => Assert.False(u.HoldsInventory));

        Assert.Equal("DIRTY", await ScalarAsync($"SELECT status FROM unit_housekeeping_status WHERE unit_id = '{stay.Rooms[0].Id}'"));
        var events = await RowsAsync(
            $"SELECT from_status, to_status, source, reservation_id, consumed_at FROM unit_housekeeping_history WHERE unit_id = '{stay.Rooms[0].Id}'");
        var e = Assert.Single(events);
        Assert.Equal(new object?[] { "READY", "DIRTY", "CHECK_OUT", booked.Id, null }, e);

        var again = await owner.PostAsync(CheckOutUrl(pid, booked), new CheckOutRequest(null));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var (code, message) = await ProblemAsync(again);
        Assert.Equal("ALREADY_CHECKED_OUT", code);
        Assert.StartsWith($"{booked.Reference} is already checked out (at ", message);
    }

    [Fact]
    public async Task Check_out_is_refused_while_the_folio_has_a_balance_unless_the_property_allows_it()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 1);
        var guest = await CreateGuestAsync(owner);
        var booked = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, await WalkInAsync(owner, pid), Today, Today.AddDays(1))));
        await OkAsync(await owner.PostAsync(CheckInUrl(pid, booked), new CheckInRequest(null)));
        app.Balances.Owed[booked.Id] = 1250.50m;

        var inHouse = await owner.GetJsonAsync<FrontDeskListDto>($"/api/v1/properties/{pid}/front-desk/in-house");
        Assert.Equal(1250.50m, inHouse.Items.Single(i => i.ReservationId == booked.Id).Balance);

        var refused = await owner.PostAsync(CheckOutUrl(pid, booked), new CheckOutRequest(null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var (code, message) = await ProblemAsync(refused);
        Assert.Equal("BALANCE_DUE", code);
        Assert.Equal($"{booked.Reference} still owes ₹1,250.5. Take the payment before check-out, or ask a manager to allow check-out " +
                     "with a balance (property setting frontdesk.allow_checkout_with_balance).", message);

        await PutSettingAsync(owner, pid, "frontdesk.allow_checkout_with_balance", true);
        Assert.Equal("CHECKED_OUT", (await OkAsync(await owner.PostAsync(CheckOutUrl(pid, booked), new CheckOutRequest(null)))).Status);
        await PutSettingAsync(owner, pid, "frontdesk.allow_checkout_with_balance", false);
    }

    [Fact]
    public async Task Leaving_early_needs_a_reason_and_releases_the_nights_left_so_the_room_can_be_sold_again()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 1, price: 3000m);
        var guest = await CreateGuestAsync(owner);
        var source = await WalkInAsync(owner, pid);
        var booked = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, Today, Today.AddDays(3))));
        Assert.Equal(9000m, booked.TotalAmount);
        await OkAsync(await owner.PostAsync(CheckInUrl(pid, booked), new CheckInRequest(null)));
        Assert.Equal(0, (await AvailabilityAsync(owner, pid, Today.AddDays(1), Today.AddDays(3), stay.Type.Id)).Free);

        Assert.Equal($"The guest is booked until {Day(Today.AddDays(3))}. Say why they are leaving early (at least 3 characters), e.g. Family emergency.",
            await FieldErrorAsync(await owner.PostAsync(CheckOutUrl(pid, booked), new CheckOutRequest(null)), "Reason"));

        var left = await OkAsync(await owner.PostAsync(CheckOutUrl(pid, booked), new CheckOutRequest(null, "Family emergency")));
        // checked out on the arrival day: the first night stays (a stay is at least one night), the other two are released
        Assert.Equal(Today.AddDays(1), left.CheckOut);
        Assert.Equal(3000m, left.TotalAmount);
        var current = Assert.Single(left.Units, u => u.Current);
        Assert.Equal((Today, Today.AddDays(1)), (current.StartDate, current.EndDate));
        Assert.Single(left.Breakdown);
        Assert.Contains(left.Notes, n => n.Note.StartsWith($"Left early on {Day(Today.AddDays(1))} instead of {Day(Today.AddDays(3))}", StringComparison.Ordinal) &&
                                         n.Note.Contains("(₹6,000 less). Reason: Family emergency"));
        Assert.Equal(1, (await AvailabilityAsync(owner, pid, Today.AddDays(1), Today.AddDays(3), stay.Type.Id)).Free);
    }

    // ------------------------------------------------------------------ walk-in: guest + booking + check-in in one call

    [Fact]
    public async Task A_walk_in_is_a_new_guest_a_booking_and_a_check_in_in_one_call_and_a_retry_returns_the_same_stay()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 2, price: 2500m);
        await SetHousekeepingAsync(owner, pid, stay.Rooms[0].Id, "DIRTY");   // the first room isn't ready: the walk-in gets the second
        var phone = "98" + Random.Shared.Next(10000000, 99999999);
        var body = new WalkInRequest(null,
            new CreateGuestRequest("Kabir", "Shah", phone, null, null, "IN", null, null, null, false, null, null),
            Today.AddDays(2), [new ReservationRoomRequest(stay.Type.Id, null, 2, 0)], stay.Plan.Id, "Late checkout if possible", null);
        var key = "walkin-" + Guid.NewGuid();

        var res = await WalkInRawAsync(owner, pid, body, key);
        var walkIn = await CreatedAsync(res);
        Assert.Equal("CHECKED_IN", walkIn.Status);
        Assert.Equal("Walk-in", walkIn.SourceName);
        Assert.Equal((Today, Today.AddDays(2)), (walkIn.CheckIn, walkIn.CheckOut));
        Assert.Equal("Kabir Shah", Assert.Single(walkIn.Guests).FullName);
        Assert.Equal(stay.Rooms[1].Id, walkIn.Units.Single(u => u.Current).UnitId);
        Assert.Equal(5000m, walkIn.TotalAmount);
        Assert.Equal(new[] { "CONFIRMED", "CHECKED_IN" }, walkIn.History.Select(h => h.ToStatus));

        // the same request again (a double tap, a retry after a timeout) returns the same stay, nothing new
        var again = await WalkInRawAsync(owner, pid, body, key);
        Assert.Equal("true", again.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(walkIn.Id, (await CreatedAsync(again)).Id);
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM guests WHERE phone_digits LIKE '%{phone}%'"));
    }

    [Fact]
    public async Task A_walk_in_that_fails_at_check_in_leaves_no_guest_no_booking_and_no_used_key()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 1);
        await SetHousekeepingAsync(owner, pid, stay.Rooms[0].Id, "DIRTY");
        var last = "Q" + Suffix().ToLowerInvariant();
        var body = new WalkInRequest(null, new CreateGuestRequest("Ira", last, null, null, null, "IN", null, null, null, false, null, null),
            Today.AddDays(1), [new ReservationRoomRequest(stay.Type.Id, null, 1, 0)], stay.Plan.Id, null, null);
        var key = "walkin-" + Guid.NewGuid();

        var res = await WalkInRawAsync(owner, pid, body, key);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var (code, message) = await ProblemAsync(res);
        Assert.Equal("HOUSEKEEPING_NOT_READY", code);
        Assert.StartsWith($"{stay.Rooms[0].Name} is dirty, not ready.", message);
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM guests WHERE last_name = '{last}'"));
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM reservation_units WHERE unit_id = '{stay.Rooms[0].Id}'"));
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM idempotency_keys WHERE key = '{key}'"));

        // with an override and a reason the same key goes through
        var ok = await CreatedAsync(await WalkInRawAsync(owner, pid, body with { OverrideHousekeeping = true, OverrideReason = "Guest will wait in the lobby" }, key));
        Assert.Equal("CHECKED_IN", ok.Status);
    }

    [Fact]
    public async Task Walk_in_errors_name_the_field()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 1);
        var rooms = new[] { new ReservationRoomRequest(stay.Type.Id, null, 1, 0) };

        Assert.Equal("Choose a returning guest, or enter the new guest's details (one of the two).",
            await FieldErrorAsync(await WalkInRawAsync(owner, pid, new WalkInRequest(null, null, Today.AddDays(1), rooms, null, null, null)), "GuestId"));
        var noName = new CreateGuestRequest(" ", null, null, null, null, null, null, null, null, false, null, null);
        Assert.NotNull(await FieldErrorAsync(await WalkInRawAsync(owner, pid, new WalkInRequest(null, noName, Today.AddDays(1), rooms, null, null, null)),
            "NewGuest.FirstName"));
        var guest = await CreateGuestAsync(owner);
        Assert.Equal($"Check-out must be after today ({Day(Today)} at the property).",
            await FieldErrorAsync(await WalkInRawAsync(owner, pid, new WalkInRequest(guest.Id, null, Today, rooms, null, null, null)), "CheckOut"));
        Assert.Contains("Idempotency-Key", await FieldErrorAsync(
            await owner.PostAsync($"/api/v1/properties/{pid}/walk-ins", new WalkInRequest(guest.Id, null, Today.AddDays(1), rooms, null, null, null)),
            "Idempotency-Key"));
    }

    // ------------------------------------------------------------------ permissions

    [Fact]
    public async Task Reception_checks_guests_in_and_out_housekeeping_cant_and_reception_cant_mark_rooms_ready()
    {
        var (owner, pid) = await OwnerAsync();
        var (hkEmail, hkPw) = await app.CreateUserAsync("HOUSEKEEPER", pid);
        var housekeeper = await app.SignInAsync(hkEmail, hkPw);
        var (deskEmail, deskPw) = await app.CreateUserAsync("RECEPTIONIST", pid);
        var desk = await app.SignInAsync(deskEmail, deskPw);
        var stay = await SetupAsync(owner, pid, rooms: 1);
        var guest = await CreateGuestAsync(owner);
        var booked = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, await WalkInAsync(owner, pid), Today, Today.AddDays(1))));

        Assert.Equal(HttpStatusCode.Forbidden, (await housekeeper.PostAsync(CheckInUrl(pid, booked), new CheckInRequest(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await housekeeper.GetAsync($"/api/v1/properties/{pid}/front-desk/arrivals")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PutAsync($"/api/v1/properties/{pid}/rooms/{stay.Rooms[0].Id}/housekeeping",
            new SetHousekeepingRequest(null, "READY", null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await housekeeper.PutAsync($"/api/v1/properties/{pid}/rooms/{stay.Rooms[0].Id}/housekeeping",
            new SetHousekeepingRequest(null, "READY", "Inspected"))).StatusCode);

        await OkAsync(await desk.PostAsync(CheckInUrl(pid, booked), new CheckInRequest(null)));
        Assert.Equal("CHECKED_OUT", (await OkAsync(await desk.PostAsync(CheckOutUrl(pid, booked), new CheckOutRequest(null)))).Status);
    }

    // ------------------------------------------------------------------ the dashboard and moving an in-house guest

    [Fact]
    public async Task The_reception_dashboard_counts_today_and_shows_each_room()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 3);
        var guest = await CreateGuestAsync(owner, "Dev", "Rao");
        var source = await WalkInAsync(owner, pid);
        var arriving = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, Today, Today.AddDays(1)) with
        { Rooms = [new ReservationRoomRequest(stay.Type.Id, stay.Rooms[0].Id, 1, 0)] }));
        var staying = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, Today, Today.AddDays(2)) with
        { Rooms = [new ReservationRoomRequest(stay.Type.Id, stay.Rooms[1].Id, 2, 0)] }));
        await OkAsync(await owner.PostAsync(CheckInUrl(pid, staying), new CheckInRequest(null)));
        await SetHousekeepingAsync(owner, pid, stay.Rooms[2].Id, "DIRTY");

        var d = await owner.GetJsonAsync<FrontDeskDashboardDto>($"/api/v1/properties/{pid}/front-desk/dashboard");
        Assert.Equal(Today, d.Today);
        Assert.True(d.ArrivalsExpected >= 2 && d.ArrivalsCheckedIn >= 1 && d.InHouseStays >= 1 && d.RoomsNotReady >= 1);
        var board = d.Rooms.Where(r => stay.Rooms.Any(s => s.Id == r.UnitId)).OrderBy(r => r.UnitCode).ToList();
        var byId = board.ToDictionary(r => r.UnitId);
        Assert.Equal(("ARRIVING", arriving.Reference, "Dev Rao"), (byId[stay.Rooms[0].Id].Occupancy, byId[stay.Rooms[0].Id].Reference, byId[stay.Rooms[0].Id].GuestName));
        Assert.Equal(("OCCUPIED", staying.Reference), (byId[stay.Rooms[1].Id].Occupancy, byId[stay.Rooms[1].Id].Reference));
        Assert.Equal(("VACANT", "DIRTY"), (byId[stay.Rooms[2].Id].Occupancy, byId[stay.Rooms[2].Id].Housekeeping));

        // a checked-in guest can't be moved from the calendar; the message points to the front desk
        var fromCalendar = await owner.PostAsync($"/api/v1/properties/{pid}/reservations/{staying.Id}/reassign",
            new ReassignRoomRequest(staying.Units.Single().Id, stay.Rooms[2].Id, null, "Noise", staying.Version));
        Assert.Contains("Move an in-house guest from the front desk", (await ProblemAsync(fromCalendar)).Message);

        // moving an in-house guest into a dirty room needs an override and a reason; the old room becomes dirty
        var current = await owner.GetJsonAsync<ReservationDto>($"/api/v1/properties/{pid}/reservations/{staying.Id}");
        var assignUrl = $"/api/v1/properties/{pid}/reservations/{staying.Id}/assign-room";
        var unitRow = current.Units.Single(u => u.Current).Id;
        Assert.Equal("Say why the guest is moving (at least 3 characters), e.g. AC not working in 102.",
            await FieldErrorAsync(await owner.PostAsync(assignUrl, new AssignRoomRequest(unitRow, stay.Rooms[2].Id, null, null, current.Version)), "Reason"));
        var dirty = await owner.PostAsync(assignUrl, new AssignRoomRequest(unitRow, stay.Rooms[2].Id, null, "Noise from the road", current.Version));
        Assert.Equal("HOUSEKEEPING_NOT_READY", (await ProblemAsync(dirty)).Code);
        var moved = await OkAsync(await owner.PostAsync(assignUrl,
            new AssignRoomRequest(unitRow, stay.Rooms[2].Id, null, "Noise from the road", current.Version, OverrideHousekeeping: true)));
        Assert.Equal(stay.Rooms[2].Id, moved.Units.Single(u => u.Current).UnitId);   // moved on the arrival day: the whole stay moves
        Assert.Equal("DIRTY", await ScalarAsync($"SELECT status FROM unit_housekeeping_status WHERE unit_id = '{stay.Rooms[1].Id}'"));
        Assert.Equal("ROOM_MOVE", await ScalarAsync($"SELECT source FROM unit_housekeeping_history WHERE unit_id = '{stay.Rooms[1].Id}'"));
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(TestClient Owner, Guid PropertyId)> OwnerAsync() => (await app.SignInOwnerAsync(), await app.MainPropertyIdAsync());

    internal static string CheckInUrl(Guid pid, ReservationDto r) => $"/api/v1/properties/{pid}/reservations/{r.Id}/check-in";
    internal static string CheckOutUrl(Guid pid, ReservationDto r) => $"/api/v1/properties/{pid}/reservations/{r.Id}/check-out";
    internal static string Day(DateOnly d) => d.ToString("ddd d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    internal static Task<HttpResponseMessage> WalkInRawAsync(TestClient c, Guid pid, WalkInRequest body, string? key = null) =>
        c.PostAsync($"/api/v1/properties/{pid}/walk-ins", body, new Dictionary<string, string> { ["Idempotency-Key"] = key ?? "walkin-" + Guid.NewGuid() });

    internal static async Task SetHousekeepingAsync(TestClient c, Guid pid, Guid unitId, string status, string? reason = null)
    {
        var res = await c.PutAsync($"/api/v1/properties/{pid}/rooms/{unitId}/housekeeping", new SetHousekeepingRequest(null, status, reason));
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
    }

    private static async Task PutSettingAsync(TestClient c, Guid pid, string key, bool value) =>
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsync($"/api/v1/properties/{pid}/settings/{key}",
            new PutPropertySettingRequest(JsonSerializer.SerializeToElement(value)))).StatusCode);

    internal static async Task<ReservationDto> OkAsync(HttpResponseMessage res)
    {
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<ReservationDto>())!;
    }

    /// <summary>The error's code and message (the body can be read only once).</summary>
    internal static async Task<(string? Code, string? Message)> ProblemAsync(HttpResponseMessage res)
    {
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("code").GetString(), body.GetProperty("message").GetString());
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }

    private async Task<List<object?[]>> RowsAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await r.ReadAsync())
        {
            var row = new object?[r.FieldCount];
            for (var i = 0; i < r.FieldCount; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }
}
