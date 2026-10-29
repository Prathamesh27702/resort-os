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
/// Clocking in and out (Sprint 18, brief §43).
/// <list type="bullet">
/// <item>An employee clocks in, takes breaks and clocks out from their phone, through the staff account linked to their
/// employee record. Events are never changed: a mistake is fixed by a manager entry or a timesheet correction.</item>
/// <item>The geofence is checked here, never on the phone: the phone sends its coordinates and accuracy; clocking in or
/// out counts only within a geofence's radius (plus the phone's accuracy, up to 50 m). Breaks are not checked.</item>
/// <item>A manager can record an event for someone (attendance.update) with a reason, e.g. a phone without signal or a
/// forgotten clock-out; never for themselves. It must come after the person's latest event.</item>
/// <item>A clock-in belongs to the rostered shift starting within 3 hours of it (or still running), else to the local
/// date; the rest of the session follows it. Each event updates the week's timesheet; a submitted one goes back to
/// DRAFT, an approved or locked one refuses new events (correct the timesheet instead).</item>
/// </list>
/// </summary>
public sealed class AttendanceService(IAppDbContext db, ICurrentUser caller, TimeProvider clock, EmployeeService employees, RosterService roster,
    TimesheetService timesheets)
{
    /// <summary>The phone's accuracy counts in the person's favour up to this many metres.</summary>
    public const int AccuracyAllowanceM = 50;

    /// <summary>A position less accurate than this can't show where the phone is.</summary>
    public const int WorstAccuracyM = 500;

    /// <summary>A clock-in belongs to a shift that starts at most this long after it (or has already started).</summary>
    public const int ShiftWindowHours = 3;

    // ------------------------------------------------------------------ geofences

    public async Task<IReadOnlyList<GeofenceDto>> GeofencesAsync(Guid propertyId, CancellationToken ct)
    {
        await employees.RequirePropertyAsync(propertyId, ct);
        return (await db.Geofences.AsNoTracking().Where(g => g.PropertyId == propertyId).OrderBy(g => g.Name).ToListAsync(ct)).Select(Dto).ToList();
    }

    public async Task<GeofenceDto> SaveGeofenceAsync(Guid propertyId, Guid? id, SaveGeofenceRequest r, CancellationToken ct)
    {
        await employees.RequirePropertyAsync(propertyId, ct);
        var name = r.Name.Trim();
        var names = await db.Geofences.AsNoTracking().Where(g => g.PropertyId == propertyId && g.Id != id).Select(g => g.Name).ToListAsync(ct);
        if (names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            throw ReservationService.Field(nameof(r.Name), $"There is already an area called {name}.");
        Geofence g;
        if (id is { } gid)
        {
            g = await db.Geofences.SingleOrDefaultAsync(x => x.Id == gid && x.PropertyId == propertyId, ct)
                ?? throw new NotFoundAppException("That area doesn't exist at this property. Reload the page.");
            if (r.Version is { } v)
            {
                if (g.Version != v)
                    throw new ConflictAppException("CONCURRENCY_CONFLICT", $"Someone else changed {g.Name} a moment ago. Reload to see their change, then make yours again.");
                ConcurrencyGuard.ExpectVersion(db, g, v);
            }
        }
        else
        {
            g = new Geofence { OrganizationId = caller.RequireOrganizationId(), PropertyId = propertyId };
            db.Geofences.Add(g);
        }
        g.Name = name;
        g.Latitude = Math.Round(r.Latitude, 6);
        g.Longitude = Math.Round(r.Longitude, 6);
        g.RadiusM = r.RadiusM;
        g.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return Dto(g);
    }

    // ------------------------------------------------------------------ the employee

    /// <summary>The caller's attendance now (attendance.self): clocked in or not, today's shift, the day's events and hours,
    /// and this week's timesheet.</summary>
    public async Task<MyAttendanceDto> MeAsync(Guid propertyId, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var emp = await timesheets.MyEmployeeAsync(propertyId, ct);
        return await MeAsync(property, emp, ct);
    }

    /// <summary>The caller clocks in, starts or ends a break, or clocks out (attendance.self).</summary>
    public async Task<MyAttendanceDto> ClockAsync(Guid propertyId, ClockRequest r, string? device, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var tz = TimesheetService.Tz(property);
        var now = clock.GetUtcNow();
        var today = property.LocalDate(now);
        var action = r.Action.Trim().ToUpperInvariant();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var emp = await timesheets.MyEmployeeAsync(propertyId, ct);
        // One person's events are written one at a time: a double tap can't clock in twice.
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM employees WHERE id = {emp.Id} FOR UPDATE", ct);
        if (!emp.WorksOn(today))
            throw new BusinessRuleAppException("NOT_EMPLOYED", today < emp.JoinedOn
                ? $"Your employee record starts on {QuoteEngine.Day(emp.JoinedOn)}. You can clock in from that day."
                : $"Your employee record shows you left on {QuoteEngine.Day(emp.LeftOn!.Value)}. Ask HR if that is wrong.");

        var recent = await RecentAsync(emp.Id, now, ct);
        var state = StateOf(recent, now, staleCountsAsOpen: false);
        RefuseTransition(action, state, tz, you: true, emp.FirstName);

        var ev = new AttendanceEvent
        {
            OrganizationId = emp.OrganizationId,
            PropertyId = propertyId,
            EmployeeId = emp.Id,
            EventType = action,
            OccurredAt = now,
            Source = AttendanceSources.Device,
            Device = device is null ? null : device.Length > 200 ? device[..200] : device,
            CreatedAt = now,
            CreatedBy = caller.RequireUserId(),
        };
        await LocateAsync(property, ev, r, checkInside: action is AttendanceEventTypes.ClockIn or AttendanceEventTypes.ClockOut, ct);
        await PlaceAsync(property, emp, ev, state, tz, ct);
        db.AttendanceEvents.Add(ev);
        await db.SaveChangesAsync(ct);

        var sheet = await timesheets.ForDayAsync(property, emp.Id, ev.WorkDate, ct);
        if (sheet.Status is TimesheetStatuses.Approved or TimesheetStatuses.Locked)
            throw new ConflictAppException("TIMESHEET_CLOSED",
                $"Your timesheet for the week of {QuoteEngine.Day(sheet.WeekStart)} is already {(sheet.Status == TimesheetStatuses.Locked ? "locked for payroll" : "approved")}. " +
                "Ask your manager to add this to it.");
        await timesheets.RecalculateAsync(property, sheet, ct, reopen: true);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await MeAsync(property, emp, ct);
    }

    // ------------------------------------------------------------------ managers

    /// <summary>The day's attendance (attendance.read): everyone on the roster that day or who clocked in, with their state.</summary>
    public async Task<AttendanceDayDto> DayAsync(Guid propertyId, DateOnly? date, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var tz = TimesheetService.Tz(property);
        var now = clock.GetUtcNow();
        var today = property.LocalDate(now);
        var day = date ?? today;

        var shifts = await db.RosterShifts.AsNoTracking()
            .Where(s => s.PropertyId == propertyId && s.Status == RosterShiftStatuses.Scheduled && s.WorkDate == day).ToListAsync(ct);
        var events = await db.AttendanceEvents.AsNoTracking().Where(e => e.PropertyId == propertyId && e.WorkDate == day)
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.CreatedAt).ToListAsync(ct);
        var empIds = shifts.Select(s => s.EmployeeId).Union(events.Select(e => e.EmployeeId)).ToList();
        var emps = await db.Employees.AsNoTracking().Where(e => empIds.Contains(e.Id)).ToListAsync(ct);
        var depts = await db.Departments.AsNoTracking().Where(d => d.PropertyId == propertyId).ToListAsync(ct);
        var templates = await roster.TemplateDtosAsync(propertyId, ct);
        var shiftDtos = RosterService.ShiftDtos(shifts, emps, depts, templates, tz);
        var users = await timesheets.UserNamesAsync(events.Select(e => e.CreatedBy), ct);
        var eventDtos = await EventDtosAsync(db, events, users, tz, ct);
        var order = depts.OrderBy(d => d.SortOrder).ThenBy(d => d.Name).Select((d, i) => (d.Id, i)).ToDictionary(x => x.Id, x => x.i);

        var rows = emps.Select(e =>
            {
                var shift = shiftDtos.Where(s => s.EmployeeId == e.Id).OrderBy(s => s.StartsAt).FirstOrDefault();
                var mine = events.Where(x => x.EmployeeId == e.Id).ToList();
                var dayShifts = shifts.Where(s => s.EmployeeId == e.Id).ToList();
                var paid = dayShifts.Count == 0 ? (int?)null : dayShifts.Sum(s => (int)(s.EndsAt - s.StartsAt).TotalMinutes - s.BreakMinutes);
                var result = TimesheetMath.Day(mine.Select(x => new Punch(x.EventType, x.OccurredAt)), shift?.StartsAt,
                    dayShifts.Count == 0 ? null : dayShifts.Max(s => s.EndsAt), paid, now);
                var st = StateOf(mine, now, staleCountsAsOpen: true);
                var state = mine.Count == 0
                    ? shift is not null && shift.StartsAt > now ? "NOT_YET" : "NOT_IN"
                    : st.Open is null ? "DONE"
                    : result.Status == TimesheetLineStatuses.MissingOut ? "MISSING_OUT"
                    : st.State;
                return new AttendanceRowDto(e.Id, e.Code, e.FullName, e.DepartmentId, depts.Single(d => d.Id == e.DepartmentId).Name, shift, state,
                    result.In is { } i ? TimesheetService.Local(i, tz) : null, result.Out is { } o && st.Open is null ? TimesheetService.Local(o, tz) : null,
                    result.WorkedMinutes, result.LateMinutes, mine.Any(x => x.Source == AttendanceSources.Manager),
                    eventDtos.Where(x => mine.Any(m => m.Id == x.Id)).ToList());
            })
            .OrderBy(r => order.GetValueOrDefault(r.DepartmentId, 999)).ThenBy(r => r.Shift?.StartsAt ?? DateTimeOffset.MaxValue).ThenBy(r => r.EmployeeName)
            .ToList();
        var geofences = (await db.Geofences.AsNoTracking().Where(g => g.PropertyId == propertyId).OrderBy(g => g.Name).ToListAsync(ct)).Select(Dto).ToList();
        return new AttendanceDayDto(day, today, rows.Count(r => r.State is "ON_SHIFT" or "ON_BREAK"), rows.Count(r => r.State == "NOT_IN"),
            rows.Count(r => r.LateMinutes > 0), rows, geofences);
    }

    /// <summary>A manager records a clock event for someone with the reason (attendance.update; never for themselves).</summary>
    public async Task<AttendanceDayDto> RecordAsync(Guid propertyId, ManagerClockRequest r, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var tz = TimesheetService.Tz(property);
        var now = clock.GetUtcNow();
        var today = property.LocalDate(now);
        var action = r.Action.Trim().ToUpperInvariant();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM employees WHERE id = {r.EmployeeId} AND property_id = {propertyId} FOR UPDATE", ct);
        var emp = await db.Employees.AsNoTracking().SingleOrDefaultAsync(e => e.Id == r.EmployeeId && e.PropertyId == propertyId, ct)
                  ?? throw ReservationService.Field(nameof(r.EmployeeId), "Choose an employee of this property.");
        if (emp.UserId is { } u && u == caller.UserId)
            throw new BusinessRuleAppException("OWN_ATTENDANCE", "You can't record your own attendance. Clock in on your phone, or ask another manager.");

        var date = r.Date ?? today;
        var at = r.Time is { } time ? Instant(date, time, tz) : now;
        if (r.Time is null && r.Date is { } d && d != today)
            throw ReservationService.Field(nameof(r.Time), "Give the time for a day other than today.");
        if (at > now.AddMinutes(1))
            throw ReservationService.Field(nameof(r.Time), $"{TimesheetService.T(TimesheetService.Local(at, tz))} on {QuoteEngine.Day(date)} hasn't happened yet. Give a time that has passed.");
        if (date < today.AddDays(-7))
            throw ReservationService.Field(nameof(r.Date), $"A manager entry goes back at most 7 days (to {QuoteEngine.Day(today.AddDays(-7))}). Correct older days on the timesheet.");
        if (!emp.WorksOn(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, tz).DateTime)))
            throw ReservationService.Field(nameof(r.Date), at < Instant(emp.JoinedOn, TimeOnly.MinValue, tz)
                ? $"{emp.FullName} joins on {QuoteEngine.Day(emp.JoinedOn)}."
                : $"{emp.FullName} left on {QuoteEngine.Day(emp.LeftOn!.Value)}.");

        var recent = await RecentAsync(emp.Id, now, ct);
        if (recent.Count > 0 && recent[^1].OccurredAt >= at)
        {
            var last = recent[^1];
            throw ReservationService.Field(nameof(r.Time),
                $"{emp.FirstName}'s latest entry is {Words(last.EventType)} at {TimesheetService.T(TimesheetService.Local(last.OccurredAt, tz))} on " +
                $"{QuoteEngine.Day(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(last.OccurredAt, tz).DateTime))}. A new entry must come after it; " +
                "to change earlier times, correct the day on the timesheet.");
        }
        // A forgotten clock-out can be closed by a manager, so an old open clock-in still counts here.
        var state = StateOf(recent, at, staleCountsAsOpen: true);
        if (action == AttendanceEventTypes.ClockIn && state.Open is not null && (at - state.Open.OccurredAt).TotalHours > TimesheetMath.OpenSessionLimitHours)
            state = state with { State = "OFF", Open = null };
        RefuseTransition(action, state, tz, you: false, emp.FirstName);

        var ev = new AttendanceEvent
        {
            OrganizationId = emp.OrganizationId,
            PropertyId = propertyId,
            EmployeeId = emp.Id,
            EventType = action,
            OccurredAt = at,
            LocationStatus = LocationStatuses.Override,
            Source = AttendanceSources.Manager,
            Reason = r.Reason.Trim(),
            CreatedAt = now,
            CreatedBy = caller.RequireUserId(),
        };
        await PlaceAsync(property, emp, ev, state, tz, ct);
        db.AttendanceEvents.Add(ev);
        await db.SaveChangesAsync(ct);

        var sheet = await timesheets.ForDayAsync(property, emp.Id, ev.WorkDate, ct);
        if (sheet.Status is TimesheetStatuses.Approved or TimesheetStatuses.Locked)
            throw new ConflictAppException("TIMESHEET_CLOSED",
                $"{emp.FullName}'s timesheet for the week of {QuoteEngine.Day(sheet.WeekStart)} is already " +
                $"{(sheet.Status == TimesheetStatuses.Locked ? "locked for payroll, so it can't change" : "approved. Correct the day on the timesheet instead")}.");
        await timesheets.RecalculateAsync(property, sheet, ct, reopen: true);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await DayAsync(propertyId, ev.WorkDate, ct);
    }

    // ------------------------------------------------------------------ helpers

    internal sealed record ClockState(string State, AttendanceEvent? Open, DateTimeOffset? Since, AttendanceEvent? Stale);

    /// <summary>Where a person stands after their events (in time order): OFF, ON_SHIFT or ON_BREAK, and the open clock-in. A
    /// clock-in older than 16 hours without a clock-out is a forgotten one: for the employee it no longer counts as open.</summary>
    internal static ClockState StateOf(IReadOnlyList<AttendanceEvent> events, DateTimeOffset now, bool staleCountsAsOpen)
    {
        AttendanceEvent? open = null;
        var state = "OFF";
        DateTimeOffset? since = null;
        foreach (var e in events)
        {
            switch (e.EventType)
            {
                case AttendanceEventTypes.ClockIn: open = e; state = "ON_SHIFT"; since = e.OccurredAt; break;
                case AttendanceEventTypes.BreakStart when open is not null: state = "ON_BREAK"; since = e.OccurredAt; break;
                case AttendanceEventTypes.BreakEnd when open is not null: state = "ON_SHIFT"; since = e.OccurredAt; break;
                case AttendanceEventTypes.ClockOut: open = null; state = "OFF"; since = e.OccurredAt; break;
            }
        }
        if (open is not null && !staleCountsAsOpen && (now - open.OccurredAt).TotalHours > TimesheetMath.OpenSessionLimitHours)
            return new ClockState("OFF", null, null, open);
        return new ClockState(state, open, since, null);
    }

    private static void RefuseTransition(string action, ClockState s, TimeZoneInfo tz, bool you, string name)
    {
        string At(DateTimeOffset? x) => x is { } v ? TimesheetService.T(TimesheetService.Local(v, tz)) : "";
        var who = you ? "You" : name;
        var whose = you ? "your" : $"{name}'s";
        string? why = (action, s.State) switch
        {
            (AttendanceEventTypes.ClockIn, "ON_SHIFT") => $"{who} clocked in at {At(s.Open!.OccurredAt)} and {(you ? "haven't" : "hasn't")} clocked out yet.",
            (AttendanceEventTypes.ClockIn, "ON_BREAK") => $"{who} {(you ? "are" : "is")} on a break since {At(s.Since)}. End the break, then clock out first.",
            (AttendanceEventTypes.BreakStart, "OFF") => $"{who} {(you ? "haven't" : "hasn't")} clocked in. Clock in first.",
            (AttendanceEventTypes.BreakStart, "ON_BREAK") => $"{who} {(you ? "are" : "is")} already on a break since {At(s.Since)}.",
            (AttendanceEventTypes.BreakEnd, "OFF") => $"{who} {(you ? "haven't" : "hasn't")} clocked in, so there is no break to end.",
            (AttendanceEventTypes.BreakEnd, "ON_SHIFT") => $"{who} {(you ? "aren't" : "isn't")} on a break. Start {whose} break first.",
            (AttendanceEventTypes.ClockOut, "OFF") => $"{who} {(you ? "haven't" : "hasn't")} clocked in, so there is nothing to clock out of.",
            _ => null,
        };
        if (why is not null) throw new ConflictAppException("ATTENDANCE_STATE", why);
    }

    /// <summary>Checks the phone's position against the property's geofences and fills the location fields.</summary>
    private async Task LocateAsync(Property property, AttendanceEvent ev, ClockRequest r, bool checkInside, CancellationToken ct)
    {
        var fences = await db.Geofences.AsNoTracking().Where(g => g.PropertyId == property.Id && g.IsActive).ToListAsync(ct);
        if (r.Latitude is { } lat && r.Longitude is { } lng)
        {
            ev.Latitude = Math.Round(lat, 6);
            ev.Longitude = Math.Round(lng, 6);
            ev.AccuracyM = r.AccuracyM;
        }
        if (fences.Count == 0)
        {
            ev.LocationStatus = LocationStatuses.NoGeofence;
            return;
        }
        if (ev.Latitude is null || ev.Longitude is null)
        {
            if (!checkInside)
            {
                ev.LocationStatus = LocationStatuses.NoGeofence;
                return;
            }
            throw ReservationService.Field(nameof(r.Latitude),
                "We need your phone's location to check you are at the resort. Allow location for this app in the browser, then try again.");
        }
        if (checkInside && r.AccuracyM is > WorstAccuracyM)
            throw ReservationService.Field(nameof(r.AccuracyM),
                $"Your phone only knows where you are to within {TimesheetMath.Distance(r.AccuracyM.Value)}. Turn on precise location (GPS) or step outside, then try again.");

        var nearest = fences
            .Select(g => (Fence: g, Metres: TimesheetMath.DistanceMetres((double)ev.Latitude, (double)ev.Longitude, (double)g.Latitude, (double)g.Longitude)))
            .OrderBy(x => x.Metres - x.Fence.RadiusM).First();
        ev.DistanceM = (int)Math.Round(nearest.Metres);
        var slack = Math.Min(r.AccuracyM ?? 0, AccuracyAllowanceM);
        if (nearest.Metres <= nearest.Fence.RadiusM + slack)
        {
            ev.GeofenceId = nearest.Fence.Id;
            ev.LocationStatus = LocationStatuses.Inside;
            return;
        }
        if (!checkInside)
        {
            ev.LocationStatus = LocationStatuses.NoGeofence;
            return;
        }
        throw new BusinessRuleAppException("OUTSIDE_GEOFENCE",
            $"You are {TimesheetMath.Distance(nearest.Metres)} from {nearest.Fence.Name}; clocking {(ev.EventType == AttendanceEventTypes.ClockIn ? "in" : "out")} " +
            $"counts within {nearest.Fence.RadiusM} m of it. Do it at the resort, or ask your manager to record it with a reason.",
            new { distanceM = ev.DistanceM, radiusM = nearest.Fence.RadiusM, geofence = nearest.Fence.Name });
    }

    /// <summary>Gives the event its work date and shift: a clock-in takes the rostered shift around it (else the local date),
    /// anything else the open clock-in's.</summary>
    private async Task PlaceAsync(Property property, Employee emp, AttendanceEvent ev, ClockState state, TimeZoneInfo tz, CancellationToken ct)
    {
        if (ev.EventType != AttendanceEventTypes.ClockIn && state.Open is { } open)
        {
            ev.WorkDate = open.WorkDate;
            ev.RosterShiftId = open.RosterShiftId;
            return;
        }
        var at = ev.OccurredAt;
        var from = at.AddHours(-RosterMath.MaxShiftHours);
        var to = at.AddHours(ShiftWindowHours);
        var shift = await db.RosterShifts.AsNoTracking()
            .Where(s => s.EmployeeId == emp.Id && s.Status == RosterShiftStatuses.Scheduled && s.StartsAt <= to && s.StartsAt >= from && s.EndsAt > at)
            .OrderBy(s => s.StartsAt).FirstOrDefaultAsync(ct);
        ev.RosterShiftId = shift?.Id;
        ev.WorkDate = shift?.WorkDate ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, tz).DateTime);
    }

    private async Task<MyAttendanceDto> MeAsync(Property property, Employee emp, CancellationToken ct)
    {
        var tz = TimesheetService.Tz(property);
        var now = clock.GetUtcNow();
        var today = property.LocalDate(now);
        var recent = await RecentAsync(emp.Id, now, ct);
        var state = StateOf(recent, now, staleCountsAsOpen: false);
        // The day being worked, or the one just finished (until 16 hours after its last entry, so a night shift's clock-out
        // still shows the night).
        var workDate = state.Open?.WorkDate
                       ?? (recent.Count > 0 && (now - recent[^1].OccurredAt).TotalHours <= TimesheetMath.OpenSessionLimitHours && state.Stale is null
                           ? recent[^1].WorkDate : (DateOnly?)null);

        var dayEvents = workDate is { } wd
            ? await db.AttendanceEvents.AsNoTracking().Where(e => e.EmployeeId == emp.Id && e.WorkDate == wd).OrderBy(e => e.OccurredAt).ThenBy(e => e.CreatedAt).ToListAsync(ct)
            : [];
        // So far: an open session counts up to now.
        var punches = dayEvents.Select(e => new Punch(e.EventType, e.OccurredAt)).ToList();
        if (state.Open is not null) punches.Add(new Punch(AttendanceEventTypes.ClockOut, now));
        var soFar = TimesheetMath.Day(punches, null, null, null, now);

        var shifts = await db.RosterShifts.AsNoTracking()
            .Where(s => s.EmployeeId == emp.Id && s.Status == RosterShiftStatuses.Scheduled && s.WorkDate >= today.AddDays(-1) && s.WorkDate <= today.AddDays(14))
            .OrderBy(s => s.StartsAt).ToListAsync(ct);
        var depts = await db.Departments.AsNoTracking().Where(d => d.PropertyId == property.Id).ToListAsync(ct);
        var shiftDtos = RosterService.ShiftDtos(shifts, [emp], depts, await roster.TemplateDtosAsync(property.Id, ct), tz);
        var todayShift = shiftDtos.FirstOrDefault(s => s.WorkDate == (workDate ?? today));
        var next = shiftDtos.FirstOrDefault(s => s.StartsAt > now && s.Id != todayShift?.Id);

        var users = await timesheets.UserNamesAsync(dayEvents.Select(e => e.CreatedBy), ct);
        var missed = state.Stale is { } st
            ? $"You clocked in on {QuoteEngine.Day(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(st.OccurredAt, tz).DateTime))} at " +
              $"{TimesheetService.T(TimesheetService.Local(st.OccurredAt, tz))} and didn't clock out. Tell your manager when you left so they can correct it."
            : null;
        var week = await db.Timesheets.AsNoTracking().Where(t => t.EmployeeId == emp.Id && t.WeekStart == RosterMath.WeekStart(today)).ToListAsync(ct);
        var weekDto = week.Count == 0 ? null : (await timesheets.SummariesAsync(property, week, ct))[0];
        var fenced = await db.Geofences.AnyAsync(g => g.PropertyId == property.Id && g.IsActive, ct);
        return new MyAttendanceDto(emp.Id, emp.FullName, depts.Single(d => d.Id == emp.DepartmentId).Name, state.State, state.Since, today, workDate,
            todayShift, next, await EventDtosAsync(db, dayEvents, users, tz, ct), soFar.WorkedMinutes, soFar.BreakMinutes, missed, fenced, weekDto);
    }

    private Task<List<AttendanceEvent>> RecentAsync(Guid employeeId, DateTimeOffset now, CancellationToken ct)
    {
        var from = now.AddDays(-8);
        return db.AttendanceEvents.AsNoTracking().Where(e => e.EmployeeId == employeeId && e.OccurredAt >= from)
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.CreatedAt).ToListAsync(ct);
    }

    internal static async Task<List<AttendanceEventDto>> EventDtosAsync(IAppDbContext db, IReadOnlyCollection<AttendanceEvent> events,
        IReadOnlyDictionary<Guid, string> users, TimeZoneInfo tz, CancellationToken ct)
    {
        var fenceIds = events.Select(e => e.GeofenceId).OfType<Guid>().Distinct().ToList();
        var fences = fenceIds.Count == 0 ? [] : await db.Geofences.AsNoTracking().Where(g => fenceIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, ct);
        return events.Select(e => new AttendanceEventDto(e.Id, e.EventType, e.OccurredAt, TimesheetService.Local(e.OccurredAt, tz), e.WorkDate, e.LocationStatus,
            e.DistanceM, e.GeofenceId is { } g ? fences.GetValueOrDefault(g) : null, e.Source, e.Reason,
            e.Source == AttendanceSources.Manager && e.CreatedBy is { } b ? users.GetValueOrDefault(b) : null)).ToList();
    }

    private static DateTimeOffset Instant(DateOnly day, TimeOnly time, TimeZoneInfo tz)
    {
        var local = day.ToDateTime(time);
        return new DateTimeOffset(local, tz.GetUtcOffset(local)).ToUniversalTime();
    }

    private static string Words(string type) => type switch
    {
        AttendanceEventTypes.ClockIn => "a clock-in",
        AttendanceEventTypes.BreakStart => "a break start",
        AttendanceEventTypes.BreakEnd => "a break end",
        _ => "a clock-out",
    };

    private static GeofenceDto Dto(Geofence g) => new(g.Id, g.Name, g.Latitude, g.Longitude, g.RadiusM, g.IsActive, g.Version);
}
