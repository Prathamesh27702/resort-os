using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Npgsql;
using Resort.Contracts;
using Resort.Tests.Support;
using static Resort.Tests.Integration.FrontDeskTests;
using static Resort.Tests.Support.BookingKit;

namespace Resort.Tests.Integration;

/// <summary>Sprint 13 — maintenance tickets: an out-of-order report blocks the room automatically and resolving it sends
/// the room to cleaning; photos stored encrypted; the ticket's status steps; who sees and does what.</summary>
public class MaintenanceTests(FrontDeskFactory app) : IClassFixture<FrontDeskFactory>
{
    // ------------------------------------------------------------------ out of order → unit block (task in the sprint)

    [Fact]
    public async Task An_out_of_order_ticket_blocks_the_room_and_resolving_it_sends_the_room_to_cleaning()
    {
        var (owner, pid) = await OwnerAsync();
        var housekeeper = await StaffAsync("HOUSEKEEPER", pid);
        var tech = await StaffAsync("MAINTENANCE", pid);
        var stay = await SetupAsync(owner, pid, rooms: 1);
        var room = stay.Rooms[0];
        // The room was waiting to be cleaned when the AC broke.
        await SetHousekeepingAsync(owner, pid, room.Id, "DIRTY");
        var taskId = (await HousekeepingTests.RoomAsync(owner, pid, room.Id)).Task!.Id;

        // A housekeeper reports it from the room, out of order for two nights.
        var ticket = await TicketCreatedAsync(await housekeeper.PostAsync(TicketsUrl(pid), new CreateTicketRequest(
            "AC not cooling", "Blows warm air", "AC", "HIGH", room.Id, null, null, null, true, Today.AddDays(2))));
        Assert.Matches("^M[0-9]{5}$", ticket.Number);
        Assert.Equal(("OPEN", room.Name, true, true), (ticket.Status, ticket.Place, ticket.OutOfOrder, ticket.BlockActive));
        Assert.Equal(("HOUSEKEEPER user", "Reported; out of order until " + Day(Today.AddDays(2))),
            (ticket.ReportedByName, Assert.Single(ticket.Comments).Body));

        // The block: OUT_OF_ORDER from tonight to the expected day, named after the ticket.
        var block = Assert.Single(await owner.GetJsonAsync<List<UnitBlockDto>>($"/api/v1/properties/{pid}/blocks?unitId={room.Id}"));
        Assert.Equal((ticket.BlockId, "OUT_OF_ORDER", Today, Today.AddDays(2), $"{ticket.Number}: AC not cooling"),
            ((Guid?)block.Id, block.BlockType, block.StartDate, block.EndDate, block.Reason));
        // ... so the room isn't for sale tonight
        Assert.DoesNotContain((await AvailabilityAsync(owner, pid, Today, Today.AddDays(1), stay.Type.Id)).FreeRooms, f => f.Id == room.Id);
        // ... it is out of order on the board with the repair, and its cleaning task is cancelled
        var row = await HousekeepingTests.RoomAsync(housekeeper, pid, room.Id);
        Assert.Equal(("OUT_OF_ORDER", ticket.Number, true), (row.Status, row.Repair!.Number, row.Repair.OutOfOrder));
        Assert.Null(row.Task);
        Assert.Equal(new object?[] { "CANCELLED", $"Out of order: {ticket.Number}: AC not cooling" },
            Assert.Single(await RowsAsync($"SELECT status, close_reason FROM housekeeping_tasks WHERE id = '{taskId}'")));
        // ... and the room status board counts the open repair
        var status = await owner.GetJsonAsync<RoomStatusBoardDto>($"/api/v1/properties/{pid}/rooms/status");
        Assert.Equal(("BLOCKED", "OUT_OF_ORDER", 1), status.Rooms.Where(r => r.UnitId == room.Id).Select(r => (r.Status, r.Housekeeping, r.OpenRepairs)).Single());

        // Maintenance works it: progress (assigns itself), on hold, then resolve.
        var working = await TicketOkAsync(await tech.PostAsync($"{TicketsUrl(pid)}/{ticket.Id}/progress", new TicketProgressRequest(null, "Checking the gas", ticket.Version)));
        Assert.Equal(("IN_PROGRESS", "MAINTENANCE user"), (working.Status, working.AssignedToName));
        var held = await TicketOkAsync(await tech.PostAsync($"{TicketsUrl(pid)}/{ticket.Id}/progress", new TicketProgressRequest("ON_HOLD", "Waiting for a capacitor", null)));
        Assert.Equal("ON_HOLD", held.Status);
        var resolved = await TicketOkAsync(await tech.PostAsync($"{TicketsUrl(pid)}/{ticket.Id}/resolve", new ResolveTicketRequest("Replaced the capacitor", held.Version)));
        Assert.Equal(("RESOLVED", "Replaced the capacitor", "MAINTENANCE user", false), (resolved.Status, resolved.Resolution, resolved.ResolvedByName, resolved.BlockActive));
        Assert.Equal(["OPEN", "IN_PROGRESS", "ON_HOLD", "RESOLVED"], resolved.Comments.Where(c => c.Kind == "STATUS").Select(c => c.ToStatus!));

        // The block is released, and the room goes to cleaning before anyone sleeps in it.
        Assert.Empty(await owner.GetJsonAsync<List<UnitBlockDto>>($"/api/v1/properties/{pid}/blocks?unitId={room.Id}"));
        Assert.Contains((await AvailabilityAsync(owner, pid, Today, Today.AddDays(1), stay.Type.Id)).FreeRooms, f => f.Id == room.Id);
        row = await HousekeepingTests.RoomAsync(housekeeper, pid, room.Id);
        Assert.Equal(("DIRTY", "AFTER_REPAIR", "MAINTENANCE"), (row.Status, row.Task!.TaskType, row.Task.Source));
        Assert.Null(row.Repair);

        // Maintenance can't close; the owner (manager) does.
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.PostAsync($"{TicketsUrl(pid)}/{ticket.Id}/close", new CloseTicketRequest(null, null))).StatusCode);
        var closed = await TicketOkAsync(await owner.PostAsync($"{TicketsUrl(pid)}/{ticket.Id}/close", new CloseTicketRequest("Checked, cooling fine", null)));
        Assert.Equal(("CLOSED", "Closed: Checked, cooling fine"), (closed.Status, closed.Comments[^1].Body));
        Assert.Empty(closed.NextActions);
        Assert.True((long)(await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE entity_id = '{ticket.Id}'"))! >= 4);
    }

    [Fact]
    public async Task A_room_with_a_guest_tonight_cant_be_taken_out_of_order_and_the_dates_are_checked()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 2);
        var room = stay.Rooms[0];
        var booked = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, (await CreateGuestAsync(owner)).Id, await WalkInAsync(owner, pid), Today, Today.AddDays(2))));
        await OkAsync(await owner.PostAsync(CheckInUrl(pid, booked), new CheckInRequest(null)));

        var res = await owner.PostAsync(TicketsUrl(pid), new CreateTicketRequest("Fan broken", null, "ELECTRICAL", "NORMAL", room.Id, null, null, null, true, Today.AddDays(1)));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var (code, message) = await ProblemAsync(res);
        Assert.Equal("BLOCK_OVERLAPS_RESERVATION", code);
        Assert.StartsWith($"{room.Name} can't be taken out of order from tonight: {room.Name} is booked on {booked.Reference} for the nights of ", message);
        Assert.EndsWith(". Move that booking to another room first, or report the problem without taking the room out of order.", message);
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM maintenance_tickets WHERE unit_id = '{room.Id}'"));

        // Reported without taking the room out: fine.
        var ticket = await TicketCreatedAsync(await owner.PostAsync(TicketsUrl(pid), new CreateTicketRequest("Fan broken", null, "ELECTRICAL", "NORMAL", room.Id, null, null, null, false, null)));
        Assert.False(ticket.OutOfOrder);
        Assert.Null(ticket.BlockId);

        // A room already blocked on those nights (say, a planned repair) says so, and what to do instead.
        var other = stay.Rooms.Single(r => r.Id != room.Id);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsync($"/api/v1/properties/{pid}/blocks",
            new CreateBlockRequest(other.Id, null, "MAINTENANCE", Today.AddDays(2), Today.AddDays(4), "Repainting"))).StatusCode);
        res = await owner.PostAsync(TicketsUrl(pid), new CreateTicketRequest("Door lock jammed", null, "FURNITURE", "HIGH", other.Id, null, null, null, true, Today.AddDays(3)));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        (code, message) = await ProblemAsync(res);
        Assert.Equal("BLOCK_OVERLAP", code);
        Assert.Contains($"{other.Name} can't be taken out of order from tonight: {other.Name} is already blocked", message);
        Assert.EndsWith("(Repainting). Release that block in Rooms first (or choose a return date before it), or report the problem without taking the room out of order.", message);

        // Field errors say what to fix.
        Assert.Equal("The expected date must be after today: the room is blocked from tonight until that day.",
            await FieldErrorAsync(await owner.PostAsync(TicketsUrl(pid), new CreateTicketRequest("Leak", null, "PLUMBING", "NORMAL", room.Id, null, null, null, true, Today)), "ExpectedReadyOn"));
        Assert.Equal("A room can be taken out of order for at most 90 days at a time.",
            await FieldErrorAsync(await owner.PostAsync(TicketsUrl(pid), new CreateTicketRequest("Leak", null, "PLUMBING", "NORMAL", room.Id, null, null, null, true, Today.AddDays(91))), "ExpectedReadyOn"));
        Assert.Equal("Say when the room should be usable again (the room is blocked until then).",
            await FieldErrorAsync(await owner.PostAsync(TicketsUrl(pid), new CreateTicketRequest("Leak", null, "PLUMBING", "NORMAL", room.Id, null, null, null, true, null)), "ExpectedReadyOn"));
        Assert.Equal("Choose the room to take out of order.",
            await FieldErrorAsync(await owner.PostAsync(TicketsUrl(pid), new CreateTicketRequest("Leak", null, "PLUMBING", "NORMAL", null, null, null, "Kitchen", true, Today.AddDays(1))), "UnitId"));
        Assert.Equal("Say where the problem is: choose a room or equipment, or type the place (e.g. Pool deck).",
            await FieldErrorAsync(await owner.PostAsync(TicketsUrl(pid), new CreateTicketRequest("Leak", null, "PLUMBING", "NORMAL", null, null, null, " ", false, null)), "Location"));
        Assert.Equal("Say what is wrong in 3–120 characters, e.g. AC not cooling.",
            await FieldErrorAsync(await owner.PostAsync(TicketsUrl(pid), new CreateTicketRequest("x", null, "PLUMBING", "NORMAL", room.Id, null, null, null, false, null)), "Title"));
    }

    // ------------------------------------------------------------------ photos

    [Fact]
    public async Task Photos_are_checked_by_their_bytes_kept_encrypted_and_limited_to_six()
    {
        var (owner, pid) = await OwnerAsync();
        var ticket = await TicketCreatedAsync(await owner.PostAsync(TicketsUrl(pid), new CreateTicketRequest(
            "Broken deck chair", null, "FURNITURE", "LOW", null, null, null, "Pool deck", false, null)));
        var jpeg = Jpeg(40_000);

        var res = await UploadAsync(owner, pid, ticket.Id, jpeg, "IMG_2041.jpg", "image/jpeg");
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var photo = (await res.Content.ReadFromJsonAsync<MaintenancePhotoDto>())!;
        Assert.Equal(("IMG_2041.jpg", "image/jpeg", jpeg.Length), (photo.FileName, photo.ContentType, photo.SizeBytes));

        // Stored encrypted: the bytes on disk aren't the photo; the API gives the photo back.
        var key = (string)(await ScalarAsync($"SELECT storage_key FROM maintenance_attachments WHERE id = '{photo.Id}'"))!;
        var onDisk = await File.ReadAllBytesAsync(Path.Combine(app.FileRoot, key));
        Assert.NotEqual(jpeg, onDisk);
        Assert.DoesNotContain("JFIF", System.Text.Encoding.ASCII.GetString(onDisk));
        var back = await owner.GetAsync($"{TicketsUrl(pid)}/{ticket.Id}/photos/{photo.Id}");
        Assert.Equal("image/jpeg", back.Content.Headers.ContentType!.MediaType);
        Assert.Equal(jpeg, await back.Content.ReadAsByteArrayAsync());

        // Not a photo (whatever it claims), too big, then the seventh.
        Assert.Equal("That file isn't a photo. Send a JPEG, PNG or WebP picture.",
            await FieldErrorAsync(await UploadAsync(owner, pid, ticket.Id, "not an image"u8.ToArray(), "chair.jpg", "image/jpeg"), "File"));
        Assert.Equal("The photo is 2.4 MB; at most 2 MB. Take it again or choose a smaller one.",
            await FieldErrorAsync(await UploadAsync(owner, pid, ticket.Id, Jpeg(2_500_000), "big.jpg", "image/jpeg"), "File"));
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Created, (await UploadAsync(owner, pid, ticket.Id, Png(), $"p{i}.png", "image/png")).StatusCode);
        var seventh = await UploadAsync(owner, pid, ticket.Id, Png(), "p7.png", "image/png");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, seventh.StatusCode);
        Assert.Equal($"{ticket.Number} already has 6 photos, the most it can have.", (await ProblemAsync(seventh)).Message);
        var full = await owner.GetJsonAsync<MaintenanceTicketDto>($"{TicketsUrl(pid)}/{ticket.Id}");
        Assert.Equal(6, full.Photos.Count);
        Assert.DoesNotContain("photo", full.NextActions);
        Assert.Equal(6, (await owner.GetJsonAsync<List<MaintenanceTicketSummaryDto>>(TicketsUrl(pid))).Single(t => t.Id == ticket.Id).Photos);
    }

    // ------------------------------------------------------------------ who sees and does what; status steps

    [Fact]
    public async Task Reporters_see_their_own_tickets_steps_follow_the_order_and_a_retried_report_is_saved_once()
    {
        var (owner, pid) = await OwnerAsync();
        var housekeeper = await StaffAsync("HOUSEKEEPER", pid);
        var tech = await StaffAsync("MAINTENANCE", pid);
        var supervisor = await StaffAsync("HOUSEKEEPING_SUPERVISOR", pid);
        var mine = await TicketCreatedAsync(await housekeeper.PostAsync(TicketsUrl(pid), new CreateTicketRequest(
            "Corridor bulb out", null, "ELECTRICAL", "LOW", null, null, null, "First floor corridor", false, null)));
        var other = await TicketCreatedAsync(await owner.PostAsync(TicketsUrl(pid), new CreateTicketRequest(
            "Gate hinge squeaks", null, "CIVIL", "LOW", null, null, null, "Main gate", false, null)));

        // The housekeeper (report only) sees their own ticket, not the owner's.
        var list = await housekeeper.GetJsonAsync<List<MaintenanceTicketSummaryDto>>(TicketsUrl(pid));
        Assert.Contains(list, t => t.Id == mine.Id);
        Assert.DoesNotContain(list, t => t.Id == other.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await housekeeper.GetAsync($"{TicketsUrl(pid)}/{other.Id}")).StatusCode);
        Assert.Equal(["comment", "photo"], (await housekeeper.GetJsonAsync<MaintenanceTicketDto>($"{TicketsUrl(pid)}/{mine.Id}")).NextActions);
        Assert.Equal(HttpStatusCode.Forbidden, (await housekeeper.PostAsync($"{TicketsUrl(pid)}/{mine.Id}/resolve", new ResolveTicketRequest("Fixed it", null))).StatusCode);
        Assert.Contains((await tech.GetJsonAsync<List<MaintenanceTicketSummaryDto>>(TicketsUrl(pid))), t => t.Id == other.Id);

        // Assign: only to someone in maintenance.
        Assert.Equal("HOUSEKEEPING_SUPERVISOR user can't work maintenance tickets at this property (no maintenance role here). Choose someone from maintenance.",
            await FieldErrorAsync(await owner.PostAsync($"{TicketsUrl(pid)}/{other.Id}/assign", new AssignTicketRequest(supervisor.Me!.UserId, null, null)), "UserId"));
        var assigned = await TicketOkAsync(await owner.PostAsync($"{TicketsUrl(pid)}/{other.Id}/assign", new AssignTicketRequest(tech.Me!.UserId, "Before the weekend", null)));
        Assert.Equal(("ASSIGNED", "MAINTENANCE user", "Assigned to MAINTENANCE user. Before the weekend"), (assigned.Status, assigned.AssignedToName, assigned.Comments[^1].Body));
        Assert.Equal(1, (await owner.GetJsonAsync<List<StaffOptionDto>>($"/api/v1/properties/{pid}/maintenance/staff")).Single(s => s.UserId == tech.Me.UserId).OpenWork);
        Assert.Contains((await tech.GetJsonAsync<List<MaintenanceTicketSummaryDto>>($"{TicketsUrl(pid)}?mine=true")), t => t.Id == other.Id);

        // Steps in order, each refusal saying why.
        var early = await owner.PostAsync($"{TicketsUrl(pid)}/{other.Id}/close", new CloseTicketRequest(null, null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, early.StatusCode);
        Assert.Equal($"{other.Number} is assigned; resolve it before closing it.", (await ProblemAsync(early)).Message);
        Assert.Equal("Say what was done or what it is waiting for (3–2000 characters).",
            await FieldErrorAsync(await tech.PostAsync($"{TicketsUrl(pid)}/{other.Id}/progress", new TicketProgressRequest(null, "", null)), "Note"));
        await TicketOkAsync(await tech.PostAsync($"{TicketsUrl(pid)}/{other.Id}/resolve", new ResolveTicketRequest("Oiled the hinge", null)));
        Assert.Equal($"{other.Number} is resolved. Close it, or report a new problem if the repair didn't hold.",
            (await ProblemAsync(await tech.PostAsync($"{TicketsUrl(pid)}/{other.Id}/progress", new TicketProgressRequest(null, "More oil", null)))).Message);
        var stale = await owner.PostAsync($"{TicketsUrl(pid)}/{other.Id}/close", new CloseTicketRequest(null, assigned.Version));
        Assert.Equal("CONCURRENCY_CONFLICT", (await ProblemAsync(stale)).Code);
        await TicketOkAsync(await owner.PostAsync($"{TicketsUrl(pid)}/{other.Id}/close", new CloseTicketRequest(null, null)));
        Assert.Equal($"{other.Number} is closed. Report a new problem if it is broken again.",
            (await ProblemAsync(await owner.PostAsync($"{TicketsUrl(pid)}/{other.Id}/comments", new AddTicketCommentRequest("again")))).Message);
        Assert.DoesNotContain((await owner.GetJsonAsync<List<MaintenanceTicketSummaryDto>>(TicketsUrl(pid))), t => t.Id == other.Id);
        Assert.Contains((await owner.GetJsonAsync<List<MaintenanceTicketSummaryDto>>($"{TicketsUrl(pid)}?status=ALL")), t => t.Id == other.Id);

        // The reporter can comment on their own ticket.
        var commented = await TicketOkAsync(await housekeeper.PostAsync($"{TicketsUrl(pid)}/{mine.Id}/comments", new AddTicketCommentRequest("Now the next one too")));
        Assert.Equal(("NOTE", "HOUSEKEEPER user"), (commented.Comments[^1].Kind, commented.Comments[^1].CreatedByName));

        // A report sent twice with the same key is saved once.
        var body = new CreateTicketRequest("Wifi down in the lobby", null, "ELECTRICAL", "URGENT", null, null, null, "Lobby", false, null);
        var headers = new Dictionary<string, string> { ["Idempotency-Key"] = "report-" + Guid.NewGuid() };
        var a = await TicketCreatedAsync(await owner.PostAsync(TicketsUrl(pid), body, headers));
        var again = await owner.PostAsync(TicketsUrl(pid), body, headers);
        Assert.Equal("true", again.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(a.Number, (await TicketCreatedAsync(again)).Number);
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM maintenance_tickets WHERE title = 'Wifi down in the lobby'"));
        // URGENT comes first on the list.
        Assert.Equal(a.Id, (await owner.GetJsonAsync<List<MaintenanceTicketSummaryDto>>(TicketsUrl(pid)))[0].Id);
    }

    [Fact]
    public async Task Equipment_is_listed_with_its_open_tickets_and_codes_are_unique()
    {
        var (owner, pid) = await OwnerAsync();
        var code = "AC-" + Suffix();
        var res = await owner.PostAsync($"/api/v1/properties/{pid}/maintenance/assets", new SaveAssetRequest(code.ToLowerInvariant(), "Split AC", "AC", null, "Lobby", true));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var asset = (await res.Content.ReadFromJsonAsync<MaintenanceAssetDto>())!;
        Assert.Equal(code, asset.Code);
        Assert.Equal($"{code} is already used by another piece of equipment here. Choose another code.",
            await FieldErrorAsync(await owner.PostAsync($"/api/v1/properties/{pid}/maintenance/assets", new SaveAssetRequest(code, "Other", "AC", null, null, true)), "Code"));
        var t = await TicketCreatedAsync(await owner.PostAsync(TicketsUrl(pid), new CreateTicketRequest("Noisy", null, "AC", "NORMAL", null, null, asset.Id, null, false, null)));
        Assert.Equal("Split AC (Lobby)", t.Place);
        Assert.Equal(1, (await owner.GetJsonAsync<List<MaintenanceAssetDto>>($"/api/v1/properties/{pid}/maintenance/assets")).Single(x => x.Id == asset.Id).OpenTickets);
        var housekeeper = await StaffAsync("HOUSEKEEPER", pid);
        Assert.Equal(HttpStatusCode.Forbidden, (await housekeeper.PostAsync($"/api/v1/properties/{pid}/maintenance/assets",
            new SaveAssetRequest("X1", "Thing", "OTHER", null, null, true))).StatusCode);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(TestClient Owner, Guid PropertyId)> OwnerAsync() => (await app.SignInOwnerAsync(), await app.MainPropertyIdAsync());

    private async Task<TestClient> StaffAsync(string role, Guid pid)
    {
        var (email, pw) = await app.CreateUserAsync(role, pid);
        return await app.SignInAsync(email, pw);
    }

    private static string TicketsUrl(Guid pid) => $"/api/v1/properties/{pid}/maintenance/tickets";

    private static async Task<MaintenanceTicketDto> TicketCreatedAsync(HttpResponseMessage res)
    {
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<MaintenanceTicketDto>())!;
    }

    private static async Task<MaintenanceTicketDto> TicketOkAsync(HttpResponseMessage res)
    {
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<MaintenanceTicketDto>())!;
    }

    private static Task<HttpResponseMessage> UploadAsync(TestClient c, Guid pid, Guid ticketId, byte[] bytes, string name, string type)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        content.Add(file, "file", name);
        return c.PostContentAsync($"{TicketsUrl(pid)}/{ticketId}/photos", content);
    }

    /// <summary>JPEG magic bytes and a JFIF header, padded to <paramref name="size"/>.</summary>
    private static byte[] Jpeg(int size)
    {
        var b = new byte[size];
        new Random(size).NextBytes(b);
        byte[] head = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00];
        head.CopyTo(b, 0);
        return b;
    }

    private static byte[] Png() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 1, 2, 3, 4];

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
