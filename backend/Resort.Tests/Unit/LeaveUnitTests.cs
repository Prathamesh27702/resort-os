using Resort.Domain.Leave;

namespace Resort.Tests.Unit;

/// <summary>Sprint 19 — the leave and payroll arithmetic: days that count (holidays and the weekly day off left out, half
/// days 0.5), monthly and yearly accrual with a maximum, and the payroll CSV.</summary>
public class LeaveUnitTests
{
    private static readonly DateOnly Mon = new(2026, 11, 2);
    private static readonly Dictionary<DateOnly, string> NoHolidays = [];

    // ------------------------------------------------------------------ required: half days, holidays inside the range

    [Fact]
    public void Monday_to_Friday_with_a_holiday_on_Wednesday_and_Friday_afternoon_off_is_3_and_a_half_days()
    {
        var holidays = new Dictionary<DateOnly, string> { [Mon.AddDays(2)] = "Diwali" };
        var d = LeaveMath.Count(Mon, Mon.AddDays(4), false, true, holidays, null);
        Assert.Equal(3.5m, d.Days);
        Assert.Equal([Mon, Mon.AddDays(1), Mon.AddDays(3), Mon.AddDays(4)], d.Counted);
        Assert.Equal((Mon.AddDays(2), "Diwali"), Assert.Single(d.Skipped));
    }

    [Fact]
    public void The_weekly_day_off_does_not_count_and_a_half_day_at_each_end_takes_one_day_off_the_total()
    {
        // Thursday 5 to Monday 9 Nov, Sunday off, starting after lunch on Thursday and back after lunch on Monday.
        var d = LeaveMath.Count(Mon.AddDays(3), Mon.AddDays(7), true, true, NoHolidays, 7);
        Assert.Equal(3m, d.Days);
        Assert.Equal(4, d.Counted.Count);
        Assert.Equal("weekly day off", Assert.Single(d.Skipped).Why);
    }

    [Fact]
    public void One_morning_off_is_half_a_day()
    {
        Assert.Equal(0.5m, LeaveMath.Count(Mon, Mon, false, true, NoHolidays, null).Days);
        Assert.Equal(0.5m, LeaveMath.Count(Mon, Mon, true, false, NoHolidays, null).Days);
        Assert.Equal(1m, LeaveMath.Count(Mon, Mon, false, false, NoHolidays, null).Days);
    }

    [Fact]
    public void A_range_of_only_holidays_counts_nothing()
    {
        var holidays = new Dictionary<DateOnly, string> { [Mon] = "A", [Mon.AddDays(1)] = "B" };
        var d = LeaveMath.Count(Mon, Mon.AddDays(1), false, false, holidays, null);
        Assert.Equal((0m, 0), (d.Days, d.Counted.Count));
    }

    // ------------------------------------------------------------------ accrual

    [Fact]
    public void Monthly_accrual_is_a_twelfth_of_the_year_and_yearly_is_pro_rated_for_someone_who_joins_in_the_year()
    {
        Assert.Equal(1m, LeaveMath.Monthly(12));
        Assert.Equal(1.25m, LeaveMath.Monthly(15));
        Assert.Equal(0.58m, LeaveMath.Monthly(7));
        Assert.Equal(6m, LeaveMath.Yearly(6, 2026, new DateOnly(2025, 3, 10)));
        Assert.Equal(6m, LeaveMath.Yearly(6, 2026, new DateOnly(2026, 1, 1)));
        // Joined 10 Apr: May to December is 8 months = 4 days of 6.
        Assert.Equal(4m, LeaveMath.Yearly(6, 2026, new DateOnly(2026, 4, 10)));
        // Joined 1 Jul: July to December = 6 months of 7 days = 3.5.
        Assert.Equal(3.5m, LeaveMath.Yearly(7, 2026, new DateOnly(2026, 7, 1)));
        Assert.Equal(0m, LeaveMath.Yearly(6, 2026, new DateOnly(2026, 12, 15)));
    }

    [Fact]
    public void Accrual_stops_at_the_maximum_balance()
    {
        Assert.Equal(1m, LeaveMath.Capped(1, 5, 12));
        Assert.Equal(0.5m, LeaveMath.Capped(1, 11.5m, 12));
        Assert.Equal(0m, LeaveMath.Capped(1, 12, 12));
        Assert.Equal(0m, LeaveMath.Capped(1, 14, 12));
        Assert.Equal(1.25m, LeaveMath.Capped(1.25m, 100, null));
    }

    // ------------------------------------------------------------------ payroll CSV

    [Fact]
    public void The_payroll_csv_has_the_documented_header_and_one_row_per_employee_with_cells_made_safe()
    {
        var start = new DateOnly(2026, 10, 1);
        var csv = PayrollCsv.Write(
        [
            new PayrollRow("E00001", "Asha Naik", "Housekeeping", "FULL_TIME", start, new DateOnly(2026, 10, 31), 31, 4, 1, 20, 9137, 65, 12, 1.5m, 0.5m,
                1, 29.5m, "CL 1.5; LOP 0.5"),
            new PayrollRow("E00002", "=HYPERLINK(\"x\")", "Kitchen, bar", "SEASONAL", start, new DateOnly(2026, 10, 31), 10, 1, 0, 0, 0, 0, 0, 0, 0, 0, 10, ""),
        ]);
        var lines = csv.Split("\r\n");
        Assert.Equal("employee_code,employee_name,department,employment_type,period_start,period_end,employed_days,weekly_offs,holidays,days_worked," +
                     "worked_hours,overtime_hours,late_minutes,paid_leave_days,unpaid_leave_days,absent_days,payable_days,leave_detail", lines[0]);
        Assert.Equal("E00001,Asha Naik,Housekeeping,FULL_TIME,2026-10-01,2026-10-31,31,4,1,20,152.28,1.08,12,1.5,0.5,1,29.5,CL 1.5; LOP 0.5", lines[1]);
        Assert.Equal("E00002,\"'=HYPERLINK(\"\"x\"\")\",\"Kitchen, bar\",SEASONAL,2026-10-01,2026-10-31,10,1,0,0,0.00,0.00,0,0,0,0,10,", lines[2]);
        Assert.Equal(29.5m, PayrollCsv.Payable(31, 0.5m, 1));
    }
}
