namespace Resort.Contracts;

// Sprint 18 — attendance and timesheets (brief §43, §44, §74). Times: StartTime/ClockIn … are local times at the property;
// the matching …At fields are the exact instants.

// ---------------------------------------------------------------- geofences

/// <summary>Where clocking in counts as being at work: within RadiusM metres of the point.</summary>
public sealed record GeofenceDto(Guid Id, string Name, decimal Latitude, decimal Longitude, int RadiusM, bool IsActive, uint Version);

public sealed record SaveGeofenceRequest(string Name, decimal Latitude, decimal Longitude, int RadiusM, bool IsActive = true, uint? Version = null);

// ---------------------------------------------------------------- clocking

/// <summary>
/// The caller clocks in, starts or ends a break, or clocks out. Action: CLOCK_IN, BREAK_START, BREAK_END, CLOCK_OUT.
/// Latitude/Longitude/AccuracyM: the phone's position (needed when the property has a geofence); the server decides
/// whether it is inside.
/// </summary>
public sealed record ClockRequest(string Action, decimal? Latitude, decimal? Longitude, int? AccuracyM);

/// <summary>
/// A manager records a clock event for someone (attendance.update), e.g. their phone had no signal or they forgot to
/// clock out. Date and Time: local at the property (default: now). Reason: always needed, kept with the event.
/// </summary>
public sealed record ManagerClockRequest(Guid EmployeeId, string Action, DateOnly? Date, TimeOnly? Time, string Reason);

/// <summary>One clock event. LocationStatus: INSIDE (a geofence), NO_GEOFENCE (none set up), OVERRIDE (a manager recorded it,
/// with Reason). DistanceM: from the nearest geofence's centre.</summary>
public sealed record AttendanceEventDto(
    Guid Id, string EventType, DateTimeOffset OccurredAt, TimeOnly Time, DateOnly WorkDate, string LocationStatus, int? DistanceM,
    string? GeofenceName, string Source, string? Reason, string? RecordedBy);

/// <summary>
/// The caller's attendance now. State: OFF, ON_SHIFT, ON_BREAK. Since: when the current state began. MissedClockOut: an
/// earlier clock-in that was never closed (more than 16 hours ago), for the manager to correct. TodayShift/NextShift:
/// from the roster. Events: the current or latest work day's events. WorkedMinutes: that day so far. Week: this week's
/// timesheet.
/// </summary>
public sealed record MyAttendanceDto(
    Guid EmployeeId, string EmployeeName, string DepartmentName, string State, DateTimeOffset? Since, DateOnly Today, DateOnly? WorkDate,
    RosterShiftDto? TodayShift, RosterShiftDto? NextShift, IReadOnlyList<AttendanceEventDto> Events, int WorkedMinutes, int BreakMinutes,
    string? MissedClockOut, bool GeofenceRequired, TimesheetSummaryDto? Week);

/// <summary>One person on the day's attendance board. State: NOT_YET (shift not started), NOT_IN (shift started, no
/// clock-in), ON_SHIFT, ON_BREAK, DONE (clocked out), MISSING_OUT.</summary>
public sealed record AttendanceRowDto(
    Guid EmployeeId, string EmployeeCode, string EmployeeName, Guid DepartmentId, string DepartmentName, RosterShiftDto? Shift, string State,
    TimeOnly? ClockIn, TimeOnly? ClockOut, int WorkedMinutes, int LateMinutes, bool Overridden, IReadOnlyList<AttendanceEventDto> Events);

/// <summary>The day's attendance: everyone on the roster that day or who clocked in, with counts.</summary>
public sealed record AttendanceDayDto(DateOnly Date, DateOnly Today, int OnShift, int NotIn, int Late, IReadOnlyList<AttendanceRowDto> Rows,
    IReadOnlyList<GeofenceDto> Geofences);

// ---------------------------------------------------------------- timesheets

/// <summary>A timesheet's head and totals. Status: DRAFT, SUBMITTED, APPROVED, REJECTED, LOCKED. OpenDays: days still on
/// shift or with a missing clock-out. Actions: what the caller may do now (submit, approve, reject, lock, correct).</summary>
public sealed record TimesheetSummaryDto(
    Guid Id, Guid EmployeeId, string EmployeeCode, string EmployeeName, string DepartmentName, DateOnly WeekStart, DateOnly WeekEnd, string Status,
    int WorkedMinutes, int BreakMinutes, int OvertimeMinutes, int LateMinutes, int EarlyMinutes, int DaysWorked, int OpenDays, int Corrections,
    DateTimeOffset? SubmittedAt, DateTimeOffset? DecidedAt, string? DecidedBy, string? RejectionReason, DateTimeOffset? LockedAt,
    IReadOnlyList<string> Actions, uint Version);

/// <summary>One work day. Status: WORKED, ABSENT (on the roster, never clocked in), ON_SHIFT, MISSING_OUT. ClockOutNextDay:
/// the clock-out is on the following calendar day (a night shift).</summary>
public sealed record TimesheetLineDto(
    DateOnly WorkDate, string? ShiftName, TimeOnly? ShiftStart, TimeOnly? ShiftEnd, int? ScheduledMinutes, TimeOnly? ClockIn, TimeOnly? ClockOut,
    bool ClockOutNextDay, DateTimeOffset? ClockInAt, DateTimeOffset? ClockOutAt, int BreakMinutes, int WorkedMinutes, int OvertimeMinutes,
    int LateMinutes, int EarlyMinutes, string Status, bool Overridden, bool Corrected, IReadOnlyList<AttendanceEventDto> Events);

/// <summary>A change to a day's times: old and new values, the reason, who made it and the timesheet's status then.</summary>
public sealed record TimesheetCorrectionDto(
    Guid Id, DateOnly WorkDate, TimeOnly? OldClockIn, TimeOnly? OldClockOut, int? OldBreakMinutes, int? OldWorkedMinutes, TimeOnly? NewClockIn,
    TimeOnly? NewClockOut, int NewBreakMinutes, int NewWorkedMinutes, string Reason, string TimesheetStatus, DateTimeOffset CreatedAt, string? By);

public sealed record TimesheetDto(TimesheetSummaryDto Sheet, string? Note, string? SubmittedBy, IReadOnlyList<TimesheetLineDto> Lines,
    IReadOnlyList<TimesheetCorrectionDto> Corrections);

/// <summary>A week's timesheets at the property (for approvers), with how many are in each status.</summary>
public sealed record TimesheetWeekDto(
    DateOnly WeekStart, DateOnly WeekEnd, DateOnly Today, IReadOnlyList<TimesheetSummaryDto> Timesheets, IReadOnlyDictionary<string, int> Counts);

public sealed record SubmitTimesheetRequest(string? Note, uint? Version = null);

public sealed record RejectTimesheetRequest(string Reason, uint? Version = null);

public sealed record TimesheetVersionRequest(uint? Version = null);

/// <summary>Approves every SUBMITTED timesheet of a week that can be approved; the others are listed with the reason.</summary>
public sealed record ApproveTimesheetWeekRequest(DateOnly WeekStart);

public sealed record ApproveTimesheetWeekResultDto(int Approved, IReadOnlyList<string> Skipped, TimesheetWeekDto Week);

/// <summary>
/// A manager sets one day's times (attendance.update; on an APPROVED timesheet also timesheet.approve). ClockIn and
/// ClockOut are local times on WorkDate (ClockOut ≤ ClockIn: the next day); both empty: the day was not worked.
/// BreakMinutes: the unpaid break. Reason: always needed. Refused once the timesheet is LOCKED.
/// </summary>
public sealed record CorrectTimesheetLineRequest(DateOnly WorkDate, TimeOnly? ClockIn, TimeOnly? ClockOut, int BreakMinutes, string Reason,
    uint? Version = null);
