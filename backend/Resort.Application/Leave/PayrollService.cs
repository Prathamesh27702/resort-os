using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Employees;
using Resort.Contracts;
using Resort.Domain.Attendance;
using Resort.Domain.Employees;
using Resort.Domain.Leave;
using Resort.Domain.Organizations;

namespace Resort.Application.Leave;

/// <summary>
/// The monthly payroll export (Sprint 19, brief §45). One row per employee who worked here during the month: days employed,
/// weekly offs, holidays, days and hours worked, overtime, paid and unpaid leave, days rostered but not worked, and the
/// payable days (<see cref="PayrollCsv"/>). Hours come from approved and locked timesheets only; making the file locks
/// the approved ones, so they can't change after payroll used them. What isn't ready (a week not approved, leave not
/// decided, a month still running) is listed first; the file is only made with those once someone says so. Each file is
/// kept as it was made, with its SHA-256. No pay amounts: the payroll provider holds the salaries.
/// </summary>
public sealed class PayrollService(IAppDbContext db, ICurrentUser caller, TimeProvider clock, EmployeeService employees, LeaveService leave)
{
    public async Task<PayrollPreviewDto> PreviewAsync(Guid propertyId, string month, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var (start, end) = Period(property, month, "Month");
        var (rows, warnings, toLock) = await BuildAsync(property, start, end, ct);
        var holidays = await db.Holidays.AsNoTracking().Where(h => h.PropertyId == propertyId && h.IsActive && h.HolidayDate >= start && h.HolidayDate <= end)
            .OrderBy(h => h.HolidayDate).Select(h => new HolidayDto(h.Id, h.HolidayDate, h.Name, h.IsActive, h.Version)).ToListAsync(ct);
        return new PayrollPreviewDto(Label(start), start, end, rows.Select(Dto).ToList(), warnings, toLock.Count, holidays);
    }

    public async Task<PayrollExportDto> CreateAsync(Guid propertyId, CreatePayrollExportRequest r, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var (start, end) = Period(property, r.Month, nameof(r.Month));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({"payroll:" + propertyId}))", ct);
        var (rows, warnings, toLock) = await BuildAsync(property, start, end, ct);
        if (warnings.Count > 0 && !r.ConfirmWarnings)
            throw new BusinessRuleAppException("PAYROLL_NOT_READY",
                $"{warnings.Count} {(warnings.Count == 1 ? "thing isn't" : "things aren't")} ready for {LeaveService.MonthName(start.Year, start.Month)}: " +
                $"{string.Join(" ", warnings.Take(3))}{(warnings.Count > 3 ? $" (and {warnings.Count - 3} more)" : "")} " +
                "Sort them out first, or confirm to make the file without them.", new { warnings });

        // The approved weeks it used are locked: payroll has them now.
        if (toLock.Count > 0)
        {
            var sheets = await db.Timesheets.Where(t => toLock.Contains(t.Id) && t.Status == TimesheetStatuses.Approved).ToListAsync(ct);
            foreach (var t in sheets)
            {
                t.Status = TimesheetStatuses.Locked;
                t.LockedAt = clock.GetUtcNow();
                t.LockedBy = caller.RequireUserId();
            }
        }
        var csv = PayrollCsv.Write(rows);
        var bytes = Encoding.UTF8.GetBytes(csv);
        var now = clock.GetUtcNow();
        var export = new PayrollExport
        {
            OrganizationId = property.OrganizationId,
            PropertyId = propertyId,
            PeriodStart = start,
            PeriodEnd = end,
            FileName = $"payroll-{property.Code.ToLowerInvariant()}-{Label(start)}-{TimeZoneInfo.ConvertTime(now, Tz(property)):yyyyMMdd-HHmm}.csv",
            Content = bytes,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            Employees = rows.Count,
            TimesheetsLocked = toLock.Count,
            Warnings = [.. warnings],
            CreatedAt = now,
            CreatedBy = caller.RequireUserId(),
        };
        db.PayrollExports.Add(export);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await DtosAsync([export], ct))[0];
    }

    public async Task<IReadOnlyList<PayrollExportDto>> ListAsync(Guid propertyId, CancellationToken ct)
    {
        await employees.RequirePropertyAsync(propertyId, ct);
        var list = await db.PayrollExports.AsNoTracking().Where(x => x.PropertyId == propertyId).OrderByDescending(x => x.CreatedAt).Take(60)
            .Select(x => new PayrollExport
            {
                Id = x.Id,
                PeriodStart = x.PeriodStart,
                PeriodEnd = x.PeriodEnd,
                Format = x.Format,
                FileName = x.FileName,
                Sha256 = x.Sha256,
                Employees = x.Employees,
                TimesheetsLocked = x.TimesheetsLocked,
                Warnings = x.Warnings,
                CreatedAt = x.CreatedAt,
                CreatedBy = x.CreatedBy,
            })
            .ToListAsync(ct);
        return await DtosAsync(list, ct);
    }

    public async Task<(byte[] Content, string FileName)> FileAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        await employees.RequirePropertyAsync(propertyId, ct);
        var x = await db.PayrollExports.AsNoTracking().Where(e => e.Id == id && e.PropertyId == propertyId)
                    .Select(e => new { e.Content, e.FileName }).SingleOrDefaultAsync(ct)
                ?? throw new NotFoundAppException("That payroll file doesn't exist at this property. Reload the page.");
        return (x.Content, x.FileName);
    }

    // ------------------------------------------------------------------ the month

    private async Task<(List<PayrollRow> Rows, List<string> Warnings, List<Guid> ToLock)> BuildAsync(Property property, DateOnly start, DateOnly end,
        CancellationToken ct)
    {
        var today = property.LocalDate(clock.GetUtcNow());
        var now = clock.GetUtcNow();
        var emps = await db.Employees.AsNoTracking()
            .Where(e => e.PropertyId == property.Id && e.JoinedOn <= end && (e.LeftOn == null || e.LeftOn >= start)).ToListAsync(ct);
        var ids = emps.Select(e => e.Id).ToList();
        var depts = await db.Departments.AsNoTracking().Where(d => d.PropertyId == property.Id).ToListAsync(ct);
        var holidays = await leave.HolidayMapAsync(property.Id, start, end, ct);
        var types = await db.LeaveTypes.AsNoTracking().Where(t => t.PropertyId == property.Id).ToDictionaryAsync(t => t.Id, ct);
        var requests = await db.LeaveRequests.AsNoTracking()
            .Where(x => ids.Contains(x.EmployeeId) && (x.Status == LeaveRequestStatuses.Approved || x.Status == LeaveRequestStatuses.Pending)
                        && x.FromDate <= end && x.ToDate >= start)
            .ToListAsync(ct);

        var weekFrom = RosterMath.WeekStart(start);
        var sheets = await db.Timesheets.AsNoTracking()
            .Where(t => t.PropertyId == property.Id && t.WeekStart >= weekFrom && t.WeekStart <= end).ToListAsync(ct);
        var sheetIds = sheets.Select(t => t.Id).ToList();
        var lines = await db.TimesheetLines.AsNoTracking()
            .Where(l => sheetIds.Contains(l.TimesheetId) && l.WorkDate >= start && l.WorkDate <= end).ToListAsync(ct);
        var counted = sheets.Where(t => t.Status is TimesheetStatuses.Approved or TimesheetStatuses.Locked).Select(t => t.Id).ToHashSet();
        var linesBySheet = lines.GroupBy(l => l.TimesheetId).ToDictionary(g => g.Key, g => g.ToList());

        var warnings = new List<string>();
        var names = emps.ToDictionary(e => e.Id, e => e.FullName);
        if (end >= today)
            warnings.Add($"{LeaveService.MonthName(start.Year, start.Month)} hasn't ended: the days from {LeaveRequestService.Short(today)} count as employed, with no hours yet.");
        foreach (var t in sheets.Where(t => !counted.Contains(t.Id) && linesBySheet.ContainsKey(t.Id) && names.ContainsKey(t.EmployeeId))
                     .OrderBy(t => t.WeekStart).ThenBy(t => names[t.EmployeeId]))
            warnings.Add($"{names[t.EmployeeId]}, week of {LeaveRequestService.Short(t.WeekStart)}: the timesheet is {SheetWords(t.Status)}, so its hours are left out.");

        // Weeks with clock-ins or started shifts that nobody has opened as a timesheet yet.
        var withSheet = sheets.Select(t => (t.EmployeeId, t.WeekStart)).ToHashSet();
        var eventDays = await db.AttendanceEvents.AsNoTracking()
            .Where(e => e.PropertyId == property.Id && e.WorkDate >= start && e.WorkDate <= end).Select(e => new { e.EmployeeId, e.WorkDate }).Distinct()
            .ToListAsync(ct);
        var shiftDays = await db.RosterShifts.AsNoTracking()
            .Where(s => s.PropertyId == property.Id && s.Status == RosterShiftStatuses.Scheduled && s.WorkDate >= start && s.WorkDate <= end && s.StartsAt <= now)
            .Select(s => new { s.EmployeeId, s.WorkDate }).Distinct().ToListAsync(ct);
        foreach (var w in eventDays.Concat(shiftDays).Select(x => (x.EmployeeId, Week: RosterMath.WeekStart(x.WorkDate))).Distinct()
                     .Where(x => !withSheet.Contains((x.EmployeeId, x.Week)) && names.ContainsKey(x.EmployeeId)).OrderBy(x => x.Week).ThenBy(x => names[x.EmployeeId]))
            warnings.Add($"{names[w.EmployeeId]}, week of {LeaveRequestService.Short(w.Week)}: no timesheet yet (open Timesheets for that week), so its hours are left out.");

        foreach (var q in requests.Where(x => x.Status == LeaveRequestStatuses.Pending).OrderBy(x => x.FromDate))
            warnings.Add($"{names[q.EmployeeId]}: {types[q.LeaveTypeId].Name} {q.Code} on {LeaveRequestService.Range(q.FromDate, q.ToDate)} isn't decided yet, so it is left out.");

        var deptOrder = depts.OrderBy(d => d.SortOrder).ThenBy(d => d.Name).Select((d, i) => (d.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var rows = new List<PayrollRow>();
        foreach (var e in emps.OrderBy(e => deptOrder.GetValueOrDefault(e.DepartmentId, 999)).ThenBy(e => e.Code))
        {
            var from = e.JoinedOn > start ? e.JoinedOn : start;
            var to = e.LeftOn is { } l && l < end ? l : end;
            int employed = 0, offs = 0, hol = 0;
            for (var d = from; d <= to; d = d.AddDays(1))
            {
                employed++;
                if (holidays.ContainsKey(d)) hol++;
                else if (e.WeeklyOff is { } off && LeaveMath.IsoDay(d) == off) offs++;
            }
            decimal paid = 0, unpaid = 0;
            var detail = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var q in requests.Where(x => x.EmployeeId == e.Id && x.Status == LeaveRequestStatuses.Approved))
            {
                var inMonth = q.CountedDates.Where(d => d >= from && d <= to).Sum(q.DayFraction);
                if (inMonth == 0) continue;
                var t = types[q.LeaveTypeId];
                if (t.IsPaid) paid += inMonth; else unpaid += inMonth;
                detail[t.Code] = detail.GetValueOrDefault(t.Code) + inMonth;
            }
            var mine = sheets.Where(t => t.EmployeeId == e.Id && counted.Contains(t.Id))
                .SelectMany(t => linesBySheet.GetValueOrDefault(t.Id) ?? []).Where(x => x.WorkDate >= from && x.WorkDate <= to).ToList();
            var worked = mine.Count(x => x.Status == TimesheetLineStatuses.Worked && x.WorkedMinutes > 0);
            var absent = mine.Count(x => x.Status == TimesheetLineStatuses.Absent);
            rows.Add(new PayrollRow(e.Code, e.FullName, depts.Single(d => d.Id == e.DepartmentId).Name, e.EmploymentType, start, end, employed, offs, hol,
                worked, mine.Sum(x => x.WorkedMinutes), mine.Sum(x => x.OvertimeMinutes), mine.Sum(x => x.LateMinutes), paid, unpaid, absent,
                PayrollCsv.Payable(employed, unpaid, absent), string.Join("; ", detail.Select(kv => $"{kv.Key} {LeaveMath.D(kv.Value)}"))));
        }
        var toLock = sheets.Where(t => t.Status == TimesheetStatuses.Approved && linesBySheet.ContainsKey(t.Id)).Select(t => t.Id).ToList();
        return (rows, warnings, toLock);
    }

    private (DateOnly Start, DateOnly End) Period(Property property, string month, string field)
    {
        var (y, m) = LeaveService.ParseMonth(month, field);
        var start = new DateOnly(y, m, 1);
        var today = property.LocalDate(clock.GetUtcNow());
        if (start > today)
            throw Reservations.ReservationService.Field(field, $"{LeaveService.MonthName(y, m)} hasn't started yet. Choose this month or an earlier one.");
        if (start < new DateOnly(today.Year - 2, today.Month, 1))
            throw Reservations.ReservationService.Field(field, "Payroll files go back two years. Choose a later month.");
        return (start, start.AddMonths(1).AddDays(-1));
    }

    private async Task<List<PayrollExportDto>> DtosAsync(IReadOnlyList<PayrollExport> list, CancellationToken ct)
    {
        var users = await LeaveService.UserNamesAsync(db, list.Select(x => x.CreatedBy), ct);
        return list.Select(x => new PayrollExportDto(x.Id, Label(x.PeriodStart), x.PeriodStart, x.PeriodEnd, x.Format, x.FileName, x.Sha256, x.Employees,
            x.TimesheetsLocked, x.Warnings, x.CreatedAt, x.CreatedBy is { } b ? users.GetValueOrDefault(b) : null)).ToList();
    }

    private static PayrollRowDto Dto(PayrollRow r) => new(r.EmployeeCode, r.EmployeeName, r.Department, r.EmploymentType, r.EmployedDays, r.WeeklyOffs,
        r.Holidays, r.DaysWorked, r.WorkedMinutes, r.OvertimeMinutes, r.LateMinutes, r.PaidLeaveDays, r.UnpaidLeaveDays, r.AbsentDays, r.PayableDays,
        r.LeaveDetail);

    private static string SheetWords(string status) => status switch
    {
        TimesheetStatuses.Draft => "still a draft",
        TimesheetStatuses.Submitted => "waiting for approval",
        _ => "sent back to the employee",
    };

    private static string Label(DateOnly d) => d.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    private static TimeZoneInfo Tz(Property p) => TimeZoneInfo.FindSystemTimeZoneById(p.Timezone);
}
