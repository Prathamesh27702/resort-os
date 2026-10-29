using Resort.Domain.Attendance;
using Resort.Domain.Employees;

namespace Resort.Tests.Unit;

/// <summary>Sprint 18 — timesheet arithmetic that needs no database (brief §44): worked time is in to out minus the
/// breaks, on exact instants so a night shift across midnight needs no special case; overtime, late and early against the
/// rostered shift; open and forgotten clock-outs; and the geofence distance.</summary>
public class AttendanceUnitTests
{
    private static readonly TimeZoneInfo Ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
    private static readonly DateOnly Thursday = new(2026, 10, 29);

    private static DateTimeOffset At(DateOnly day, int h, int m) => RosterMath.Instants(day, new TimeOnly(h, m), new TimeOnly(h, m).AddMinutes(1), Ist).Start;

    private static Punch P(string type, DateOnly day, int h, int m) => new(type, At(day, h, m));

    // ------------------------------------------------------------------ required: 08:03 in, 12:00–12:30 break, 16:10 out = 7 h 37 min

    [Fact]
    public void In_at_08_03_a_break_from_12_00_to_12_30_and_out_at_16_10_is_7_hours_37_minutes()
    {
        var (shiftStart, shiftEnd) = RosterMath.Instants(Thursday, new TimeOnly(8, 0), new TimeOnly(16, 0), Ist);
        var day = TimesheetMath.Day(
            [
                P(AttendanceEventTypes.ClockIn, Thursday, 8, 3), P(AttendanceEventTypes.BreakStart, Thursday, 12, 0),
                P(AttendanceEventTypes.BreakEnd, Thursday, 12, 30), P(AttendanceEventTypes.ClockOut, Thursday, 16, 10),
            ], shiftStart, shiftEnd, 8 * 60 - 30, At(Thursday, 20, 0));

        Assert.Equal(7 * 60 + 37, day.WorkedMinutes);
        Assert.Equal((30, 3, 0, 7, TimesheetLineStatuses.Worked), (day.BreakMinutes, day.LateMinutes, day.EarlyMinutes, day.OvertimeMinutes, day.Status));
        Assert.Equal((At(Thursday, 8, 3), At(Thursday, 16, 10)), (day.In!.Value, day.Out!.Value));
    }

    // ------------------------------------------------------------------ required: a shift crossing midnight

    [Fact]
    public void A_night_shift_from_22_55_to_07_10_the_next_morning_counts_for_the_day_it_started()
    {
        var friday = Thursday.AddDays(1);
        var (shiftStart, shiftEnd) = RosterMath.Instants(Thursday, new TimeOnly(23, 0), new TimeOnly(7, 0), Ist);
        var day = TimesheetMath.Day(
            [
                P(AttendanceEventTypes.ClockIn, Thursday, 22, 55), P(AttendanceEventTypes.BreakStart, friday, 2, 45),
                P(AttendanceEventTypes.BreakEnd, friday, 3, 15), P(AttendanceEventTypes.ClockOut, friday, 7, 10),
            ], shiftStart, shiftEnd, 8 * 60 - 30, At(friday, 9, 0));

        Assert.Equal(8 * 60 + 15 - 30, day.WorkedMinutes);   // 22:55 → 07:10 is 8 h 15 min, less the 30-minute break
        Assert.Equal((0, 0, 15), (day.LateMinutes, day.EarlyMinutes, day.OvertimeMinutes));
        Assert.Equal(At(friday, 7, 10), day.Out);
    }

    [Fact]
    public void Leaving_early_and_coming_late_are_counted_against_the_shift_and_overtime_only_beyond_it()
    {
        var (s, e) = RosterMath.Instants(Thursday, new TimeOnly(7, 0), new TimeOnly(15, 0), Ist);
        var day = TimesheetMath.Day([P(AttendanceEventTypes.ClockIn, Thursday, 7, 20), P(AttendanceEventTypes.ClockOut, Thursday, 14, 30)], s, e, 450,
            At(Thursday, 20, 0));
        Assert.Equal((430, 20, 30, 0), (day.WorkedMinutes, day.LateMinutes, day.EarlyMinutes, day.OvertimeMinutes));

        // Without a rostered shift: no late or early, overtime after 8 hours.
        var unrostered = TimesheetMath.Day([P(AttendanceEventTypes.ClockIn, Thursday, 9, 0), P(AttendanceEventTypes.ClockOut, Thursday, 18, 30)], null, null,
            null, At(Thursday, 20, 0));
        Assert.Equal((570, 0, 0, 90), (unrostered.WorkedMinutes, unrostered.LateMinutes, unrostered.EarlyMinutes, unrostered.OvertimeMinutes));
    }

    [Fact]
    public void Two_sessions_on_one_day_add_up_and_a_break_still_open_at_clock_out_ends_there()
    {
        var day = TimesheetMath.Day(
            [
                P(AttendanceEventTypes.ClockIn, Thursday, 7, 0), P(AttendanceEventTypes.ClockOut, Thursday, 11, 0),
                P(AttendanceEventTypes.ClockIn, Thursday, 17, 0), P(AttendanceEventTypes.BreakStart, Thursday, 20, 0),
                P(AttendanceEventTypes.ClockOut, Thursday, 20, 20),
            ], null, null, null, At(Thursday, 23, 0));
        Assert.Equal((4 * 60 + 3 * 60, 20, 2), (day.WorkedMinutes, day.BreakMinutes, day.Sessions));
        Assert.Equal((At(Thursday, 7, 0), At(Thursday, 20, 20)), (day.In!.Value, day.Out!.Value));
    }

    [Fact]
    public void A_clock_in_without_a_clock_out_is_on_shift_for_16_hours_and_then_a_missing_clock_out()
    {
        Punch[] punches = [P(AttendanceEventTypes.ClockIn, Thursday, 8, 0)];
        Assert.Equal(TimesheetLineStatuses.OnShift, TimesheetMath.Day(punches, null, null, null, At(Thursday, 23, 59)).Status);
        var missing = TimesheetMath.Day(punches, null, null, null, At(Thursday.AddDays(1), 0, 1));
        Assert.Equal((TimesheetLineStatuses.MissingOut, 0), (missing.Status, missing.WorkedMinutes));
        Assert.Equal(TimesheetLineStatuses.Absent, TimesheetMath.Day([], null, null, null, At(Thursday, 12, 0)).Status);
    }

    [Fact]
    public void A_correction_gives_the_times_directly_and_works_out_the_same_way()
    {
        var (s, e) = RosterMath.Instants(Thursday, new TimeOnly(8, 0), new TimeOnly(16, 0), Ist);
        var day = TimesheetMath.FromTimes(At(Thursday, 8, 3), At(Thursday, 16, 10), 30, s, e, 450);
        Assert.Equal((457, 3, 0, 7), (day.WorkedMinutes, day.LateMinutes, day.EarlyMinutes, day.OvertimeMinutes));
    }

    [Fact]
    public void The_distance_between_two_points_is_the_great_circle_distance_in_metres()
    {
        // 0.01° of longitude at Calangute (15.5° N) is about 1.07 km.
        Assert.InRange(TimesheetMath.DistanceMetres(15.5440, 73.7553, 15.5440, 73.7653), 1065, 1080);
        Assert.Equal(0, TimesheetMath.DistanceMetres(15.5440, 73.7553, 15.5440, 73.7553), 3);
        Assert.InRange(TimesheetMath.DistanceMetres(15.5440, 73.7553, 15.5449, 73.7553), 99, 101);   // 0.0009° of latitude ≈ 100 m
        Assert.Equal(("1.2 km", "340 m"), (TimesheetMath.Distance(1234), TimesheetMath.Distance(340.2)));
    }
}
