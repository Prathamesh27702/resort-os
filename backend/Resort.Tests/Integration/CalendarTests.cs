using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Resort.Contracts;
using Resort.Tests.Support;
using static Resort.Tests.Support.BookingKit;

namespace Resort.Tests.Integration;

/// <summary>Sprint 6 — the reservation calendar and moving a booking to another room. Real API, real PostgreSQL.
/// Every test builds its own rooms, so tests don't see each other's bookings.</summary>
public class CalendarTests(ResortApiFactory app) : IClassFixture<ResortApiFactory>
{
    // ------------------------------------------------------------------ brief §16 / §58: moves can't double book

    [Fact]
    public async Task Two_users_moving_bookings_into_the_same_room_at_the_same_moment_exactly_one_wins_20_times()
    {
        var (owner, pid) = await OwnerAsync();
        var (receptionEmail, receptionPw) = await app.CreateUserAsync("RECEPTIONIST", pid);
        var desk = await app.SignInAsync(receptionEmail, receptionPw);
        var guest = await CreateGuestAsync(owner);
        var source = await WalkInAsync(owner, pid);
        var (from, to) = (Today.AddDays(30), Today.AddDays(33));

        for (var run = 0; run < 20; run++)
        {
            var stay = await SetupAsync(owner, pid, rooms: 3);
            var a = await BookInAsync(owner, pid, stay, 0, guest.Id, source, from, to);
            var b = await BookInAsync(owner, pid, stay, 1, guest.Id, source, from.AddDays(1), to.AddDays(1));
            var target = stay.Rooms[2].Id;

            // two receptionists drag a booking onto Room 3 together
            using var barrier = new Barrier(2);
            var moveA = Task.Run(() => { barrier.SignalAndWait(); return ReassignAsync(owner, pid, a, target, "Guest asked for a sea view"); });
            var moveB = Task.Run(() => { barrier.SignalAndWait(); return ReassignAsync(desk, pid, b, target, "Upgrade for an anniversary"); });
            var results = await Task.WhenAll(moveA, moveB);

            Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
            var loser = Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
            var (code, message) = await ProblemAsync(loser);
            Assert.Equal("ROOM_UNAVAILABLE", code);
            Assert.Matches("isn't free for the nights of .*: it is booked on R[0-9]{5,}", message);
            Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM reservation_units WHERE unit_id = '{target}' AND holds_inventory"));
            // the loser's booking is untouched: still in its own room, still holding it
            var lost = results[0].StatusCode == HttpStatusCode.Conflict ? a : b;
            var after = await owner.GetJsonAsync<ReservationDto>($"/api/v1/properties/{pid}/reservations/{lost.Id}");
            Assert.Equal(lost.Units.Single(u => u.Current).UnitId, after.Units.Single(u => u.Current).UnitId);
        }
    }

    [Fact]
    public async Task Reception_moves_a_guest_to_another_room_in_one_call_and_the_audit_trail_shows_who_and_why()
    {
        var (owner, pid) = await OwnerAsync();
        var (deskEmail, deskPw) = await app.CreateUserAsync("RECEPTIONIST", pid);
        var desk = await app.SignInAsync(deskEmail, deskPw);
        var stay = await SetupAsync(owner, pid, rooms: 2, price: 4000m);
        var guest = await CreateGuestAsync(owner, "Asha", "Iyer");
        var source = await WalkInAsync(owner, pid);
        var (from, to) = (Today.AddDays(12), Today.AddDays(15));
        var booked = await BookInAsync(owner, pid, stay, 0, guest.Id, source, from, to);
        var deskUserId = await ScalarAsync($"SELECT id FROM users WHERE email = '{deskEmail}'");

        var res = await ReassignAsync(desk, pid, booked, stay.Rooms[1].Id, "AC not working in the first room");
        var moved = await OkAsync(res);

        var current = Assert.Single(moved.Units, u => u.Current);
        Assert.Equal(stay.Rooms[1].Id, current.UnitId);
        Assert.Equal((from, to), (current.StartDate, current.EndDate));
        Assert.True(current.HoldsInventory);
        var old = Assert.Single(moved.Units, u => !u.Current);
        Assert.Equal(stay.Rooms[0].Id, old.UnitId);
        Assert.False(old.HoldsInventory);
        // the price stays as booked, night by night
        Assert.Equal(booked.TotalAmount, moved.TotalAmount);
        Assert.Equal(12000m, current.Total);
        Assert.Equal(3, moved.Breakdown.Count);
        Assert.All(moved.Breakdown, n => Assert.Equal(current.Id, n.ReservationUnitId));
        Assert.NotEqual(booked.Version, moved.Version);
        var note = Assert.Single(moved.Notes);
        Assert.Equal($"Moved from {stay.Rooms[0].Name} to {stay.Rooms[1].Name} (for the nights of {from:d MMM} – {to.AddDays(-1):d MMM yyyy}). " +
                     "Reason: AC not working in the first room", note.Note);

        // audit trail: who and why on both rows
        var audit = await RowsAsync($"""
            SELECT action, user_id, reason FROM audit_logs
             WHERE entity_type = 'ReservationUnit'
               AND ((entity_id = '{old.Id}' AND action = 'UPDATE') OR (entity_id = '{current.Id}' AND action = 'INSERT'))
             ORDER BY id
            """);
        Assert.Equal(2, audit.Count);
        Assert.All(audit, row => Assert.Equal(deskUserId, row[1]));
        Assert.All(audit, row => Assert.Equal("AC not working in the first room", row[2]));

        // the first room is free again for those nights; the second is not
        var cal = await CalendarAsync(owner, pid, from.AddDays(-1), to.AddDays(1));
        var entry = Assert.Single(cal.Bookings, x => x.ReservationId == booked.Id);
        Assert.Equal(stay.Rooms[1].Id, entry.UnitId);
        Assert.Equal(HttpStatusCode.Created, (await BookInRawAsync(owner, pid, stay, 0, guest.Id, source, from, to)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await BookInRawAsync(owner, pid, stay, 1, guest.Id, source, from, to)).StatusCode);
    }

    [Fact]
    public async Task A_move_onto_a_blocked_or_booked_room_is_refused_with_the_reason()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 3);
        var guest = await CreateGuestAsync(owner);
        var source = await WalkInAsync(owner, pid);
        var (from, to) = (Today.AddDays(20), Today.AddDays(22));
        var booked = await BookInAsync(owner, pid, stay, 0, guest.Id, source, from, to);
        var other = await BookInAsync(owner, pid, stay, 2, guest.Id, source, to.AddDays(-1), to.AddDays(2));
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsync($"/api/v1/properties/{pid}/blocks",
            new CreateBlockRequest(stay.Rooms[1].Id, null, "MAINTENANCE", from.AddDays(1), from.AddDays(4), "AC repair"))).StatusCode);

        var onBlock = await ReassignAsync(owner, pid, booked, stay.Rooms[1].Id, "Guest wants a quieter room");
        Assert.Equal(HttpStatusCode.Conflict, onBlock.StatusCode);
        Assert.Equal(("ROOM_UNAVAILABLE",
                $"{stay.Rooms[1].Name} isn't free for the nights of {from:d MMM} – {to.AddDays(-1):d MMM yyyy}: it is blocked (AC repair). Choose another room."),
            await ProblemAsync(onBlock));

        var onBooking = await ReassignAsync(owner, pid, booked, stay.Rooms[2].Id, "Guest wants a quieter room");
        Assert.Equal(HttpStatusCode.Conflict, onBooking.StatusCode);
        Assert.Contains($"it is booked on {other.Reference}", (await ProblemAsync(onBooking)).Message);

        // nothing changed
        var after = await owner.GetJsonAsync<ReservationDto>($"/api/v1/properties/{pid}/reservations/{booked.Id}");
        Assert.Equal(stay.Rooms[0].Id, Assert.Single(after.Units).UnitId);
        Assert.Empty(after.Notes);
    }

    [Fact]
    public async Task Field_errors_say_why_under_the_field()
    {
        var (owner, pid) = await OwnerAsync();
        var small = await SetupAsync(owner, pid, rooms: 1, maxOcc: 2);
        var big = await SetupAsync(owner, pid, rooms: 2, maxOcc: 3);
        var dorm = await SetupAsync(owner, pid, rooms: 1, mode: "BED", beds: 2, price: 800m);
        var guest = await CreateGuestAsync(owner);
        var source = await WalkInAsync(owner, pid);
        var (from, to) = (Today.AddDays(8), Today.AddDays(10));
        var trio = await CreatedAsync(await BookAsync(owner, pid,
            NewBooking(big, guest.Id, source, from, to, adults: 3) with { Rooms = [new ReservationRoomRequest(big.Type.Id, big.Rooms[0].Id, 3, 0)] }));
        var unit = trio.Units.Single();
        string Url(ReservationDto r) => $"/api/v1/properties/{pid}/reservations/{r.Id}/reassign";

        Assert.Equal("Say why the guest is moving (at least 3 characters), e.g. AC not working in 102.",
            await FieldErrorAsync(await owner.PostAsync(Url(trio), new ReassignRoomRequest(unit.Id, big.Rooms[1].Id, null, " ", trio.Version)), "Reason"));
        Assert.Equal("The reason must be at least 3 characters.",
            await FieldErrorAsync(await owner.PostAsync(Url(trio), new ReassignRoomRequest(unit.Id, big.Rooms[1].Id, null, "ab", trio.Version)), "Reason"));
        Assert.Equal("Choose the room to move the guest to.",
            await FieldErrorAsync(await owner.PostAsync(Url(trio), new ReassignRoomRequest(unit.Id, Guid.Empty, null, "Upgrade", trio.Version)), "UnitId"));
        Assert.Contains("sleeps at most 2; this room has 3 guests",
            await FieldErrorAsync(await owner.PostAsync(Url(trio), new ReassignRoomRequest(unit.Id, small.Rooms[0].Id, null, "Upgrade", trio.Version)), "UnitId"));
        Assert.Contains("is a dormitory, sold by the bed",
            await FieldErrorAsync(await owner.PostAsync(Url(trio), new ReassignRoomRequest(unit.Id, dorm.Rooms[0].Id, null, "Upgrade", trio.Version)), "UnitId"));
        Assert.Contains("The guest is already in",
            await FieldErrorAsync(await owner.PostAsync(Url(trio), new ReassignRoomRequest(unit.Id, big.Rooms[0].Id, null, "Upgrade", trio.Version)), "UnitId"));
        Assert.Contains("is no longer part of",
            await FieldErrorAsync(await owner.PostAsync(Url(trio), new ReassignRoomRequest(Guid.NewGuid(), big.Rooms[1].Id, null, "Upgrade", trio.Version)), "ReservationUnitId"));

        // someone changed the booking since the calendar was loaded
        var stale = await owner.PostAsync(Url(trio), new ReassignRoomRequest(unit.Id, big.Rooms[1].Id, null, "Upgrade", trio.Version + 1));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("CONCURRENCY_CONFLICT", await stale.ProblemCodeAsync());

        // dorm beds: a bed must be chosen, and a bed guest can't go to a private room
        var bedGuest = await CreatedAsync(await BookAsync(owner, pid, NewBooking(dorm, guest.Id, source, from, to)));
        var bedRow = bedGuest.Units.Single();
        Assert.Equal($"Choose the bed in {dorm.Rooms[0].Name}.",
            await FieldErrorAsync(await owner.PostAsync(Url(bedGuest), new ReassignRoomRequest(bedRow.Id, dorm.Rooms[0].Id, null, "Lower bunk", bedGuest.Version)), "BedId"));
        Assert.Contains("is a private room",
            await FieldErrorAsync(await owner.PostAsync(Url(bedGuest), new ReassignRoomRequest(bedRow.Id, big.Rooms[1].Id, null, "Lower bunk", bedGuest.Version)), "UnitId"));
        var beds = (await owner.GetJsonAsync<CalendarDto>($"/api/v1/properties/{pid}/calendar?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}"))
            .Rooms.Single(r => r.Id == dorm.Rooms[0].Id).Beds;
        var otherBed = beds.Single(b => b.Id != bedRow.BedId);
        var movedBed = await OkAsync(await owner.PostAsync(Url(bedGuest),
            new ReassignRoomRequest(bedRow.Id, dorm.Rooms[0].Id, otherBed.Id, "Lower bunk", bedGuest.Version)));
        Assert.Equal(otherBed.Id, movedBed.Units.Single(u => u.Current).BedId);

        // a cancelled booking can't be moved
        var cancelled = await OkAsync(await owner.PostAsync($"/api/v1/properties/{pid}/reservations/{trio.Id}/cancel",
            new ReservationStatusRequest("Guest changed plans")));
        var gone = await owner.PostAsync(Url(cancelled), new ReassignRoomRequest(unit.Id, big.Rooms[1].Id, null, "Upgrade", cancelled.Version));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, gone.StatusCode);
        var problem = await ProblemAsync(gone);
        Assert.Equal("RESERVATION_NOT_MOVABLE", problem.Code);
        Assert.Contains("is cancelled, so its room can't be changed", problem.Message);
    }

    // ------------------------------------------------------------------ the calendar read model

    [Fact]
    public async Task The_calendar_shows_rooms_bookings_and_blocks_in_the_window()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 2);
        var dorm = await SetupAsync(owner, pid, rooms: 1, mode: "BED", beds: 3, price: 800m);
        var guest = await CreateGuestAsync(owner, "Ravi", "Menon");
        var source = await WalkInAsync(owner, pid);
        var (from, to) = (Today.AddDays(50), Today.AddDays(57));

        var early = await BookInAsync(owner, pid, stay, 0, guest.Id, source, from.AddDays(-3), from.AddDays(2));    // starts before the window
        var late = await BookInAsync(owner, pid, stay, 0, guest.Id, source, to.AddDays(-1), to.AddDays(3));         // ends after it
        var outside = await BookInAsync(owner, pid, stay, 1, guest.Id, source, to, to.AddDays(1));                  // departure day = window end
        var group = await CreatedAsync(await BookAsync(owner, pid, NewBooking(dorm, guest.Id, source, from, from.AddDays(2), adults: 2)));
        var enquiry = await CreatedAsync(await BookAsync(owner, pid,
            NewBooking(stay, guest.Id, source, from.AddDays(3), from.AddDays(4)) with { Status = "INQUIRY" }));
        var cancelled = await BookInAsync(owner, pid, stay, 1, guest.Id, source, from.AddDays(1), from.AddDays(2));
        await OkAsync(await owner.PostAsync($"/api/v1/properties/{pid}/reservations/{cancelled.Id}/cancel", new ReservationStatusRequest("Changed plans")));
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsync($"/api/v1/properties/{pid}/blocks",
            new CreateBlockRequest(stay.Rooms[1].Id, null, "OUT_OF_ORDER", from.AddDays(3), from.AddDays(5), "Leaking roof"))).StatusCode);

        var cal = await CalendarAsync(owner, pid, from, to);
        Assert.Equal((from, to, Today), (cal.From, cal.To, cal.Today));

        var room = cal.Rooms.Single(r => r.Id == stay.Rooms[0].Id);
        Assert.Equal((stay.Type.Name, "UNIT", true), (room.AccommodationTypeName, room.InventoryMode, room.IsActive));
        Assert.Empty(room.Beds);
        Assert.Equal(["B01", "B02", "B03"], cal.Rooms.Single(r => r.Id == dorm.Rooms[0].Id).Beds.Select(b => b.Code));

        var mine = cal.Bookings.Where(b => b.UnitId == stay.Rooms[0].Id || b.UnitId == stay.Rooms[1].Id || b.UnitId == dorm.Rooms[0].Id).ToList();
        Assert.Equal([early.Reference, group.Reference, group.Reference, late.Reference], mine.Select(b => b.Reference));
        var e = mine[0];
        Assert.Equal((from.AddDays(-3), from.AddDays(2)), (e.StartDate, e.EndDate));   // real dates; the client clips to the window
        Assert.Equal(("Ravi Menon", "Walk-in", "CONFIRMED", "NONE"), (e.GuestName, e.SourceName, e.Status, e.PaymentStatus));
        Assert.Equal(early.Version, e.Version);
        Assert.All(mine.Where(b => b.ReservationId == group.Id), b => Assert.NotNull(b.BedId));
        Assert.DoesNotContain(cal.Bookings, b => b.ReservationId == outside.Id || b.ReservationId == enquiry.Id || b.ReservationId == cancelled.Id);

        var block = Assert.Single(cal.Blocks, b => b.UnitId == stay.Rooms[1].Id);
        Assert.Equal(("OUT_OF_ORDER", "Leaking roof", (Guid?)null), (block.BlockType, block.Reason, block.BedId));
    }

    [Fact]
    public async Task The_calendar_window_is_checked_and_reads_need_the_permission()
    {
        var (owner, pid) = await OwnerAsync();
        var d = Today;
        Assert.Equal("Enter the first day to show (from=YYYY-MM-DD).",
            await FieldErrorAsync(await owner.GetAsync($"/api/v1/properties/{pid}/calendar?to={d:yyyy-MM-dd}"), "From"));
        Assert.Equal("The end of the window must be after its start.",
            await FieldErrorAsync(await owner.GetAsync($"/api/v1/properties/{pid}/calendar?from={d:yyyy-MM-dd}&to={d:yyyy-MM-dd}"), "To"));
        Assert.Equal("The calendar shows at most 62 days at a time; this window is 63.",
            await FieldErrorAsync(await owner.GetAsync($"/api/v1/properties/{pid}/calendar?from={d:yyyy-MM-dd}&to={d.AddDays(63):yyyy-MM-dd}"), "To"));
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/v1/properties/{pid}/calendar?from={d:yyyy-MM-dd}&to={d.AddDays(62):yyyy-MM-dd}")).StatusCode);

        var (hkEmail, hkPw) = await app.CreateUserAsync("HOUSEKEEPER", pid);
        var housekeeper = await app.SignInAsync(hkEmail, hkPw);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await housekeeper.GetAsync($"/api/v1/properties/{pid}/calendar?from={d:yyyy-MM-dd}&to={d.AddDays(7):yyyy-MM-dd}")).StatusCode);

        // a supervisor can read the calendar but not move bookings
        var stay = await SetupAsync(owner, pid, rooms: 2);
        var booked = await BookInAsync(owner, pid, stay, 0, (await CreateGuestAsync(owner)).Id, await WalkInAsync(owner, pid), d.AddDays(5), d.AddDays(6));
        var (supEmail, supPw) = await app.CreateUserAsync("HOUSEKEEPING_SUPERVISOR", pid);
        var supervisor = await app.SignInAsync(supEmail, supPw);
        Assert.Equal(HttpStatusCode.OK,
            (await supervisor.GetAsync($"/api/v1/properties/{pid}/calendar?from={d:yyyy-MM-dd}&to={d.AddDays(7):yyyy-MM-dd}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ReassignAsync(supervisor, pid, booked, stay.Rooms[1].Id, "Not mine to move")).StatusCode);

        var (_, _, otherEmail, otherPw) = await app.CreateSecondTenantAsync();
        var stranger = await app.SignInAsync(otherEmail, otherPw);
        Assert.Equal(HttpStatusCode.NotFound,
            (await stranger.GetAsync($"/api/v1/properties/{pid}/calendar?from={d:yyyy-MM-dd}&to={d.AddDays(7):yyyy-MM-dd}")).StatusCode);
    }

    [Fact]
    public async Task The_calendar_query_uses_the_calendar_index()
    {
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using (var off = new NpgsqlCommand("SET enable_seqscan = off", conn)) await off.ExecuteNonQueryAsync();
        await using var cmd = new NpgsqlCommand("""
            EXPLAIN SELECT id FROM reservation_units
             WHERE property_id = gen_random_uuid() AND superseded_at IS NULL AND start_date < '2027-02-01' AND '2027-01-01' < end_date
            """, conn);
        var plan = new List<string>();
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) plan.Add(r.GetString(0));
        Assert.Contains(plan, line => line.Contains("ix_reservation_units_calendar"));
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(TestClient Owner, Guid PropertyId)> OwnerAsync() => (await app.SignInOwnerAsync(), await app.MainPropertyIdAsync());

    /// <summary>Books room number <paramref name="room"/> of the stay for one guest.</summary>
    private static async Task<ReservationDto> BookInAsync(TestClient c, Guid pid, Stay s, int room, Guid guestId, Guid sourceId, DateOnly from, DateOnly to) =>
        await CreatedAsync(await BookInRawAsync(c, pid, s, room, guestId, sourceId, from, to));

    private static Task<HttpResponseMessage> BookInRawAsync(TestClient c, Guid pid, Stay s, int room, Guid guestId, Guid sourceId, DateOnly from, DateOnly to) =>
        BookAsync(c, pid, NewBooking(s, guestId, sourceId, from, to) with { Rooms = [new ReservationRoomRequest(s.Type.Id, s.Rooms[room].Id, 2, 0)] });

    private static Task<HttpResponseMessage> ReassignAsync(TestClient c, Guid pid, ReservationDto r, Guid toUnit, string reason) =>
        c.PostAsync($"/api/v1/properties/{pid}/reservations/{r.Id}/reassign",
            new ReassignRoomRequest(r.Units.Single(u => u.Current).Id, toUnit, null, reason, r.Version));

    private static Task<CalendarDto> CalendarAsync(TestClient c, Guid pid, DateOnly from, DateOnly to) =>
        c.GetJsonAsync<CalendarDto>($"/api/v1/properties/{pid}/calendar?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    private static async Task<ReservationDto> OkAsync(HttpResponseMessage res)
    {
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<ReservationDto>())!;
    }

    /// <summary>The error's code and message (the body can be read only once).</summary>
    private static async Task<(string? Code, string? Message)> ProblemAsync(HttpResponseMessage res)
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
