using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Resort.Contracts;
using Resort.Domain.Reservations;
using Resort.Tests.Support;
using static Resort.Tests.Support.BookingKit;

namespace Resort.Tests.Integration;

/// <summary>Sprint 5 — availability, reservations, idempotency and the status graph. Real API, real PostgreSQL.
/// Every test builds its own room types, so tests don't see each other's bookings.</summary>
public class ReservationTests(ResortApiFactory app) : IClassFixture<ResortApiFactory>
{
    // ------------------------------------------------------------------ brief §16: no double booking

    [Fact]
    public async Task Two_bookings_for_the_last_room_at_the_same_moment_exactly_one_wins_50_times()
    {
        var (owner, pid) = await OwnerAsync();
        var (receptionEmail, receptionPw) = await app.CreateUserAsync("RECEPTIONIST", pid);
        var desk = await app.SignInAsync(receptionEmail, receptionPw);
        var guest = await CreateGuestAsync(owner);
        var source = await WalkInAsync(owner, pid);
        var checkIn = Today.AddDays(30);

        for (var run = 0; run < 50; run++)
        {
            var stay = await SetupAsync(owner, pid, rooms: 1);
            var body = NewBooking(stay, guest.Id, source, checkIn, checkIn.AddDays(2));

            // two browsers press "Book" together
            using var barrier = new Barrier(2);
            var a = Task.Run(() => { barrier.SignalAndWait(); return BookAsync(owner, pid, body); });
            var b = Task.Run(() => { barrier.SignalAndWait(); return BookAsync(desk, pid, body); });
            var results = await Task.WhenAll(a, b);

            Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
            var loser = Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
            Assert.Equal("ROOM_UNAVAILABLE", await loser.ProblemCodeAsync());
            Assert.Equal(1L, await ScalarAsync(
                $"SELECT count(*) FROM reservation_units WHERE unit_id = '{stay.Rooms[0].Id}' AND holds_inventory"));
        }
    }

    [Fact]
    public async Task Dorm_with_one_bed_left_and_five_parallel_requests_books_exactly_one()
    {
        var (owner, pid) = await OwnerAsync();
        var guest = await CreateGuestAsync(owner);
        var source = await WalkInAsync(owner, pid);
        var checkIn = Today.AddDays(40);

        for (var run = 0; run < 10; run++)
        {
            var dorm = await SetupAsync(owner, pid, rooms: 1, mode: "BED", beds: 3, price: 800m);
            // two of the three beds are already taken
            Assert.Equal(HttpStatusCode.Created,
                (await BookAsync(owner, pid, NewBooking(dorm, guest.Id, source, checkIn, checkIn.AddDays(3), adults: 2))).StatusCode);

            using var barrier = new Barrier(5);
            var tasks = Enumerable.Range(0, 5).Select(_ => Task.Run(() =>
            {
                barrier.SignalAndWait();
                return BookAsync(owner, pid, NewBooking(dorm, guest.Id, source, checkIn.AddDays(1), checkIn.AddDays(2)));
            })).ToList();
            var results = await Task.WhenAll(tasks);

            Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
            Assert.Equal(4, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
            foreach (var r in results.Where(r => r.StatusCode == HttpStatusCode.Conflict))
                Assert.Equal("ROOM_UNAVAILABLE", await r.ProblemCodeAsync());
        }
    }

    [Fact]
    public async Task The_database_itself_refuses_two_held_rows_for_the_same_room_and_night()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 1);
        var r = await CreatedAsync(await BookAsync(owner, pid,
            NewBooking(stay, (await CreateGuestAsync(owner)).Id, await WalkInAsync(owner, pid), Today.AddDays(5), Today.AddDays(8))));

        // bypass the API: copy the held row onto overlapping nights
        var ex = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"""
            INSERT INTO reservation_units (organization_id, property_id, reservation_id, accommodation_type_id, unit_id, start_date, end_date, adults, holds_inventory)
            SELECT organization_id, property_id, reservation_id, accommodation_type_id, unit_id, start_date + 2, end_date + 2, adults, true
              FROM reservation_units WHERE reservation_id = '{r.Id}'
            """));
        Assert.Equal(PostgresErrorCodes.ExclusionViolation, ex.SqlState);
        Assert.Equal("ex_reservation_units_unit", ex.ConstraintName);

        // the same nights on a superseded (not holding) row are fine: history doesn't block anything
        await SqlAsync($"""
            INSERT INTO reservation_units (organization_id, property_id, reservation_id, accommodation_type_id, unit_id, start_date, end_date, adults, holds_inventory, superseded_at)
            SELECT organization_id, property_id, reservation_id, accommodation_type_id, unit_id, start_date, end_date, adults, false, now()
              FROM reservation_units WHERE reservation_id = '{r.Id}'
            """);

        // and nothing about a reservation can be deleted
        var del = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"DELETE FROM reservations WHERE id = '{r.Id}'"));
        Assert.Contains("cannot be deleted", del.MessageText);
    }

    // ------------------------------------------------------------------ idempotency

    [Fact]
    public async Task Same_request_retried_with_the_same_key_books_once_and_returns_the_identical_response()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 3);
        var body = NewBooking(stay, (await CreateGuestAsync(owner)).Id, await WalkInAsync(owner, pid), Today.AddDays(12), Today.AddDays(14));
        var key = "retry-" + Guid.NewGuid();

        var first = await BookAsync(owner, pid, body, key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        var second = await BookAsync(owner, pid, body, key);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Equal(first.Headers.Location, second.Headers.Location);

        // five more retries at the same moment (a phone on bad Wi-Fi): still one booking
        using var barrier = new Barrier(5);
        var retries = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            return BookAsync(owner, pid, body, key);
        })));
        var id = (await CreatedAsync(first)).Id;
        foreach (var r in retries) Assert.Equal(id, (await CreatedAsync(r)).Id);
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM reservation_units WHERE accommodation_type_id = '{stay.Type.Id}'"));

        // a brand-new key books a second room
        var other = await CreatedAsync(await BookAsync(owner, pid, body));
        Assert.NotEqual(id, other.Id);
    }

    [Fact]
    public async Task Same_key_with_a_different_body_is_refused_and_a_missing_key_says_why()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 2);
        var body = NewBooking(stay, (await CreateGuestAsync(owner)).Id, await WalkInAsync(owner, pid), Today.AddDays(3), Today.AddDays(4));
        var key = "key-" + Guid.NewGuid();
        var first = await CreatedAsync(await BookAsync(owner, pid, body, key));

        var changed = await BookAsync(owner, pid, body with { CheckOut = Today.AddDays(5) }, key);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, changed.StatusCode);
        var err = await changed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", err.GetProperty("code").GetString());
        Assert.Contains(first.Reference, err.GetProperty("message").GetString());

        var noKey = await owner.PostAsync($"/api/v1/properties/{pid}/reservations", body);
        Assert.Contains("Idempotency-Key header", await FieldErrorAsync(noKey, "Idempotency-Key"));
        var shortKey = await BookAsync(owner, pid, body, "abc");
        Assert.Contains("8–100 characters", await FieldErrorAsync(shortKey, "Idempotency-Key"));
    }

    [Fact]
    public async Task A_failed_booking_is_not_stored_so_its_retry_is_judged_again()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 1);
        var guest = await CreateGuestAsync(owner);
        var source = await WalkInAsync(owner, pid);
        var holder = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, Today.AddDays(9), Today.AddDays(10))));

        var key = "fail-" + Guid.NewGuid();
        var body = NewBooking(stay, guest.Id, source, Today.AddDays(9), Today.AddDays(10));
        Assert.Equal(HttpStatusCode.Conflict, (await BookAsync(owner, pid, body, key)).StatusCode);
        await PostAsync(owner, $"/api/v1/properties/{pid}/reservations/{holder.Id}/cancel", new ReservationStatusRequest("Guest changed plans"));
        Assert.Equal(HttpStatusCode.Created, (await BookAsync(owner, pid, body, key)).StatusCode);
    }

    // ------------------------------------------------------------------ inventory

    [Fact]
    public async Task Cancelling_frees_the_room_and_availability_shows_it_again()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 1, price: 4000m, makeDefault: true);
        var guest = await CreateGuestAsync(owner);
        var source = await WalkInAsync(owner, pid);
        var (from, to) = (Today.AddDays(20), Today.AddDays(23));

        var before = await AvailabilityAsync(owner, pid, from, to, stay.Type.Id);
        Assert.Equal((1, 1, true, 12000m), (before.Total, before.Free, before.Fits, before.Price));
        Assert.Equal(stay.Rooms[0].Id, Assert.Single(before.FreeRooms).Id);

        var booked = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, from, to)));
        Assert.Equal("CONFIRMED", booked.Status);
        Assert.Matches("^R[0-9]{5,}$", booked.Reference);
        var after = await AvailabilityAsync(owner, pid, from, to, stay.Type.Id);
        Assert.Equal(0, after.Free);
        Assert.False(after.Fits);
        // an overlapping stay is refused with the reason; one starting on the departure day is fine (half-open dates)
        var clash = await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, to.AddDays(-1), to.AddDays(1)) with
        {
            Rooms = [new ReservationRoomRequest(stay.Type.Id, stay.Rooms[0].Id, 2, 0)],
        });
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Contains($"booked on {booked.Reference}", (await clash.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        var next = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, to, to.AddDays(1))));

        var noReason = await owner.PostAsync($"/api/v1/properties/{pid}/reservations/{booked.Id}/cancel", new ReservationStatusRequest(null));
        Assert.Contains("Say why", await FieldErrorAsync(noReason, "Reason"));
        var cancelled = await PostAsync(owner, $"/api/v1/properties/{pid}/reservations/{booked.Id}/cancel", new ReservationStatusRequest("Guest changed plans"));
        Assert.Equal("CANCELLED", cancelled.Status);
        Assert.All(cancelled.Units, u => Assert.False(u.HoldsInventory));

        Assert.Equal(1, (await AvailabilityAsync(owner, pid, from, to, stay.Type.Id)).Free);
        Assert.Equal(HttpStatusCode.Created, (await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, from, to))).StatusCode);
        Assert.NotEqual(next.Id, booked.Id);
    }

    [Fact]
    public async Task Blocks_and_bookings_respect_each_other()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 1);
        var guest = await CreateGuestAsync(owner);
        var source = await WalkInAsync(owner, pid);
        var booked = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, Today.AddDays(10), Today.AddDays(12))));

        var block = await owner.PostAsync($"/api/v1/properties/{pid}/blocks",
            new CreateBlockRequest(stay.Rooms[0].Id, null, "MAINTENANCE", Today.AddDays(11), Today.AddDays(13), "AC repair"));
        Assert.Equal(HttpStatusCode.Conflict, block.StatusCode);
        Assert.Equal("BLOCK_OVERLAPS_RESERVATION", await block.ProblemCodeAsync());

        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsync($"/api/v1/properties/{pid}/blocks",
            new CreateBlockRequest(stay.Rooms[0].Id, null, "MAINTENANCE", Today.AddDays(12), Today.AddDays(14), "AC repair"))).StatusCode);
        var onBlock = await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, Today.AddDays(13), Today.AddDays(15)));
        Assert.Equal(HttpStatusCode.Conflict, onBlock.StatusCode);
        Assert.Equal("ROOM_UNAVAILABLE", await onBlock.ProblemCodeAsync());

        // the database's own guard, bypassing the API
        var ex = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync($"""
            UPDATE reservation_units SET end_date = end_date + 2 WHERE reservation_id = '{booked.Id}'
            """));
        Assert.Equal("ex_reservation_units_blocked", ex.ConstraintName);
    }

    [Fact]
    public async Task An_enquiry_holds_nothing_and_must_find_a_free_room_to_be_confirmed()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 1);
        var guest = await CreateGuestAsync(owner);
        var source = await WalkInAsync(owner, pid);
        var (from, to) = (Today.AddDays(15), Today.AddDays(17));

        var enquiry = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, from, to) with { Status = "INQUIRY" }));
        Assert.Equal("INQUIRY", enquiry.Status);
        Assert.False(Assert.Single(enquiry.Units).HoldsInventory);
        Assert.Equal(1, (await AvailabilityAsync(owner, pid, from, to, stay.Type.Id)).Free);

        var booking = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, from, to)));
        var confirm = await owner.PostAsync($"/api/v1/properties/{pid}/reservations/{enquiry.Id}/confirm", new ReservationStatusRequest(null));
        Assert.Equal(HttpStatusCode.Conflict, confirm.StatusCode);
        Assert.Equal("ROOM_UNAVAILABLE", await confirm.ProblemCodeAsync());

        await PostAsync(owner, $"/api/v1/properties/{pid}/reservations/{booking.Id}/cancel", new ReservationStatusRequest("Double entry"));
        var confirmed = await PostAsync(owner, $"/api/v1/properties/{pid}/reservations/{enquiry.Id}/confirm", new ReservationStatusRequest(null));
        Assert.Equal("CONFIRMED", confirmed.Status);
        Assert.True(Assert.Single(confirmed.Units).HoldsInventory);
        Assert.Equal(["INQUIRY", "CONFIRMED"], confirmed.History.Select(h => h.ToStatus));
    }

    // ------------------------------------------------------------------ status graph

    [Fact]
    public async Task Status_changes_outside_the_allowed_graph_are_refused_with_the_reason()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 2);
        var guest = await CreateGuestAsync(owner);
        var source = await WalkInAsync(owner, pid);
        var pending = await CreatedAsync(await BookAsync(owner, pid,
            NewBooking(stay, guest.Id, source, Today.AddDays(2), Today.AddDays(3)) with { Status = "PENDING" }));

        // no-show only applies to confirmed bookings
        var noShow = await owner.PostAsync($"/api/v1/properties/{pid}/reservations/{pending.Id}/no-show", new ReservationStatusRequest("Didn't come"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noShow.StatusCode);
        var err = await noShow.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVALID_STATUS_TRANSITION", err.GetProperty("code").GetString());
        Assert.Contains("pending, so it can't be marked no-show", err.GetProperty("message").GetString());

        await PostAsync(owner, $"/api/v1/properties/{pid}/reservations/{pending.Id}/cancel", new ReservationStatusRequest("Guest changed plans"));
        foreach (var action in new[] { "cancel", "confirm", "no-show" })
        {
            var res = await owner.PostAsync($"/api/v1/properties/{pid}/reservations/{pending.Id}/{action}", new ReservationStatusRequest("Trying again"));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
            Assert.Equal("INVALID_STATUS_TRANSITION", await res.ProblemCodeAsync());
        }
        var changeCancelled = await owner.PutAsync($"/api/v1/properties/{pid}/reservations/{pending.Id}",
            new UpdateReservationRequest(Today.AddDays(2), Today.AddDays(4), [new(stay.Type.Id, null, 2, 0)], null, 0));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, changeCancelled.StatusCode);

        // the database refuses it too, whatever writes to it
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            SqlAsync($"UPDATE reservations SET status = 'CONFIRMED' WHERE id = '{pending.Id}'"));
        Assert.Equal("ck_reservations_status_transition", ex.ConstraintName);

        // a confirmed booking can't be a no-show before its arrival day
        var future = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, Today.AddDays(2), Today.AddDays(3))));
        var early = await owner.PostAsync($"/api/v1/properties/{pid}/reservations/{future.Id}/no-show", new ReservationStatusRequest("Didn't come"));
        Assert.Equal("NO_SHOW_TOO_EARLY", await early.ProblemCodeAsync());
        // … and from its arrival day it can, which frees the room
        var tonight = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, Today, Today.AddDays(1))));
        var gone = await PostAsync(owner, $"/api/v1/properties/{pid}/reservations/{tonight.Id}/no-show", new ReservationStatusRequest("Didn't arrive by midnight"));
        Assert.Equal("NO_SHOW", gone.Status);
        Assert.All(gone.Units, u => Assert.False(u.HoldsInventory));
    }

    [Fact]
    public async Task The_code_and_the_database_use_the_same_status_graph()
    {
        var rows = await RowsAsync("SELECT from_status, to_status FROM reservation_status_transitions");
        var db = rows.Select(r => ((string?)r[0], (string)r[1]!)).ToHashSet();
        var code = ReservationStatuses.Transitions.SelectMany(kv => kv.Value.Select(to => ((string?)kv.Key, to)))
            .Concat(ReservationStatuses.Initial.Select(s => ((string?)null, s))).ToHashSet();
        Assert.True(db.SetEquals(code), $"database: {string.Join(", ", db)}; code: {string.Join(", ", code)}");
    }

    [Fact]
    public async Task Every_status_change_is_in_the_history_and_the_audit_trail_with_its_reason()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 1);
        var r = await CreatedAsync(await BookAsync(owner, pid,
            NewBooking(stay, (await CreateGuestAsync(owner)).Id, await WalkInAsync(owner, pid), Today.AddDays(6), Today.AddDays(7)) with
            {
                Status = "PENDING",
                Note = "Arriving by the late bus",
            }));
        await PostAsync(owner, $"/api/v1/properties/{pid}/reservations/{r.Id}/confirm", new ReservationStatusRequest(null));
        var done = await PostAsync(owner, $"/api/v1/properties/{pid}/reservations/{r.Id}/cancel", new ReservationStatusRequest("Flight cancelled"));

        Assert.Equal([(null, "PENDING"), ("PENDING", "CONFIRMED"), ("CONFIRMED", "CANCELLED")],
            done.History.Select(h => (h.FromStatus, h.ToStatus)));
        Assert.Equal("Flight cancelled", done.History[^1].Reason);
        Assert.Equal("Test Owner", done.History[^1].ChangedByName);
        Assert.Equal("Arriving by the late bus", Assert.Single(done.Notes).Note);
        Assert.Empty(done.NextStatuses.Except(["REFUNDED"]));

        var audit = await RowsAsync(
            $"SELECT reason, changes::text FROM audit_logs WHERE entity_type = 'Reservation' AND entity_id = '{r.Id}' AND action = 'UPDATE' ORDER BY id");
        var last = audit[^1];
        Assert.Equal("Flight cancelled", last[0]);
        using var changes = JsonDocument.Parse((string)last[1]!);
        Assert.Equal("CONFIRMED", changes.RootElement.GetProperty("Status").GetProperty("old").GetString());
        Assert.Equal("CANCELLED", changes.RootElement.GetProperty("Status").GetProperty("new").GetString());

        var edit = await Assert.ThrowsAsync<PostgresException>(() =>
            SqlAsync($"UPDATE reservation_status_history SET reason = 'x' WHERE reservation_id = '{r.Id}'"));
        Assert.Contains("cannot be changed", edit.MessageText);
    }

    // ------------------------------------------------------------------ changes and prices

    [Fact]
    public async Task Changing_dates_checks_availability_again_keeps_the_room_and_re_prices()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 2, price: 3000m, extraAdult: 1000m, baseOcc: 2, maxOcc: 3);
        var guest = await CreateGuestAsync(owner);
        var source = await WalkInAsync(owner, pid);
        var d = Today.AddDays(50);

        var a = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, d, d.AddDays(2)) with
        {
            Rooms = [new ReservationRoomRequest(stay.Type.Id, stay.Rooms[1].Id, 3, 0)],
        }));
        Assert.Equal(2 * (3000m + 1000m), a.TotalAmount);    // 2 nights, one extra adult
        Assert.Equal(2, a.Breakdown.Count);
        var b = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, d.AddDays(2), d.AddDays(4)) with
        {
            Rooms = [new ReservationRoomRequest(stay.Type.Id, stay.Rooms[1].Id, 2, 0)],
        }));

        // extend A into B's nights in the same room: refused, naming B
        var clash = await owner.PutAsync($"/api/v1/properties/{pid}/reservations/{a.Id}",
            new UpdateReservationRequest(d, d.AddDays(3), [new(stay.Type.Id, stay.Rooms[1].Id, 3, 0)], null, a.Version));
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Contains(b.Reference, (await clash.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());

        // any room of the type: A keeps nothing it can't have, takes the other room, re-priced for 3 nights, 2 adults
        var moved = await owner.PutAsync($"/api/v1/properties/{pid}/reservations/{a.Id}",
            new UpdateReservationRequest(d, d.AddDays(3), [new(stay.Type.Id, null, 2, 0)], "Late check-out", a.Version));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var changed = (await moved.Content.ReadFromJsonAsync<ReservationDto>())!;
        Assert.Equal(3 * 3000m, changed.TotalAmount);
        Assert.Equal(stay.Rooms[0].Id, Assert.Single(changed.Units, u => u.Current).UnitId);
        Assert.Single(changed.Units, u => !u.Current);                 // the old row is kept as history
        Assert.Equal("Late check-out", changed.SpecialRequests);

        // shortening keeps the same room
        var shorter = await owner.PutAsync($"/api/v1/properties/{pid}/reservations/{a.Id}",
            new UpdateReservationRequest(d, d.AddDays(1), [new(stay.Type.Id, null, 2, 0)], null, changed.Version));
        var s = (await shorter.Content.ReadFromJsonAsync<ReservationDto>())!;
        Assert.Equal(stay.Rooms[0].Id, Assert.Single(s.Units, u => u.Current).UnitId);
        Assert.Equal(3000m, s.TotalAmount);

        // a stale version is refused
        var stale = await owner.PutAsync($"/api/v1/properties/{pid}/reservations/{a.Id}",
            new UpdateReservationRequest(d, d.AddDays(2), [new(stay.Type.Id, null, 2, 0)], null, a.Version));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("CONCURRENCY_CONFLICT", await stale.ProblemCodeAsync());
    }

    [Fact]
    public async Task A_dorm_group_takes_one_bed_each_priced_per_bed()
    {
        var (owner, pid) = await OwnerAsync();
        var dorm = await SetupAsync(owner, pid, rooms: 1, mode: "BED", beds: 6, price: 850m, makeDefault: true);
        var guest = await CreateGuestAsync(owner);
        var (from, to) = (Today.AddDays(8), Today.AddDays(10));

        var av = await AvailabilityAsync(owner, pid, from, to, dorm.Type.Id, adults: 3, children: 1);
        Assert.Equal((6, 6, true), (av.Total, av.Free, av.Fits));
        Assert.Equal(4 * 2 * 850m, av.Price);

        var r = await CreatedAsync(await BookAsync(owner, pid, NewBooking(dorm, guest.Id, await WalkInAsync(owner, pid), from, to, adults: 3, children: 1)));
        Assert.Equal(4, r.Units.Count(u => u.Current && u.BedId != null));
        Assert.Equal(4 * 2 * 850m, r.TotalAmount);
        Assert.Equal((3, 1), (r.Adults, r.Children));
        Assert.Equal(2, (await AvailabilityAsync(owner, pid, from, to, dorm.Type.Id)).Free);

        var tooMany = await BookAsync(owner, pid, NewBooking(dorm, guest.Id, await WalkInAsync(owner, pid), from, to, adults: 3));
        Assert.Equal(HttpStatusCode.Conflict, tooMany.StatusCode);
        Assert.Contains("Only 2 beds are free", (await tooMany.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
    }

    [Fact]
    public async Task Field_errors_say_why_under_the_field()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 1, maxOcc: 2);
        var guest = await CreateGuestAsync(owner);
        var source = await WalkInAsync(owner, pid);

        var past = await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, Today.AddDays(-1), Today.AddDays(1)));
        Assert.Contains("Check-in can't be before today", await FieldErrorAsync(past, "CheckIn"));

        var backwards = await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, Today.AddDays(3), Today.AddDays(3)));
        Assert.Equal("The check-out date must be after check-in.", await FieldErrorAsync(backwards, "CheckOut"));

        var crowded = await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, Today.AddDays(3), Today.AddDays(4), adults: 3));
        Assert.Contains("sleeps at most 2 guests; this room has 3", await FieldErrorAsync(crowded, "Rooms[0].Adults"));

        var noAdult = await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, Today.AddDays(3), Today.AddDays(4), adults: 0, children: 1));
        Assert.Equal("Each room needs at least one adult.", await FieldErrorAsync(noAdult, "Rooms[0].Adults"));

        var noGuest = await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, Today.AddDays(3), Today.AddDays(4)) with { GuestIds = [] });
        Assert.Equal("Choose the guest the booking is for.", await FieldErrorAsync(noGuest, "GuestIds"));

        var noRate = await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, Today.AddDays(300), Today.AddDays(301)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noRate.StatusCode);
        Assert.Equal("RATE_NOT_SET", await noRate.ProblemCodeAsync());

        var badAvailability = await owner.GetAsync($"/api/v1/properties/{pid}/availability?from={Today.AddDays(3):yyyy-MM-dd}&to={Today.AddDays(2):yyyy-MM-dd}");
        Assert.Equal("At least one adult is needed.", await FieldErrorAsync(badAvailability, "Adults"));
    }

    // ------------------------------------------------------------------ lists, guests, access

    [Fact]
    public async Task List_search_and_guest_stays_find_the_booking()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 1);
        var guest = await CreateGuestAsync(owner, "Kavya", "Nair" + Suffix().ToLowerInvariant());
        var r = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, await WalkInAsync(owner, pid), Today.AddDays(4), Today.AddDays(6))));

        var byName = await owner.GetJsonAsync<PagedResult<ReservationListItemDto>>(
            $"/api/v1/properties/{pid}/reservations?q={Uri.EscapeDataString(guest.LastName!)}");
        var item = Assert.Single(byName.Items);
        Assert.Equal((r.Id, guest.FullName, stay.Rooms[0].Name, "Walk-in"), (item.Id, item.GuestName, item.Rooms, item.SourceName));

        var byRef = await owner.GetJsonAsync<PagedResult<ReservationListItemDto>>(
            $"/api/v1/properties/{pid}/reservations?q={r.Reference.ToLowerInvariant()}");
        Assert.Equal(r.Id, Assert.Single(byRef.Items).Id);

        var badStatus = await owner.GetAsync($"/api/v1/properties/{pid}/reservations?status=BOOKED");
        Assert.Contains("'BOOKED' isn't a reservation status", await FieldErrorAsync(badStatus, "Status"));

        var stays = await owner.GetJsonAsync<List<GuestStayDto>>($"/api/v1/guests/{guest.Id}/stays");
        Assert.Equal((r.Id, r.Reference, "CONFIRMED"), (Assert.Single(stays).ReservationId, stays[0].Reference, stays[0].Status));
    }

    [Fact]
    public async Task Who_may_see_and_book_and_other_tenants_get_not_found()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 2);
        var guest = await CreateGuestAsync(owner);
        var source = await WalkInAsync(owner, pid);
        var r = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, Today.AddDays(3), Today.AddDays(4))));

        var (hkEmail, hkPw) = await app.CreateUserAsync("HOUSEKEEPER", pid);
        var housekeeper = await app.SignInAsync(hkEmail, hkPw);
        Assert.Equal(HttpStatusCode.Forbidden, (await housekeeper.GetAsync($"/api/v1/properties/{pid}/reservations")).StatusCode);

        var (supEmail, supPw) = await app.CreateUserAsync("HOUSEKEEPING_SUPERVISOR", pid);
        var supervisor = await app.SignInAsync(supEmail, supPw);
        Assert.Equal(HttpStatusCode.OK, (await supervisor.GetAsync($"/api/v1/properties/{pid}/reservations/{r.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await BookAsync(supervisor, pid, NewBooking(stay, guest.Id, source, Today.AddDays(3), Today.AddDays(4)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await supervisor.PostAsync($"/api/v1/properties/{pid}/reservations/{r.Id}/cancel",
            new ReservationStatusRequest("Not mine to cancel"))).StatusCode);

        var (_, otherPid, otherEmail, otherPw) = await app.CreateSecondTenantAsync();
        var stranger = await app.SignInAsync(otherEmail, otherPw);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/properties/{otherPid}/reservations/{r.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/properties/{pid}/reservations/{r.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/properties/{pid}/reservations")).StatusCode);
    }

    [Fact]
    public async Task Merging_guests_moves_their_bookings_to_the_survivor()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 1);
        var keep = await CreateGuestAsync(owner);
        var dup = await CreateGuestAsync(owner);
        var r = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, dup.Id, await WalkInAsync(owner, pid), Today.AddDays(3), Today.AddDays(4))));

        var merge = await owner.PostAsync("/api/v1/guests/merge", new MergeGuestsRequest(keep.Id, keep.Version, dup.Id, dup.Version, "Same person"));
        Assert.Equal(HttpStatusCode.OK, merge.StatusCode);
        var after = await owner.GetJsonAsync<ReservationDto>($"/api/v1/properties/{pid}/reservations/{r.Id}");
        Assert.Equal(keep.Id, Assert.Single(after.Guests).GuestId);
        Assert.Equal(r.Id, Assert.Single(await owner.GetJsonAsync<List<GuestStayDto>>($"/api/v1/guests/{keep.Id}/stays")).ReservationId);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(TestClient Owner, Guid PropertyId)> OwnerAsync() => (await app.SignInOwnerAsync(), await app.MainPropertyIdAsync());

    private async Task SqlAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
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
