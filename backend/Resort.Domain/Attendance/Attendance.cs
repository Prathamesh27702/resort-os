using Resort.Domain.Common;
using Resort.Domain.Guests;

namespace Resort.Domain.Attendance;

public static class AttendanceEventTypes
{
    public const string ClockIn = "CLOCK_IN";
    public const string BreakStart = "BREAK_START";
    public const string BreakEnd = "BREAK_END";
    public const string ClockOut = "CLOCK_OUT";
    public static readonly string[] All = [ClockIn, BreakStart, BreakEnd, ClockOut];
}

public static class LocationStatuses
{
    public const string Inside = "INSIDE";
    public const string NoGeofence = "NO_GEOFENCE";
    public const string Override = "OVERRIDE";
}

public static class AttendanceSources
{
    public const string Device = "DEVICE";
    public const string Manager = "MANAGER";
}

public static class TimesheetStatuses
{
    public const string Draft = "DRAFT";
    public const string Submitted = "SUBMITTED";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Locked = "LOCKED";
}

public static class TimesheetLineStatuses
{
    public const string Worked = "WORKED";
    public const string Absent = "ABSENT";
    public const string OnShift = "ON_SHIFT";
    public const string MissingOut = "MISSING_OUT";
}

/// <summary>Where clocking in counts as being at work: a point and a radius in metres.</summary>
public class Geofence : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Name { get; set; } = default!;
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public int RadiusM { get; set; }
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }
}

/// <summary>A clock-in, break or clock-out, never changed. WorkDate: the day it counts for (the rostered shift's date, else
/// the local date of the clock-in). Source MANAGER: a manager recorded it for the person, with the reason.</summary>
public class AttendanceEvent : Entity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid EmployeeId { get; set; }
    public string EventType { get; set; } = default!;
    public DateTimeOffset OccurredAt { get; set; }
    public DateOnly WorkDate { get; set; }
    public Guid? RosterShiftId { get; set; }
    [PersonalData] public decimal? Latitude { get; set; }
    [PersonalData] public decimal? Longitude { get; set; }
    public int? AccuracyM { get; set; }
    public int? DistanceM { get; set; }
    public Guid? GeofenceId { get; set; }
    public string LocationStatus { get; set; } = LocationStatuses.NoGeofence;
    public string Source { get; set; } = AttendanceSources.Device;
    public string? Reason { get; set; }
    public string? Device { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

/// <summary>One employee's week (Monday to Sunday) with its totals. DRAFT → SUBMITTED → APPROVED or REJECTED → LOCKED.</summary>
public class Timesheet : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid EmployeeId { get; set; }
    public DateOnly WeekStart { get; set; }
    public string Status { get; set; } = TimesheetStatuses.Draft;
    public int WorkedMinutes { get; set; }
    public int BreakMinutes { get; set; }
    public int OvertimeMinutes { get; set; }
    public int LateMinutes { get; set; }
    public int EarlyMinutes { get; set; }
    public short DaysWorked { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public Guid? SubmittedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public Guid? DecidedBy { get; set; }
    public string? RejectionReason { get; set; }
    public DateTimeOffset? LockedAt { get; set; }
    public Guid? LockedBy { get; set; }
    public uint Version { get; set; }

    /// <summary>Lines are worked out from the clock events only while the employee still owns the timesheet.</summary>
    public bool Open => Status is TimesheetStatuses.Draft or TimesheetStatuses.Rejected;
}

/// <summary>One work day of a timesheet. Corrected: a manager set the times; never recalculated from events again.</summary>
public class TimesheetLine : AuditableEntity, IOrganizationScoped
{
    public Guid OrganizationId { get; set; }
    public Guid TimesheetId { get; set; }
    public DateOnly WorkDate { get; set; }
    public Guid? RosterShiftId { get; set; }
    public DateTimeOffset? ScheduledStart { get; set; }
    public DateTimeOffset? ScheduledEnd { get; set; }
    public int? ScheduledMinutes { get; set; }
    public DateTimeOffset? ClockInAt { get; set; }
    public DateTimeOffset? ClockOutAt { get; set; }
    public int BreakMinutes { get; set; }
    public int WorkedMinutes { get; set; }
    public int OvertimeMinutes { get; set; }
    public int LateMinutes { get; set; }
    public int EarlyMinutes { get; set; }
    public short Sessions { get; set; }
    public string Status { get; set; } = TimesheetLineStatuses.Worked;
    public bool Overridden { get; set; }
    public bool Corrected { get; set; }

    public void Apply(DayResult r)
    {
        ClockInAt = r.In;
        ClockOutAt = r.Out;
        BreakMinutes = r.BreakMinutes;
        WorkedMinutes = r.WorkedMinutes;
        OvertimeMinutes = r.OvertimeMinutes;
        LateMinutes = r.LateMinutes;
        EarlyMinutes = r.EarlyMinutes;
        Sessions = (short)r.Sessions;
        Status = r.Status;
    }
}

/// <summary>A manager's change to one day's times, with the reason. Never changed.</summary>
public class TimesheetCorrection : Entity, IOrganizationScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid TimesheetId { get; set; }
    public DateOnly WorkDate { get; set; }
    public string TimesheetStatus { get; set; } = default!;
    public DateTimeOffset? OldClockInAt { get; set; }
    public DateTimeOffset? OldClockOutAt { get; set; }
    public int? OldBreakMinutes { get; set; }
    public int? OldWorkedMinutes { get; set; }
    public DateTimeOffset? NewClockInAt { get; set; }
    public DateTimeOffset? NewClockOutAt { get; set; }
    public int NewBreakMinutes { get; set; }
    public int NewWorkedMinutes { get; set; }
    public string Reason { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

/// <summary>A clock event as the arithmetic sees it.</summary>
public sealed record Punch(string Type, DateTimeOffset At);

/// <summary>A worked-out day: first in, last out, unpaid breaks, minutes worked (in minus out minus breaks), overtime
/// (worked beyond the shift's paid length, or beyond 8 hours without a shift), late (in after the shift start) and early
/// (out before the shift end).</summary>
public sealed record DayResult(
    DateTimeOffset? In, DateTimeOffset? Out, int BreakMinutes, int WorkedMinutes, int OvertimeMinutes, int LateMinutes, int EarlyMinutes,
    int Sessions, string Status);

/// <summary>
/// Pure timesheet arithmetic (brief §44). Everything is on exact instants, so a shift that crosses midnight or a clock
/// change needs no special case; dates and times are only turned into local ones (the property's time zone) for display.
/// Example: 08:03 in, 12:00–12:30 break, 16:10 out = 8 h 7 min − 30 min = 7 h 37 min.
/// </summary>
public static class TimesheetMath
{
    /// <summary>A day without a roster shift: overtime counts after this many minutes.</summary>
    public const int StandardDayMinutes = 8 * 60;

    /// <summary>A clock-in with no clock-out for longer than this is a forgotten clock-out, not someone still at work.</summary>
    public const int OpenSessionLimitHours = 16;

    /// <summary>Works out one day from its clock events (in time order). A break still open at clock-out ends there. An
    /// open session counts nothing yet: ON_SHIFT while it is under 16 hours old, MISSING_OUT after that.</summary>
    public static DayResult Day(IEnumerable<Punch> punches, DateTimeOffset? scheduledStart, DateTimeOffset? scheduledEnd, int? scheduledMinutes,
        DateTimeOffset now)
    {
        DateTimeOffset? first = null, last = null, sessionIn = null, breakFrom = null;
        double workedSeconds = 0, breakSeconds = 0, sessionBreaks = 0;
        var sessions = 0;
        foreach (var p in punches.OrderBy(p => p.At))
        {
            switch (p.Type)
            {
                case AttendanceEventTypes.ClockIn when sessionIn is null:
                    sessionIn = p.At;
                    first ??= p.At;
                    sessionBreaks = 0;
                    break;
                case AttendanceEventTypes.BreakStart when sessionIn is not null && breakFrom is null:
                    breakFrom = p.At;
                    break;
                case AttendanceEventTypes.BreakEnd when breakFrom is not null:
                    sessionBreaks += (p.At - breakFrom.Value).TotalSeconds;
                    breakFrom = null;
                    break;
                case AttendanceEventTypes.ClockOut when sessionIn is not null:
                    if (breakFrom is not null) sessionBreaks += (p.At - breakFrom.Value).TotalSeconds;
                    workedSeconds += (p.At - sessionIn.Value).TotalSeconds - sessionBreaks;
                    breakSeconds += sessionBreaks;
                    sessions++;
                    last = p.At;
                    sessionIn = null;
                    breakFrom = null;
                    break;
            }
        }

        var status = sessionIn is not null
            ? (now - sessionIn.Value).TotalHours > OpenSessionLimitHours ? TimesheetLineStatuses.MissingOut : TimesheetLineStatuses.OnShift
            : first is null ? TimesheetLineStatuses.Absent : TimesheetLineStatuses.Worked;
        if (status == TimesheetLineStatuses.Worked)
            return Result(first, last, (int)(breakSeconds / 60), (int)(workedSeconds / 60), sessions, status, scheduledStart, scheduledEnd, scheduledMinutes);
        // Still at work, or the clock-out is missing: only what is known (when they came in, how late) until it is closed.
        var late = first is { } f && scheduledStart is { } s && f > s ? (int)((f - s).TotalMinutes) : 0;
        return new DayResult(first, last, (int)(breakSeconds / 60), (int)(workedSeconds / 60), 0, late, 0, sessions, status);
    }

    /// <summary>A day from times a manager gave (a correction): in, out and the unpaid break.</summary>
    public static DayResult FromTimes(DateTimeOffset clockIn, DateTimeOffset clockOut, int breakMinutes, DateTimeOffset? scheduledStart,
        DateTimeOffset? scheduledEnd, int? scheduledMinutes) =>
        Result(clockIn, clockOut, breakMinutes, (int)((clockOut - clockIn).TotalSeconds / 60) - breakMinutes, 1, TimesheetLineStatuses.Worked,
            scheduledStart, scheduledEnd, scheduledMinutes);

    private static DayResult Result(DateTimeOffset? first, DateTimeOffset? last, int breakMinutes, int worked, int sessions, string status,
        DateTimeOffset? scheduledStart, DateTimeOffset? scheduledEnd, int? scheduledMinutes)
    {
        worked = Math.Max(0, worked);
        var late = first is { } f && scheduledStart is { } s && f > s ? (int)(f - s).TotalMinutes : 0;
        var early = last is { } l && scheduledEnd is { } e && l < e ? (int)(e - l).TotalMinutes : 0;
        var overtime = Math.Max(0, worked - (scheduledMinutes ?? StandardDayMinutes));
        return new DayResult(first, last, breakMinutes, worked, overtime, late, early, sessions, status);
    }

    /// <summary>Distance in metres between two points on the earth (haversine).</summary>
    public static double DistanceMetres(double lat1, double lng1, double lat2, double lng2)
    {
        const double r = 6_371_000;
        static double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLng = Rad(lng2 - lng1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>"1.2 km" or "340 m".</summary>
    public static string Distance(double metres) =>
        metres >= 1000 ? $"{(metres / 1000).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} km" : $"{Math.Round(metres):0} m";
}
