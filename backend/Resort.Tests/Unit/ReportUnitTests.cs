using Resort.Application.Reports;

namespace Resort.Tests.Unit;

/// <summary>Sprint 23 — the arithmetic of the reports against numbers worked out by hand: occupancy, ADR and RevPAR (none
/// when there is nothing to divide by), the default commission, the months of a period and the days the job refreshes.</summary>
public class ReportUnitTests
{
    [Fact]
    public void Occupancy_adr_and_revpar_round_like_the_reports()
    {
        // The fixture month of the integration test: 17 unit-nights sold of 181, ₹47,900 room revenue.
        Assert.Equal(9.4m, ReportMath.Percent(17, 181));
        Assert.Equal(2_817.65m, ReportMath.PerUnit(47_900m, 17));
        Assert.Equal(264.64m, ReportMath.PerUnit(47_900m, 181));
        Assert.Equal(33.3m, ReportMath.Percent(2, 6));
        Assert.Equal(0.1m, ReportMath.Percent(1, 2000));   // 0.05 rounds away from zero
        Assert.Null(ReportMath.Percent(3, 0));
        Assert.Null(ReportMath.PerUnit(1_000m, 0));
    }

    [Theory]
    [InlineData("OTA", 18)]
    [InlineData("TRAVEL_AGENT", 10)]
    [InlineData("DIRECT", 0)]
    [InlineData("CORPORATE", 0)]
    public void Commission_defaults_to_18_percent_for_an_ota_and_10_for_a_travel_agent(string channel, int percent) =>
        Assert.Equal(percent, ReportMath.DefaultCommission(channel));

    [Fact]
    public void Months_and_refresh_ranges_cross_the_year_end()
    {
        Assert.Equal([new DateOnly(2025, 11, 1), new DateOnly(2025, 12, 1), new DateOnly(2026, 1, 1)],
            ReportMath.MonthsBetween(new DateOnly(2025, 11, 20), new DateOnly(2026, 1, 3)));
        Assert.Equal(new DateOnly(2028, 2, 29), ReportMath.MonthEnd(new DateOnly(2028, 2, 10)));
        Assert.Equal((new DateOnly(2025, 12, 1), new DateOnly(2026, 4, 9)), ReportMath.RecentRange(new DateOnly(2026, 1, 9)));
        // The nightly rebuild goes back to the start of last financial year: 1 April 2025 on 9 January 2027.
        Assert.Equal((new DateOnly(2025, 4, 1), new DateOnly(2027, 4, 9)), ReportMath.FullRange(new DateOnly(2027, 1, 9)));
        var (f, t) = ReportMath.FullRange(new DateOnly(2027, 3, 31));
        Assert.True(t.DayNumber - f.DayNumber <= ReportMath.MaxRefreshDays);
    }
}
