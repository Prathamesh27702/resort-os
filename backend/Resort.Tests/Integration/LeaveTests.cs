using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Npgsql;
using Resort.Contracts;
using Resort.Domain.Employees;
using Resort.Tests.Support;
using static Resort.Tests.Support.BookingKit;

namespace Resort.Tests.Integration;

/// <summary>Sprint 19 — leave, holidays and payroll: an employee asks for leave over a holiday with a half day, is refused
/// while her balance is short, then the manager sees the cover warnings and approves; the balance (the ledger's sum) goes
/// down, her full day comes off the roster and the half day stays; the month's payroll file shows it. Also overlapping
/// leave, own leave, refusing and cancelling, accruals (twice is once, a maximum, a part year), and two requests at the
/// same moment. The clock is pinned to the property's local times. Each test uses a property of its own. Real API, real
/// PostgreSQL.</summary>
public class LeaveTests(FrontDeskFactory app) : IClassFixture<FrontDeskFactory>
{
    private static readonly TimeZoneInfo Ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
    private static readonly DateOnly Today = new(2026, 10, 26);           // a Monday
    private static readonly DateOnly Tue = Today.AddDays(1), Wed = Today.AddDays(2), Thu = Today.AddDays(3);

    // ------------------------------------------------------------------ acceptance: brief §74 second half

    [Fact]
    public async Task An_employee_asks_for_leave_the_manager_approves_and_the_balance_roster_and_payroll_follow()
    {
        var s = await SetupAsync();
        Pin(Today, 9, 0);
        await OkAsync<HolidayDto>(await s.Hr.PostAsync($"{Url(s.Pid)}/leave/holidays", new SaveHolidayRequest(Wed, "Test holiday")), HttpStatusCode.Created);
        await ShiftAsync(s, Tue);
        await ShiftAsync(s, Thu);
        var cl = await TypeAsync(s, "CL");

        // Tuesday to Thursday, Wednesday a holiday, Thursday morning only: 1.5 days. With nothing in her balance: refused, saying so.
        var ask = new CreateLeaveRequest(null, cl.Id, Tue, Thu, false, true, "Family function");
        Assert.Equal("You have 0 days of Casual leave. These dates are 1.5 days. Ask for fewer days, or choose Leave without pay.",
            await FieldErrorAsync(await s.Asha.PostAsync($"{Url(s.Pid)}/leave/requests", ask), "LeaveTypeId"));

        // HR brings in her balance from before: 5 days.
        await OkAsync<LeaveLedgerDto>(await s.Hr.PostAsync($"{Url(s.Pid)}/leave/adjustments",
            new AdjustLeaveRequest(s.AshaId, cl.Id, 5, "Balance from the old register", Opening: true)));

        var preview = await OkAsync<LeavePreviewDto>(await s.Asha.PostAsync($"{Url(s.Pid)}/leave/requests/preview", ask));
        Assert.Equal((1.5m, 5m, 3.5m), (preview.Days, preview.Balance!.Value, preview.BalanceAfter!.Value));
        Assert.Equal([$"{Short(Wed)}: Test holiday"], preview.Skipped);
        var req = await OkAsync<LeaveRequestDto>(await s.Asha.PostAsync($"{Url(s.Pid)}/leave/requests", ask), HttpStatusCode.Created);
        Assert.Equal(("PENDING", 1.5m, "LV00001"), (req.Status, req.Days, req.Code));
        Assert.Equal(["cancel"], req.Actions);

        // The manager sees it with the cover warnings: Tuesday's shift comes off the roster and leaves Morning short.
        var pending = await OkAsync<LeaveListDto>(await s.Manager.GetAsync($"{Url(s.Pid)}/leave/requests?status=PENDING"));
        var row = Assert.Single(pending.Requests);
        Assert.Equal(["approve", "reject", "cancel"], row.Actions);
        Assert.Equal(["ROSTERED", "SHORT_STAFFED"], row.Warnings.Select(w => w.Kind));
        Assert.Equal($"{Short(Tue)}: Front office · Morning would have 0 of the 1 people it needs.", row.Warnings[1].Message);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Asha.PostAsync($"{Url(s.Pid)}/leave/requests/{req.Id}/approve", new ApproveLeaveRequest(null))).StatusCode);

        var approved = await OkAsync<LeaveRequestDto>(await s.Manager.PostAsync($"{Url(s.Pid)}/leave/requests/{req.Id}/approve",
            new ApproveLeaveRequest("Enjoy", req.Version)));
        Assert.Equal(("APPROVED", 1, 3.5m), (approved.Status, approved.ShiftsCancelled, approved.Balance!.Value));

        // Balance = ledger sum; the ledger shows the opening and the leave taken.
        var ledger = await OkAsync<LeaveLedgerDto>(await s.Asha.GetAsync($"{Url(s.Pid)}/leave/balances/{s.AshaId}"));
        Assert.Equal([("TAKEN", -1.5m, 3.5m), ("OPENING", 5m, 5m)], ledger.Entries.Select(e => (e.EntryType, e.Days, e.BalanceAfter)));
        Assert.Equal(3.5m, ledger.Employee.Balances.Single(b => b.Code == "CL").Balance);
        Assert.Equal(0L, await ScalarAsync($"""
            SELECT count(*) FROM leave_balances b
            WHERE b.balance <> (SELECT COALESCE(sum(days), 0) FROM leave_ledger l WHERE l.employee_id = b.employee_id AND l.leave_type_id = b.leave_type_id)
            """));

        // The roster: Tuesday's shift is gone with the reason, Thursday's (a half day) stays; both days show the leave.
        var week = await OkAsync<RosterWeekDto>(await s.Manager.GetAsync($"{Url(s.Pid)}/roster?week={Today:yyyy-MM-dd}"));
        Assert.Equal([Thu], week.Shifts.Where(x => x.EmployeeId == s.AshaId).Select(x => x.WorkDate));
        Assert.Equal([(Tue, false), (Thu, true)], week.Leave.Select(l => (l.Date, l.Half)));
        Assert.Equal("On leave: LV00001 (Casual leave)", await ScalarAsync($"SELECT cancel_reason FROM roster_shifts WHERE employee_id = '{s.AshaId}' AND work_date = '{Tue:yyyy-MM-dd}'"));
        var onLeave = await s.Manager.PostAsync($"{Url(s.Pid)}/roster/shifts", new SaveRosterShiftRequest(s.AshaId, Tue, s.Morning, null, null, null, null));
        Assert.Equal(HttpStatusCode.Conflict, onLeave.StatusCode);
        Assert.Equal(("ON_LEAVE", $"Asha Naik is on leave on {Day(Tue)} (CL LV00001). Choose someone else, or cancel the leave first."),
            await FrontDeskTests.ProblemAsync(onLeave));

        // October's payroll: her 1.5 days of paid leave and the holiday, with the month not over yet.
        var pay = await OkAsync<PayrollPreviewDto>(await s.Hr.GetAsync($"{Url(s.Pid)}/payroll/preview?month=2026-10"));
        var line = pay.Rows.Single(r => r.EmployeeName == "Asha Naik");
        Assert.Equal((31, 1, 1.5m, 0m, 31m, "CL 1.5"), (line.EmployedDays, line.Holidays, line.PaidLeaveDays, line.UnpaidLeaveDays, line.PayableDays, line.LeaveDetail));
        Assert.Contains(pay.Warnings, w => w.StartsWith("October 2026 hasn't ended", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Manager.GetAsync($"{Url(s.Pid)}/payroll/preview?month=2026-10")).StatusCode);
    }

    // ------------------------------------------------------------------ required: half days, holidays inside the range, insufficient balance

    [Fact]
    public async Task Half_days_and_holidays_inside_the_range_are_counted_and_impossible_ones_are_refused_under_the_field()
    {
        var s = await SetupAsync();
        Pin(Today, 9, 0);
        await OkAsync<HolidayDto>(await s.Hr.PostAsync($"{Url(s.Pid)}/leave/holidays", new SaveHolidayRequest(Wed, "Test holiday")), HttpStatusCode.Created);
        Assert.Equal($"{Day(Wed)} is already a holiday: Test holiday.",
            await FieldErrorAsync(await s.Hr.PostAsync($"{Url(s.Pid)}/leave/holidays", new SaveHolidayRequest(Wed, "Again")), "Date"));
        var cl = await TypeAsync(s, "CL");
        var el = await TypeAsync(s, "EL");
        var lop = await TypeAsync(s, "LOP");
        await OkAsync<LeaveLedgerDto>(await s.Hr.PostAsync($"{Url(s.Pid)}/leave/adjustments", new AdjustLeaveRequest(s.AshaId, cl.Id, 2, "Opening", true)));

        async Task<LeavePreviewDto> Preview(CreateLeaveRequest r) => await OkAsync<LeavePreviewDto>(await s.Asha.PostAsync($"{Url(s.Pid)}/leave/requests/preview", r));
        Task<string?> Refused(CreateLeaveRequest r, string field) => s.Asha.PostAsync($"{Url(s.Pid)}/leave/requests/preview", r).ContinueWith(t => FieldErrorAsync(t.Result, field)).Unwrap();

        Assert.Equal(0.5m, (await Preview(new(null, cl.Id, Tue, Tue, true, false, null))).Days);
        Assert.Equal(1.5m, (await Preview(new(null, cl.Id, Tue, Thu, true, false, null))).Days);
        Assert.Equal(2m, (await Preview(new(null, cl.Id, Tue, Thu, false, false, null))).Days);
        Assert.Equal($"{Short(Wed)} is a holiday (Test holiday), so it can't be a half day.",
            await Refused(new(null, cl.Id, Wed, Thu, true, false, null), "HalfDayStart"));
        Assert.Equal($"{Day(Wed)} is a holiday (Test holiday), so there is no leave to take.",
            await Refused(new(null, cl.Id, Wed, Wed, false, false, null), "FromDate"));
        Assert.Equal("For one day, choose either the morning off or the afternoon off, not both (that is a whole day).",
            await Refused(new(null, cl.Id, Tue, Tue, true, true, null), "HalfDayEnd"));
        Assert.Equal("Earned leave is taken in whole days only.", await Refused(new(null, el.Id, Tue, Tue, false, true, null), "HalfDayEnd"));
        Assert.Equal("The last day is before the first day.", await Refused(new(null, cl.Id, Thu, Tue, false, false, null), "ToDate"));
        Assert.Equal($"Leave can be asked for up to 30 days back (from {Day(Today.AddDays(-30))}). For older days ask HR.",
            await Refused(new(null, cl.Id, Today.AddDays(-31), Today.AddDays(-31), false, false, null), "FromDate"));

        // Insufficient balance counts what is already waiting for approval.
        await OkAsync<LeaveRequestDto>(await s.Asha.PostAsync($"{Url(s.Pid)}/leave/requests", new CreateLeaveRequest(null, cl.Id, Tue, Tue, false, false, null)),
            HttpStatusCode.Created);
        Assert.Equal("You have 2 days of Casual leave (1 already asked for and waiting, so 1 left). These dates are 1.5 days. Ask for fewer days, or choose Leave without pay.",
            await Refused(new(null, cl.Id, Thu, Today.AddDays(4), false, true, null), "LeaveTypeId"));
        // Leave without pay needs no balance.
        Assert.Null((await Preview(new(null, lop.Id, Thu, Today.AddDays(4), false, false, null))).Balance);
    }

    // ------------------------------------------------------------------ overlap, own leave, refusing, cancelling

    [Fact]
    public async Task Leave_cannot_overlap_nobody_approves_their_own_and_approved_leave_is_cancelled_only_before_it_starts()
    {
        var s = await SetupAsync();
        Pin(Today, 9, 0);
        var cl = await TypeAsync(s, "CL");
        await OkAsync<LeaveLedgerDto>(await s.Hr.PostAsync($"{Url(s.Pid)}/leave/adjustments", new AdjustLeaveRequest(s.AshaId, cl.Id, 6, "Opening", true)));
        var a = await OkAsync<LeaveRequestDto>(await s.Asha.PostAsync($"{Url(s.Pid)}/leave/requests", new CreateLeaveRequest(null, cl.Id, Tue, Wed, false, false, null)),
            HttpStatusCode.Created);
        var clash = await s.Asha.PostAsync($"{Url(s.Pid)}/leave/requests", new CreateLeaveRequest(null, cl.Id, Wed, Thu, false, false, null));
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Equal(("LEAVE_OVERLAP", $"You already have Casual leave {a.Code} on {Short(Tue)} – {Short(Wed)} (waiting for approval). Leave can't overlap: choose other dates, or cancel {a.Code} first."),
            await FrontDeskTests.ProblemAsync(clash));

        // The manager's own leave: another manager or HR approves it.
        var mine = await OkAsync<LeaveRequestDto>(await s.Manager.PostAsync($"{Url(s.Pid)}/leave/requests",
            new CreateLeaveRequest(null, (await TypeAsync(s, "LOP")).Id, Thu, Thu, false, false, null)), HttpStatusCode.Created);
        Assert.Equal(["cancel"], mine.Actions);
        var own = await s.Manager.PostAsync($"{Url(s.Pid)}/leave/requests/{mine.Id}/approve", new ApproveLeaveRequest(null));
        Assert.Equal(("OWN_LEAVE", "You can't approve your own leave. Another manager or HR does it."), await FrontDeskTests.ProblemAsync(own));
        var refused = await OkAsync<LeaveRequestDto>(await s.Hr.PostAsync($"{Url(s.Pid)}/leave/requests/{mine.Id}/reject", new RejectLeaveRequest("Audit that day")));
        Assert.Equal(("REJECTED", "Audit that day"), (refused.Status, refused.DecisionNote));

        // Approved, then cancelled the day before it starts: the days go back.
        await OkAsync<LeaveRequestDto>(await s.Manager.PostAsync($"{Url(s.Pid)}/leave/requests/{a.Id}/approve", new ApproveLeaveRequest(null)));
        Assert.Equal(4m, await BalanceAsync(s, cl.Id));
        var cancelled = await OkAsync<LeaveRequestDto>(await s.Asha.PostAsync($"{Url(s.Pid)}/leave/requests/{a.Id}/cancel", new CancelLeaveRequest("Trip postponed")));
        Assert.Equal(("CANCELLED", "Trip postponed"), (cancelled.Status, cancelled.CancelReason));
        Assert.Equal(6m, await BalanceAsync(s, cl.Id));

        // The same dates can be asked for again; once the leave has begun it can't be cancelled.
        var again = await OkAsync<LeaveRequestDto>(await s.Asha.PostAsync($"{Url(s.Pid)}/leave/requests", new CreateLeaveRequest(null, cl.Id, Tue, Wed, false, false, null)),
            HttpStatusCode.Created);
        await OkAsync<LeaveRequestDto>(await s.Manager.PostAsync($"{Url(s.Pid)}/leave/requests/{again.Id}/approve", new ApproveLeaveRequest(null)));
        Pin(Tue, 10, 0);
        var late = await s.Asha.PostAsync($"{Url(s.Pid)}/leave/requests/{again.Id}/cancel", new CancelLeaveRequest("Came back"));
        Assert.Equal(("LEAVE_STARTED", $"{again.Code} began on {Day(Tue)}, so it can't be cancelled now. If you came back early, HR adjusts the balance."),
            await FrontDeskTests.ProblemAsync(late));
        Assert.Equal(4m, await BalanceAsync(s, cl.Id));
        // The ledger can't be changed by hand.
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"UPDATE leave_ledger SET days = 10 WHERE employee_id = '{s.AshaId}'"));
    }

    // ------------------------------------------------------------------ accrual

    [Fact]
    public async Task The_accrual_posts_a_month_once_stops_at_the_maximum_and_gives_a_part_year_to_someone_who_joined_in_the_year()
    {
        var s = await SetupAsync();
        Pin(Today, 9, 0);
        var types = await OkAsync<List<LeaveTypeDto>>(await s.Hr.GetAsync($"{Url(s.Pid)}/leave/types"));
        Assert.Equal(["CL", "SL", "EL", "LOP"], types.Select(t => t.Code));
        var cl = types.Single(t => t.Code == "CL");
        // A newcomer who joined on 10 Apr 2026.
        var kavyaId = await EmployeeAsync(s.Owner, s.Pid, s.Dept, "Kavya", "Shetty", null, new DateOnly(2026, 4, 10));

        var run = await OkAsync<AccrualResultDto>(await s.Hr.PostAsync($"{Url(s.Pid)}/leave/accruals/run", new RunAccrualRequest("2026-10")));
        // Three people × (CL monthly, SL yearly, EL monthly).
        Assert.Equal((9, 0), (run.Posted, run.AlreadyPosted));
        var again = await OkAsync<AccrualResultDto>(await s.Hr.PostAsync($"{Url(s.Pid)}/leave/accruals/run", new RunAccrualRequest("2026-10")));
        Assert.Equal((0, 9), (again.Posted, again.AlreadyPosted));
        var balances = await OkAsync<List<EmployeeLeaveBalancesDto>>(await s.Hr.GetAsync($"{Url(s.Pid)}/leave/balances"));
        var kavya = balances.Single(b => b.EmployeeId == kavyaId).Balances.ToDictionary(b => b.Code, b => b.Balance);
        Assert.Equal((1m, 4m, 1.25m), (kavya["CL"], kavya["SL"], kavya["EL"]));
        var asha = balances.Single(b => b.EmployeeId == s.AshaId).Balances.ToDictionary(b => b.Code, b => b.Balance);
        Assert.Equal((1m, 6m), (asha["CL"], asha["SL"]));

        // CL stops at 12: with 11.5 in hand, November adds half a day and says why.
        await OkAsync<LeaveLedgerDto>(await s.Hr.PostAsync($"{Url(s.Pid)}/leave/adjustments", new AdjustLeaveRequest(s.AshaId, cl.Id, 10.5m, "Old register")));
        Pin(new DateOnly(2026, 11, 2), 9, 0);
        var nov = await OkAsync<AccrualResultDto>(await s.Hr.PostAsync($"{Url(s.Pid)}/leave/accruals/run", new RunAccrualRequest("2026-11")));
        Assert.Contains("Asha Naik: CL is at its maximum of 12 days, so 0.5 of 1 was added.", nov.Notes);
        Assert.Equal(12m, await BalanceAsync(s, cl.Id));
        Assert.Equal("December 2026 hasn't started yet. Leave is earned on the 1st of the month.",
            await FieldErrorAsync(await s.Hr.PostAsync($"{Url(s.Pid)}/leave/accruals/run", new RunAccrualRequest("2026-12")), "Month"));
        // Taking off more than there is: refused under the days.
        Assert.Equal("Asha Naik has 12 days of Casual leave. You can take off at most 12.",
            await FieldErrorAsync(await s.Hr.PostAsync($"{Url(s.Pid)}/leave/adjustments", new AdjustLeaveRequest(s.AshaId, cl.Id, -13, "Mistake")), "Days"));
    }

    // ------------------------------------------------------------------ two at once

    [Fact]
    public async Task Two_requests_for_the_same_days_at_the_same_moment_make_exactly_one()
    {
        var s = await SetupAsync();
        Pin(Today, 9, 0);
        var lop = await TypeAsync(s, "LOP");
        for (var i = 0; i < 5; i++)
        {
            var day = Today.AddDays(7 + i * 2);
            var gate = new Barrier(2);
            var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
            {
                gate.SignalAndWait();
                return await s.Asha.PostAsync($"{Url(s.Pid)}/leave/requests", new CreateLeaveRequest(null, lop.Id, day, day, false, false, null));
            })).ToArray();
            var results = await Task.WhenAll(tasks);
            Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], results.Select(r => r.StatusCode).Order());
        }
        Assert.Equal(5L, await ScalarAsync($"SELECT count(*) FROM leave_requests WHERE employee_id = '{s.AshaId}'"));
    }

    [Fact]
    public async Task Approvals_at_the_same_moment_never_take_a_balance_below_zero_5_times()
    {
        var s = await SetupAsync();
        Pin(Today, 9, 0);
        var cl = await TypeAsync(s, "CL");
        var day = Today.AddDays(7);
        for (var round = 0; round < 5; round++)
        {
            // Four one-day requests asked while she had 4 days, then HR takes 2 off: only two of them can be approved.
            await OkAsync<LeaveLedgerDto>(await s.Hr.PostAsync($"{Url(s.Pid)}/leave/adjustments", new AdjustLeaveRequest(s.AshaId, cl.Id, 4, "Test round", Opening: false)));
            var ids = new List<Guid>();
            for (var tries = 0; ids.Count < 4; tries++)
            {
                Assert.True(tries < 20, "Could not ask for four days");
                var res = await s.Asha.PostAsync($"{Url(s.Pid)}/leave/requests", new CreateLeaveRequest(null, cl.Id, day, day, false, false, null));
                day = day.AddDays(1);
                if (res.StatusCode == HttpStatusCode.Created) ids.Add((await res.Content.ReadFromJsonAsync<LeaveRequestDto>())!.Id);
            }
            await OkAsync<LeaveLedgerDto>(await s.Hr.PostAsync($"{Url(s.Pid)}/leave/adjustments", new AdjustLeaveRequest(s.AshaId, cl.Id, -2, "Test round", Opening: false)));

            var gate = new Barrier(ids.Count);
            var tasks = ids.Select((id, i) => Task.Run(async () =>
            {
                gate.SignalAndWait();
                return await (i % 2 == 0 ? s.Manager : s.Hr).PostAsync($"{Url(s.Pid)}/leave/requests/{id}/approve", new ApproveLeaveRequest(null));
            })).ToArray();
            var results = await Task.WhenAll(tasks);
            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.UnprocessableEntity, HttpStatusCode.UnprocessableEntity],
                results.Select(r => r.StatusCode).Order());
            Assert.Equal(0m, await BalanceAsync(s, cl.Id));
            // The two left waiting are refused, so the next round starts with nothing waiting.
            foreach (var (id, res) in ids.Zip(results).Where(x => x.Second.StatusCode != HttpStatusCode.OK))
            {
                Assert.Contains("INSUFFICIENT_LEAVE_BALANCE", await res.Content.ReadAsStringAsync());
                await OkAsync<LeaveRequestDto>(await s.Manager.PostAsync($"{Url(s.Pid)}/leave/requests/{id}/reject", new RejectLeaveRequest("No balance left")));
            }
        }
        Assert.Equal(10L, await ScalarAsync($"SELECT count(*) FROM leave_ledger WHERE employee_id = '{s.AshaId}' AND entry_type = 'TAKEN'"));
    }

    // ------------------------------------------------------------------ payroll export

    [Fact]
    public async Task The_payroll_file_waits_for_unready_weeks_unless_confirmed_locks_the_approved_ones_and_is_kept_as_made()
    {
        var s = await SetupAsync();
        // September: Asha worked Tue 1 Sep (approved), Thu 3 Sep is still waiting; a day of leave without pay on Fri 4 Sep.
        var sep1 = new DateOnly(2026, 9, 1);
        Pin(sep1.AddDays(-1), 9, 0);
        await OkAsync<RosterShiftDto>(await s.Owner.PostAsync($"{Url(s.Pid)}/roster/shifts", new SaveRosterShiftRequest(s.AshaId, sep1, s.Morning, null, null, null, null)),
            HttpStatusCode.Created);
        var lop = await TypeAsync(s, "LOP");
        var off = await OkAsync<LeaveRequestDto>(await s.Asha.PostAsync($"{Url(s.Pid)}/leave/requests",
            new CreateLeaveRequest(null, lop.Id, sep1.AddDays(3), sep1.AddDays(3), false, false, "Personal")), HttpStatusCode.Created);
        await OkAsync<LeaveRequestDto>(await s.Manager.PostAsync($"{Url(s.Pid)}/leave/requests/{off.Id}/approve", new ApproveLeaveRequest(null)));
        foreach (var (h, m, a) in new[] { (8, 0, "CLOCK_IN"), (16, 0, "CLOCK_OUT") })
        {
            Pin(sep1, h, m);
            Assert.True((await s.Asha.PostAsync($"{Url(s.Pid)}/attendance/me/clock", new ClockRequest(a, null, null, null))).IsSuccessStatusCode);
        }
        Pin(sep1.AddDays(2), 8, 0);
        Assert.True((await s.Asha.PostAsync($"{Url(s.Pid)}/attendance/me/clock", new ClockRequest("CLOCK_IN", null, null, null))).IsSuccessStatusCode);
        Pin(sep1.AddDays(2), 12, 0);
        Assert.True((await s.Asha.PostAsync($"{Url(s.Pid)}/attendance/me/clock", new ClockRequest("CLOCK_OUT", null, null, null))).IsSuccessStatusCode);

        // The week of 31 Aug is submitted and approved; nothing of the week of 7 Sep yet.
        Pin(new DateOnly(2026, 9, 7), 9, 0);
        var sheet = await OkAsync<TimesheetDto>(await s.Asha.GetAsync($"{Url(s.Pid)}/timesheets/mine?week=2026-09-01"));
        await OkAsync<TimesheetDto>(await s.Asha.PostAsync($"{Url(s.Pid)}/timesheets/{sheet.Sheet.Id}/submit", new SubmitTimesheetRequest(null)));
        await OkAsync<TimesheetDto>(await s.Manager.PostAsync($"{Url(s.Pid)}/timesheets/{sheet.Sheet.Id}/approve", new TimesheetVersionRequest()));
        Pin(Today, 9, 0);

        var preview = await OkAsync<PayrollPreviewDto>(await s.Hr.GetAsync($"{Url(s.Pid)}/payroll/preview?month=2026-09"));
        Assert.Equal(1, preview.ApprovedToLock);
        Assert.Empty(preview.Warnings);
        var row = preview.Rows.Single(r => r.EmployeeName == "Asha Naik");
        // 8 h on the 1st (30 min past the shift's paid 7 h 30 min) and 4 h on the 3rd (no shift); a day without pay.
        Assert.Equal((30, 2, 720, 30, 0m, 1m, 29m, "LOP 1"),
            (row.EmployedDays, row.DaysWorked, row.WorkedMinutes, row.OvertimeMinutes, row.PaidLeaveDays, row.UnpaidLeaveDays, row.PayableDays, row.LeaveDetail));

        // October has work that isn't approved: refused unless confirmed.
        await ShiftAsync(s, Today.AddDays(-7));
        Pin(Today.AddDays(-7), 8, 0);
        Assert.True((await s.Asha.PostAsync($"{Url(s.Pid)}/attendance/me/clock", new ClockRequest("CLOCK_IN", null, null, null))).IsSuccessStatusCode);
        Pin(Today, 9, 0);
        var notReady = await s.Hr.PostAsync($"{Url(s.Pid)}/payroll/exports", new CreatePayrollExportRequest("2026-10"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notReady.StatusCode);
        var (code, message) = await FrontDeskTests.ProblemAsync(notReady);
        Assert.Equal("PAYROLL_NOT_READY", code);
        Assert.StartsWith("2 things aren't ready for October 2026: October 2026 hasn't ended", message, StringComparison.Ordinal);
        Assert.Contains("Asha Naik, week of Mon 19 Oct: the timesheet is still a draft, so its hours are left out.", message);

        // September's file: made, the approved week locked, kept with its SHA-256.
        var export = await OkAsync<PayrollExportDto>(await s.Hr.PostAsync($"{Url(s.Pid)}/payroll/exports", new CreatePayrollExportRequest("2026-09")), HttpStatusCode.Created);
        Assert.Equal((1, 2), (export.TimesheetsLocked, export.Employees));
        Assert.Equal("LOCKED", await ScalarAsync($"SELECT status FROM timesheets WHERE id = '{sheet.Sheet.Id}'"));
        var file = await s.Hr.GetAsync($"{Url(s.Pid)}/payroll/exports/{export.Id}/file");
        Assert.Equal("text/csv", file.Content.Headers.ContentType!.MediaType);
        var bytes = await file.Content.ReadAsByteArrayAsync();
        Assert.Equal(export.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var lines = System.Text.Encoding.UTF8.GetString(bytes).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("employee_code,employee_name,department,", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, l => l.Contains(",Asha Naik,Front office,FULL_TIME,2026-09-01,2026-09-30,30,0,0,2,12.00,0.50,0,0,1,0,29,LOP 1", StringComparison.Ordinal));
        Assert.Single(await OkAsync<List<PayrollExportDto>>(await s.Hr.GetAsync($"{Url(s.Pid)}/payroll/exports")));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"UPDATE payroll_exports SET employees = 0 WHERE id = '{export.Id}'"));
    }

    // ------------------------------------------------------------------ set-up

    private sealed record Setup(Guid Pid, Guid Dept, TestClient Owner, TestClient Manager, TestClient Hr, TestClient Asha, Guid AshaId, Guid Morning);

    /// <summary>A property with Front office and a Morning shift (08:00–16:00, needs 1 person), Asha (a housekeeper's
    /// account, linked) and the manager's own employee record. No geofence.</summary>
    private async Task<Setup> SetupAsync()
    {
        app.Clock.At = null;
        var owner = await app.SignInOwnerAsync();
        var code = "L" + Suffix();
        var res = await owner.PostAsync("/api/v1/properties", new CreatePropertyRequest(code, $"Leave Shack {code}", null, "RESORT", "Asia/Kolkata", "INR",
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
        var ashaId = await EmployeeAsync(owner, pid, dept.Id, "Asha", "Naik", await UserIdAsync(aEmail), new DateOnly(2024, 1, 1));
        await EmployeeAsync(owner, pid, dept.Id, "Rohan", "Desai", await UserIdAsync(mEmail), new DateOnly(2024, 1, 1));
        return new Setup(pid, dept.Id, owner, manager, hr, asha, ashaId, morning.Id);
    }

    private async Task<LeaveTypeDto> TypeAsync(Setup s, string code) =>
        (await OkAsync<List<LeaveTypeDto>>(await s.Asha.GetAsync($"{Url(s.Pid)}/leave/types"))).Single(t => t.Code == code);

    private async Task<decimal> BalanceAsync(Setup s, Guid typeId) =>
        (decimal)(await ScalarAsync($"SELECT balance FROM leave_balances WHERE employee_id = '{s.AshaId}' AND leave_type_id = '{typeId}'"))!;

    private async Task ShiftAsync(Setup s, DateOnly day)
    {
        var at = app.Clock.At;
        Pin(day.AddDays(-1), 9, 0);
        await OkAsync<RosterShiftDto>(await s.Owner.PostAsync($"{Url(s.Pid)}/roster/shifts", new SaveRosterShiftRequest(s.AshaId, day, s.Morning, null, null, null, null)),
            HttpStatusCode.Created);
        app.Clock.At = at;
    }

    private static async Task<Guid> EmployeeAsync(TestClient c, Guid pid, Guid dept, string first, string last, Guid? userId, DateOnly joined) =>
        (await OkAsync<EmployeeDto>(await c.PostAsync($"{Url(pid)}/employees",
            new SaveEmployeeRequest(first, last, null, null, null, dept, null, "FULL_TIME", joined, null, userId, null, null, null, null)),
            HttpStatusCode.Created)).Id;

    private async Task<Guid> UserIdAsync(string email) => (Guid)(await ScalarAsync($"SELECT id FROM users WHERE normalized_email = lower('{email}')"))!;

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

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
