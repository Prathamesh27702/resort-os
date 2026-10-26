using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Resort.Contracts;
using Resort.Tests.Support;
using static Resort.Tests.Integration.FrontDeskTests;
using static Resort.Tests.Support.BookingKit;

namespace Resort.Tests.Integration;

/// <summary>Sprint 13 — housekeeping: a check-out makes exactly one cleaning task; a housekeeper finishes a room in two
/// taps; a supervisor inspects it READY or sends it back; checklists; who may do what. Real API, real PostgreSQL.</summary>
public class HousekeepingTests(FrontDeskFactory app) : IClassFixture<FrontDeskFactory>
{
    // ------------------------------------------------------------------ required: checkout → one DIRTY task, idempotent

    [Fact]
    public async Task Check_out_creates_one_dirty_task_and_retried_check_outs_at_the_same_moment_make_no_second_one()
    {
        var (owner, pid) = await OwnerAsync();
        var (email, pw) = await app.CreateUserAsync("RECEPTIONIST", pid);
        var desk = await app.SignInAsync(email, pw);
        var guest = await CreateGuestAsync(owner);
        var source = await WalkInAsync(owner, pid);

        for (var run = 0; run < 5; run++)
        {
            var stay = await SetupAsync(owner, pid, rooms: 1);
            var room = stay.Rooms[0];
            var booked = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, Today, Today.AddDays(1))));
            await OkAsync(await owner.PostAsync(CheckInUrl(pid, booked), new CheckInRequest(null)));

            // The guest's check-out is sent three times at once (a double tap and a retry on a weak signal).
            using var barrier = new Barrier(3);
            var tries = new[] { owner, desk, owner }.Select(c => Task.Run(() =>
            {
                barrier.SignalAndWait();
                return c.PostAsync(CheckOutUrl(pid, booked), new CheckOutRequest(null));
            })).ToArray();
            var results = await Task.WhenAll(tries);
            Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
            foreach (var loser in results.Where(r => r.StatusCode != HttpStatusCode.OK))
            {
                Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
                Assert.Contains((await ProblemAsync(loser)).Code, new[] { "ALREADY_CHECKED_OUT", "CONCURRENCY_CONFLICT" });
            }
            // And once more later.
            Assert.Equal("ALREADY_CHECKED_OUT", (await ProblemAsync(await owner.PostAsync(CheckOutUrl(pid, booked), new CheckOutRequest(null)))).Code);

            var tasks = await RowsAsync($"SELECT status, source, task_type, reservation_id, source_change_id FROM housekeeping_tasks WHERE unit_id = '{room.Id}'");
            var task = Assert.Single(tasks);
            Assert.Equal(new object?[] { "OPEN", "CHECK_OUT", "DEPARTURE", booked.Id }, task[..4]);
            var change = Assert.Single(await RowsAsync(
                $"SELECT id, to_status, consumed_at IS NOT NULL FROM unit_housekeeping_history WHERE unit_id = '{room.Id}'"));
            Assert.Equal(new object?[] { task[4], "DIRTY", true }, change);
        }

        // The board shows the last room dirty, with its task from the check-out and the default checklist.
        var board = await owner.GetJsonAsync<HousekeepingBoardDto>($"/api/v1/properties/{pid}/housekeeping/board");
        Assert.True(board.InspectionRequired);
        Assert.Equal(Today, board.Today);
        var dirty = board.Rooms.Where(r => r.Status == "DIRTY" && r.Task?.Source == "CHECK_OUT").ToList();
        Assert.True(dirty.Count >= 5);
        Assert.All(dirty, r =>
        {
            Assert.Equal("OPEN", r.Task!.Status);
            Assert.NotNull(r.Task.ReservationReference);
            Assert.True(r.DepartureToday);
            Assert.False(r.Occupied);
            Assert.Equal(7, r.Task.Checklist.Count);
            Assert.Contains("start", r.Task.NextActions);
        });
    }

    // ------------------------------------------------------------------ the review demo

    [Fact]
    public async Task Checkout_then_two_taps_by_the_housekeeper_then_the_supervisor_marks_it_ready_and_the_room_can_be_checked_into()
    {
        var (owner, pid) = await OwnerAsync();
        var housekeeper = await StaffAsync("HOUSEKEEPER", pid);
        var supervisor = await StaffAsync("HOUSEKEEPING_SUPERVISOR", pid);
        var stay = await SetupAsync(owner, pid, rooms: 1);
        var room = stay.Rooms[0];
        var guest = await CreateGuestAsync(owner);
        var source = await WalkInAsync(owner, pid);
        var first = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, Today, Today.AddDays(1))));
        await OkAsync(await owner.PostAsync(CheckInUrl(pid, first), new CheckInRequest(null)));
        await OkAsync(await owner.PostAsync(CheckOutUrl(pid, first), new CheckOutRequest(null)));
        // The next guest arrives today in the same room: it is a rush room.
        var next = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, Today, Today.AddDays(2))));

        var row = await RoomAsync(housekeeper, pid, room.Id);
        Assert.Equal(("DIRTY", true, true), (row.Status, row.Rush, row.ArrivalToday));
        Assert.Equal(new[] { "start" }, row.Task!.NextActions);
        var board = await housekeeper.GetJsonAsync<HousekeepingBoardDto>($"/api/v1/properties/{pid}/housekeeping/board");
        Assert.True(board.Rooms[0].Rush);   // rush rooms come first

        // Tap 1: Start. Nobody had it, so it is the housekeeper's now.
        var started = await TaskOkAsync(await housekeeper.PostAsync($"{TaskUrl(pid, row.Task.Id)}/start", new StartTaskRequest(row.Task.Version)));
        Assert.Equal(("IN_PROGRESS", housekeeper.Me!.UserId, "HOUSEKEEPER user"), (started.Status, started.AssignedTo!.Value, started.StartedByName));
        Assert.Equal("CLEANING", (await RoomAsync(owner, pid, room.Id)).Status);
        // Tap 2: Done (every checklist line counts as done).
        var finished = await TaskOkAsync(await housekeeper.PostAsync($"{TaskUrl(pid, row.Task.Id)}/finish", new FinishTaskRequest(null, null, started.Version)));
        Assert.Equal("CLEANED", finished.Status);
        Assert.All(finished.Checklist, l => Assert.True(l.Done));
        Assert.Empty(finished.NextActions);   // the housekeeper can't inspect

        // Cleaned but not inspected: reception can't put the guest in yet.
        var (code, message) = await ProblemAsync(await owner.PostAsync(CheckInUrl(pid, next), new CheckInRequest(null)));
        Assert.Equal("HOUSEKEEPING_NOT_READY", code);
        Assert.Contains($"{room.Name} is cleaned and waiting for inspection", message);

        // The housekeeper can't inspect; the supervisor marks it READY.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await housekeeper.PostAsync($"{TaskUrl(pid, row.Task.Id)}/inspect", new InspectTaskRequest("PASS", null, null))).StatusCode);
        var inspected = await TaskOkAsync(await supervisor.PostAsync($"{TaskUrl(pid, row.Task.Id)}/inspect",
            new InspectTaskRequest("PASS", "Looks good", finished.Version)));
        Assert.Equal(("DONE", "HOUSEKEEPING_SUPERVISOR user", "Looks good"), (inspected.Status, inspected.InspectedByName, inspected.InspectionNote));
        var ready = await RoomAsync(owner, pid, room.Id);
        Assert.Equal(("READY", false), (ready.Status, ready.Rush));
        Assert.Null(ready.Task);

        // The room is available: the next guest checks in.
        Assert.Equal("CHECKED_IN", (await OkAsync(await owner.PostAsync(CheckInUrl(pid, next), new CheckInRequest(null)))).Status);

        // The trail: CHECK_OUT → DIRTY, then the board's three steps.
        var trail = await RowsAsync($"SELECT to_status, source FROM unit_housekeeping_history WHERE unit_id = '{room.Id}' ORDER BY changed_at, id");
        Assert.Equal(["DIRTY:CHECK_OUT", "CLEANING:HOUSEKEEPING", "CLEAN:HOUSEKEEPING", "READY:HOUSEKEEPING"], trail.Select(t => $"{t[0]}:{t[1]}"));
        Assert.True((long)(await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE entity_id = '{row.Task.Id}'"))! >= 3);

        // Doing it again says what happened.
        var again = await housekeeper.PostAsync($"{TaskUrl(pid, row.Task.Id)}/start", new StartTaskRequest(null));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal($"{room.Name} is already cleaned and ready.", (await ProblemAsync(again)).Message);
    }

    [Fact]
    public async Task A_failed_inspection_sends_the_room_back_to_the_same_housekeeper_with_what_to_redo()
    {
        var (owner, pid) = await OwnerAsync();
        var housekeeper = await StaffAsync("HOUSEKEEPER", pid);
        var supervisor = await StaffAsync("HOUSEKEEPING_SUPERVISOR", pid);
        var task = await DirtyRoomAsync(owner, pid);

        var s = await TaskOkAsync(await housekeeper.PostAsync($"{TaskUrl(pid, task.Id)}/start", new StartTaskRequest(null)));
        var f = await TaskOkAsync(await housekeeper.PostAsync($"{TaskUrl(pid, task.Id)}/finish", new FinishTaskRequest(null, "Minibar restocked", s.Version)));

        // A failure needs the reason, under the Note field.
        Assert.Equal("Say what needs redoing (at least 3 characters), e.g. Bathroom mirror smeared.",
            await FieldErrorAsync(await supervisor.PostAsync($"{TaskUrl(pid, task.Id)}/inspect", new InspectTaskRequest("FAIL", " ", null)), "Note"));

        // Starting the inspection shows the room as being inspected.
        var looking = await TaskOkAsync(await supervisor.PostAsync($"{TaskUrl(pid, task.Id)}/inspect", new InspectTaskRequest("START", null, f.Version)));
        Assert.Equal("INSPECTION", (await RoomAsync(owner, pid, task.UnitId)).Status);
        var failed = await TaskOkAsync(await supervisor.PostAsync($"{TaskUrl(pid, task.Id)}/inspect",
            new InspectTaskRequest("FAIL", "Bathroom mirror smeared", looking.Version)));
        Assert.Equal(("OPEN", 1, "Bathroom mirror smeared"), (failed.Status, failed.FailedInspections, failed.InspectionNote));
        Assert.Equal(housekeeper.Me!.UserId, failed.AssignedTo);
        Assert.All(failed.Checklist, l => Assert.False(l.Done));
        Assert.Null(failed.StartedAt);
        Assert.Equal("DIRTY", (await RoomAsync(owner, pid, task.UnitId)).Status);

        // Back to the same housekeeper: two taps again, then it passes.
        var s2 = await TaskOkAsync(await housekeeper.PostAsync($"{TaskUrl(pid, task.Id)}/start", new StartTaskRequest(null)));
        var f2 = await TaskOkAsync(await housekeeper.PostAsync($"{TaskUrl(pid, task.Id)}/finish", new FinishTaskRequest(null, null, s2.Version)));
        Assert.Equal("DONE", (await TaskOkAsync(await supervisor.PostAsync($"{TaskUrl(pid, task.Id)}/inspect", new InspectTaskRequest("PASS", null, f2.Version)))).Status);
        Assert.Equal("READY", (await RoomAsync(owner, pid, task.UnitId)).Status);
    }

    [Fact]
    public async Task Without_inspection_the_housekeepers_done_makes_the_room_ready()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 1);
        try
        {
            await PutSettingAsync(owner, pid, "housekeeping.inspection_required", false);
            var housekeeper = await StaffAsync("HOUSEKEEPER", pid);
            await SetHousekeepingAsync(owner, pid, stay.Rooms[0].Id, "DIRTY", "Spilled tea");
            var row = await RoomAsync(housekeeper, pid, stay.Rooms[0].Id);
            Assert.Equal(("MANUAL", "Spilled tea"), (row.Task!.Source, row.Task.Note));
            var s = await TaskOkAsync(await housekeeper.PostAsync($"{TaskUrl(pid, row.Task.Id)}/start", new StartTaskRequest(null)));
            var f = await TaskOkAsync(await housekeeper.PostAsync($"{TaskUrl(pid, row.Task.Id)}/finish", new FinishTaskRequest(null, null, s.Version)));
            Assert.Equal("DONE", f.Status);
            Assert.Equal("READY", (await RoomAsync(owner, pid, stay.Rooms[0].Id)).Status);
        }
        finally
        {
            await PutSettingAsync(owner, pid, "housekeeping.inspection_required", true);
        }
    }

    // ------------------------------------------------------------------ two people, one room

    [Fact]
    public async Task Two_housekeepers_starting_the_same_room_at_the_same_moment_exactly_one_gets_it_10_times()
    {
        var (owner, pid) = await OwnerAsync();
        var a = await StaffAsync("HOUSEKEEPER", pid);
        var b = await StaffAsync("HOUSEKEEPER", pid);
        for (var run = 0; run < 10; run++)
        {
            var task = await DirtyRoomAsync(owner, pid);
            using var barrier = new Barrier(2);
            var ta = Task.Run(() => { barrier.SignalAndWait(); return a.PostAsync($"{TaskUrl(pid, task.Id)}/start", new StartTaskRequest(task.Version)); });
            var tb = Task.Run(() => { barrier.SignalAndWait(); return b.PostAsync($"{TaskUrl(pid, task.Id)}/start", new StartTaskRequest(null)); });
            var results = await Task.WhenAll(ta, tb);
            Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
            var loser = Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
            var (code, message) = await ProblemAsync(loser);
            Assert.Contains(code, new[] { "TASK_ALREADY_STARTED", "CONCURRENCY_CONFLICT" });
            if (code == "TASK_ALREADY_STARTED") Assert.Matches("is already being cleaned by HOUSEKEEPER user \\(started [0-9]{2}:[0-9]{2}\\)\\.$", message!);
            Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM unit_housekeeping_history WHERE unit_id = '{task.UnitId}' AND to_status = 'CLEANING'"));
        }
    }

    // ------------------------------------------------------------------ who may do what

    [Fact]
    public async Task Supervisors_assign_housekeepers_start_only_their_own_and_only_housekeeping_staff_can_be_given_rooms()
    {
        var (owner, pid) = await OwnerAsync();
        var asha = await StaffAsync("HOUSEKEEPER", pid);
        var ravi = await StaffAsync("HOUSEKEEPER", pid);
        var supervisor = await StaffAsync("HOUSEKEEPING_SUPERVISOR", pid);
        var (rEmail, rPw) = await app.CreateUserAsync("RECEPTIONIST", pid);
        var receptionist = await app.SignInAsync(rEmail, rPw);
        var task = await DirtyRoomAsync(owner, pid);

        // Housekeepers can't assign or see the staff list.
        Assert.Equal(HttpStatusCode.Forbidden, (await asha.PostAsync($"{TaskUrl(pid, task.Id)}/assign", new AssignTaskRequest(asha.Me!.UserId, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await asha.GetAsync($"/api/v1/properties/{pid}/housekeeping/staff")).StatusCode);

        var staff = await supervisor.GetJsonAsync<List<StaffOptionDto>>($"/api/v1/properties/{pid}/housekeeping/staff");
        Assert.Contains(staff, s => s.UserId == asha.Me.UserId);
        Assert.DoesNotContain(staff, s => s.UserId == receptionist.Me!.UserId);

        Assert.Equal("RECEPTIONIST user can't clean rooms at this property (no housekeeping role here). Choose someone from housekeeping.",
            await FieldErrorAsync(await supervisor.PostAsync($"{TaskUrl(pid, task.Id)}/assign", new AssignTaskRequest(receptionist.Me!.UserId, null)), "UserId"));
        var assigned = await TaskOkAsync(await supervisor.PostAsync($"{TaskUrl(pid, task.Id)}/assign", new AssignTaskRequest(asha.Me.UserId, task.Version)));
        Assert.Equal("HOUSEKEEPER user", assigned.AssignedToName);
        Assert.Equal(1, (await supervisor.GetJsonAsync<List<StaffOptionDto>>($"/api/v1/properties/{pid}/housekeeping/staff")).Single(s => s.UserId == asha.Me.UserId).OpenWork);

        // Someone else's room: Ravi can't start it (and isn't offered the button); Asha can.
        Assert.DoesNotContain("start", (await RoomAsync(ravi, pid, task.UnitId)).Task!.NextActions);
        var (code, message) = await ProblemAsync(await ravi.PostAsync($"{TaskUrl(pid, task.Id)}/start", new StartTaskRequest(null)));
        Assert.Equal(("TASK_ASSIGNED_TO_SOMEONE_ELSE", $"{(await RoomAsync(owner, pid, task.UnitId)).Place} is assigned to HOUSEKEEPER user. Ask your supervisor to give it to you."),
            (code, message));
        var s = await TaskOkAsync(await asha.PostAsync($"{TaskUrl(pid, task.Id)}/start", new StartTaskRequest(null)));
        // ... and only she (or a supervisor) can finish it.
        Assert.Equal("TASK_ASSIGNED_TO_SOMEONE_ELSE", (await ProblemAsync(await ravi.PostAsync($"{TaskUrl(pid, task.Id)}/finish", new FinishTaskRequest(null, null, null)))).Code);
        await TaskOkAsync(await supervisor.PostAsync($"{TaskUrl(pid, task.Id)}/finish", new FinishTaskRequest(null, null, s.Version)));

        // Reception can read the board but can't clean or inspect.
        Assert.Equal(HttpStatusCode.OK, (await receptionist.GetAsync($"/api/v1/properties/{pid}/housekeeping/board")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await receptionist.PostAsync($"{TaskUrl(pid, task.Id)}/inspect", new InspectTaskRequest("PASS", null, null))).StatusCode);
        // A task id from nowhere.
        Assert.Equal(HttpStatusCode.NotFound, (await supervisor.PostAsync($"{TaskUrl(pid, Guid.NewGuid())}/start", new StartTaskRequest(null))).StatusCode);
    }

    // ------------------------------------------------------------------ checklists and tasks by hand

    [Fact]
    public async Task A_required_checklist_line_must_be_ticked_and_a_room_type_can_have_its_own_checklist()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 1);
        var res = await owner.PostAsync($"/api/v1/properties/{pid}/housekeeping/checklists", new SaveChecklistTemplateRequest(
            "Villa departure", "DEPARTURE", stay.Type.Id, true,
            [new ChecklistItemRequest("Fresh linen", false), new ChecklistItemRequest("Check the minibar", true)]));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var template = (await res.Content.ReadFromJsonAsync<ChecklistTemplateDto>())!;
        Assert.Equal(stay.Type.Name, template.AccommodationTypeName);

        // A second active one for the same room type and kind is refused, naming the first.
        Assert.Equal($"\"Villa departure\" is already the departure clean checklist for {stay.Type.Name}. Switch that one off first, or edit it instead.",
            await FieldErrorAsync(await owner.PostAsync($"/api/v1/properties/{pid}/housekeeping/checklists", new SaveChecklistTemplateRequest(
                "Other", "DEPARTURE", stay.Type.Id, true, [new ChecklistItemRequest("Sweep", false)])), "TaskType"));
        Assert.Equal("Each line needs 2–120 characters, e.g. Fresh towels.",
            await FieldErrorAsync(await owner.PostAsync($"/api/v1/properties/{pid}/housekeeping/checklists", new SaveChecklistTemplateRequest(
                "Other", "STAYOVER", null, true, [new ChecklistItemRequest("x", false)])), "Items[0].Label"));

        var housekeeper = await StaffAsync("HOUSEKEEPER", pid);
        await SetHousekeepingAsync(owner, pid, stay.Rooms[0].Id, "DIRTY");
        var task = (await RoomAsync(housekeeper, pid, stay.Rooms[0].Id)).Task!;
        Assert.Equal(["Fresh linen", "Check the minibar"], task.Checklist.Select(l => l.Label));
        var s = await TaskOkAsync(await housekeeper.PostAsync($"{TaskUrl(pid, task.Id)}/start", new StartTaskRequest(null)));
        Assert.Equal("Tick every required line before finishing: Check the minibar.",
            await FieldErrorAsync(await housekeeper.PostAsync($"{TaskUrl(pid, task.Id)}/finish", new FinishTaskRequest([task.Checklist[0].Id], null, null)), "Checked"));
        var f = await TaskOkAsync(await housekeeper.PostAsync($"{TaskUrl(pid, task.Id)}/finish", new FinishTaskRequest([task.Checklist[1].Id], null, s.Version)));
        Assert.Equal([false, true], f.Checklist.Select(l => l.Done));

        // Editing the template later doesn't change the task's copy.
        await TaskOkPutAsync(owner, $"/api/v1/properties/{pid}/housekeeping/checklists/{template.Id}", new SaveChecklistTemplateRequest(
            "Villa departure", "DEPARTURE", stay.Type.Id, false, [new ChecklistItemRequest("Only line", false)]));
        Assert.Equal(["Fresh linen", "Check the minibar"],
            (await owner.GetJsonAsync<HousekeepingTaskDto>(TaskUrl(pid, task.Id))).Checklist.Select(l => l.Label));
    }

    [Fact]
    public async Task Front_desk_ready_closes_the_task_a_stay_over_task_can_be_made_by_hand_and_a_room_has_one_open_task()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 1, mode: "BED", beds: 2);
        var dorm = stay.Rooms[0];
        var beds = (await owner.GetJsonAsync<RoomDto>($"/api/v1/properties/{pid}/rooms/{dorm.Id}")).Beds;
        var supervisor = await StaffAsync("HOUSEKEEPING_SUPERVISOR", pid);
        var housekeeper = await StaffAsync("HOUSEKEEPER", pid);

        // A dorm needs the bed.
        Assert.Equal($"{dorm.Name} is a dormitory; choose the bed.", await FieldErrorAsync(await supervisor.PostAsync(
            $"/api/v1/properties/{pid}/housekeeping/tasks", new CreateTaskRequest(dorm.Id, null, "STAYOVER", null, null)), "BedId"));
        var res = await supervisor.PostAsync($"/api/v1/properties/{pid}/housekeeping/tasks",
            new CreateTaskRequest(dorm.Id, beds[0].Id, "STAYOVER", "Guest asked for fresh towels", housekeeper.Me!.UserId));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var task = (await res.Content.ReadFromJsonAsync<HousekeepingTaskDto>())!;
        Assert.Equal(($"{dorm.Name} bed {beds[0].Code}", "STAYOVER", "HOUSEKEEPER user"), (task.Place, task.TaskType, task.AssignedToName));
        var row = await RoomAsync(owner, pid, dorm.Id, beds[0].Id);
        Assert.Equal("DIRTY", row.Status);
        Assert.Equal("READY", (await RoomAsync(owner, pid, dorm.Id, beds[1].Id)).Status);

        var dup = await supervisor.PostAsync($"/api/v1/properties/{pid}/housekeeping/tasks", new CreateTaskRequest(dorm.Id, beds[0].Id, "DEEP_CLEAN", null, null));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Equal($"{dorm.Name} bed {beds[0].Code} already has an open task (stay-over clean, room dirty). Finish or inspect that one instead.",
            (await ProblemAsync(dup)).Message);
        Assert.Equal(HttpStatusCode.Forbidden, (await housekeeper.PostAsync($"/api/v1/properties/{pid}/housekeeping/tasks",
            new CreateTaskRequest(dorm.Id, beds[1].Id, "STAYOVER", null, null))).StatusCode);

        // Reception marks the bed ready by hand: the task is closed, not left hanging.
        var put = await owner.PutAsync($"/api/v1/properties/{pid}/rooms/{dorm.Id}/housekeeping", new SetHousekeepingRequest(beds[0].Id, "READY", "Checked by the manager"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Null((await RoomAsync(owner, pid, dorm.Id, beds[0].Id)).Task);
        Assert.Equal(new object?[] { "DONE", "Checked by the manager" },
            Assert.Single(await RowsAsync($"SELECT status, close_reason FROM housekeeping_tasks WHERE id = '{task.Id}'")));
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM housekeeping_tasks WHERE unit_id = '{dorm.Id}'"));
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(TestClient Owner, Guid PropertyId)> OwnerAsync() => (await app.SignInOwnerAsync(), await app.MainPropertyIdAsync());

    private async Task<TestClient> StaffAsync(string role, Guid pid)
    {
        var (email, pw) = await app.CreateUserAsync(role, pid);
        return await app.SignInAsync(email, pw);
    }

    private static string TaskUrl(Guid pid, Guid id) => $"/api/v1/properties/{pid}/housekeeping/tasks/{id}";

    /// <summary>A new room made DIRTY at the front desk, and its task.</summary>
    private async Task<HousekeepingTaskDto> DirtyRoomAsync(TestClient owner, Guid pid)
    {
        var stay = await SetupAsync(owner, pid, rooms: 1);
        await SetHousekeepingAsync(owner, pid, stay.Rooms[0].Id, "DIRTY");
        return (await RoomAsync(owner, pid, stay.Rooms[0].Id)).Task!;
    }

    internal static async Task<HousekeepingRoomDto> RoomAsync(TestClient c, Guid pid, Guid unitId, Guid? bedId = null) =>
        (await c.GetJsonAsync<HousekeepingBoardDto>($"/api/v1/properties/{pid}/housekeeping/board")).Rooms.Single(r => r.UnitId == unitId && r.BedId == bedId);

    private static async Task<HousekeepingTaskDto> TaskOkAsync(HttpResponseMessage res)
    {
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<HousekeepingTaskDto>())!;
    }

    private static async Task TaskOkPutAsync(TestClient c, string url, object body)
    {
        var res = await c.PutAsync(url, body);
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
    }

    private static async Task PutSettingAsync(TestClient c, Guid pid, string key, bool value) =>
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsync($"/api/v1/properties/{pid}/settings/{key}",
            new PutPropertySettingRequest(JsonSerializer.SerializeToElement(value)))).StatusCode);

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
