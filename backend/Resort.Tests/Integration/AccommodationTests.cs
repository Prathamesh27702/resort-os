using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Resort.Contracts;
using Resort.Tests.Support;

namespace Resort.Tests.Integration;

/// <summary>Sprint 2 — room types, rooms, dorm beds, blocks. Real API, real PostgreSQL.</summary>
public class AccommodationTests(ResortApiFactory app) : IClassFixture<ResortApiFactory>
{
    private static readonly TimeZoneInfo Ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
    private static DateOnly TodayAtProperty => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Ist).DateTime);

    // ------------------------------------------------------------------ setup & capacity

    [Fact]
    public async Task Owner_sets_up_rooms_and_a_dorm_and_capacity_is_calculated()
    {
        var (owner, pid) = await OwnerAsync();
        var deluxe = await CreateTypeAsync(owner, pid, "UNIT", baseOcc: 2, maxOcc: 3);
        var dorm = await CreateTypeAsync(owner, pid, "BED", baseOcc: 1, maxOcc: 1);
        await CreateRoomAsync(owner, pid, deluxe.Id);
        await CreateRoomAsync(owner, pid, deluxe.Id);
        var dormRoom = await CreateRoomAsync(owner, pid, dorm.Id);
        for (var i = 1; i <= 10; i++)
        {
            var bed = await owner.PostAsync($"/api/v1/properties/{pid}/rooms/{dormRoom.Id}/beds",
                new CreateBedRequest($"B{i:00}", $"Bed {i}", i % 2 == 1 ? "BUNK_LOWER" : "BUNK_UPPER", i));
            Assert.Equal(HttpStatusCode.Created, bed.StatusCode);
        }

        var types = await owner.GetJsonAsync<List<AccommodationTypeDto>>($"/api/v1/properties/{pid}/accommodation-types");
        var d = types.Single(t => t.Id == deluxe.Id);
        var b = types.Single(t => t.Id == dorm.Id);
        Assert.Equal(2, d.SellableItems);
        Assert.Equal(6, d.MaxGuests);           // 2 rooms × 3 guests
        Assert.Equal(10, b.SellableItems);      // dorm sells each bed
        Assert.Equal(10, b.MaxGuests);

        var room = await owner.GetJsonAsync<RoomDto>($"/api/v1/properties/{pid}/rooms/{dormRoom.Id}");
        Assert.Equal(10, room.Beds.Count);
        Assert.Equal("BED", room.InventoryMode);
    }

    [Fact]
    public async Task Beds_cannot_be_added_to_a_room_sold_as_a_whole()
    {
        var (owner, pid) = await OwnerAsync();
        var type = await CreateTypeAsync(owner, pid, "UNIT");
        var room = await CreateRoomAsync(owner, pid, type.Id);

        var res = await owner.PostAsync($"/api/v1/properties/{pid}/rooms/{room.Id}/beds", new CreateBedRequest("B01", "Bed 1", "SINGLE", 1));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Equal("BEDS_NOT_ALLOWED", await res.ProblemCodeAsync());

        // and the database refuses it even if someone bypasses the API
        var ex = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(
            $"INSERT INTO beds (organization_id, property_id, unit_id, code, name) " +
            $"SELECT organization_id, property_id, id, 'X1', 'x' FROM accommodation_units WHERE id = '{room.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    // ------------------------------------------------------------------ blocks

    [Fact]
    public async Task Overlapping_blocks_are_refused_by_the_api_and_by_the_database()
    {
        var (owner, pid) = await OwnerAsync();
        var room = await CreateRoomAsync(owner, pid, (await CreateTypeAsync(owner, pid, "UNIT")).Id);
        var start = TodayAtProperty.AddDays(10);

        var first = await BlockAsync(owner, pid, room.Id, null, start, start.AddDays(2));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var overlap = await BlockAsync(owner, pid, room.Id, null, start.AddDays(1), start.AddDays(3));
        Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);
        Assert.Equal("BLOCK_OVERLAP", await overlap.ProblemCodeAsync());

        // end date is exclusive: a block starting on the first block's end date is fine
        var backToBack = await BlockAsync(owner, pid, room.Id, null, start.AddDays(2), start.AddDays(4));
        Assert.Equal(HttpStatusCode.Created, backToBack.StatusCode);

        // database guard (EXCLUDE constraint) when bypassing the API
        var ex = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(
            "INSERT INTO unit_blocks (organization_id, property_id, unit_id, block_type, start_date, end_date, reason) " +
            $"SELECT organization_id, property_id, id, 'HOLD', '{start.AddDays(1):yyyy-MM-dd}', '{start.AddDays(2):yyyy-MM-dd}', 'direct' " +
            $"FROM accommodation_units WHERE id = '{room.Id}'"));
        Assert.Equal(PostgresErrorCodes.ExclusionViolation, ex.SqlState);
    }

    [Fact]
    public async Task Concurrent_block_requests_for_the_same_room_only_one_wins()
    {
        var (owner, pid) = await OwnerAsync();
        var room = await CreateRoomAsync(owner, pid, (await CreateTypeAsync(owner, pid, "UNIT")).Id);
        var start = TodayAtProperty.AddDays(20);

        using var gate = new SemaphoreSlim(0);
        var tasks = Enumerable.Range(0, 10).Select(async i =>
        {
            await gate.WaitAsync();
            return await BlockAsync(owner, pid, room.Id, null, start.AddDays(i % 2), start.AddDays(3), $"request {i}");
        }).ToList();
        gate.Release(10);
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(9, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var stored = await ScalarAsync($"SELECT count(*) FROM unit_blocks WHERE unit_id = '{room.Id}' AND status = 'ACTIVE'");
        Assert.Equal(1L, stored);
    }

    [Fact]
    public async Task Dorm_beds_can_be_blocked_individually_but_not_under_a_whole_dorm_block()
    {
        var (owner, pid) = await OwnerAsync();
        var dorm = await CreateRoomAsync(owner, pid, (await CreateTypeAsync(owner, pid, "BED", 1, 1)).Id);
        var b1 = await AddBedAsync(owner, pid, dorm.Id, "B01");
        var b2 = await AddBedAsync(owner, pid, dorm.Id, "B02");
        var start = TodayAtProperty.AddDays(5);

        Assert.Equal(HttpStatusCode.Created, (await BlockAsync(owner, pid, dorm.Id, b1.Id, start, start.AddDays(2))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await BlockAsync(owner, pid, dorm.Id, b2.Id, start, start.AddDays(2))).StatusCode);

        var sameBed = await BlockAsync(owner, pid, dorm.Id, b1.Id, start.AddDays(1), start.AddDays(3));
        Assert.Equal(HttpStatusCode.Conflict, sameBed.StatusCode);

        var wholeDorm = await BlockAsync(owner, pid, dorm.Id, null, start.AddDays(1), start.AddDays(4));
        Assert.Equal(HttpStatusCode.Conflict, wholeDorm.StatusCode);

        // status board: both beds blocked on the first night => dorm BLOCKED; on the end date (exclusive) => AVAILABLE
        var board = await owner.GetJsonAsync<RoomStatusBoardDto>($"/api/v1/properties/{pid}/rooms/status?date={start:yyyy-MM-dd}");
        var row = board.Rooms.Single(r => r.UnitId == dorm.Id);
        Assert.Equal("BLOCKED", row.Status);
        Assert.Equal(2, row.BlockedBeds);
        var later = await owner.GetJsonAsync<RoomStatusBoardDto>($"/api/v1/properties/{pid}/rooms/status?date={start.AddDays(2):yyyy-MM-dd}");
        Assert.Equal("AVAILABLE", later.Rooms.Single(r => r.UnitId == dorm.Id).Status);
    }

    [Fact]
    public async Task Status_board_shows_partly_blocked_dorm_and_blocked_room()
    {
        var (owner, pid) = await OwnerAsync();
        var room = await CreateRoomAsync(owner, pid, (await CreateTypeAsync(owner, pid, "UNIT")).Id);
        var dorm = await CreateRoomAsync(owner, pid, (await CreateTypeAsync(owner, pid, "BED", 1, 1)).Id);
        var bed = await AddBedAsync(owner, pid, dorm.Id, "B01");
        await AddBedAsync(owner, pid, dorm.Id, "B02");
        var day = TodayAtProperty.AddDays(30);
        await BlockAsync(owner, pid, room.Id, null, day, day.AddDays(1));
        await BlockAsync(owner, pid, dorm.Id, bed.Id, day, day.AddDays(1));

        var board = await owner.GetJsonAsync<RoomStatusBoardDto>($"/api/v1/properties/{pid}/rooms/status?date={day:yyyy-MM-dd}");
        Assert.Equal(day, board.Date);
        Assert.Equal("BLOCKED", board.Rooms.Single(r => r.UnitId == room.Id).Status);
        var d = board.Rooms.Single(r => r.UnitId == dorm.Id);
        Assert.Equal("PARTLY_BLOCKED", d.Status);
        Assert.Equal("BLOCKED", d.Beds.Single(b => b.BedId == bed.Id).Status);
    }

    [Fact]
    public async Task Block_and_release_are_audited_with_their_reasons()
    {
        var (owner, pid) = await OwnerAsync();
        var room = await CreateRoomAsync(owner, pid, (await CreateTypeAsync(owner, pid, "UNIT")).Id);
        var start = TodayAtProperty.AddDays(3);
        var created = await BlockAsync(owner, pid, room.Id, null, start, start.AddDays(2), "Repainting walls");
        var block = (await created.Content.ReadFromJsonAsync<UnitBlockDto>())!;
        Assert.Equal("Test Owner", block.CreatedByName);
        Assert.Equal(2, block.Nights);

        var released = await owner.PostAsync($"/api/v1/properties/{pid}/blocks/{block.Id}/release", new ReleaseBlockRequest("Finished a day early"));
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);

        var log = await owner.GetJsonAsync<PagedResult<AuditLogDto>>($"/api/v1/audit-logs?entityType=UnitBlock&entityId={block.Id}");
        Assert.Equal("Repainting walls", log.Items.Single(i => i.Action == "INSERT").Reason);
        var update = log.Items.Single(i => i.Action == "UPDATE");
        Assert.Equal("Finished a day early", update.Reason);
        Assert.Equal("RELEASED", update.Changes!.Value.GetProperty("Status").GetProperty("new").GetString());

        var again = await owner.PostAsync($"/api/v1/properties/{pid}/blocks/{block.Id}/release", new ReleaseBlockRequest("twice"));
        Assert.Equal("BLOCK_NOT_ACTIVE", await again.ProblemCodeAsync());

        // released blocks drop off the default list and no longer stop a new block
        var list = await owner.GetJsonAsync<List<UnitBlockDto>>($"/api/v1/properties/{pid}/blocks?unitId={room.Id}");
        Assert.Empty(list);
        Assert.Equal(HttpStatusCode.Created, (await BlockAsync(owner, pid, room.Id, null, start, start.AddDays(2))).StatusCode);
    }

    [Fact]
    public async Task Blocks_need_a_reason_and_must_end_after_today()
    {
        var (owner, pid) = await OwnerAsync();
        var room = await CreateRoomAsync(owner, pid, (await CreateTypeAsync(owner, pid, "UNIT")).Id);

        var noReason = await BlockAsync(owner, pid, room.Id, null, TodayAtProperty.AddDays(1), TodayAtProperty.AddDays(2), " ");
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        var past = await BlockAsync(owner, pid, room.Id, null, TodayAtProperty.AddDays(-3), TodayAtProperty);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, past.StatusCode);
        Assert.Equal("BLOCK_IN_PAST", await past.ProblemCodeAsync());

        var release = await owner.PostAsync($"/api/v1/properties/{pid}/blocks/{Guid.NewGuid()}/release", new ReleaseBlockRequest(""));
        Assert.Equal(HttpStatusCode.BadRequest, release.StatusCode);
    }

    // ------------------------------------------------------------------ permissions & scoping

    [Fact]
    public async Task Receptionist_can_read_rooms_but_not_change_them_and_manager_can_block_only()
    {
        var (owner, pid) = await OwnerAsync();
        var type = await CreateTypeAsync(owner, pid, "UNIT");
        var room = await CreateRoomAsync(owner, pid, type.Id);

        var (re, rp) = await app.CreateUserAsync("RECEPTIONIST", pid);
        var desk = await app.SignInAsync(re, rp);
        Assert.Equal(HttpStatusCode.OK, (await desk.GetAsync($"/api/v1/properties/{pid}/rooms")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await desk.GetAsync($"/api/v1/properties/{pid}/rooms/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PostAsync($"/api/v1/properties/{pid}/rooms",
            new CreateRoomRequest("999", "x", type.Id, null, null, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PostAsync($"/api/v1/properties/{pid}/accommodation-types",
            NewType("UNIT", 2, 2))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await BlockAsync(desk, pid, room.Id, null, TodayAtProperty.AddDays(1), TodayAtProperty.AddDays(2))).StatusCode);

        var (me, mp) = await app.CreateUserAsync("MANAGER", pid);
        var manager = await app.SignInAsync(me, mp);
        Assert.Equal(HttpStatusCode.Created, (await BlockAsync(manager, pid, room.Id, null, TodayAtProperty.AddDays(40), TodayAtProperty.AddDays(41))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsync($"/api/v1/properties/{pid}/rooms",
            new CreateRoomRequest("998", "x", type.Id, null, null, 0))).StatusCode);

        // waiters have no accommodation.read at all
        var (we, wp) = await app.CreateUserAsync("WAITER", pid);
        var waiter = await app.SignInAsync(we, wp);
        Assert.Equal(HttpStatusCode.Forbidden, (await waiter.GetAsync($"/api/v1/properties/{pid}/rooms")).StatusCode);
    }

    [Fact]
    public async Task Rooms_are_scoped_to_their_property_and_tenant()
    {
        var (owner, pid) = await OwnerAsync();
        var room = await CreateRoomAsync(owner, pid, (await CreateTypeAsync(owner, pid, "UNIT")).Id);

        // second property in the same organisation: the room's id under the other property's route is 404
        var created = await owner.PostAsync("/api/v1/properties", new CreatePropertyRequest(
            "P" + Random.Shared.Next(1000, 9999), "Second", null, "RESORT", "Asia/Kolkata", "INR", null, null, null, null, null, null,
            null, "IN", null, null, new TimeOnly(14, 0), new TimeOnly(11, 0), null, null));
        var second = (await created.Content.ReadFromJsonAsync<PropertyDto>())!;
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/v1/properties/{second.Id}/rooms/{room.Id}")).StatusCode);
        Assert.Empty(await owner.GetJsonAsync<List<RoomDto>>($"/api/v1/properties/{second.Id}/rooms"));

        // another tenant's owner can't see this property's rooms at all
        var other = await app.CreateSecondTenantAsync();
        var otherOwner = await app.SignInAsync(other.OwnerEmail, other.Password);
        Assert.Equal(HttpStatusCode.NotFound, (await otherOwner.GetAsync($"/api/v1/properties/{pid}/rooms")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherOwner.GetAsync($"/api/v1/properties/{pid}/rooms/{room.Id}")).StatusCode);
    }

    // ------------------------------------------------------------------ deactivation & integrity rules

    [Fact]
    public async Task Types_and_rooms_are_deactivated_not_deleted_and_mode_rules_hold()
    {
        var (owner, pid) = await OwnerAsync();
        var roomType = await CreateTypeAsync(owner, pid, "UNIT");
        var dormType = await CreateTypeAsync(owner, pid, "BED", 1, 1);
        var room = await CreateRoomAsync(owner, pid, roomType.Id);
        var dorm = await CreateRoomAsync(owner, pid, dormType.Id);
        await AddBedAsync(owner, pid, dorm.Id, "B01");

        // can't deactivate a type that still has active rooms
        var deact = await owner.PutAsync($"/api/v1/properties/{pid}/accommodation-types/{roomType.Id}", UpdateOf(roomType) with { IsActive = false });
        Assert.Equal("TYPE_HAS_ACTIVE_UNITS", await deact.ProblemCodeAsync());

        // a dorm with beds can't become a whole-room type, either by moving the unit or switching the type
        var move = await owner.PutAsync($"/api/v1/properties/{pid}/rooms/{dorm.Id}",
            new UpdateRoomRequest(dorm.Name, roomType.Id, null, null, 0, true, dorm.Version));
        Assert.Equal("UNIT_HAS_BEDS", await move.ProblemCodeAsync());
        var switchMode = await owner.PutAsync($"/api/v1/properties/{pid}/accommodation-types/{dormType.Id}",
            UpdateOf(dormType) with { InventoryMode = "UNIT", BaseOccupancy = 2, MaxOccupancy = 2 });
        Assert.Equal("TYPE_HAS_BEDS", await switchMode.ProblemCodeAsync());

        // deactivate the room, then the type
        var r = await owner.PutAsync($"/api/v1/properties/{pid}/rooms/{room.Id}",
            new UpdateRoomRequest(room.Name, roomType.Id, null, null, 0, false, room.Version));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var t = await owner.GetJsonAsync<AccommodationTypeDto>($"/api/v1/properties/{pid}/accommodation-types/{roomType.Id}");
        Assert.Equal(0, t.SellableItems);
        var ok = await owner.PutAsync($"/api/v1/properties/{pid}/accommodation-types/{roomType.Id}", UpdateOf(t) with { IsActive = false });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // stale version
        var stale = await owner.PutAsync($"/api/v1/properties/{pid}/accommodation-types/{roomType.Id}", UpdateOf(t) with { Name = "Stale" });
        Assert.Equal("CONCURRENCY_CONFLICT", await stale.ProblemCodeAsync());

        // nothing was deleted
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM accommodation_units WHERE id = '{room.Id}' AND NOT is_active"));
    }

    [Fact]
    public async Task Duplicate_codes_unknown_amenities_and_bed_type_occupancy_are_rejected()
    {
        var (owner, pid) = await OwnerAsync();
        var type = await CreateTypeAsync(owner, pid, "UNIT");
        var dup = await owner.PostAsync($"/api/v1/properties/{pid}/accommodation-types", NewType("UNIT", 2, 2) with { Code = type.Code });
        Assert.Equal("TYPE_CODE_TAKEN", await dup.ProblemCodeAsync());

        var room = await CreateRoomAsync(owner, pid, type.Id);
        var dupRoom = await owner.PostAsync($"/api/v1/properties/{pid}/rooms", new CreateRoomRequest(room.Code, "x", type.Id, null, null, 0));
        Assert.Equal("ROOM_CODE_TAKEN", await dupRoom.ProblemCodeAsync());

        var badAmenity = await owner.PostAsync($"/api/v1/properties/{pid}/accommodation-types", NewType("UNIT", 2, 2) with { AmenityIds = [Guid.NewGuid()] });
        Assert.Equal("UNKNOWN_AMENITY", await badAmenity.ProblemCodeAsync());

        var bedTwoGuests = await owner.PostAsync($"/api/v1/properties/{pid}/accommodation-types", NewType("BED", 2, 2));
        Assert.Equal(HttpStatusCode.BadRequest, bedTwoGuests.StatusCode);

        var amenities = await owner.GetJsonAsync<List<AmenityDto>>($"/api/v1/properties/{pid}/amenities");
        Assert.Contains(amenities, a => a.Code == "WIFI");
        var withWifi = await owner.PostAsync($"/api/v1/properties/{pid}/accommodation-types",
            NewType("UNIT", 2, 2) with { AmenityIds = [amenities.Single(a => a.Code == "WIFI").Id] });
        Assert.Equal(HttpStatusCode.Created, withWifi.StatusCode);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(TestClient Owner, Guid PropertyId)> OwnerAsync() =>
        (await app.SignInOwnerAsync(), await app.MainPropertyIdAsync());

    private static string Suffix() => Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private static CreateAccommodationTypeRequest NewType(string mode, short baseOcc, short maxOcc) =>
        new("T_" + Suffix(), mode == "BED" ? "Dorm bed" : "Room type", null, mode, baseOcc, maxOcc, null, 0, null);

    private static UpdateAccommodationTypeRequest UpdateOf(AccommodationTypeDto t) =>
        new(t.Name, t.Description, t.InventoryMode, t.BaseOccupancy, t.MaxOccupancy, t.SizeSqm, t.SortOrder, t.AmenityIds, t.IsActive, t.Version);

    private static async Task<AccommodationTypeDto> CreateTypeAsync(TestClient c, Guid pid, string mode, short baseOcc = 2, short maxOcc = 2)
    {
        var res = await c.PostAsync($"/api/v1/properties/{pid}/accommodation-types", NewType(mode, baseOcc, maxOcc));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<AccommodationTypeDto>())!;
    }

    private static async Task<RoomDto> CreateRoomAsync(TestClient c, Guid pid, Guid typeId)
    {
        var code = "R" + Suffix();
        var res = await c.PostAsync($"/api/v1/properties/{pid}/rooms", new CreateRoomRequest(code, "Room " + code, typeId, null, null, 0));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<RoomDto>())!;
    }

    private static async Task<BedDto> AddBedAsync(TestClient c, Guid pid, Guid roomId, string code)
    {
        var res = await c.PostAsync($"/api/v1/properties/{pid}/rooms/{roomId}/beds", new CreateBedRequest(code, "Bed " + code, "SINGLE", 0));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<BedDto>())!;
    }

    private static Task<HttpResponseMessage> BlockAsync(TestClient c, Guid pid, Guid unitId, Guid? bedId, DateOnly start, DateOnly end, string reason = "Maintenance work") =>
        c.PostAsync($"/api/v1/properties/{pid}/blocks", new CreateBlockRequest(unitId, bedId, "MAINTENANCE", start, end, reason));

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
}
