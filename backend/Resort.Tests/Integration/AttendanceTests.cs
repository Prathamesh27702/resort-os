using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Resort.Contracts;
using Resort.Domain.Employees;
using Resort.Tests.Support;
using static Resort.Tests.Support.BookingKit;

namespace Resort.Tests.Integration;

/// <summary>Sprint 18 — attendance and timesheets: an employee clocks in and out on their phone inside the geofence (the
/// server checks it), takes a break and gets 7 h 37 min for 08:03–16:10 with a 30-minute break; a night shift across
/// midnight counts for the day it started; the week is submitted, approved, locked, and can't change after that; a
/// forgotten clock-out is corrected by a manager with the reason kept; a double tap clocks in once. The clock is pinned to
/// the property's local times. Each test uses a property of its own. Real API, real PostgreSQL.</summary>
public class AttendanceTests(FrontDeskFactory app) : IClassFixture<FrontDeskFactory>
{
    private static readonly TimeZoneInfo Ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
    private static readonly DateOnly Monday = new(2026, 10, 19);
    private static readonly DateOnly Thursday = Monday.AddDays(3);

    // The resort's gate (Calangute, Goa) and a point about 1.1 km up the road.
    private const decimal GateLat = 15.544000m, GateLng = 73.755300m, AwayLat = 15.544000m, AwayLng = 73.765300m;

    // ------------------------------------------------------------------ acceptance: brief §74 up to approval

    [Fact]
    public async Task An_employee_clocks_in_and_out_on_a_phone_at_the_resort_and_the_manager_approves_the_week()
    {
        var s = await SetupAsync();
        await ShiftAsync(s, Thursday, s.Morning);

        // Away from the resort: refused with how far away it is. No location at all: asked to allow it.
        Pin(Thursday, 8, 1);
        var away = await s.Asha.PostAsync(Clock(s), new ClockRequest("CLOCK_IN", AwayLat, AwayLng, 12));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, away.StatusCode);
        var (code, message) = await FrontDeskTests.ProblemAsync(away);
        Assert.Equal("OUTSIDE_GEOFENCE", code);
        Assert.Equal("You are 1.1 km from Main gate; clocking in counts within 150 m of it. Do it at the resort, or ask your manager to record it with a reason.", message);
        Assert.Equal("We need your phone's location to check you are at the resort. Allow location for this app in the browser, then try again.",
            await FieldErrorAsync(await s.Asha.PostAsync(Clock(s), new ClockRequest("CLOCK_IN", null, null, null)), "Latitude"));
        Assert.Equal("Your phone only knows where you are to within 1.2 km. Turn on precise location (GPS) or step outside, then try again.",
            await FieldErrorAsync(await s.Asha.PostAsync(Clock(s), new ClockRequest("CLOCK_IN", GateLat, GateLng, 1200)), "AccuracyM"));

        // At the gate: 08:03 in (3 minutes late for the 08:00 shift), a break 12:00–12:30, out at 16:10.
        Pin(Thursday, 8, 3);
        var me = await ClockAsync(s, "CLOCK_IN", GateLat + 0.0003m);
        Assert.Equal(("ON_SHIFT", "INSIDE", "Main gate"), (me.State, me.Events[0].LocationStatus, me.Events[0].GeofenceName));
        Assert.Equal((Thursday, "Morning"), (me.WorkDate!.Value, me.TodayShift!.TemplateName));
        var twice = await s.Asha.PostAsync(Clock(s), new ClockRequest("CLOCK_IN", GateLat, GateLng, 10));
        Assert.Equal(("ATTENDANCE_STATE", "You clocked in at 08:03 and haven't clocked out yet."), await FrontDeskTests.ProblemAsync(twice));
        Pin(Thursday, 12, 0);
        Assert.Equal("ON_BREAK", (await ClockAsync(s, "BREAK_START")).State);
        Pin(Thursday, 12, 30);
        await ClockAsync(s, "BREAK_END");
        Pin(Thursday, 16, 10);
        me = await ClockAsync(s, "CLOCK_OUT");
        Assert.Equal(("OFF", 457, 30), (me.State, me.WorkedMinutes, me.BreakMinutes));

        // The week's timesheet: Thursday is 7 h 37 min, 3 minutes late.
        var sheet = await OkAsync<TimesheetDto>(await s.Asha.GetAsync($"{Url(s.Pid)}/timesheets/mine?week={Thursday:yyyy-MM-dd}"));
        var line = Assert.Single(sheet.Lines);
        Assert.Equal((Thursday, "Morning", new TimeOnly(8, 3), new TimeOnly(16, 10), 457, 30, 3, 0, 7, "WORKED"),
            (line.WorkDate, line.ShiftName, line.ClockIn!.Value, line.ClockOut!.Value, line.WorkedMinutes, line.BreakMinutes, line.LateMinutes,
                line.EarlyMinutes, line.OvertimeMinutes, line.Status));
        Assert.Equal(("DRAFT", 457, 1), (sheet.Sheet.Status, sheet.Sheet.WorkedMinutes, sheet.Sheet.DaysWorked));
        Assert.Equal(["submit"], sheet.Sheet.Actions);

        // She submits; the manager sees it in the week, can't touch her own, and approves Asha's; HR locks it for payroll.
        Pin(Thursday.AddDays(4), 10, 0);
        sheet = await OkAsync<TimesheetDto>(await s.Asha.PostAsync($"{Url(s.Pid)}/timesheets/{sheet.Sheet.Id}/submit", new SubmitTimesheetRequest("Thursday only")));
        Assert.Equal("SUBMITTED", sheet.Sheet.Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Asha.PostAsync($"{Url(s.Pid)}/timesheets/{sheet.Sheet.Id}/approve", new TimesheetVersionRequest())).StatusCode);
        var week = await OkAsync<TimesheetWeekDto>(await s.Manager.GetAsync($"{Url(s.Pid)}/timesheets?week={Monday:yyyy-MM-dd}"));
        var row = Assert.Single(week.Timesheets);
        Assert.Equal(("Asha Naik", "SUBMITTED", 457), (row.EmployeeName, row.Status, row.WorkedMinutes));
        Assert.Equal(["approve", "reject", "correct"], row.Actions);
        var approved = await OkAsync<ApproveTimesheetWeekResultDto>(await s.Manager.PostAsync($"{Url(s.Pid)}/timesheets/approve-week", new ApproveTimesheetWeekRequest(Monday)));
        Assert.Equal((1, 0), (approved.Approved, approved.Skipped.Count));
        Assert.Equal(("APPROVED", "MANAGER user"), (approved.Week.Timesheets[0].Status, approved.Week.Timesheets[0].DecidedBy));
        var locked = await OkAsync<TimesheetDto>(await s.Hr.PostAsync($"{Url(s.Pid)}/timesheets/{sheet.Sheet.Id}/lock", new TimesheetVersionRequest()));
        Assert.Equal("LOCKED", locked.Sheet.Status);

        // Locked: no correction, no new clock-in that week, and the database refuses any change.
        var refused = await s.Hr.PostAsync($"{Url(s.Pid)}/timesheets/{sheet.Sheet.Id}/corrections",
            new CorrectTimesheetLineRequest(Thursday, new TimeOnly(8, 0), new TimeOnly(16, 0), 30, "Rounding"));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(("TIMESHEET_LOCKED", $"This timesheet was locked for payroll on {Day(Thursday.AddDays(4))}. It can't change any more."),
            await FrontDeskTests.ProblemAsync(refused));
        Pin(Monday.AddDays(6), 9, 0);
        var closed = await s.Asha.PostAsync(Clock(s), new ClockRequest("CLOCK_IN", GateLat, GateLng, 10));
        Assert.Equal(("TIMESHEET_CLOSED", $"Your timesheet for the week of {Day(Monday)} is already locked for payroll. Ask your manager to add this to it."),
            await FrontDeskTests.ProblemAsync(closed));
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"UPDATE timesheet_lines SET worked_minutes = 600 WHERE timesheet_id = '{sheet.Sheet.Id}'"));
        Assert.Contains("locked for payroll", ex.MessageText);
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM attendance_events WHERE employee_id = '{s.AshaId}' AND occurred_at >= '{Monday.AddDays(6):yyyy-MM-dd}'"));
    }

    // ------------------------------------------------------------------ required: a shift crossing midnight

    [Fact]
    public async Task A_night_shift_on_Sunday_that_ends_on_Monday_morning_counts_in_the_week_it_started()
    {
        var s = await SetupAsync();
        var sunday = Monday.AddDays(6);
        await ShiftAsync(s, sunday, s.Night);

        Pin(sunday, 22, 55);
        var me = await ClockAsync(s, "CLOCK_IN");
        Assert.Equal((sunday, "Night"), (me.WorkDate!.Value, me.TodayShift!.TemplateName));
        Pin(sunday.AddDays(1), 2, 45);
        await ClockAsync(s, "BREAK_START");
        Pin(sunday.AddDays(1), 3, 15);
        await ClockAsync(s, "BREAK_END");
        Pin(sunday.AddDays(1), 7, 10);
        me = await ClockAsync(s, "CLOCK_OUT");
        Assert.Equal(("OFF", 465), (me.State, me.WorkedMinutes));

        // Sunday's line, in the week of the 19th; the next week has nothing.
        var sheet = await OkAsync<TimesheetDto>(await s.Asha.GetAsync($"{Url(s.Pid)}/timesheets/mine?week={sunday:yyyy-MM-dd}"));
        var line = Assert.Single(sheet.Lines);
        Assert.Equal((sunday, new TimeOnly(22, 55), new TimeOnly(7, 10), true, 465, 30, 15, 0, 0),
            (line.WorkDate, line.ClockIn!.Value, line.ClockOut!.Value, line.ClockOutNextDay, line.WorkedMinutes, line.BreakMinutes, line.OvertimeMinutes,
                line.LateMinutes, line.EarlyMinutes));
        Assert.Equal(Monday, sheet.Sheet.WeekStart);
        var next = await OkAsync<TimesheetDto>(await s.Asha.GetAsync($"{Url(s.Pid)}/timesheets/mine"));
        Assert.Equal((Monday.AddDays(7), 0), (next.Sheet.WeekStart, next.Lines.Count));
    }

    // ------------------------------------------------------------------ corrections and manager entries

    [Fact]
    public async Task A_forgotten_clock_out_blocks_approval_until_a_manager_corrects_the_day_and_the_correction_is_kept()
    {
        var s = await SetupAsync();
        await ShiftAsync(s, Monday, s.Morning);
        await ShiftAsync(s, Monday.AddDays(1), s.Morning);

        // Monday: in at 07:58, never out. Tuesday she can still clock in (Monday is a missing clock-out).
        Pin(Monday, 7, 58);
        await ClockAsync(s, "CLOCK_IN");
        Pin(Monday.AddDays(1), 8, 0);
        var me = await OkAsync<MyAttendanceDto>(await s.Asha.GetAsync($"{Url(s.Pid)}/attendance/me"));
        Assert.Equal(("OFF", $"You clocked in on {Day(Monday)} at 07:58 and didn't clock out. Tell your manager when you left so they can correct it."),
            (me.State, me.MissedClockOut));
        await ClockAsync(s, "CLOCK_IN");
        Pin(Monday.AddDays(1), 16, 0);
        await ClockAsync(s, "CLOCK_OUT");

        // Submitted, but the manager can't approve it until Monday is closed.
        var sheet = await OkAsync<TimesheetDto>(await s.Asha.GetAsync($"{Url(s.Pid)}/timesheets/mine"));
        Assert.Equal(["MISSING_OUT", "WORKED"], sheet.Lines.Select(l => l.Status));
        sheet = await OkAsync<TimesheetDto>(await s.Asha.PostAsync($"{Url(s.Pid)}/timesheets/{sheet.Sheet.Id}/submit", new SubmitTimesheetRequest("Forgot Monday's clock-out, left at 16:00")));
        var refused = await s.Manager.PostAsync($"{Url(s.Pid)}/timesheets/{sheet.Sheet.Id}/approve", new TimesheetVersionRequest());
        Assert.Equal(("TIMESHEET_OPEN_DAY", "Mon 19 Oct has a clock-in at 07:58 with no clock-out. Correct that day first."), await FrontDeskTests.ProblemAsync(refused));

        // The manager corrects Monday with the reason; the old and new values are kept, and approval goes through.
        Assert.Equal("Say why the times change, 3–300 characters (e.g. Forgot to clock out, left at 16:00).",
            await FieldErrorAsync(await s.Manager.PostAsync(Correct(s, sheet), new CorrectTimesheetLineRequest(Monday, new TimeOnly(7, 58), new TimeOnly(16, 0), 30, "")), "Reason"));
        Assert.Equal("Give both the clock-in and the clock-out time, or leave both empty if the day wasn't worked.",
            await FieldErrorAsync(await s.Manager.PostAsync(Correct(s, sheet), new CorrectTimesheetLineRequest(Monday, new TimeOnly(7, 58), null, 30, "Left at four")), "ClockOut"));
        Assert.Equal($"Choose a day in the week of {Day(Monday)} (Monday to Sunday).",
            await FieldErrorAsync(await s.Manager.PostAsync(Correct(s, sheet), new CorrectTimesheetLineRequest(Monday.AddDays(7), new TimeOnly(8, 0), new TimeOnly(16, 0), 30, "Left at four")), "WorkDate"));
        sheet = await OkAsync<TimesheetDto>(await s.Manager.PostAsync(Correct(s, sheet),
            new CorrectTimesheetLineRequest(Monday, new TimeOnly(7, 58), new TimeOnly(16, 0), 30, "Forgot to clock out, left at 16:00 (checked with the gate log)")));
        var monday = sheet.Lines[0];
        Assert.Equal(("WORKED", 452, true, 0, 2), (monday.Status, monday.WorkedMinutes, monday.Corrected, monday.LateMinutes, monday.OvertimeMinutes));
        var correction = Assert.Single(sheet.Corrections);
        Assert.Equal((new TimeOnly(7, 58), (TimeOnly?)null, 0, new TimeOnly(16, 0), 452, "SUBMITTED", "MANAGER user"),
            (correction.OldClockIn!.Value, correction.OldClockOut, correction.OldWorkedMinutes!.Value, correction.NewClockOut!.Value, correction.NewWorkedMinutes,
                correction.TimesheetStatus, correction.By));
        sheet = await OkAsync<TimesheetDto>(await s.Manager.PostAsync($"{Url(s.Pid)}/timesheets/{sheet.Sheet.Id}/approve", new TimesheetVersionRequest(sheet.Sheet.Version)));
        Assert.Equal(("APPROVED", 452 + 480), (sheet.Sheet.Status, sheet.Sheet.WorkedMinutes));   // Tuesday 08:00–16:00, no break taken

        // A correction after approval keeps it approved and is listed; the database never lets a correction change.
        Pin(Monday.AddDays(2), 9, 0);
        sheet = await OkAsync<TimesheetDto>(await s.Hr.PostAsync(Correct(s, sheet),
            new CorrectTimesheetLineRequest(Monday.AddDays(1), new TimeOnly(8, 0), new TimeOnly(17, 0), 30, "Stayed for the audit, signed by Rohan")));
        Assert.Equal(("APPROVED", 2, 452 + 510), (sheet.Sheet.Status, sheet.Corrections.Count, sheet.Sheet.WorkedMinutes));
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE entity_type = 'TimesheetCorrection' AND changes::text LIKE '%Stayed for the audit%'"));
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"UPDATE timesheet_corrections SET reason = 'x' WHERE timesheet_id = '{sheet.Sheet.Id}'"));
        Assert.Contains("append-only", ex.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_manager_records_a_clock_in_with_a_reason_but_never_their_own_and_a_rejected_week_goes_back_to_the_employee()
    {
        var s = await SetupAsync();
        await ShiftAsync(s, Thursday, s.Morning);

        // Her phone has no signal at the beach: the manager clocks her in at 08:00 with the reason.
        Pin(Thursday, 8, 20);
        Assert.Equal("Say why you are recording this for them, 3–300 characters (e.g. Phone had no signal at the beach).",
            await FieldErrorAsync(await s.Manager.PostAsync($"{Url(s.Pid)}/attendance/events", new ManagerClockRequest(s.AshaId, "CLOCK_IN", null, new TimeOnly(8, 0), "")), "Reason"));
        Assert.Equal($"09:00 on {Day(Thursday)} hasn't happened yet. Give a time that has passed.",
            await FieldErrorAsync(await s.Manager.PostAsync($"{Url(s.Pid)}/attendance/events", new ManagerClockRequest(s.AshaId, "CLOCK_IN", null, new TimeOnly(9, 0), "No signal")), "Time"));
        var day = await OkAsync<AttendanceDayDto>(await s.Manager.PostAsync($"{Url(s.Pid)}/attendance/events",
            new ManagerClockRequest(s.AshaId, "CLOCK_IN", null, new TimeOnly(8, 0), "Phone had no signal at the beach")));
        var row = Assert.Single(day.Rows);
        Assert.Equal(("ON_SHIFT", new TimeOnly(8, 0), true, "OVERRIDE", "MANAGER user"),
            (row.State, row.ClockIn!.Value, row.Overridden, row.Events[0].LocationStatus, row.Events[0].RecordedBy));

        // A manager can't record their own attendance, nor something before the person's latest entry.
        var own = await s.Manager.PostAsync($"{Url(s.Pid)}/attendance/events", new ManagerClockRequest(s.ManagerEmployeeId, "CLOCK_IN", null, null, "Forgot my phone"));
        Assert.Equal(("OWN_ATTENDANCE", "You can't record your own attendance. Clock in on your phone, or ask another manager."), await FrontDeskTests.ProblemAsync(own));
        Assert.Equal($"Asha's latest entry is a clock-in at 08:00 on {Day(Thursday)}. A new entry must come after it; to change earlier times, correct the day on the timesheet.",
            await FieldErrorAsync(await s.Manager.PostAsync($"{Url(s.Pid)}/attendance/events", new ManagerClockRequest(s.AshaId, "CLOCK_OUT", null, new TimeOnly(7, 30), "Left early")), "Time"));

        // A housekeeper can clock themselves in but can't see the board.
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Asha.GetAsync($"{Url(s.Pid)}/attendance")).StatusCode);

        // She clocks out herself and submits; the manager sends it back with the reason.
        Pin(Thursday, 16, 5);
        await ClockAsync(s, "CLOCK_OUT");
        var sheet = await OkAsync<TimesheetDto>(await s.Asha.GetAsync($"{Url(s.Pid)}/timesheets/mine"));
        Assert.True(sheet.Lines[0].Overridden);
        sheet = await OkAsync<TimesheetDto>(await s.Asha.PostAsync($"{Url(s.Pid)}/timesheets/{sheet.Sheet.Id}/submit", new SubmitTimesheetRequest(null)));
        Assert.Equal("Say what needs fixing, 3–300 characters (e.g. Thursday's clock-out is missing).",
            await FieldErrorAsync(await s.Manager.PostAsync($"{Url(s.Pid)}/timesheets/{sheet.Sheet.Id}/reject", new RejectTimesheetRequest("")), "Reason"));
        sheet = await OkAsync<TimesheetDto>(await s.Manager.PostAsync($"{Url(s.Pid)}/timesheets/{sheet.Sheet.Id}/reject", new RejectTimesheetRequest("Add Friday's beach clean-up")));
        Assert.Equal(("REJECTED", "Add Friday's beach clean-up"), (sheet.Sheet.Status, sheet.Sheet.RejectionReason));

        // Friday she works again; the rejected sheet takes the new day and she submits once more.
        Pin(Thursday.AddDays(1), 9, 0);
        await ClockAsync(s, "CLOCK_IN");
        Pin(Thursday.AddDays(1), 12, 0);
        await ClockAsync(s, "CLOCK_OUT");
        sheet = await OkAsync<TimesheetDto>(await s.Asha.GetAsync($"{Url(s.Pid)}/timesheets/mine"));
        Assert.Equal((2, "REJECTED"), (sheet.Lines.Count, sheet.Sheet.Status));
        sheet = await OkAsync<TimesheetDto>(await s.Asha.PostAsync($"{Url(s.Pid)}/timesheets/{sheet.Sheet.Id}/submit", new SubmitTimesheetRequest("Added Friday")));
        Assert.Equal(("SUBMITTED", (string?)null), (sheet.Sheet.Status, sheet.Sheet.RejectionReason));
    }

    // ------------------------------------------------------------------ a double tap clocks in once

    [Fact]
    public async Task Two_clock_ins_sent_at_the_same_moment_make_exactly_one_5_times()
    {
        var s = await SetupAsync();
        for (var run = 0; run < 5; run++)
        {
            Pin(Monday.AddDays(run), 9, 0);
            using var barrier = new Barrier(2);
            var taps = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            {
                barrier.SignalAndWait();
                return s.Asha.PostAsync(Clock(s), new ClockRequest("CLOCK_IN", GateLat, GateLng, 10));
            })).ToArray();
            var results = await Task.WhenAll(taps);
            Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
            Assert.Equal("ATTENDANCE_STATE", (await FrontDeskTests.ProblemAsync(results.Single(r => r.StatusCode != HttpStatusCode.OK))).Code);
            Pin(Monday.AddDays(run), 17, 0);
            await ClockAsync(s, "CLOCK_OUT");
        }
        Assert.Equal(5L, await ScalarAsync($"SELECT count(*) FROM attendance_events WHERE employee_id = '{s.AshaId}' AND event_type = 'CLOCK_IN'"));
        var sheet = await OkAsync<TimesheetDto>(await s.Asha.GetAsync($"{Url(s.Pid)}/timesheets/mine"));
        Assert.Equal((5, 5 * 480, 0), (sheet.Sheet.DaysWorked, sheet.Sheet.WorkedMinutes, sheet.Sheet.OvertimeMinutes));
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"DELETE FROM attendance_events WHERE employee_id = '{s.AshaId}'"));
        Assert.Contains("append-only", ex.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Setup(Guid Pid, TestClient Owner, TestClient Manager, TestClient Hr, TestClient Asha, Guid AshaId, Guid ManagerEmployeeId,
        Guid Morning, Guid Night);

    /// <summary>A property with a 150 m geofence at the gate, Front office with a Morning (08:00–16:00) and a Night
    /// (23:00–07:00) shift, Asha (a housekeeper's account, linked) and the manager's own employee record.</summary>
    private async Task<Setup> SetupAsync()
    {
        app.Clock.At = null;
        var owner = await app.SignInOwnerAsync();
        var code = "P" + Suffix();
        var res = await owner.PostAsync("/api/v1/properties", new CreatePropertyRequest(code, $"Beach Shack {code}", null, "RESORT", "Asia/Kolkata", "INR",
            null, null, null, null, null, "30", null, "IN", null, null, new TimeOnly(14, 0), new TimeOnly(11, 0), null, null));
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        var pid = (await res.Content.ReadFromJsonAsync<PropertyDto>())!.Id;
        var (mEmail, mPw) = await app.CreateUserAsync("MANAGER", pid);
        var (hEmail, hPw) = await app.CreateUserAsync("HR", pid);
        var (aEmail, aPw) = await app.CreateUserAsync("HOUSEKEEPER", pid);
        var manager = await app.SignInAsync(mEmail, mPw);
        var hr = await app.SignInAsync(hEmail, hPw);
        var asha = await app.SignInAsync(aEmail, aPw);

        var dept = await OkAsync<DepartmentDto>(await owner.PostAsync($"{Url(pid)}/departments", new SaveDepartmentRequest("FRONT", "Front office")), HttpStatusCode.Created);
        var morning = await OkAsync<ShiftTemplateDto>(await owner.PostAsync($"{Url(pid)}/roster/templates",
            new SaveShiftTemplateRequest(dept.Id, "FO-AM", "Morning", new TimeOnly(8, 0), new TimeOnly(16, 0), 30, 1)), HttpStatusCode.Created);
        var night = await OkAsync<ShiftTemplateDto>(await owner.PostAsync($"{Url(pid)}/roster/templates",
            new SaveShiftTemplateRequest(dept.Id, "FO-NIGHT", "Night", new TimeOnly(23, 0), new TimeOnly(7, 0), 30, 0)), HttpStatusCode.Created);
        var ashaId = await EmployeeAsync(owner, pid, dept.Id, "Asha", "Naik", await UserIdAsync(aEmail));
        var managerId = await EmployeeAsync(owner, pid, dept.Id, "Rohan", "Desai", await UserIdAsync(mEmail));
        await OkAsync<GeofenceDto>(await manager.PostAsync($"{Url(pid)}/attendance/geofences", new SaveGeofenceRequest("Main gate", GateLat, GateLng, 150)),
            HttpStatusCode.Created);
        return new Setup(pid, owner, manager, hr, asha, ashaId, managerId, morning.Id, night.Id);
    }

    private async Task ShiftAsync(Setup s, DateOnly day, Guid template)
    {
        Pin(day.AddDays(-1), 9, 0);
        await OkAsync<RosterShiftDto>(await s.Owner.PostAsync($"{Url(s.Pid)}/roster/shifts", new SaveRosterShiftRequest(s.AshaId, day, template, null, null, null, null)),
            HttpStatusCode.Created);
    }

    private async Task<MyAttendanceDto> ClockAsync(Setup s, string action, decimal lat = GateLat) =>
        await OkAsync<MyAttendanceDto>(await s.Asha.PostAsync(Clock(s), new ClockRequest(action, lat, GateLng, 10)));

    private static async Task<Guid> EmployeeAsync(TestClient c, Guid pid, Guid dept, string first, string last, Guid userId) =>
        (await OkAsync<EmployeeDto>(await c.PostAsync($"{Url(pid)}/employees",
            new SaveEmployeeRequest(first, last, null, null, null, dept, null, "FULL_TIME", new DateOnly(2024, 1, 1), null, userId, null, null, null, null)),
            HttpStatusCode.Created)).Id;

    private async Task<Guid> UserIdAsync(string email) => (Guid)(await ScalarAsync($"SELECT id FROM users WHERE normalized_email = lower('{email}')"))!;

    /// <summary>Stands the clock at a local time at the property.</summary>
    private void Pin(DateOnly day, int h, int m) => app.Clock.At = RosterMath.Instants(day, new TimeOnly(h, m), new TimeOnly(h, m).AddMinutes(1), Ist).Start;

    private static string Url(Guid pid) => $"/api/v1/properties/{pid}";
    private static string Clock(Setup s) => $"{Url(s.Pid)}/attendance/me/clock";
    private static string Correct(Setup s, TimesheetDto t) => $"{Url(s.Pid)}/timesheets/{t.Sheet.Id}/corrections";
    private static string Day(DateOnly d) => Resort.Domain.Rates.QuoteEngine.Day(d);

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

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
