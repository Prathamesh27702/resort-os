using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Reservations;
using Resort.Contracts;
using Resort.Domain.Employees;
using Resort.Domain.Organizations;
using Resort.Domain.Rates;

namespace Resort.Application.Employees;

/// <summary>
/// The shift roster (Sprint 17, brief §46).
/// <list type="bullet">
/// <item>Shift templates say what a department works (Kitchen · Breakfast 06:00–14:00) and how many people each needs every
/// day. A week's shortage view lists every template × day with fewer people than that.</item>
/// <item>A shift is one person on one local work date with exact start and end instants from the property's time zone
/// (a shift ending at or before it starts ends the next day). At most 16 hours.</item>
/// <item>One person can't be on two overlapping shifts: the service checks first and names the other shift (409
/// SHIFT_CLASH) under a lock on the employee's row, and an EXCLUDE constraint on roster_shifts is the final guard. A
/// manager can allow a deliberate overlap (a handover) with a reason; those are shown as warnings.</item>
/// <item>Warnings, never refusals: a shift on the person's weekly day off, and more than 48 hours in a week.</item>
/// <item>Every change is audited; a removed shift stays as CANCELLED with the reason.</item>
/// </list>
/// </summary>
public sealed class RosterService(IAppDbContext db, ICurrentUser caller, TimeProvider clock, EmployeeService employees)
{
    public const int WeekLimitMinutes = 48 * 60;

    // ------------------------------------------------------------------ shift templates

    public async Task<IReadOnlyList<ShiftTemplateDto>> TemplatesAsync(Guid propertyId, CancellationToken ct)
    {
        await employees.RequirePropertyAsync(propertyId, ct);
        return await TemplateDtosAsync(propertyId, ct);
    }

    public async Task<ShiftTemplateDto> SaveTemplateAsync(Guid propertyId, Guid? id, SaveShiftTemplateRequest r, CancellationToken ct)
    {
        await employees.RequirePropertyAsync(propertyId, ct);
        var code = r.Code.Trim().ToUpperInvariant();
        var dept = await db.Departments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == r.DepartmentId && d.PropertyId == propertyId, ct)
                   ?? throw ReservationService.Field(nameof(r.DepartmentId), "Choose the department that works this shift.");
        if (await db.ShiftTemplates.AnyAsync(t => t.PropertyId == propertyId && t.Code == code && t.Id != id, ct))
            throw ReservationService.Field(nameof(r.Code), $"Another shift already has the code {code}.");
        var minutes = RosterMath.Minutes(r.StartTime, r.EndTime);
        if (minutes > RosterMath.MaxShiftHours * 60)
            throw ReservationService.Field(nameof(r.EndTime),
                $"{T(r.StartTime)}–{T(r.EndTime)} is {RosterMath.Hours(minutes)} long. A shift is at most {RosterMath.MaxShiftHours} hours.");
        if (r.BreakMinutes >= minutes)
            throw ReservationService.Field(nameof(r.BreakMinutes), $"The break must be shorter than the shift ({RosterMath.Hours(minutes)}).");

        ShiftTemplate t;
        if (id is { } tid)
        {
            t = await db.ShiftTemplates.SingleOrDefaultAsync(x => x.Id == tid && x.PropertyId == propertyId, ct)
                ?? throw new NotFoundAppException("That shift doesn't exist at this property. Reload the page.");
            if (r.Version is { } v) Expect(t, v, $"the {t.Name} shift");
        }
        else
        {
            if (!dept.IsActive) throw ReservationService.Field(nameof(r.DepartmentId), $"{dept.Name} is switched off. Choose another department.");
            t = new ShiftTemplate { OrganizationId = caller.RequireOrganizationId(), PropertyId = propertyId };
            db.ShiftTemplates.Add(t);
        }
        // Rostered shifts keep the times they were given; a changed template applies to shifts added from now on.
        t.DepartmentId = dept.Id;
        t.Code = code;
        t.Name = r.Name.Trim();
        t.StartTime = r.StartTime;
        t.EndTime = r.EndTime;
        t.BreakMinutes = (short)r.BreakMinutes;
        t.MinStaff = (short)r.MinStaff;
        t.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return (await TemplateDtosAsync(propertyId, ct)).Single(x => x.Id == t.Id);
    }

    // ------------------------------------------------------------------ the week

    /// <summary>One week (Monday to Sunday) with its shortages and warnings. Week: any day in it (default: this week).</summary>
    public async Task<RosterWeekDto> WeekAsync(Guid propertyId, DateOnly? week, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var tz = Tz(property);
        var today = property.LocalDate(clock.GetUtcNow());
        var from = RosterMath.WeekStart(week ?? today);
        var to = from.AddDays(6);

        var shifts = await db.RosterShifts.AsNoTracking()
            .Where(s => s.PropertyId == propertyId && s.Status == RosterShiftStatuses.Scheduled && s.WorkDate >= from && s.WorkDate <= to)
            .ToListAsync(ct);
        var rosteredIds = shifts.Select(s => s.EmployeeId).Distinct().ToList();
        var emps = await db.Employees.AsNoTracking()
            .Where(e => e.PropertyId == propertyId && (rosteredIds.Contains(e.Id)
                        || (e.Status == EmployeeStatuses.Active && e.JoinedOn <= to) || (e.LeftOn != null && e.LeftOn >= from && e.JoinedOn <= to)))
            .ToListAsync(ct);
        var depts = await db.Departments.AsNoTracking().Where(d => d.PropertyId == propertyId).ToListAsync(ct);
        var desIds = emps.Select(e => e.DesignationId).OfType<Guid>().Distinct().ToList();
        var des = await db.Designations.AsNoTracking().Where(d => desIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var templates = await TemplateDtosAsync(propertyId, ct);

        var deptOrder = depts.OrderBy(d => d.SortOrder).ThenBy(d => d.Name).Select((d, i) => (d.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var shiftDtos = ShiftDtos(shifts, emps, depts, templates, tz)
            .OrderBy(s => s.WorkDate).ThenBy(s => s.StartsAt).ThenBy(s => s.EmployeeName).ToList();

        var empDtos = emps
            .OrderBy(e => deptOrder.GetValueOrDefault(e.DepartmentId, 999)).ThenBy(e => e.FirstName).ThenBy(e => e.LastName)
            .Select(e => new RosterEmployeeDto(e.Id, e.Code, e.FullName, e.DepartmentId, e.DesignationId is { } d ? des.GetValueOrDefault(d) : null,
                e.WeeklyOff, e.JoinedOn, e.LeftOn, shiftDtos.Where(s => s.EmployeeId == e.Id).Sum(s => s.PaidMinutes),
                shiftDtos.Count(s => s.EmployeeId == e.Id)))
            .ToList();

        var shortages = new List<RosterShortageDto>();
        for (var day = from; day <= to; day = day.AddDays(1))
            foreach (var t in templates.Where(t => t.IsActive && t.MinStaff > 0))
            {
                var rostered = shiftDtos.Count(s => s.TemplateId == t.Id && s.WorkDate == day);
                if (rostered < t.MinStaff)
                    shortages.Add(new RosterShortageDto(day, t.Id, t.Name, t.DepartmentId, t.DepartmentName, t.StartTime, t.EndTime, t.MinStaff, rostered,
                        t.MinStaff - rostered));
            }

        var warnings = new List<RosterWarningDto>();
        foreach (var s in shiftDtos)
        {
            var e = emps.Single(x => x.Id == s.EmployeeId);
            if (e.WeeklyOff is { } off && RosterMath.IsoDay(s.WorkDate) == off)
                warnings.Add(new RosterWarningDto("DAY_OFF", s.WorkDate, e.Id, s.Id,
                    $"{e.FullName} is on the roster on {RosterMath.DayName(off)} {Short(s.WorkDate)}, their weekly day off."));
            if (s.OverlapAllowed)
                warnings.Add(new RosterWarningDto("OVERLAP_ALLOWED", s.WorkDate, e.Id, s.Id,
                    $"{e.FullName} works two shifts at once on {Short(s.WorkDate)} ({T(s.StartTime)}–{T(s.EndTime)}): {s.OverlapReason}"));
        }
        foreach (var e in empDtos.Where(e => e.PaidMinutes > WeekLimitMinutes))
            warnings.Add(new RosterWarningDto("LONG_WEEK", null, e.Id, null,
                $"{e.Name} has {RosterMath.Hours(e.PaidMinutes)} this week, over the 48-hour limit for a week."));

        var shownDepts = depts.Where(d => d.IsActive || empDtos.Any(e => e.DepartmentId == d.Id) || shiftDtos.Any(s => s.DepartmentId == d.Id))
            .OrderBy(d => d.SortOrder).ThenBy(d => d.Name).Select(d => new RosterDepartmentDto(d.Id, d.Code, d.Name)).ToList();
        return new RosterWeekDto(from, to, today, shownDepts, empDtos, templates, shiftDtos, shortages,
            warnings.OrderBy(w => w.Date ?? DateOnly.MaxValue).ThenBy(w => w.Kind).ToList());
    }

    // ------------------------------------------------------------------ shifts

    /// <summary>Puts a person on a shift or changes one (roster.manage; audited). An overlap with another of their shifts is
    /// refused (409 SHIFT_CLASH naming it) unless AllowOverlap with a reason.</summary>
    public async Task<RosterShiftDto> SaveShiftAsync(Guid propertyId, Guid? id, SaveRosterShiftRequest r, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var tz = Tz(property);
        var today = property.LocalDate(clock.GetUtcNow());
        if (r.WorkDate < today.AddDays(-31))
            throw ReservationService.Field(nameof(r.WorkDate), $"{QuoteEngine.Day(r.WorkDate)} is more than a month ago. The roster can be changed back to {QuoteEngine.Day(today.AddDays(-31))}.");
        if (r.WorkDate > today.AddDays(180))
            throw ReservationService.Field(nameof(r.WorkDate), $"The roster goes at most 180 days ahead (until {QuoteEngine.Day(today.AddDays(180))}).");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        RosterShift? shift = null;
        if (id is { } sid)
        {
            shift = await db.RosterShifts.SingleOrDefaultAsync(s => s.Id == sid && s.PropertyId == propertyId, ct)
                    ?? throw new NotFoundAppException("That shift isn't on this property's roster. Reload the roster.");
            if (shift.Status != RosterShiftStatuses.Scheduled)
                throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION", "This shift was removed from the roster. Reload the roster.");
            if (r.Version is { } v) Expect(shift, v, "this shift");
        }
        // One person's shifts change one at a time: the clash check below can't miss a shift saved at the same moment.
        await LockEmployeesAsync([r.EmployeeId, .. shift is null ? Array.Empty<Guid>() : [shift.EmployeeId]], ct);

        var emp = await db.Employees.AsNoTracking().SingleOrDefaultAsync(e => e.Id == r.EmployeeId && e.PropertyId == propertyId, ct)
                  ?? throw ReservationService.Field(nameof(r.EmployeeId), "Choose an employee of this property.");
        if (!emp.WorksOn(r.WorkDate))
            throw ReservationService.Field(r.WorkDate < emp.JoinedOn ? nameof(r.WorkDate) : nameof(r.EmployeeId),
                r.WorkDate < emp.JoinedOn
                    ? $"{emp.FullName} joins on {QuoteEngine.Day(emp.JoinedOn)}, after {QuoteEngine.Day(r.WorkDate)}."
                    : $"{emp.FullName} left on {QuoteEngine.Day(emp.LeftOn!.Value)}, before {QuoteEngine.Day(r.WorkDate)}. Choose someone else.");

        ShiftTemplate? template = null;
        if (r.TemplateId is { } tid)
        {
            template = await db.ShiftTemplates.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tid && t.PropertyId == propertyId, ct)
                       ?? throw ReservationService.Field(nameof(r.TemplateId), "Choose a shift of this property.");
            if (!template.IsActive && shift?.TemplateId != template.Id)
                throw ReservationService.Field(nameof(r.TemplateId), $"The {template.Name} shift is switched off. Choose another or switch it on.");
        }
        var deptId = r.DepartmentId ?? template?.DepartmentId ?? emp.DepartmentId;
        var dept = await db.Departments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == deptId && d.PropertyId == propertyId, ct)
                   ?? throw ReservationService.Field(nameof(r.DepartmentId), "Choose a department of this property.");
        if (!dept.IsActive && shift?.DepartmentId != dept.Id)
            throw ReservationService.Field(nameof(r.DepartmentId), $"{dept.Name} is switched off. Choose another department.");
        var start = r.StartTime ?? template?.StartTime
                    ?? throw ReservationService.Field(nameof(r.StartTime), "Choose a shift, or give the start time.");
        var end = r.EndTime ?? template?.EndTime
                  ?? throw ReservationService.Field(nameof(r.EndTime), "Choose a shift, or give the end time.");
        var minutes = RosterMath.Minutes(start, end);
        if (minutes > RosterMath.MaxShiftHours * 60)
            throw ReservationService.Field(nameof(r.EndTime),
                $"{T(start)}–{T(end)} is {RosterMath.Hours(minutes)} long. A shift is at most {RosterMath.MaxShiftHours} hours; split it into two.");
        var breakMinutes = r.BreakMinutes ?? template?.BreakMinutes ?? 0;
        if (breakMinutes >= minutes)
            throw ReservationService.Field(nameof(r.BreakMinutes), $"The break must be shorter than the shift ({RosterMath.Hours(minutes)}).");
        var (startsAt, endsAt) = RosterMath.Instants(r.WorkDate, start, end, tz);

        var clash = await ClashAsync(emp.Id, startsAt, endsAt, shift?.Id, ct);
        if (clash is not null && !r.AllowOverlap)
            throw new ConflictAppException("SHIFT_CLASH", await ClashMessageAsync(emp.FullName, clash, tz, ct), new { shiftId = clash.Id });
        if (clash is not null && (r.OverlapReason is null || r.OverlapReason.Trim().Length < 3))
            throw ReservationService.Field(nameof(r.OverlapReason), $"Say why {emp.FirstName} works two shifts at once, e.g. a handover (3–200 characters).");

        if (shift is null)
        {
            shift = new RosterShift { OrganizationId = emp.OrganizationId, PropertyId = propertyId };
            db.RosterShifts.Add(shift);
        }
        shift.EmployeeId = emp.Id;
        shift.DepartmentId = dept.Id;
        shift.TemplateId = template?.Id;
        shift.WorkDate = r.WorkDate;
        shift.StartsAt = startsAt;
        shift.EndsAt = endsAt;
        shift.BreakMinutes = (short)breakMinutes;
        shift.OverlapAllowed = clash is not null;
        shift.OverlapReason = clash is not null ? r.OverlapReason!.Trim() : null;
        shift.Note = ReservationService.Clean(r.Note);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ShiftDtoAsync(propertyId, shift.Id, ct);
    }

    /// <summary>Takes a shift off the roster (roster.manage; audited). The row stays as CANCELLED with the reason.</summary>
    public async Task CancelShiftAsync(Guid propertyId, Guid id, CancelRosterShiftRequest r, CancellationToken ct)
    {
        await employees.RequirePropertyAsync(propertyId, ct);
        var shift = await db.RosterShifts.SingleOrDefaultAsync(s => s.Id == id && s.PropertyId == propertyId, ct)
                    ?? throw new NotFoundAppException("That shift isn't on this property's roster. Reload the roster.");
        if (shift.Status != RosterShiftStatuses.Scheduled)
            throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION", "This shift was already removed from the roster.");
        if (r.Version is { } v) Expect(shift, v, "this shift");
        shift.Status = RosterShiftStatuses.Cancelled;
        shift.CancelReason = r.Reason.Trim();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Copies one week's shifts into another, day for day (roster.manage; each new shift audited). Shifts that
    /// would clash, fall on a day the person doesn't work here, or use a switched-off shift or department are skipped and
    /// listed with the reason.</summary>
    public async Task<CopyRosterWeekResultDto> CopyWeekAsync(Guid propertyId, CopyRosterWeekRequest r, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var tz = Tz(property);
        var today = property.LocalDate(clock.GetUtcNow());
        var from = RosterMath.WeekStart(r.FromWeek);
        var to = RosterMath.WeekStart(r.ToWeek);
        if (from == to)
            throw ReservationService.Field(nameof(r.ToWeek), "Choose a different week to copy into: this is the week being copied.");
        if (to < RosterMath.WeekStart(today))
            throw ReservationService.Field(nameof(r.ToWeek), $"The week of {QuoteEngine.Day(to)} has passed. Copy into this week or a later one.");
        if (to > today.AddDays(180))
            throw ReservationService.Field(nameof(r.ToWeek), $"The roster goes at most 180 days ahead (until {QuoteEngine.Day(today.AddDays(180))}).");
        var shift = to.DayNumber - from.DayNumber;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var source = await db.RosterShifts.AsNoTracking()
            .Where(s => s.PropertyId == propertyId && s.Status == RosterShiftStatuses.Scheduled && s.WorkDate >= from && s.WorkDate <= from.AddDays(6))
            .ToListAsync(ct);
        if (source.Count == 0)
            throw ReservationService.Field(nameof(r.FromWeek), $"Nobody is on the roster in the week of {QuoteEngine.Day(from)}, so there is nothing to copy.");
        var empIds = source.Select(s => s.EmployeeId).Distinct().ToList();
        await LockEmployeesAsync(empIds, ct);
        var emps = await db.Employees.AsNoTracking().Where(e => empIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        var templates = await db.ShiftTemplates.AsNoTracking().Where(t => t.PropertyId == propertyId).ToDictionaryAsync(t => t.Id, ct);
        var depts = await db.Departments.AsNoTracking().Where(d => d.PropertyId == propertyId).ToDictionaryAsync(d => d.Id, ct);
        var existing = await db.RosterShifts.AsNoTracking()
            .Where(s => s.PropertyId == propertyId && s.Status == RosterShiftStatuses.Scheduled && empIds.Contains(s.EmployeeId)
                        && s.WorkDate >= to.AddDays(-1) && s.WorkDate <= to.AddDays(7))
            .ToListAsync(ct);

        var skipped = new List<string>();
        var copied = 0;
        foreach (var s in source.OrderBy(s => s.WorkDate).ThenBy(s => s.StartsAt))
        {
            var e = emps[s.EmployeeId];
            var day = s.WorkDate.AddDays(shift);
            var (st, en) = (Local(s.StartsAt, tz), Local(s.EndsAt, tz));
            var label = $"{e.FullName}, {Short(day)} {T(st)}–{T(en)}";
            if (!e.WorksOn(day))
            {
                skipped.Add($"{label}: {(day < e.JoinedOn ? $"joins on {Short(e.JoinedOn)}" : $"left on {Short(e.LeftOn!.Value)}")}.");
                continue;
            }
            if (s.TemplateId is { } tid && !templates[tid].IsActive)
            {
                skipped.Add($"{label}: the {templates[tid].Name} shift is switched off.");
                continue;
            }
            if (!depts[s.DepartmentId].IsActive)
            {
                skipped.Add($"{label}: {depts[s.DepartmentId].Name} is switched off.");
                continue;
            }
            var (startsAt, endsAt) = RosterMath.Instants(day, st, en, tz);
            if (existing.FirstOrDefault(x => x.EmployeeId == e.Id && x.Overlaps(startsAt, endsAt)) is { } other)
            {
                skipped.Add($"{label}: already on {T(Local(other.StartsAt, tz))}–{T(Local(other.EndsAt, tz))} that day.");
                continue;
            }
            var copy = new RosterShift
            {
                OrganizationId = s.OrganizationId,
                PropertyId = propertyId,
                EmployeeId = e.Id,
                DepartmentId = s.DepartmentId,
                TemplateId = s.TemplateId,
                WorkDate = day,
                StartsAt = startsAt,
                EndsAt = endsAt,
                BreakMinutes = s.BreakMinutes,
                Note = s.Note,
            };
            db.RosterShifts.Add(copy);
            existing.Add(copy);
            copied++;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new CopyRosterWeekResultDto(copied, skipped, await WeekAsync(propertyId, to, ct));
    }

    /// <summary>The caller's own shifts (roster.self) from their linked employee record: from the start of this week, four
    /// weeks (or from a given day).</summary>
    public async Task<MyRosterDto> MineAsync(Guid propertyId, DateOnly? from, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var tz = Tz(property);
        var uid = caller.RequireUserId();
        var emp = await db.Employees.AsNoTracking().SingleOrDefaultAsync(e => e.UserId == uid && e.PropertyId == propertyId, ct)
                  ?? throw new NotFoundAppException("Your account isn't linked to an employee record at this property. Ask HR to link it on your employee profile.");
        var start = from ?? RosterMath.WeekStart(property.LocalDate(clock.GetUtcNow()));
        var shifts = await db.RosterShifts.AsNoTracking()
            .Where(s => s.EmployeeId == emp.Id && s.Status == RosterShiftStatuses.Scheduled && s.WorkDate >= start && s.WorkDate < start.AddDays(28))
            .ToListAsync(ct);
        var depts = await db.Departments.AsNoTracking().Where(d => d.PropertyId == propertyId).ToListAsync(ct);
        var templates = await TemplateDtosAsync(propertyId, ct);
        return new MyRosterDto(emp.Id, emp.FullName, depts.Single(d => d.Id == emp.DepartmentId).Name,
            ShiftDtos(shifts, [emp], depts, templates, tz).OrderBy(s => s.StartsAt).ToList());
    }

    /// <summary>An employee's scheduled shifts from a day (for the profile page).</summary>
    public async Task<IReadOnlyList<RosterShiftDto>> UpcomingAsync(Guid propertyId, Guid employeeId, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var emp = await employees.LoadAsync(propertyId, employeeId, ct);
        var today = property.LocalDate(clock.GetUtcNow());
        var shifts = await db.RosterShifts.AsNoTracking()
            .Where(s => s.EmployeeId == emp.Id && s.Status == RosterShiftStatuses.Scheduled && s.WorkDate >= today).OrderBy(s => s.StartsAt).Take(30)
            .ToListAsync(ct);
        var depts = await db.Departments.AsNoTracking().Where(d => d.PropertyId == propertyId).ToListAsync(ct);
        return ShiftDtos(shifts, [emp], depts, await TemplateDtosAsync(propertyId, ct), Tz(property)).OrderBy(s => s.StartsAt).ToList();
    }

    // ------------------------------------------------------------------ helpers

    private async Task LockEmployeesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().Order().ToArray();
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM employees WHERE id = ANY({list}) ORDER BY id FOR UPDATE", ct);
    }

    private Task<RosterShift?> ClashAsync(Guid employeeId, DateTimeOffset start, DateTimeOffset end, Guid? exceptId, CancellationToken ct) =>
        db.RosterShifts.AsNoTracking()
            .Where(s => s.EmployeeId == employeeId && s.Status == RosterShiftStatuses.Scheduled && s.Id != exceptId && s.StartsAt < end && start < s.EndsAt)
            .OrderBy(s => s.StartsAt).FirstOrDefaultAsync(ct);

    private async Task<string> ClashMessageAsync(string name, RosterShift other, TimeZoneInfo tz, CancellationToken ct)
    {
        var dept = await db.Departments.AsNoTracking().Where(d => d.Id == other.DepartmentId).Select(d => d.Name).SingleAsync(ct);
        var tpl = other.TemplateId is { } t ? await db.ShiftTemplates.AsNoTracking().Where(x => x.Id == t).Select(x => x.Name).SingleAsync(ct) : null;
        return $"{name} is already on {dept}{(tpl is null ? "" : " · " + tpl)} {T(Local(other.StartsAt, tz))}–{T(Local(other.EndsAt, tz))} on " +
               $"{QuoteEngine.Day(other.WorkDate)}. One person can't work two shifts at once: choose another time or another person, or allow the overlap " +
               "with a reason (e.g. a handover).";
    }

    private async Task<RosterShiftDto> ShiftDtoAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var s = await db.RosterShifts.AsNoTracking().SingleAsync(x => x.Id == id, ct);
        var emp = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == s.EmployeeId, ct);
        var depts = await db.Departments.AsNoTracking().Where(d => d.PropertyId == propertyId).ToListAsync(ct);
        return ShiftDtos([s], [emp], depts, await TemplateDtosAsync(propertyId, ct), Tz(property))[0];
    }

    private static List<RosterShiftDto> ShiftDtos(IEnumerable<RosterShift> shifts, IReadOnlyCollection<Employee> emps, IReadOnlyCollection<Department> depts,
        IReadOnlyCollection<ShiftTemplateDto> templates, TimeZoneInfo tz) =>
        shifts.Select(s =>
        {
            var e = emps.Single(x => x.Id == s.EmployeeId);
            var paid = (int)(s.EndsAt - s.StartsAt).TotalMinutes - s.BreakMinutes;
            return new RosterShiftDto(s.Id, e.Id, e.Code, e.FullName, s.DepartmentId, depts.Single(d => d.Id == s.DepartmentId).Name, s.TemplateId,
                s.TemplateId is { } t ? templates.Single(x => x.Id == t).Name : null, s.WorkDate, Local(s.StartsAt, tz), Local(s.EndsAt, tz), s.StartsAt,
                s.EndsAt, s.BreakMinutes, paid, s.OverlapAllowed, s.OverlapReason, s.Note, s.Version);
        }).ToList();

    private async Task<List<ShiftTemplateDto>> TemplateDtosAsync(Guid propertyId, CancellationToken ct)
    {
        var depts = await db.Departments.AsNoTracking().Where(d => d.PropertyId == propertyId).ToListAsync(ct);
        var templates = await db.ShiftTemplates.AsNoTracking().Where(t => t.PropertyId == propertyId).ToListAsync(ct);
        var order = depts.OrderBy(d => d.SortOrder).ThenBy(d => d.Name).Select((d, i) => (d.Id, i)).ToDictionary(x => x.Id, x => x.i);
        return templates.OrderBy(t => order.GetValueOrDefault(t.DepartmentId)).ThenBy(t => t.StartTime).ThenBy(t => t.Name)
            .Select(t =>
            {
                var minutes = RosterMath.Minutes(t.StartTime, t.EndTime);
                return new ShiftTemplateDto(t.Id, t.DepartmentId, depts.Single(d => d.Id == t.DepartmentId).Name, t.Code, t.Name, t.StartTime, t.EndTime,
                    t.EndTime <= t.StartTime, t.BreakMinutes, minutes - t.BreakMinutes, t.MinStaff, t.IsActive, t.Version);
            }).ToList();
    }

    private void Expect<T>(T entity, uint version, string what) where T : class, Domain.Common.IVersioned
    {
        if (entity.Version != version)
            throw new ConflictAppException("CONCURRENCY_CONFLICT", $"Someone else changed {what} a moment ago. Reload to see their change, then make yours again.");
        ConcurrencyGuard.ExpectVersion(db, entity, version);
    }

    private static TimeZoneInfo Tz(Property p) => TimeZoneInfo.FindSystemTimeZoneById(p.Timezone);
    private static TimeOnly Local(DateTimeOffset instant, TimeZoneInfo tz) => TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, tz).DateTime);
    private static string T(TimeOnly t) => t.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    private static string Short(DateOnly d) => d.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture);
}
