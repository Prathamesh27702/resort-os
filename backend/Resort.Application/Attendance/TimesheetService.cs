using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Employees;
using Resort.Application.Reservations;
using Resort.Contracts;
using Resort.Domain.Attendance;
using Resort.Domain.Employees;
using Resort.Domain.Organizations;
using Resort.Domain.Rates;

namespace Resort.Application.Attendance;

/// <summary>
/// Weekly timesheets (Sprint 18, brief §44).
/// <list type="bullet">
/// <item>One timesheet per employee and week (Monday to Sunday), with a line per work day that has a clock-in or a rostered
/// shift that has started. Lines are worked out from the clock events in the property's time zone
/// (<see cref="TimesheetMath"/>) while the timesheet is DRAFT or REJECTED, and frozen once it is submitted.</item>
/// <item>DRAFT → SUBMITTED (the employee) → APPROVED or REJECTED with a reason (a manager; never their own) → LOCKED (for
/// payroll; the database refuses any change after that). A new clock-in reopens a SUBMITTED timesheet.</item>
/// <item>A manager corrects a day (in, out, break) with a reason: the old and new values are kept in
/// timesheet_corrections, the line is never recalculated from events again, and an approved timesheet stays approved
/// (correcting one needs timesheet.approve too). A locked timesheet can't be corrected.</item>
/// </list>
/// </summary>
public sealed class TimesheetService(IAppDbContext db, ICurrentUser caller, CallerAccess access, TimeProvider clock, EmployeeService employees)
{
    // ------------------------------------------------------------------ reading

    /// <summary>A week's timesheets at the property (attendance.read). Everyone with a clock-in or a started shift that week
    /// gets one (DRAFT) so absences show too.</summary>
    public async Task<TimesheetWeekDto> WeekAsync(Guid propertyId, DateOnly? week, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var now = clock.GetUtcNow();
        var today = property.LocalDate(now);
        var from = RosterMath.WeekStart(week ?? today);
        var to = from.AddDays(6);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var withShifts = await db.RosterShifts.AsNoTracking()
            .Where(s => s.PropertyId == propertyId && s.Status == RosterShiftStatuses.Scheduled && s.WorkDate >= from && s.WorkDate <= to && s.StartsAt <= now)
            .Select(s => s.EmployeeId).Distinct().ToListAsync(ct);
        var withEvents = await db.AttendanceEvents.AsNoTracking()
            .Where(e => e.PropertyId == propertyId && e.WorkDate >= from && e.WorkDate <= to).Select(e => e.EmployeeId).Distinct().ToListAsync(ct);
        foreach (var id in withShifts.Union(withEvents).Order())
            await EnsureAsync(property, id, from, ct);

        var sheets = await LockSheetsAsync(db.Timesheets.Where(t => t.PropertyId == propertyId && t.WeekStart == from), ct);
        foreach (var t in sheets) await RecalculateAsync(property, t, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var dtos = await SummariesAsync(property, sheets, ct);
        var counts = new[] { TimesheetStatuses.Draft, TimesheetStatuses.Submitted, TimesheetStatuses.Approved, TimesheetStatuses.Rejected, TimesheetStatuses.Locked }
            .ToDictionary(s => s, s => dtos.Count(d => d.Status == s));
        return new TimesheetWeekDto(from, to, today, dtos, counts);
    }

    /// <summary>One timesheet with its days, events and corrections (attendance.read).</summary>
    public async Task<TimesheetDto> GetAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var t = await LoadAsync(propertyId, id, ct);
        await RecalculateAsync(property, t, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await DtoAsync(property, t, ct);
    }

    /// <summary>The caller's own timesheet for a week (timesheet.self), from their linked employee record.</summary>
    public async Task<TimesheetDto> MineAsync(Guid propertyId, DateOnly? week, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var emp = await MyEmployeeAsync(propertyId, ct);
        var from = RosterMath.WeekStart(week ?? property.LocalDate(clock.GetUtcNow()));
        if (from < RosterMath.WeekStart(emp.JoinedOn))
            throw ReservationService.Field("Week", $"You joined on {QuoteEngine.Day(emp.JoinedOn)}. Choose that week or a later one.");
        if (from > RosterMath.WeekStart(property.LocalDate(clock.GetUtcNow())))
            throw ReservationService.Field("Week", "That week hasn't started yet. Choose this week or an earlier one.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await EnsureAsync(property, emp.Id, from, ct);
        var t = (await LockSheetsAsync(db.Timesheets.Where(x => x.EmployeeId == emp.Id && x.WeekStart == from), ct)).Single();
        await RecalculateAsync(property, t, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await DtoAsync(property, t, ct);
    }

    // ------------------------------------------------------------------ the states

    /// <summary>The employee hands in their week (timesheet.self, own timesheet only).</summary>
    public async Task<TimesheetDto> SubmitAsync(Guid propertyId, Guid id, SubmitTimesheetRequest r, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var emp = await MyEmployeeAsync(propertyId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var t = await LoadAsync(propertyId, id, ct);
        if (t.EmployeeId != emp.Id) throw new ForbiddenAppException("This isn't your timesheet. You can only submit your own.");
        if (r.Version is { } v) Expect(t, v);
        if (!t.Open)
            throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION", t.Status switch
            {
                TimesheetStatuses.Submitted => "You already submitted this timesheet. Your manager will approve or send it back.",
                TimesheetStatuses.Approved => "This timesheet is already approved.",
                _ => "This timesheet is locked for payroll.",
            });
        await RecalculateAsync(property, t, ct);
        var lines = await db.TimesheetLines.Where(l => l.TimesheetId == t.Id).OrderBy(l => l.WorkDate).ToListAsync(ct);
        if (lines.Count == 0)
            throw new BusinessRuleAppException("TIMESHEET_EMPTY", $"There is nothing on your timesheet for the week of {QuoteEngine.Day(t.WeekStart)} yet: no clock-ins and no shifts.");
        if (lines.FirstOrDefault(l => l.Status == TimesheetLineStatuses.OnShift) is { } on)
            throw new BusinessRuleAppException("STILL_CLOCKED_IN", $"You are still clocked in since {T(Local(on.ClockInAt!.Value, Tz(property)))}. Clock out first, then submit.");
        t.Status = TimesheetStatuses.Submitted;
        t.SubmittedAt = clock.GetUtcNow();
        t.SubmittedBy = caller.RequireUserId();
        t.RejectionReason = null;
        t.DecidedAt = null;
        t.DecidedBy = null;
        t.Note = ReservationService.Clean(r.Note);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await DtoAsync(property, t, ct);
    }

    /// <summary>A manager approves a submitted timesheet (timesheet.approve; not their own). Every day must be closed.</summary>
    public async Task<TimesheetDto> ApproveAsync(Guid propertyId, Guid id, TimesheetVersionRequest r, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var t = await LoadAsync(propertyId, id, ct);
        if (r.Version is { } v) Expect(t, v);
        if (await ApproveRefusalAsync(property, t, ct) is { } why) throw why;
        t.Status = TimesheetStatuses.Approved;
        t.DecidedAt = clock.GetUtcNow();
        t.DecidedBy = caller.RequireUserId();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await DtoAsync(property, t, ct);
    }

    /// <summary>Approves every submitted timesheet of a week that can be; the others are listed with the reason.</summary>
    public async Task<ApproveTimesheetWeekResultDto> ApproveWeekAsync(Guid propertyId, ApproveTimesheetWeekRequest r, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var from = RosterMath.WeekStart(r.WeekStart);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var sheets = await LockSheetsAsync(db.Timesheets.Where(t => t.PropertyId == propertyId && t.WeekStart == from && t.Status == TimesheetStatuses.Submitted), ct);
        if (sheets.Count == 0)
            throw ReservationService.Field(nameof(r.WeekStart), $"No timesheets are waiting for approval in the week of {QuoteEngine.Day(from)}.");
        var names = await db.Employees.AsNoTracking().Where(e => sheets.Select(s => s.EmployeeId).Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        var skipped = new List<string>();
        var approved = 0;
        foreach (var t in sheets.OrderBy(s => names[s.EmployeeId].FirstName))
        {
            if (await ApproveRefusalAsync(property, t, ct) is { } why)
            {
                skipped.Add($"{names[t.EmployeeId].FullName}: {why.Message}");
                continue;
            }
            t.Status = TimesheetStatuses.Approved;
            t.DecidedAt = clock.GetUtcNow();
            t.DecidedBy = caller.RequireUserId();
            approved++;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new ApproveTimesheetWeekResultDto(approved, skipped, await WeekAsync(propertyId, from, ct));
    }

    /// <summary>A manager sends a submitted timesheet back with the reason (timesheet.approve; not their own).</summary>
    public async Task<TimesheetDto> RejectAsync(Guid propertyId, Guid id, RejectTimesheetRequest r, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var t = await LoadAsync(propertyId, id, ct);
        if (r.Version is { } v) Expect(t, v);
        await RequireNotOwnAsync(t, "send back", ct);
        if (t.Status != TimesheetStatuses.Submitted)
            throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION", $"Only a submitted timesheet can be sent back; this one is {Words(t.Status)}.");
        t.Status = TimesheetStatuses.Rejected;
        t.RejectionReason = r.Reason.Trim();
        t.DecidedAt = clock.GetUtcNow();
        t.DecidedBy = caller.RequireUserId();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await DtoAsync(property, t, ct);
    }

    /// <summary>Locks an approved timesheet for payroll (timesheet.approve; not their own). Nothing changes after that.</summary>
    public async Task<TimesheetDto> LockAsync(Guid propertyId, Guid id, TimesheetVersionRequest r, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var t = await LoadAsync(propertyId, id, ct);
        if (r.Version is { } v) Expect(t, v);
        await RequireNotOwnAsync(t, "lock", ct);
        if (t.Status != TimesheetStatuses.Approved)
            throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION", $"Only an approved timesheet can be locked; this one is {Words(t.Status)}.");
        t.Status = TimesheetStatuses.Locked;
        t.LockedAt = clock.GetUtcNow();
        t.LockedBy = caller.RequireUserId();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await DtoAsync(property, t, ct);
    }

    // ------------------------------------------------------------------ corrections

    /// <summary>A manager sets one day's times with the reason (attendance.update; an approved timesheet also needs
    /// timesheet.approve). The old and new values are kept; a locked timesheet can't be corrected.</summary>
    public async Task<TimesheetDto> CorrectAsync(Guid propertyId, Guid id, CorrectTimesheetLineRequest r, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var tz = Tz(property);
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var t = await LoadAsync(propertyId, id, ct);
        if (r.Version is { } v) Expect(t, v);
        if (t.Status == TimesheetStatuses.Locked)
            throw new ConflictAppException("TIMESHEET_LOCKED",
                $"This timesheet was locked for payroll on {QuoteEngine.Day(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(t.LockedAt!.Value, tz).DateTime))}. It can't change any more.");
        await RequireNotOwnAsync(t, "correct", ct);
        if (t.Status == TimesheetStatuses.Approved && !(await access.GetAsync(ct)).Has(Perm.TimesheetApprove, propertyId))
            throw new ForbiddenAppException("This timesheet is approved. Correcting it needs the right to approve timesheets.");
        if (r.WorkDate < t.WeekStart || r.WorkDate > t.WeekStart.AddDays(6))
            throw ReservationService.Field(nameof(r.WorkDate), $"Choose a day in the week of {QuoteEngine.Day(t.WeekStart)} (Monday to Sunday).");
        var emp = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == t.EmployeeId, ct);
        if (!emp.WorksOn(r.WorkDate))
            throw ReservationService.Field(nameof(r.WorkDate), r.WorkDate < emp.JoinedOn
                ? $"{emp.FullName} joined on {QuoteEngine.Day(emp.JoinedOn)}, after {QuoteEngine.Day(r.WorkDate)}."
                : $"{emp.FullName} left on {QuoteEngine.Day(emp.LeftOn!.Value)}, before {QuoteEngine.Day(r.WorkDate)}.");
        if ((r.ClockIn is null) != (r.ClockOut is null))
            throw ReservationService.Field(r.ClockIn is null ? nameof(r.ClockIn) : nameof(r.ClockOut),
                "Give both the clock-in and the clock-out time, or leave both empty if the day wasn't worked.");

        var (shiftId, schedStart, schedEnd, schedMinutes) = await ScheduleAsync(t.EmployeeId, r.WorkDate, ct);
        DayResult result;
        if (r.ClockIn is { } cin && r.ClockOut is { } cout)
        {
            var minutes = RosterMath.Minutes(cin, cout);
            if (minutes > RosterMath.MaxShiftHours * 60)
                throw ReservationService.Field(nameof(r.ClockOut), $"{T(cin)}–{T(cout)} is {RosterMath.Hours(minutes)}. A day is at most {RosterMath.MaxShiftHours} hours.");
            if (r.BreakMinutes >= minutes)
                throw ReservationService.Field(nameof(r.BreakMinutes), $"The break must be shorter than the time worked ({RosterMath.Hours(minutes)}).");
            var (inAt, outAt) = RosterMath.Instants(r.WorkDate, cin, cout, tz);
            if (outAt > now.AddMinutes(1))
                throw ReservationService.Field(nameof(r.ClockOut), $"{T(cout)}{(cout <= cin ? " the next day" : "")} is later than now. Give a time that has passed.");
            result = TimesheetMath.FromTimes(inAt, outAt, r.BreakMinutes, schedStart, schedEnd, schedMinutes);
        }
        else
        {
            if (r.BreakMinutes != 0) throw ReservationService.Field(nameof(r.BreakMinutes), "A day that wasn't worked has no break. Set the break to 0.");
            result = new DayResult(null, null, 0, 0, 0, 0, 0, 0, TimesheetLineStatuses.Absent);
        }

        var line = await db.TimesheetLines.SingleOrDefaultAsync(l => l.TimesheetId == t.Id && l.WorkDate == r.WorkDate, ct);
        var isNew = line is null;
        if (line is null)
        {
            line = new TimesheetLine { OrganizationId = t.OrganizationId, TimesheetId = t.Id, WorkDate = r.WorkDate };
            db.TimesheetLines.Add(line);
        }
        db.TimesheetCorrections.Add(new TimesheetCorrection
        {
            OrganizationId = t.OrganizationId,
            TimesheetId = t.Id,
            WorkDate = r.WorkDate,
            TimesheetStatus = t.Status,
            OldClockInAt = line.ClockInAt,
            OldClockOutAt = line.ClockOutAt,
            OldBreakMinutes = isNew ? null : line.BreakMinutes,
            OldWorkedMinutes = isNew ? null : line.WorkedMinutes,
            NewClockInAt = result.In,
            NewClockOutAt = result.Out,
            NewBreakMinutes = result.BreakMinutes,
            NewWorkedMinutes = result.WorkedMinutes,
            Reason = r.Reason.Trim(),
            CreatedAt = now,
            CreatedBy = caller.RequireUserId(),
        });
        line.RosterShiftId = shiftId;
        line.ScheduledStart = schedStart;
        line.ScheduledEnd = schedEnd;
        line.ScheduledMinutes = schedMinutes;
        line.Apply(result);
        line.Corrected = true;
        await db.SaveChangesAsync(ct);
        await TotalsAsync(t, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await DtoAsync(property, t, ct);
    }

    // ------------------------------------------------------------------ used by clocking

    /// <summary>The timesheet a work date falls in, created as DRAFT if it isn't there yet, locked FOR UPDATE.</summary>
    internal async Task<Timesheet> ForDayAsync(Property property, Guid employeeId, DateOnly workDate, CancellationToken ct)
    {
        var from = RosterMath.WeekStart(workDate);
        await EnsureAsync(property, employeeId, from, ct);
        return (await LockSheetsAsync(db.Timesheets.Where(t => t.EmployeeId == employeeId && t.WeekStart == from), ct)).Single();
    }

    /// <summary>Works the open timesheet's lines out again from the events (DRAFT, REJECTED; SUBMITTED goes back to DRAFT first
    /// when <paramref name="reopen"/>), then its totals.</summary>
    internal async Task RecalculateAsync(Property property, Timesheet t, CancellationToken ct, bool reopen = false)
    {
        if (reopen && t.Status == TimesheetStatuses.Submitted)
        {
            t.Status = TimesheetStatuses.Draft;
            t.SubmittedAt = null;
            t.SubmittedBy = null;
        }
        if (!t.Open) return;
        var now = clock.GetUtcNow();
        var to = t.WeekStart.AddDays(6);
        var events = await db.AttendanceEvents.AsNoTracking()
            .Where(e => e.EmployeeId == t.EmployeeId && e.WorkDate >= t.WeekStart && e.WorkDate <= to).OrderBy(e => e.OccurredAt).ThenBy(e => e.CreatedAt)
            .ToListAsync(ct);
        var shifts = await db.RosterShifts.AsNoTracking()
            .Where(s => s.EmployeeId == t.EmployeeId && s.Status == RosterShiftStatuses.Scheduled && s.WorkDate >= t.WeekStart && s.WorkDate <= to)
            .ToListAsync(ct);
        var lines = await db.TimesheetLines.Where(l => l.TimesheetId == t.Id).ToListAsync(ct);

        for (var day = t.WeekStart; day <= to; day = day.AddDays(1))
        {
            var dayEvents = events.Where(e => e.WorkDate == day).ToList();
            var dayShifts = shifts.Where(s => s.WorkDate == day).OrderBy(s => s.StartsAt).ToList();
            var line = lines.SingleOrDefault(l => l.WorkDate == day);
            if (line?.Corrected == true) continue;
            if (dayEvents.Count == 0 && !dayShifts.Any(s => s.StartsAt <= now))
            {
                if (line is not null) db.TimesheetLines.Remove(line);
                continue;
            }
            var (shiftId, schedStart, schedEnd, schedMinutes) = Schedule(dayShifts);
            var result = TimesheetMath.Day(dayEvents.Select(e => new Punch(e.EventType, e.OccurredAt)), schedStart, schedEnd, schedMinutes, now);
            if (line is null)
            {
                line = new TimesheetLine { OrganizationId = t.OrganizationId, TimesheetId = t.Id, WorkDate = day };
                db.TimesheetLines.Add(line);
                lines.Add(line);
            }
            line.RosterShiftId = shiftId;
            line.ScheduledStart = schedStart;
            line.ScheduledEnd = schedEnd;
            line.ScheduledMinutes = schedMinutes;
            line.Overridden = dayEvents.Any(e => e.Source == AttendanceSources.Manager);
            line.Apply(result);
        }
        await db.SaveChangesAsync(ct);
        await TotalsAsync(t, ct);
    }

    // ------------------------------------------------------------------ helpers

    private async Task TotalsAsync(Timesheet t, CancellationToken ct)
    {
        var lines = await db.TimesheetLines.AsNoTracking().Where(l => l.TimesheetId == t.Id).ToListAsync(ct);
        t.WorkedMinutes = lines.Sum(l => l.WorkedMinutes);
        t.BreakMinutes = lines.Sum(l => l.BreakMinutes);
        t.OvertimeMinutes = lines.Sum(l => l.OvertimeMinutes);
        t.LateMinutes = lines.Sum(l => l.LateMinutes);
        t.EarlyMinutes = lines.Sum(l => l.EarlyMinutes);
        t.DaysWorked = (short)lines.Count(l => l.Status == TimesheetLineStatuses.Worked && l.WorkedMinutes > 0);
    }

    private async Task<(Guid? Id, DateTimeOffset? Start, DateTimeOffset? End, int? Minutes)> ScheduleAsync(Guid employeeId, DateOnly day, CancellationToken ct) =>
        Schedule(await db.RosterShifts.AsNoTracking()
            .Where(s => s.EmployeeId == employeeId && s.Status == RosterShiftStatuses.Scheduled && s.WorkDate == day).OrderBy(s => s.StartsAt).ToListAsync(ct));

    /// <summary>A day's rostered time: the first shift's start, the last one's end, and the paid minutes of all of them.</summary>
    private static (Guid? Id, DateTimeOffset? Start, DateTimeOffset? End, int? Minutes) Schedule(List<RosterShift> shifts) =>
        shifts.Count == 0
            ? (null, null, null, null)
            : (shifts[0].Id, shifts[0].StartsAt, shifts.Max(s => s.EndsAt), shifts.Sum(s => (int)(s.EndsAt - s.StartsAt).TotalMinutes - s.BreakMinutes));

    /// <summary>Why a timesheet can't be approved now, or null.</summary>
    private async Task<AppException?> ApproveRefusalAsync(Property property, Timesheet t, CancellationToken ct)
    {
        var emp = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == t.EmployeeId, ct);
        if (emp.UserId is { } u && u == caller.UserId)
            return new BusinessRuleAppException("OWN_TIMESHEET", "You can't approve your own timesheet. Another manager or HR approves it.");
        if (t.Status != TimesheetStatuses.Submitted)
            return new BusinessRuleAppException("INVALID_STATUS_TRANSITION", $"Only a submitted timesheet can be approved; this one is {Words(t.Status)}.");
        var open = await db.TimesheetLines.AsNoTracking()
            .Where(l => l.TimesheetId == t.Id && (l.Status == TimesheetLineStatuses.OnShift || l.Status == TimesheetLineStatuses.MissingOut))
            .OrderBy(l => l.WorkDate).FirstOrDefaultAsync(ct);
        if (open is not null)
            return new BusinessRuleAppException("TIMESHEET_OPEN_DAY",
                $"{Short(open.WorkDate)} has a clock-in at {T(Local(open.ClockInAt!.Value, Tz(property)))} with no clock-out. Correct that day first.");
        return null;
    }

    private async Task RequireNotOwnAsync(Timesheet t, string verb, CancellationToken ct)
    {
        var emp = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == t.EmployeeId, ct);
        if (emp.UserId is { } u && u == caller.UserId)
            throw new BusinessRuleAppException("OWN_TIMESHEET", $"You can't {verb} your own timesheet. Another manager or HR does it.");
    }

    private async Task EnsureAsync(Property property, Guid employeeId, DateOnly weekStart, CancellationToken ct)
    {
        var uid = caller.UserId;
        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO timesheets (id, organization_id, property_id, employee_id, week_start, created_by, updated_by)
             VALUES ({Guid.CreateVersion7()}, {property.OrganizationId}, {property.Id}, {employeeId}, {weekStart}, {uid}, {uid})
             ON CONFLICT (employee_id, week_start) DO NOTHING
             """, ct);
    }

    private async Task<List<Timesheet>> LockSheetsAsync(IQueryable<Timesheet> query, CancellationToken ct)
    {
        var ids = await query.Select(t => t.Id).OrderBy(id => id).ToListAsync(ct);
        if (ids.Count == 0) return [];
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM timesheets WHERE id = ANY({ids.ToArray()}) ORDER BY id FOR UPDATE", ct);
        return await db.Timesheets.Where(t => ids.Contains(t.Id)).ToListAsync(ct);
    }

    private async Task<Timesheet> LoadAsync(Guid propertyId, Guid id, CancellationToken ct) =>
        (await LockSheetsAsync(db.Timesheets.Where(t => t.Id == id && t.PropertyId == propertyId), ct)).SingleOrDefault()
        ?? throw new NotFoundAppException("That timesheet doesn't exist at this property. Reload the page.");

    internal async Task<Employee> MyEmployeeAsync(Guid propertyId, CancellationToken ct)
    {
        var uid = caller.RequireUserId();
        return await db.Employees.AsNoTracking().SingleOrDefaultAsync(e => e.UserId == uid && e.PropertyId == propertyId, ct)
               ?? throw new NotFoundAppException("Your account isn't linked to an employee record at this property. Ask HR to link it on your employee profile.");
    }

    internal async Task<IReadOnlyList<TimesheetSummaryDto>> SummariesAsync(Property property, IReadOnlyCollection<Timesheet> sheets, CancellationToken ct)
    {
        if (sheets.Count == 0) return [];
        var ids = sheets.Select(s => s.Id).ToList();
        var empIds = sheets.Select(s => s.EmployeeId).Distinct().ToList();
        var emps = await db.Employees.AsNoTracking().Where(e => empIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        var depts = await db.Departments.AsNoTracking().Where(d => d.PropertyId == property.Id).ToDictionaryAsync(d => d.Id, ct);
        var open = await db.TimesheetLines.AsNoTracking()
            .Where(l => ids.Contains(l.TimesheetId) && (l.Status == TimesheetLineStatuses.OnShift || l.Status == TimesheetLineStatuses.MissingOut))
            .GroupBy(l => l.TimesheetId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var corrections = await db.TimesheetCorrections.AsNoTracking().Where(c => ids.Contains(c.TimesheetId))
            .GroupBy(c => c.TimesheetId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var users = await UserNamesAsync(sheets.Select(s => s.DecidedBy), ct);
        var a = await access.GetAsync(ct);
        var order = depts.Values.OrderBy(d => d.SortOrder).ThenBy(d => d.Name).Select((d, i) => (d.Id, i)).ToDictionary(x => x.Id, x => x.i);

        return sheets
            .OrderBy(s => order.GetValueOrDefault(emps[s.EmployeeId].DepartmentId, 999)).ThenBy(s => emps[s.EmployeeId].FirstName).ThenBy(s => s.WeekStart)
            .Select(s =>
            {
                var e = emps[s.EmployeeId];
                var own = e.UserId is { } u && u == caller.UserId;
                var openDays = open.GetValueOrDefault(s.Id);
                var actions = new List<string>();
                if (own && s.Open && a.Has(Perm.TimesheetSelf, property.Id)) actions.Add("submit");
                if (!own && s.Status == TimesheetStatuses.Submitted && a.Has(Perm.TimesheetApprove, property.Id)) actions.AddRange(["approve", "reject"]);
                if (!own && s.Status == TimesheetStatuses.Approved && a.Has(Perm.TimesheetApprove, property.Id)) actions.Add("lock");
                if (!own && s.Status != TimesheetStatuses.Locked && a.Has(Perm.AttendanceUpdate, property.Id)
                    && (s.Status != TimesheetStatuses.Approved || a.Has(Perm.TimesheetApprove, property.Id))) actions.Add("correct");
                return new TimesheetSummaryDto(s.Id, e.Id, e.Code, e.FullName, depts[e.DepartmentId].Name, s.WeekStart, s.WeekStart.AddDays(6), s.Status,
                    s.WorkedMinutes, s.BreakMinutes, s.OvertimeMinutes, s.LateMinutes, s.EarlyMinutes, s.DaysWorked, openDays,
                    corrections.GetValueOrDefault(s.Id), s.SubmittedAt, s.DecidedAt, s.DecidedBy is { } d ? users.GetValueOrDefault(d) : null,
                    s.RejectionReason, s.LockedAt, actions, s.Version);
            }).ToList();
    }

    private async Task<TimesheetDto> DtoAsync(Property property, Timesheet t, CancellationToken ct)
    {
        var tz = Tz(property);
        var summary = (await SummariesAsync(property, [t], ct))[0];
        var lines = await db.TimesheetLines.AsNoTracking().Where(l => l.TimesheetId == t.Id).OrderBy(l => l.WorkDate).ToListAsync(ct);
        var to = t.WeekStart.AddDays(6);
        var events = await db.AttendanceEvents.AsNoTracking()
            .Where(e => e.EmployeeId == t.EmployeeId && e.WorkDate >= t.WeekStart && e.WorkDate <= to).OrderBy(e => e.OccurredAt).ThenBy(e => e.CreatedAt)
            .ToListAsync(ct);
        var shiftIds = lines.Select(l => l.RosterShiftId).OfType<Guid>().ToList();
        var shifts = await db.RosterShifts.AsNoTracking().Where(s => shiftIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var templateIds = shifts.Values.Select(s => s.TemplateId).OfType<Guid>().ToList();
        var templates = await db.ShiftTemplates.AsNoTracking().Where(x => templateIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var corrections = await db.TimesheetCorrections.AsNoTracking().Where(c => c.TimesheetId == t.Id).OrderBy(c => c.CreatedAt).ToListAsync(ct);
        var users = await UserNamesAsync(corrections.Select(c => c.CreatedBy).Append(t.SubmittedBy).Concat(events.Select(e => e.CreatedBy)), ct);
        var eventDtos = await AttendanceService.EventDtosAsync(db, events, users, tz, ct);

        TimeOnly? L(DateTimeOffset? x) => x is { } v ? Local(v, tz) : null;
        var lineDtos = lines.Select(l =>
        {
            var shift = l.RosterShiftId is { } sid ? shifts.GetValueOrDefault(sid) : null;
            var name = shift?.TemplateId is { } tid ? templates.GetValueOrDefault(tid) : shift is null ? null : "Shift";
            var nextDay = l.ClockOutAt is { } o && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(o, tz).DateTime) > l.WorkDate;
            return new TimesheetLineDto(l.WorkDate, name, L(l.ScheduledStart), L(l.ScheduledEnd), l.ScheduledMinutes, L(l.ClockInAt), L(l.ClockOutAt), nextDay,
                l.ClockInAt, l.ClockOutAt, l.BreakMinutes, l.WorkedMinutes, l.OvertimeMinutes, l.LateMinutes, l.EarlyMinutes, l.Status, l.Overridden,
                l.Corrected, eventDtos.Where(e => e.WorkDate == l.WorkDate).ToList());
        }).ToList();
        var correctionDtos = corrections.Select(c => new TimesheetCorrectionDto(c.Id, c.WorkDate, L(c.OldClockInAt), L(c.OldClockOutAt), c.OldBreakMinutes,
            c.OldWorkedMinutes, L(c.NewClockInAt), L(c.NewClockOutAt), c.NewBreakMinutes, c.NewWorkedMinutes, c.Reason, c.TimesheetStatus, c.CreatedAt,
            c.CreatedBy is { } b ? users.GetValueOrDefault(b) : null)).ToList();
        return new TimesheetDto(summary, t.Note, t.SubmittedBy is { } sb ? users.GetValueOrDefault(sb) : null, lineDtos, correctionDtos);
    }

    internal async Task<Dictionary<Guid, string>> UserNamesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.OfType<Guid>().Distinct().ToList();
        return list.Count == 0 ? [] : await db.Users.AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
    }

    private void Expect(Timesheet t, uint version)
    {
        if (t.Version != version)
            throw new ConflictAppException("CONCURRENCY_CONFLICT", "Someone else changed this timesheet a moment ago. Reload to see their change, then try again.");
        ConcurrencyGuard.ExpectVersion(db, t, version);
    }

    private static string Words(string status) => status switch
    {
        TimesheetStatuses.Draft => "still a draft",
        TimesheetStatuses.Submitted => "submitted",
        TimesheetStatuses.Approved => "approved",
        TimesheetStatuses.Rejected => "sent back to the employee",
        _ => "locked for payroll",
    };

    internal static TimeZoneInfo Tz(Property p) => TimeZoneInfo.FindSystemTimeZoneById(p.Timezone);
    internal static TimeOnly Local(DateTimeOffset instant, TimeZoneInfo tz) => TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, tz).DateTime);
    internal static string T(TimeOnly t) => t.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    internal static string Short(DateOnly d) => d.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture);
}
