using Resort.Domain.Finance;

namespace Resort.Tests.Unit;

/// <summary>Sprint 22 — the money and calendar rules of expenses and budgets: the Indian financial year, the GST split, what
/// counts as a cost, spreading a yearly budget, the run-rate forecast and how a department stands.</summary>
public class FinanceUnitTests
{
    [Theory]
    [InlineData("2026-04-01", "2026-04-01", "2026-27")]
    [InlineData("2027-03-31", "2026-04-01", "2026-27")]
    [InlineData("2026-03-31", "2025-04-01", "2025-26")]
    [InlineData("2099-12-31", "2099-04-01", "2099-00")]
    public void The_financial_year_runs_april_to_march(string day, string start, string label)
    {
        var s = FinanceMath.YearStart(DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal((DateOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture), label), (s, FinanceMath.YearLabel(s)));
        Assert.Equal([new DateOnly(s.Year, 4, 1), new DateOnly(s.Year + 1, 3, 1)], new[] { FinanceMath.Months(s)[0], FinanceMath.Months(s)[11] });
    }

    [Theory]
    [InlineData(180, true, 90, 90, 0)]
    [InlineData(180.01, true, 90, 90.01, 0)]
    [InlineData(0.01, true, 0, 0.01, 0)]
    [InlineData(900, false, 0, 0, 900)]
    public void Gst_splits_into_halves_within_the_state_and_igst_from_another(decimal gst, bool intra, decimal cgst, decimal sgst, decimal igst)
    {
        Assert.Equal((cgst, sgst, igst), FinanceMath.SplitGst(gst, intra));
        var (c, s, i) = FinanceMath.SplitGst(gst, intra);
        Assert.Equal(gst, c + s + i);
    }

    [Fact]
    public void Only_the_taxable_value_is_a_cost_when_the_tax_comes_back_as_input_credit()
    {
        Assert.Equal(1_000m, FinanceMath.Cost(1_180m, 180m, itcEligible: true));
        Assert.Equal(1_180m, FinanceMath.Cost(1_180m, 180m, itcEligible: false));
        Assert.Equal(450m, FinanceMath.Cost(450m, 0m, itcEligible: false));
    }

    [Theory]
    [InlineData(120_000, 10_000, 10_000)]
    [InlineData(100_000, 8_333, 8_337)]
    [InlineData(11, 0, 11)]
    [InlineData(1_000.50, 83, 87.50)]
    public void A_yearly_budget_spreads_in_whole_rupees_and_march_takes_the_rest(decimal annual, decimal month, decimal march)
    {
        var m = FinanceMath.Spread(annual);
        Assert.Equal(12, m.Length);
        Assert.All(m[..11], x => Assert.Equal(month, x));
        Assert.Equal(march, m[11]);
        Assert.Equal(annual, m.Sum());
    }

    [Fact]
    public void The_forecast_runs_the_spending_so_far_over_the_whole_year()
    {
        var y = new DateOnly(2026, 4, 1);
        Assert.Equal(36_500m, FinanceMath.Forecast(21_400m, y, new DateOnly(2026, 10, 31)));   // 214 of 365 days
        Assert.Equal(36_500m, FinanceMath.Forecast(100m, y, y));                                // one day in
        Assert.Equal(5_000m, FinanceMath.Forecast(5_000m, y, new DateOnly(2027, 3, 31)));      // the whole year
        Assert.Equal(5_000m, FinanceMath.Forecast(5_000m, y, new DateOnly(2027, 6, 1)));       // after the year: what it was
        Assert.Null(FinanceMath.Forecast(0m, y, new DateOnly(2026, 3, 31)));                   // before the year
        // 2027-28 has 29 February 2028: 366 days.
        Assert.Equal(366m, FinanceMath.Forecast(1m, new DateOnly(2027, 4, 1), new DateOnly(2027, 4, 1)));
    }

    [Theory]
    [InlineData(21_000, 21_400, 36_000, 36_500, "OVER")]
    [InlineData(35_000, 10_700, 60_000, 18_250, "ON_TRACK")]
    [InlineData(21_000, 20_000, 36_000, 40_000, "HEADING_OVER")]
    [InlineData(0, 500, 0, 900, "NO_BUDGET")]
    [InlineData(0, 0, 0, 0, "NO_BUDGET")]
    public void A_department_stands_over_heading_over_on_track_or_without_a_budget(decimal budget, decimal actual, decimal annual, decimal forecast, string standing) =>
        Assert.Equal(standing, FinanceMath.Standing(budget, actual, annual, forecast));

    [Fact]
    public void Expense_moves_in_code_match_the_database_trigger()
    {
        // fn_expenses_frozen lets only a SUBMITTED row change, and the CHECK lists the four statuses: so every move leaves SUBMITTED.
        Assert.All(ExpenseStatuses.Moves, m => Assert.Equal(ExpenseStatuses.Submitted, m.From));
        var sql = File.ReadAllText(Path.Combine(Repo(), "database", "migrations", "V024__expenses.sql"));
        Assert.Contains("IF OLD.status <> 'SUBMITTED' THEN", sql);
        Assert.Contains("CHECK (status IN ('SUBMITTED','APPROVED','REJECTED','CANCELLED'))", sql);
    }

    private static string Repo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "ResortOS.sln"))) d = d.Parent;
        return d!.FullName;
    }
}
