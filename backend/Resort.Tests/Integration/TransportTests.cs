using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Resort.Contracts;
using Resort.Domain.Employees;
using Resort.Tests.Support;
using static Resort.Tests.Support.BookingKit;

namespace Resort.Tests.Integration;

/// <summary>Sprint 21 — beach transport: a seat on the shuttle goes on the guest's bill as ₹500 (and comes off when it is
/// cancelled), the last seat booked twice at the same moment goes to one person, the driver sees today's manifest on a
/// phone, ticks who boarded and leaves (the rest are no-shows), a driver can't run someone else's trip, a vehicle or a
/// driver can't be on two trips at once, a cancelled trip refunds everyone, and the manifest prints as a PDF. Each test uses
/// a property of its own; trips are tomorrow (real clock) unless the clock is pinned. Real API, real PostgreSQL.</summary>
public class TransportTests(FrontDeskFactory app) : IClassFixture<FrontDeskFactory>
{
    private static readonly TimeZoneInfo Ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
    private static DateOnly Tomorrow => Today.AddDays(1);

    // ------------------------------------------------------------------ required: transfer booking posts to folio

    [Fact]
    public async Task A_transfer_booking_posts_500_a_seat_to_the_guests_folio_and_cancelling_it_reverses_the_charge()
    {
        var s = await SetupAsync();
        var trip = await TripAsync(s, Tomorrow, 8);
        Assert.Equal((12, 0, 12, 500m, "08:00", "08:20", "Force Traveller", "Santosh Driver"),
            (trip.Capacity, trip.SeatsBooked, trip.SeatsLeft, trip.Price, trip.DepartTime, trip.ArriveTime, trip.VehicleName, trip.DriverName));
        Assert.Equal(s.VanReg, trip.Registration);
        Assert.Contains("book", trip.Actions);

        var raos = await BookOkAsync(s.Desk, s, new BookTransportRequest(trip.Id, 3, s.Stay1.Id, null, Note: "Pick up at the cottages"));
        Assert.Equal(("TP00001", "ROOM_CHARGE", 500m, 1500m, "BOOKED", "Asha Rao"), (raos.Code, raos.Settlement, raos.UnitPrice, raos.Amount, raos.Status, raos.GuestName));
        Assert.NotNull(raos.FolioNumber);
        Assert.Equal(["cancel"], raos.Actions);   // the desk can't tick boarding

        // The bill: one TRANSPORT line of 3 × ₹500, no GST (transport has no tax category).
        Assert.Equal((3m, 500m, "TRANSPORT"), await FirstAsync(
            $"SELECT quantity, unit_price, category FROM folio_items WHERE reservation_id = '{s.Stay1.Id}' AND source = 'TRANSPORT'"));
        Assert.Equal(1500m, await ScalarAsync($"SELECT sum(total) FROM folio_items WHERE reservation_id = '{s.Stay1.Id}' AND source = 'TRANSPORT'"));
        Assert.Equal($"Beach shuttle, {Short(Tomorrow)} 08:00 (TP00001)",
            await ScalarAsync($"SELECT description FROM folio_items WHERE reservation_id = '{s.Stay1.Id}' AND source = 'TRANSPORT'"));

        // A retried click with the same key gives the same booking back, not a second one.
        var key = "tp-" + Guid.NewGuid();
        var first = await Book(s.Desk, s, new BookTransportRequest(trip.Id, 1, s.Stay2.Id, null), key);
        var retry = await Book(s.Desk, s, new BookTransportRequest(trip.Id, 1, s.Stay2.Id, null), key);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.True(retry.Headers.Contains("Idempotent-Replayed"));
        var menon = (await first.Content.ReadFromJsonAsync<TripPassengerDto>())!;
        Assert.Equal(menon.Id, (await retry.Content.ReadFromJsonAsync<TripPassengerDto>())!.Id);
        Assert.Equal(4L, await ScalarAsync($"SELECT seats_booked::bigint FROM vehicle_trips WHERE id = '{trip.Id}'"));

        // Cancelling before the trip leaves takes the charge off the bill, with the reason.
        Assert.Equal("Say why it is cancelled (3–300 characters), e.g. Guest changed plans.",
            await FieldErrorAsync(await s.Desk.PostAsync($"{Url(s.Pid)}/transport/bookings/{raos.Id}/cancel", new CancelPassengerRequest(" ")), "Reason"));
        var cancelled = await OkAsync<TripPassengerDto>(await s.Desk.PostAsync($"{Url(s.Pid)}/transport/bookings/{raos.Id}/cancel",
            new CancelPassengerRequest("Going by scooter", raos.Version)));
        Assert.Equal(("CANCELLED", "REVERSED"), (cancelled.Status, cancelled.Refund!));
        Assert.Empty(cancelled.Actions);
        Assert.Equal(0m, await ScalarAsync($"SELECT sum(total) FROM folio_items WHERE reservation_id = '{s.Stay1.Id}' AND source = 'TRANSPORT'"));
        Assert.Equal("TP00001: Going by scooter", await ScalarAsync($"SELECT reason FROM folio_items WHERE reservation_id = '{s.Stay1.Id}' AND item_type = 'REVERSAL'"));
        Assert.Equal(1L, await ScalarAsync($"SELECT seats_booked::bigint FROM vehicle_trips WHERE id = '{trip.Id}'"));

        // A visitor can't be charged: the seat goes on a stay's bill or is complimentary with the reason.
        Assert.Equal("A seat on Beach shuttle costs ₹500. Choose the guest's stay so it goes on their bill, or make it complimentary with the reason.",
            await FieldErrorAsync(await Book(s.Desk, s, new BookTransportRequest(trip.Id, 1, null, "Day visitor")), "ReservationId"));
        var comp = await BookOkAsync(s.Desk, s, new BookTransportRequest(trip.Id, 1, null, "Travel agent Mr Kulkarni", true, "Familiarisation visit"));
        Assert.Equal(("COMPLIMENTARY", null), (comp.Settlement, comp.FolioNumber));
        // The stay's dates: the Raos leave in three days, so a trip the day after isn't theirs to book.
        var later = await TripAsync(s, Today.AddDays(4), 8);
        Assert.Equal($"{s.Stay1.Reference} stays from {Short(Today)} to {Short(Today.AddDays(3))}, so it can't be booked on {Short(Today.AddDays(4))}.",
            await FieldErrorAsync(await Book(s.Desk, s, new BookTransportRequest(later.Id, 1, s.Stay1.Id, null)), "ReservationId"));
    }

    // ------------------------------------------------------------------ capacity

    [Fact]
    public async Task The_last_seat_booked_twice_at_the_same_moment_goes_to_exactly_one_of_them_10_times()
    {
        var s = await SetupAsync();
        for (var round = 0; round < 10; round++)
        {
            var trip = await TripAsync(s, Tomorrow, 6 + round);
            await BookOkAsync(s.Desk, s, new BookTransportRequest(trip.Id, 11, s.Stay1.Id, null));
            var gate = new Barrier(2);
            var tasks = new[] { (s.Desk, s.Stay1.Id), (s.Manager, s.Stay2.Id) }.Select(x => Task.Run(async () =>
            {
                gate.SignalAndWait();
                return await Book(x.Item1, s, new BookTransportRequest(trip.Id, 1, x.Item2, null));
            })).ToArray();
            var results = await Task.WhenAll(tasks);
            Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], results.Select(r => r.StatusCode).Order());
            var loser = results.Single(r => r.StatusCode == HttpStatusCode.Conflict);
            Assert.Equal(("TRIP_FULL", $"Beach shuttle at {6 + round:00}:00 on {Short(Tomorrow)} is full (12 of 12 seats booked). Choose another trip."),
                await FrontDeskTests.ProblemAsync(loser));
            Assert.Equal(12L, await ScalarAsync($"SELECT seats_booked::bigint FROM vehicle_trips WHERE id = '{trip.Id}'"));
        }
        // seats_booked is always the live passengers' sum, and never above the seats.
        Assert.Equal(0L, await ScalarAsync($"""
            SELECT count(*) FROM vehicle_trips t
            WHERE t.property_id = '{s.Pid}' AND (t.seats_booked > t.capacity
               OR t.seats_booked <> (SELECT COALESCE(sum(seats), 0) FROM trip_passengers p WHERE p.trip_id = t.id AND p.status <> 'CANCELLED'))
            """));
    }

    [Fact]
    public async Task The_database_refuses_an_extra_passenger_over_the_seats_or_on_a_trip_that_left_even_without_the_service()
    {
        var s = await SetupAsync();
        var trip = await TripAsync(s, Tomorrow, 8);
        var b = await BookOkAsync(s.Desk, s, new BookTransportRequest(trip.Id, 12, s.Stay1.Id, null));
        var three = await Book(s.Desk, s, new BookTransportRequest(trip.Id, 3, s.Stay2.Id, null));
        Assert.Equal("TRIP_FULL", (await FrontDeskTests.ProblemAsync(three)).Code);
        string Insert(string code) => $"""
            INSERT INTO trip_passengers (organization_id, property_id, code, trip_id, guest_name, seats, unit_price, amount, settlement, complimentary_reason)
            SELECT organization_id, property_id, '{code}', id, 'Sneaky', 1, 0, 0, 'COMPLIMENTARY', 'Test' FROM vehicle_trips WHERE id = '{trip.Id}'
            """;
        var full = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(Insert("TP09999")));
        Assert.Equal(("23514", "ck_vehicle_trips_capacity"), (full.SqlState, full.ConstraintName));

        // A trip only moves forward, and a passenger keeps their trip and seats.
        var back = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"UPDATE vehicle_trips SET status = 'COMPLETED', departed_at = now(), completed_at = now() WHERE id = '{trip.Id}'"));
        Assert.Equal("ck_vehicle_trips_transition", back.ConstraintName);
        var seats = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"UPDATE trip_passengers SET seats = 1, amount = 500 WHERE id = '{b.Id}'"));
        Assert.Equal("ck_trip_passengers_fixed", seats.ConstraintName);

        // Once the trip has left nobody more gets on it, not even by SQL.
        Pin(Tomorrow, 7, 55);
        await OkAsync<TripDetailDto>(await s.Driver.PostAsync($"{Url(s.Pid)}/transport/trips/{trip.Id}/depart", new TripStepRequest()));
        app.Clock.At = null;
        await ExecuteAsync($"UPDATE vehicle_trips SET capacity = 13 WHERE id = '{trip.Id}'");
        var closed = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(Insert("TP09998")));
        Assert.Equal("ck_trip_passengers_trip_open", closed.ConstraintName);
    }

    // ------------------------------------------------------------------ acceptance: the driver sees today's manifest

    [Fact]
    public async Task The_driver_sees_todays_manifest_on_a_phone_ticks_who_boarded_and_leaves_with_the_rest_as_no_shows()
    {
        var s = await SetupAsync();
        var trip = await TripAsync(s, Tomorrow, 9);
        var other = await TripAsync(s, Tomorrow, 12, driver: false);
        var raos = await BookOkAsync(s.Desk, s, new BookTransportRequest(trip.Id, 2, s.Stay1.Id, null, Note: "Child seat"));
        var menons = await BookOkAsync(s.Desk, s, new BookTransportRequest(trip.Id, 2, s.Stay2.Id, null));
        await BookOkAsync(s.Desk, s, new BookTransportRequest(other.Id, 1, s.Stay2.Id, null));

        // On the day, the driver's own trips with the passengers (and not the noon trip, which has no driver).
        Pin(Tomorrow, 8, 40);
        var mine = await OkAsync<MyTripsDto>(await s.Driver.GetAsync($"{Url(s.Pid)}/transport/my-trips"));
        Assert.Equal(Tomorrow, mine.Date);
        var today = Assert.Single(mine.Trips);
        Assert.Equal((trip.Id, 4, 0), (today.Trip.Id, today.Trip.SeatsBooked, today.Trip.OnBoard));
        Assert.Equal(["board", "depart", "manifest"], today.Trip.Actions);
        Assert.Equal([("TP00001", "Asha Rao", 2, "Child seat"), ("TP00002", "Vikram Menon", 2, null)],
            today.Passengers.Select(p => (p.Code, p.GuestName, p.Seats, p.Note)));
        Assert.All(today.Passengers, p => Assert.Equal(["board"], p.Actions));
        Assert.NotNull(today.Passengers[0].Room);

        // The Raos get on; a mistaken tick is undone; the driver leaves; the Menons never came.
        var on = await OkAsync<TripPassengerDto>(await s.Driver.PostAsync($"{Url(s.Pid)}/transport/bookings/{raos.Id}/board", new TripStepRequest()));
        Assert.Equal(("BOARDED", "Santosh Driver"), (on.Status, on.BoardedBy!));
        Assert.Equal(["unboard"], on.Actions);
        await OkAsync<TripPassengerDto>(await s.Driver.PostAsync($"{Url(s.Pid)}/transport/bookings/{menons.Id}/board", new TripStepRequest()));
        var off = await OkAsync<TripPassengerDto>(await s.Driver.PostAsync($"{Url(s.Pid)}/transport/bookings/{menons.Id}/unboard", new TripStepRequest()));
        Assert.Equal(("BOOKED", null), (off.Status, off.BoardedAt));
        // A boarded guest can't be cancelled by the desk.
        var boardedCancel = await s.Desk.PostAsync($"{Url(s.Pid)}/transport/bookings/{raos.Id}/cancel", new CancelPassengerRequest("Changed plans"));
        Assert.Equal(("PASSENGER_ON_BOARD", "Asha Rao (TP00001) is ticked as on board. Untick them first if they got off, then cancel."),
            await FrontDeskTests.ProblemAsync(boardedCancel));

        Pin(Tomorrow, 9, 2);
        var left = await OkAsync<TripDetailDto>(await s.Driver.PostAsync($"{Url(s.Pid)}/transport/trips/{trip.Id}/depart", new TripStepRequest()));
        Assert.Equal(("DEPARTED", 2, 4), (left.Trip.Status, left.Trip.OnBoard, left.Trip.SeatsBooked));
        Assert.Equal(["complete", "manifest"], left.Trip.Actions);
        Assert.Equal([("TP00001", "BOARDED"), ("TP00002", "NO_SHOW")], left.Passengers.Select(p => (p.Code, p.Status)));
        // The no-show keeps the charge; nobody can be booked or cancelled any more.
        Assert.Equal(1000m, await ScalarAsync($"SELECT sum(total) FROM folio_items WHERE reservation_id = '{s.Stay2.Id}' AND source = 'TRANSPORT' AND description LIKE '%TP00002%'"));
        var late = await Book(s.Desk, s, new BookTransportRequest(trip.Id, 1, s.Stay2.Id, null));
        Assert.Equal(("TRIP_DEPARTED", $"Beach shuttle at 09:00 on {Short(Tomorrow)} has already left, so nobody more can be booked on it. Choose a later trip."),
            await FrontDeskTests.ProblemAsync(late));
        var done = await OkAsync<TripDetailDto>(await s.Driver.PostAsync($"{Url(s.Pid)}/transport/trips/{trip.Id}/complete", new TripStepRequest()));
        Assert.Equal("COMPLETED", done.Trip.Status);
        app.Clock.At = null;
        Assert.Equal(3L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE entity_type = 'VehicleTrip' AND entity_id = '{trip.Id}'"));
    }

    [Fact]
    public async Task A_driver_runs_only_their_own_trips_and_a_receptionist_or_housekeeper_cannot_run_any()
    {
        var s = await SetupAsync();
        var mine = await TripAsync(s, Tomorrow, 9);
        var theirs = await TripAsync(s, Tomorrow, 11, driver: false);
        var p = await BookOkAsync(s.Desk, s, new BookTransportRequest(theirs.Id, 1, s.Stay1.Id, null));
        var detail = await OkAsync<TripDetailDto>(await s.Driver.GetAsync($"{Url(s.Pid)}/transport/trips/{theirs.Id}"));
        Assert.Equal(["manifest"], detail.Trip.Actions);   // the driver can read it, not run it
        Assert.Empty(detail.Passengers[0].Actions);
        var refused = await s.Driver.PostAsync($"{Url(s.Pid)}/transport/bookings/{p.Id}/board", new TripStepRequest());
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal($"Beach shuttle at 11:00 on {Short(Tomorrow)} has no driver yet, so only a transport manager can run it.",
            (await FrontDeskTests.ProblemAsync(refused)).Message);

        // The receptionist books but doesn't run trips; a housekeeper can't see transport at all; the driver can't book.
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Desk.PostAsync($"{Url(s.Pid)}/transport/trips/{mine.Id}/depart", new TripStepRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Housekeeper.GetAsync($"{Url(s.Pid)}/transport/trips")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Book(s.Driver, s, new BookTransportRequest(mine.Id, 1, s.Stay1.Id, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Driver.PostAsync($"{Url(s.Pid)}/transport/trips/{mine.Id}/cancel", new CancelTripRequest("Flat tyre"))).StatusCode);

        // A manager can run any trip; departing before the trip's day is refused with the reason.
        var early = await s.Manager.PostAsync($"{Url(s.Pid)}/transport/trips/{theirs.Id}/depart", new TripStepRequest());
        Assert.Equal(("TRIP_NOT_TODAY", $"Beach shuttle at 11:00 on {Short(Tomorrow)} is on {Short(Tomorrow)}, so it can't leave today."),
            await FrontDeskTests.ProblemAsync(early));
        await OkAsync<TripPassengerDto>(await s.Manager.PostAsync($"{Url(s.Pid)}/transport/bookings/{p.Id}/board", new TripStepRequest()));
        var complete = await s.Manager.PostAsync($"{Url(s.Pid)}/transport/trips/{theirs.Id}/complete", new TripStepRequest());
        Assert.Equal(("INVALID_STATUS_TRANSITION", $"Beach shuttle at 11:00 on {Short(Tomorrow)} hasn't left yet. Mark it as left first."),
            await FrontDeskTests.ProblemAsync(complete));
    }

    // ------------------------------------------------------------------ planning and cancelling

    [Fact]
    public async Task Trips_are_put_on_for_days_and_times_a_vehicle_or_driver_cannot_be_on_two_trips_at_once_and_seats_stay_within_the_vehicle()
    {
        var s = await SetupAsync();
        var made = await OkAsync<CreateTripsResultDto>(await s.Manager.PostAsync($"{Url(s.Pid)}/transport/trips",
            new CreateTripsRequest(s.Beach, s.Van, s.DriverId, Tomorrow, Tomorrow.AddDays(1), [new TimeOnly(8, 0), new TimeOnly(16, 0)])));
        Assert.Equal((4, 0), (made.Created, made.Skipped.Count));
        // The jeep at 08:10 with the same driver: the driver is on the 08:00 run until 08:20.
        var clash = await OkAsync<CreateTripsResultDto>(await s.Manager.PostAsync($"{Url(s.Pid)}/transport/trips",
            new CreateTripsRequest(s.Beach, s.Jeep, s.DriverId, Tomorrow, Tomorrow, [new TimeOnly(8, 10), new TimeOnly(10, 0)])));
        Assert.Equal(1, clash.Created);
        Assert.Equal([$"{Short(Tomorrow)} 08:10: Santosh Driver is on the 08:00 Beach shuttle"], clash.Skipped);
        // The van again at 16:00: already on.
        var again = await OkAsync<CreateTripsResultDto>(await s.Manager.PostAsync($"{Url(s.Pid)}/transport/trips",
            new CreateTripsRequest(s.Beach, s.Van, null, Tomorrow, Tomorrow, [new TimeOnly(16, 0)])));
        Assert.Equal([$"{Short(Tomorrow)} 16:00: Force Traveller is on the 16:00 Beach shuttle"], again.Skipped);
        Assert.Equal($"Trips can start from today ({Day(Today)}).",
            await FieldErrorAsync(await s.Manager.PostAsync($"{Url(s.Pid)}/transport/trips",
                new CreateTripsRequest(s.Beach, s.Van, null, Today.AddDays(-1), Today, [new TimeOnly(8, 0)])), "FromDate"));
        Assert.Equal($"Housekeeper user can't run trips at {s.Name} (no Driver role here, or the account is switched off). Give them the Driver role first.",
            await FieldErrorAsync(await s.Manager.PostAsync($"{Url(s.Pid)}/transport/trips",
                new CreateTripsRequest(s.Beach, s.Van, s.Housekeeper.Me!.UserId, Tomorrow, Tomorrow, [new TimeOnly(18, 0)])), "DriverUserId"));
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Desk.PostAsync($"{Url(s.Pid)}/transport/trips",
            new CreateTripsRequest(s.Beach, s.Van, null, Tomorrow, Tomorrow, [new TimeOnly(18, 0)]))).StatusCode);

        var day = await OkAsync<TransportDayDto>(await s.Manager.GetAsync($"{Url(s.Pid)}/transport/trips?date={Tomorrow:yyyy-MM-dd}"));
        Assert.Equal([("08:00", "Force Traveller"), ("10:00", "Mahindra Bolero"), ("16:00", "Force Traveller")],
            day.Trips.Select(t => (t.DepartTime, t.VehicleName)));
        Assert.Equal("Santosh Driver", day.Drivers[0].Name);
        Assert.True(day.Drivers[0].IsDriver);
        var eight = day.Trips[0];

        // Seats: not above the vehicle's, not below what is booked.
        await BookOkAsync(s.Desk, s, new BookTransportRequest(eight.Id, 8, s.Stay1.Id, null));
        Assert.Equal("Mahindra Bolero has 7 passenger seats, so the trip can't have more than 7.",
            await FieldErrorAsync(await s.Manager.PutAsync($"{Url(s.Pid)}/transport/trips/{eight.Id}",
                new UpdateTripRequest(s.Jeep, s.DriverId, 12, 500m, null)), "Capacity"));
        Assert.Equal("8 seats are already booked, so it can't have fewer than 8. Cancel passengers first to make it smaller.",
            await FieldErrorAsync(await s.Manager.PutAsync($"{Url(s.Pid)}/transport/trips/{eight.Id}",
                new UpdateTripRequest(s.Van, s.DriverId, 6, 500m, null)), "Capacity"));
        // The version on screen moved when the seats were booked (the trigger updates the trip), so a stale one is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await s.Manager.PutAsync($"{Url(s.Pid)}/transport/trips/{eight.Id}",
            new UpdateTripRequest(s.Van, s.DriverId, 10, 550m, null, eight.Version))).StatusCode);
        var fresh = await OkAsync<TripDetailDto>(await s.Manager.GetAsync($"{Url(s.Pid)}/transport/trips/{eight.Id}"));
        var changed = await OkAsync<TripDto>(await s.Manager.PutAsync($"{Url(s.Pid)}/transport/trips/{eight.Id}",
            new UpdateTripRequest(s.Van, s.DriverId, 10, 550m, "Two seats kept for luggage", fresh.Trip.Version)));
        Assert.Equal((10, 8, 2, 550m), (changed.Capacity, changed.SeatsBooked, changed.SeatsLeft, changed.Price));

        // Vehicles: registration unique and kept without spaces; a vehicle's seats can't drop below a booked trip.
        Assert.Equal($"Force Traveller already has the registration {s.VanReg}. Check the number.",
            await FieldErrorAsync(await s.Owner.PostAsync($"{Url(s.Pid)}/transport/vehicles",
                new SaveVehicleRequest("VAN2", "Second van", "mh-07 ab " + s.VanReg[6..].ToLowerInvariant(), 12)), "Registration"));
        Assert.Equal("The registration number is 4–12 letters and digits, e.g. MH 07 AB 1234.",
            await FieldErrorAsync(await s.Owner.PostAsync($"{Url(s.Pid)}/transport/vehicles", new SaveVehicleRequest("VAN2", "Second van", "MH", 12)), "Registration"));
        Assert.Equal($"The 08:00 trip on {Short(Tomorrow)} already has 8 seats booked in it, so it can't have fewer than 8 seats. Move those passengers first.",
            await FieldErrorAsync(await s.Owner.PutAsync($"{Url(s.Pid)}/transport/vehicles/{s.Van}",
                new SaveVehicleRequest("VAN1", "Force Traveller", s.VanReg, 6)), "Seats"));
    }

    [Fact]
    public async Task A_cancelled_trip_refunds_every_passenger_on_the_bill_and_takes_no_more_bookings()
    {
        var s = await SetupAsync();
        var trip = await TripAsync(s, Tomorrow, 16);
        var raos = await BookOkAsync(s.Desk, s, new BookTransportRequest(trip.Id, 2, s.Stay1.Id, null));
        await BookOkAsync(s.Desk, s, new BookTransportRequest(trip.Id, 3, s.Stay2.Id, null));
        await BookOkAsync(s.Desk, s, new BookTransportRequest(trip.Id, 1, null, "Chef Ravi", true, "Staff errand"));
        await OkAsync<TripPassengerDto>(await s.Manager.PostAsync($"{Url(s.Pid)}/transport/bookings/{raos.Id}/board", new TripStepRequest()));

        Assert.Equal("Say why the trip is cancelled (3–300 characters), e.g. Van broke down.",
            await FieldErrorAsync(await s.Manager.PostAsync($"{Url(s.Pid)}/transport/trips/{trip.Id}/cancel", new CancelTripRequest("")), "Reason"));
        var result = await OkAsync<CancelTripResultDto>(await s.Manager.PostAsync($"{Url(s.Pid)}/transport/trips/{trip.Id}/cancel",
            new CancelTripRequest("Van broke down")));
        Assert.Equal(("CANCELLED", "Van broke down", 0), (result.Trip.Status, result.Trip.CancelReason!, result.Trip.SeatsBooked));
        Assert.Equal((2, 2500m, 1), (result.Refunded, result.AmountRefunded, result.NothingToRefund));
        Assert.Equal([("TP00001", "REVERSED"), ("TP00002", "REVERSED"), ("TP00003", "NONE")], result.Passengers.Select(p => (p.Code, p.Refund!)));
        Assert.Equal(0m, await ScalarAsync($"SELECT COALESCE(sum(total), 0) FROM folio_items WHERE property_id = '{s.Pid}' AND source = 'TRANSPORT'"));
        Assert.Equal("TP00002: Trip cancelled: Van broke down", await ScalarAsync($"SELECT reason FROM folio_items WHERE reservation_id = '{s.Stay2.Id}' AND item_type = 'REVERSAL'"));

        var after = await Book(s.Desk, s, new BookTransportRequest(trip.Id, 1, s.Stay1.Id, null));
        Assert.Equal(("TRIP_CANCELLED", $"Beach shuttle at 16:00 on {Short(Tomorrow)} was cancelled (Van broke down). Choose another trip."),
            await FrontDeskTests.ProblemAsync(after));
        // The van is free again at 16:00 for a new trip.
        var made = await OkAsync<CreateTripsResultDto>(await s.Manager.PostAsync($"{Url(s.Pid)}/transport/trips",
            new CreateTripsRequest(s.Beach, s.Van, s.DriverId, Tomorrow, Tomorrow, [new TimeOnly(16, 0)])));
        Assert.Equal(1, made.Created);
    }

    [Fact]
    public async Task The_manifest_prints_as_a_pdf_with_the_passengers()
    {
        var s = await SetupAsync();
        var trip = await TripAsync(s, Tomorrow, 8);
        await BookOkAsync(s.Desk, s, new BookTransportRequest(trip.Id, 2, s.Stay1.Id, null, Note: "Child seat"));
        var res = await s.Driver.GetAsync($"{Url(s.Pid)}/transport/trips/{trip.Id}/manifest.pdf");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/pdf", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"manifest-BEACH-{Tomorrow:yyyyMMdd}-0800.pdf", res.Content.Headers.ContentDisposition!.FileName);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 1000);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
    }

    // ------------------------------------------------------------------ setup

    private sealed record Setup(Guid Pid, string Name, TestClient Owner, TestClient Manager, TestClient Desk, TestClient Driver, Guid DriverId,
        TestClient Housekeeper, ReservationDto Stay1, ReservationDto Stay2, Guid Beach, Guid Van, Guid Jeep, string VanReg);

    /// <summary>A property of its own with two checked-in stays (today to three days ahead), a 12-seat van and a 7-seat jeep,
    /// the beach shuttle route (8 km, 20 minutes, ₹500 a seat), and a driver.</summary>
    private async Task<Setup> SetupAsync()
    {
        app.Clock.At = null;
        var owner = await app.SignInOwnerAsync();
        var code = "T" + Suffix();
        var name = $"Shuttle Stop {code}";
        var res = await owner.PostAsync("/api/v1/properties", new CreatePropertyRequest(code, name, null, "RESORT", "Asia/Kolkata", "INR",
            null, null, null, null, null, "30", null, "IN", null, null, new TimeOnly(14, 0), new TimeOnly(11, 0), null, null));
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        var pid = (await res.Content.ReadFromJsonAsync<PropertyDto>())!.Id;
        var manager = await StaffAsync("MANAGER", pid);
        var desk = await StaffAsync("RECEPTIONIST", pid);
        var driver = await StaffAsync("DRIVER", pid);
        var housekeeper = await StaffAsync("HOUSEKEEPER", pid);
        await ExecuteAsync($"UPDATE users SET full_name = 'Santosh Driver' WHERE id = '{driver.Me!.UserId}'");
        await ExecuteAsync($"UPDATE users SET full_name = 'Housekeeper user' WHERE id = '{housekeeper.Me!.UserId}'");

        var stay = await BookingKit.SetupAsync(owner, pid, rooms: 2);
        var source = await WalkInAsync(owner, pid);
        var asha = await CreateGuestAsync(owner, "Asha", "Rao");
        var vikram = await CreateGuestAsync(owner, "Vikram", "Menon");
        var stay1 = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, asha.Id, source, Today, Today.AddDays(3), adults: 2)));
        var stay2 = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, vikram.Id, source, Today, Today.AddDays(3), adults: 2)));
        await FrontDeskTests.OkAsync(await owner.PostAsync(FrontDeskTests.CheckInUrl(pid, stay1), new CheckInRequest(null)));
        await FrontDeskTests.OkAsync(await owner.PostAsync(FrontDeskTests.CheckInUrl(pid, stay2), new CheckInRequest(null)));

        // Registrations are unique in the organisation, which every test property shares.
        var n = Random.Shared.Next(1000, 9999);
        var van = await OkAsync<VehicleDto>(await owner.PostAsync($"{Url(pid)}/transport/vehicles",
            new SaveVehicleRequest("van1", "Force Traveller", $"MH 07 AB {n}", 12)), HttpStatusCode.Created);
        Assert.Equal(("VAN1", $"MH07AB{n}"), (van.Code, van.Registration));
        var jeep = await OkAsync<VehicleDto>(await owner.PostAsync($"{Url(pid)}/transport/vehicles",
            new SaveVehicleRequest("JEEP1", "Mahindra Bolero", $"MH07X{n}", 7)), HttpStatusCode.Created);
        var beach = await OkAsync<TransportRouteDto>(await owner.PostAsync($"{Url(pid)}/transport/routes",
            new SaveTransportRouteRequest("BEACH", "Beach shuttle", "Resort gate", "Tarkarli beach", 8m, 20, 500m)), HttpStatusCode.Created);
        return new Setup(pid, name, owner, manager, desk, driver, driver.Me!.UserId, housekeeper, stay1, stay2, beach.Id, van.Id, jeep.Id, van.Registration);
    }

    private async Task<TestClient> StaffAsync(string role, Guid pid)
    {
        var (email, pw) = await app.CreateUserAsync(role, pid);
        return await app.SignInAsync(email, pw);
    }

    /// <summary>One beach trip in the van at a whole hour (local), with the driver unless asked not to, from the day's board.</summary>
    private static async Task<TripDto> TripAsync(Setup s, DateOnly day, int hour, bool driver = true)
    {
        var made = await OkAsync<CreateTripsResultDto>(await s.Owner.PostAsync($"{Url(s.Pid)}/transport/trips",
            new CreateTripsRequest(s.Beach, driver ? s.Van : s.Jeep, driver ? s.DriverId : null, day, day, [new TimeOnly(hour, 0)])));
        Assert.True(made.Created == 1, string.Join("; ", made.Skipped));
        var board = await OkAsync<TransportDayDto>(await s.Owner.GetAsync($"{Url(s.Pid)}/transport/trips?date={day:yyyy-MM-dd}"));
        var trip = board.Trips.Single(x => x.DepartTime == $"{hour:00}:00" && x.Status == "SCHEDULED");
        return trip;
    }

    private static Task<HttpResponseMessage> Book(TestClient c, Setup s, BookTransportRequest body, string? key = null) =>
        c.PostAsync($"{Url(s.Pid)}/transport/bookings", body, new Dictionary<string, string> { ["Idempotency-Key"] = key ?? "tp-" + Guid.NewGuid() });

    private static async Task<TripPassengerDto> BookOkAsync(TestClient c, Setup s, BookTransportRequest body, string? key = null) =>
        await OkAsync<TripPassengerDto>(await Book(c, s, body, key), HttpStatusCode.Created);

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
