using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Resort.Application.Common;
using Resort.Application.Reservations;
using Resort.Contracts;
using Resort.Domain.Finance;
using Resort.Domain.Organizations;
using Resort.Domain.Reports;

namespace Resort.Application.Reports;

/// <summary>Reporting:RefreshMinutes (default 15): how often the job adds up the recent days; Reporting:JobEnabled.</summary>
public sealed class ReportingOptions
{
    public const string Section = "Reporting";
    public int RefreshMinutes { get; set; } = 15;
    public bool JobEnabled { get; set; } = true;
}

/// <summary>The arithmetic of the reports, kept apart so the unit tests check it against hand-worked numbers.</summary>
public static class ReportMath
{
    /// <summary>part / whole as a percentage with one decimal; null when there is nothing to divide by.</summary>
    public static decimal? Percent(decimal part, decimal whole) => whole == 0 ? null : decimal.Round(part * 100 / whole, 1, MidpointRounding.AwayFromZero);

    /// <summary>An amount per unit (ADR: room revenue per unit sold; RevPAR: per unit available); null for no units.</summary>
    public static decimal? PerUnit(decimal amount, int units) => units == 0 ? null : decimal.Round(amount / units, 2, MidpointRounding.AwayFromZero);

    /// <summary>The commission when nothing is set: 18 % for an OTA, 10 % for a travel agent, else none (mirrors
    /// reporting.commission_percent in V025).</summary>
    public static decimal DefaultCommission(string channel) => channel switch { "OTA" => 18m, "TRAVEL_AGENT" => 10m, _ => 0m };

    public static DateOnly MonthStart(DateOnly d) => new(d.Year, d.Month, 1);
    public static DateOnly MonthEnd(DateOnly d) => MonthStart(d).AddMonths(1).AddDays(-1);
    public static string MonthLabel(DateOnly m) => m.ToString("MMM yyyy", CultureInfo.InvariantCulture);
    public static string DayLabel(DateOnly d) => d.ToString("ddd d MMM", CultureInfo.InvariantCulture);

    /// <summary>The months from the one <paramref name="from"/> falls in to the one <paramref name="to"/> falls in.</summary>
    public static List<DateOnly> MonthsBetween(DateOnly from, DateOnly to)
    {
        var list = new List<DateOnly>();
        for (var m = MonthStart(from); m <= to; m = m.AddMonths(1)) list.Add(m);
        return list;
    }

    /// <summary>The days the job refreshes every few minutes: from the 1st of last month to 90 days ahead (late bills,
    /// approvals and bookings for the coming months).</summary>
    public static (DateOnly From, DateOnly To) RecentRange(DateOnly today) => (MonthStart(today).AddMonths(-1), today.AddDays(90));

    /// <summary>The nightly rebuild: from the start of last financial year to 90 days ahead.</summary>
    public static (DateOnly From, DateOnly To) FullRange(DateOnly today) => (FinanceMath.YearStart(today).AddYears(-1), today.AddDays(90));

    public const int MaxRefreshDays = 900;
}

/// <summary>Runs the database function reporting.refresh for one property (the job, Refresh now and saving commission).</summary>
public sealed class ReportRefresher(IAppDbContext db)
{
    public async Task<int> RefreshAsync(Guid propertyId, DateOnly from, DateOnly to, bool full, CancellationToken ct) =>
        await db.Database.SqlQuery<int>($"SELECT reporting.refresh({propertyId}, {from}, {to}, {full}) AS \"Value\"").SingleAsync(ct);
}

/// <summary>
/// Dashboards and reports (Sprint 23, brief §50–55). Everything is read from the reporting summary (reporting.daily_stats,
/// daily_channel_stats, daily_costs), one row per property and day, so a year is a few hundred rows however busy the
/// property was (brief §88: no dashboard adds up the raw transaction tables). What a caller sees follows their
/// permissions: occupancy and arrivals with report.operational.read; revenue, GST and costs with financial_report.read; pay
/// with employee_salary.read; the budget with budget.read.
/// </summary>
public sealed class ReportService(IAppDbContext db, CallerAccess access, ICurrentUser caller, AuditContext audit, TimeProvider clock,
    ReportRefresher refresher, IOptions<ReportingOptions> options)
{
    public const string CommissionSetting = "reports.commission_percent";

    // ------------------------------------------------------------------ dashboard

    public async Task<DashboardDto> DashboardAsync(Guid propertyId, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var can = await AccessAsync(propertyId, ct);
        var today = property.LocalDate(clock.GetUtcNow());
        var month = ReportMath.MonthStart(today);
        var trendStart = month.AddMonths(-11);
        var lastYearStart = month.AddYears(-1);
        var from = trendStart < lastYearStart ? trendStart : lastYearStart;
        var days = await DaysAsync(propertyId, from, today.AddDays(14), ct);
        var costs = can.Financial ? await CostsAsync(propertyId, from, today, ct) : [];

        var t = days.SingleOrDefault(d => d.BusinessDate == today) ?? new DailyStat { BusinessDate = today };
        var todayStats = new DashboardTodayDto(today, t.UnitsAvailable, t.UnitsSold, ReportMath.Percent(t.UnitsSold, t.UnitsAvailable),
            t.GuestsInHouse, t.Arrivals, t.Departures, t.OutOfOrder);
        var ahead = days.Where(d => d.BusinessDate > today && d.BusinessDate <= today.AddDays(14))
            .Select(d => Point(d, false)).ToList();

        ReportKpisDto Same(DateOnly start)
        {
            var end = start.AddDays(today.Day - 1);
            if (end > ReportMath.MonthEnd(start)) end = ReportMath.MonthEnd(start);
            return Kpis(days, costs, start, end, can);
        }
        var mtd = Kpis(days, costs, month, today, can);
        var monthDays = days.Where(d => d.BusinessDate >= month && d.BusinessDate <= today).Select(d => Point(d, can.Financial)).ToList();
        var trend = Enumerable.Range(0, 12).Select(i => trendStart.AddMonths(i)).Select(m =>
        {
            var end = m == month ? today : ReportMath.MonthEnd(m);
            var k = Kpis(days, costs, m, end, can);
            return new ReportMonthPointDto(m, ReportMath.MonthLabel(m), k.OccupancyPercent, k.Adr, k.TotalRevenue);
        }).ToList();

        IReadOnlyList<ChannelRowDto>? channels = null;
        GstRowDto? gst = null;
        if (can.Financial)
        {
            channels = (await ChannelRowsAsync(propertyId, month, today, ct)).Rows;
            gst = Gst(month, days.Where(d => d.BusinessDate >= month && d.BusinessDate <= today));
        }
        var budget = can.Budget ? await BudgetForMonthAsync(propertyId, month, today, can, ct) : null;
        return new DashboardDto(today, can, await FreshnessAsync(propertyId, ct), todayStats, ahead, mtd,
            can.Financial ? Same(month.AddMonths(-1)) : null, can.Financial ? Same(lastYearStart) : null,
            monthDays, trend, channels, gst, budget);
    }

    // ------------------------------------------------------------------ occupancy and revenue

    public async Task<OccupancyReportDto> OccupancyAsync(Guid propertyId, DateOnly? from, DateOnly? to, string? by, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var can = await AccessAsync(propertyId, ct);
        var (start, end, grouping) = Period(property, from, to, by);
        var days = await DaysAsync(propertyId, start, end, ct);
        OccupancyRowDto Row(DateOnly period, string label, List<DailyStat> d)
        {
            var avail = d.Sum(x => x.UnitsAvailable);
            var sold = d.Sum(x => x.UnitsSold);
            var room = d.Sum(x => x.RoomRevenue);
            return new OccupancyRowDto(period, label, d.Sum(x => x.RoomsAvailable), d.Sum(x => x.RoomsSold), d.Sum(x => x.BedsAvailable),
                d.Sum(x => x.BedsSold), avail, sold, d.Sum(x => x.OutOfOrder), ReportMath.Percent(sold, avail), d.Sum(x => x.GuestsInHouse),
                d.Sum(x => x.Arrivals), d.Sum(x => x.Departures),
                can.Financial ? room : null, can.Financial ? ReportMath.PerUnit(room, sold) : null, can.Financial ? ReportMath.PerUnit(room, avail) : null);
        }
        var rows = Groups(days, start, end, grouping).Select(g => Row(g.Period, g.Label, g.Days)).ToList();
        return new OccupancyReportDto(start, end, grouping, rows, Row(start, "Total", days), await FreshnessAsync(propertyId, ct));
    }

    public async Task<RevenueReportDto> RevenueAsync(Guid propertyId, DateOnly? from, DateOnly? to, string? by, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var (start, end, grouping) = Period(property, from, to, by);
        var days = await DaysAsync(propertyId, start, end, ct);
        static RevenueRowDto Row(DateOnly period, string label, List<DailyStat> d) =>
            new(period, label, d.Sum(x => x.RoomRevenue), d.Sum(x => x.FnbRevenue), d.Sum(x => x.ActivityRevenue), d.Sum(x => x.TransportRevenue),
                d.Sum(x => x.OtherRevenue), d.Sum(x => x.TotalRevenue), d.Sum(x => x.TaxCharged), d.Sum(x => x.PaymentsReceived), d.Sum(x => x.RefundsPaid));
        var rows = Groups(days, start, end, grouping).Select(g => Row(g.Period, g.Label, g.Days)).ToList();
        return new RevenueReportDto(start, end, grouping, rows, Row(start, "Total", days), await FreshnessAsync(propertyId, ct));
    }

    // ------------------------------------------------------------------ booking sources (OTA net)

    public async Task<ChannelReportDto> ChannelsAsync(Guid propertyId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var (start, end, _) = Period(property, from, to, "day");
        var (rows, groups, total) = await ChannelRowsAsync(propertyId, start, end, ct);
        return new ChannelReportDto(start, end, rows, groups, total, await FreshnessAsync(propertyId, ct));
    }

    private async Task<(List<ChannelRowDto> Rows, List<ChannelGroupDto> Groups, ChannelGroupDto Total)> ChannelRowsAsync(
        Guid propertyId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var stats = await db.DailyChannelStats.AsNoTracking()
            .Where(c => c.PropertyId == propertyId && c.BusinessDate >= from && c.BusinessDate <= to)
            .GroupBy(c => c.SourceId)
            .Select(g => new { SourceId = g.Key, Units = g.Sum(x => x.UnitsSold), Revenue = g.Sum(x => x.RoomRevenue), Commission = g.Sum(x => x.Commission) })
            .ToListAsync(ct);
        var rates = (await CommissionAsync(propertyId, ct)).ToDictionary(r => r.SourceId);
        var ids = stats.Select(s => s.SourceId).ToList();
        var sources = await db.ReservationSources.AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var units = stats.Sum(s => s.Units);
        var rows = stats.Where(s => sources.ContainsKey(s.SourceId)).Select(s =>
        {
            var src = sources[s.SourceId];
            return new ChannelRowDto(s.SourceId, src.Code, src.Name, src.Channel, s.Units, ReportMath.Percent(s.Units, units), s.Revenue,
                rates.TryGetValue(s.SourceId, out var r) ? r.Percent : ReportMath.DefaultCommission(src.Channel), s.Commission,
                s.Revenue - s.Commission, ReportMath.PerUnit(s.Revenue, s.Units));
        }).OrderByDescending(r => r.RoomRevenue).ThenByDescending(r => r.UnitsSold).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
        var groups = rows.GroupBy(r => r.Channel)
            .Select(g => new ChannelGroupDto(g.Key, g.Sum(x => x.UnitsSold), g.Sum(x => x.RoomRevenue), g.Sum(x => x.Commission), g.Sum(x => x.Net)))
            .OrderByDescending(g => g.RoomRevenue).ToList();
        var total = new ChannelGroupDto("ALL", rows.Sum(x => x.UnitsSold), rows.Sum(x => x.RoomRevenue), rows.Sum(x => x.Commission), rows.Sum(x => x.Net));
        return (rows, groups, total);
    }

    // ------------------------------------------------------------------ GST

    public async Task<GstReportDto> GstAsync(Guid propertyId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var (start, end) = MonthPeriod(property, from, to);
        var days = await DaysAsync(propertyId, start, end, ct);
        var rows = ReportMath.MonthsBetween(start, end).Select(m => Gst(m, days.Where(d => ReportMath.MonthStart(d.BusinessDate) == m))).ToList();
        var total = Gst(start, days) with { Label = "Total" };
        return new GstReportDto(start, end, rows, total, await FreshnessAsync(propertyId, ct));
    }

    private static GstRowDto Gst(DateOnly month, IEnumerable<DailyStat> days)
    {
        var d = days.ToList();
        var cgst = d.Sum(x => x.OutputCgst);
        var sgst = d.Sum(x => x.OutputSgst);
        var igst = d.Sum(x => x.OutputIgst);
        var cess = d.Sum(x => x.OutputCess);
        var pos = d.Sum(x => x.PosGstUninvoiced);
        var output = cgst + sgst + igst + cess + pos;
        var input = d.Sum(x => x.InputCgst + x.InputSgst + x.InputIgst);
        return new GstRowDto(month, ReportMath.MonthLabel(month), d.Sum(x => x.OutputTaxable), cgst, sgst, igst, cess, pos, output,
            d.Sum(x => x.InputCgst), d.Sum(x => x.InputSgst), d.Sum(x => x.InputIgst), input,
            Math.Max(output - input, 0), Math.Max(input - output, 0), d.Sum(x => x.TaxCharged));
    }

    // ------------------------------------------------------------------ food and labour cost

    public async Task<CostReportDto> CostsAsync(Guid propertyId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var can = await AccessAsync(propertyId, ct);
        var (start, end) = MonthPeriod(property, from, to);
        var days = await DaysAsync(propertyId, start, end, ct);
        var costs = await CostsAsync(propertyId, start, end, ct);
        CostRowDto Row(DateOnly month, string label, List<DailyStat> d, List<DailyCost> c)
        {
            var revenue = d.Sum(x => x.TotalRevenue);
            var sales = d.Sum(x => x.RestaurantSales);
            var food = d.Sum(x => x.FoodCost);
            var labour = c.Sum(x => x.Labour);
            return new CostRowDto(month, label, revenue, sales, food, ReportMath.Percent(food, sales), d.Sum(x => x.StockWrittenOff),
                can.Pay ? labour : null, can.Pay ? ReportMath.Percent(labour, revenue) : null, c.Sum(x => x.Expenses), c.Sum(x => x.SupplierBills));
        }
        var rows = ReportMath.MonthsBetween(start, end).Select(m => Row(m, ReportMath.MonthLabel(m),
            days.Where(d => ReportMath.MonthStart(d.BusinessDate) == m).ToList(),
            costs.Where(c => ReportMath.MonthStart(c.BusinessDate) == m).ToList())).ToList();
        return new CostReportDto(start, end, rows, Row(start, "Total", days, costs), await DepartmentsAsync(propertyId, costs, can, ct), can.Pay,
            await FreshnessAsync(propertyId, ct));
    }

    private async Task<List<DepartmentCostDto>> DepartmentsAsync(Guid propertyId, List<DailyCost> costs, ReportAccessDto can, CancellationToken ct)
    {
        var centres = await db.CostCentres.AsNoTracking().Where(c => c.PropertyId == propertyId).OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        var list = new List<DepartmentCostDto>();
        foreach (var g in costs.GroupBy(c => c.CostCentreId))
        {
            var e = g.Sum(x => x.Expenses);
            var b = g.Sum(x => x.SupplierBills);
            var l = can.Pay ? g.Sum(x => x.Labour) : 0;
            if (e == 0 && b == 0 && l == 0) continue;
            var c = g.Key is { } id ? centres.FirstOrDefault(x => x.Id == id) : null;
            list.Add(new DepartmentCostDto(g.Key, c?.Code ?? "NONE", c?.Name ?? "No department", e, b, can.Pay ? l : null, e + b + l));
        }
        return list.OrderBy(d => d.CostCentreId is null ? 1 : 0)
            .ThenBy(d => centres.FindIndex(c => c.Id == d.CostCentreId)).ToList();
    }

    // ------------------------------------------------------------------ profit and loss

    public async Task<PnlReportDto> PnlAsync(Guid propertyId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var can = await AccessAsync(propertyId, ct);
        var (start, end) = MonthPeriod(property, from, to);
        var days = await DaysAsync(propertyId, start, end, ct);
        var costs = await CostsAsync(propertyId, start, end, ct);
        var centres = await db.CostCentres.AsNoTracking().Where(c => c.PropertyId == propertyId).OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        return Pnl(start, end, days, costs, centres, can, await FreshnessAsync(propertyId, ct));
    }

    private static PnlReportDto Pnl(DateOnly start, DateOnly end, List<DailyStat> days, List<DailyCost> costs, List<CostCentre> centres,
        ReportAccessDto can, ReportFreshnessDto freshness)
    {
        var months = ReportMath.MonthsBetween(start, end);
        var lines = new List<PnlLineDto>();
        PnlLineDto Line(string key, string label, string kind, Func<DateOnly, decimal> amount)
        {
            var values = months.Select(amount).ToList();
            var line = new PnlLineDto(key, label, kind, values, values.Sum());
            lines.Add(line);
            return line;
        }
        decimal D(DateOnly m, Func<DailyStat, decimal> f) => days.Where(d => ReportMath.MonthStart(d.BusinessDate) == m).Sum(f);
        decimal C(DateOnly m, Guid? centre, Func<DailyCost, decimal> f) =>
            costs.Where(c => ReportMath.MonthStart(c.BusinessDate) == m && c.CostCentreId == centre).Sum(f);
        decimal Sum(DateOnly m, params PnlLineDto[] of) => of.Sum(l => l.Months[months.IndexOf(m)]);

        var rev = new[]
        {
            Line("REV_ROOM", "Rooms", "REVENUE", m => D(m, d => d.RoomRevenue)),
            Line("REV_FNB", "Food and drink", "REVENUE", m => D(m, d => d.FnbRevenue)),
            Line("REV_ACTIVITY", "Activities", "REVENUE", m => D(m, d => d.ActivityRevenue)),
            Line("REV_TRANSPORT", "Transport", "REVENUE", m => D(m, d => d.TransportRevenue)),
            Line("REV_OTHER", "Other income", "REVENUE", m => D(m, d => d.OtherRevenue)),
        };
        var revenue = Line("REVENUE", "Total revenue", "TOTAL", m => Sum(m, rev));
        var cos = new[]
        {
            Line("COS_FOOD", "Food and drink used (stock)", "COST", m => D(m, d => d.FoodCost)),
            Line("COS_WASTE", "Stock written off", "COST", m => D(m, d => d.StockWrittenOff)),
        };
        var costOfSales = Line("COST_OF_SALES", "Cost of sales", "TOTAL", m => Sum(m, cos));
        var gross = Line("GROSS", "Gross profit", "PROFIT", m => Sum(m, revenue) - Sum(m, costOfSales));
        var opex = new List<PnlLineDto>();
        var used = costs.Where(c => c.Expenses != 0).Select(c => c.CostCentreId).Distinct().ToList();
        foreach (var c in centres.Where(c => used.Contains(c.Id)))
            opex.Add(Line("EXP_" + c.Code, $"Expenses: {c.Name}", "COST", m => C(m, c.Id, x => x.Expenses)));
        if (used.Contains(null)) opex.Add(Line("EXP_NONE", "Expenses: no department", "COST", m => C(m, null, x => x.Expenses)));
        if (can.Pay)
            opex.Add(Line("PAY", "Pay (estimate)", "COST", m => costs.Where(c => ReportMath.MonthStart(c.BusinessDate) == m).Sum(c => c.Labour)));
        var operating = Line("OPEX", "Operating costs", "TOTAL", m => Sum(m, [.. opex]));
        Line("PROFIT", can.Pay ? "Operating profit" : "Operating profit before pay", "PROFIT", m => Sum(m, gross) - Sum(m, operating));
        return new PnlReportDto(start, end, months, months.Select(ReportMath.MonthLabel).ToList(), lines, can.Pay, freshness);
    }

    // ------------------------------------------------------------------ the month at a glance

    public async Task<MonthReportDto> MonthAsync(Guid propertyId, DateOnly? month, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var can = await AccessAsync(propertyId, ct);
        var today = property.LocalDate(clock.GetUtcNow());
        var m = ReportMath.MonthStart(month ?? today);
        if (m > ReportMath.MonthStart(today))
            throw ReservationService.Field("month", $"{ReportMath.MonthLabel(m)} hasn't started yet. Choose this month or an earlier one.");
        if (m < ReportMath.MonthStart(today).AddYears(-5))
            throw ReservationService.Field("month", "Choose a month in the last five years.");
        DateOnly End(DateOnly s) => s == ReportMath.MonthStart(today) ? today : ReportMath.MonthEnd(s);
        var prev = m.AddMonths(-1);
        var lastYear = m.AddYears(-1);
        var days = await DaysAsync(propertyId, lastYear, End(m), ct);
        var costs = can.Financial ? await CostsAsync(propertyId, lastYear, End(m), ct) : [];
        var freshness = await FreshnessAsync(propertyId, ct);

        PnlReportDto? pnl = null;
        IReadOnlyList<ChannelRowDto>? channels = null;
        GstRowDto? gst = null;
        IReadOnlyList<DepartmentCostDto>? departments = null;
        if (can.Financial)
        {
            var mine = days.Where(d => d.BusinessDate >= m).ToList();
            var mineCosts = costs.Where(c => c.BusinessDate >= m).ToList();
            var centres = await db.CostCentres.AsNoTracking().Where(c => c.PropertyId == propertyId).OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
            pnl = Pnl(m, End(m), mine, mineCosts, centres, can, freshness);
            channels = (await ChannelRowsAsync(propertyId, m, End(m), ct)).Rows;
            gst = Gst(m, mine);
            departments = await DepartmentsAsync(propertyId, mineCosts, can, ct);
        }
        var budget = can.Budget ? await BudgetForMonthAsync(propertyId, m, End(m), can, ct) : null;
        return new MonthReportDto(m, ReportMath.MonthLabel(m), can, Kpis(days, costs, m, End(m), can), Kpis(days, costs, prev, ReportMath.MonthEnd(prev), can),
            Kpis(days, costs, lastYear, ReportMath.MonthEnd(lastYear), can), pnl, channels, gst, departments, budget, freshness);
    }

    // ------------------------------------------------------------------ refresh and commission

    /// <summary>Refresh now: adds up the days again (default the last financial year to 90 days ahead).</summary>
    public async Task<ReportFreshnessDto> RefreshAsync(Guid propertyId, RefreshReportsRequest r, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var today = property.LocalDate(clock.GetUtcNow());
        var (defFrom, defTo) = ReportMath.FullRange(today);
        var from = r.From ?? defFrom;
        var to = r.To ?? defTo;
        if (to < from) throw ReservationService.Field(nameof(r.To), "The end date must be on or after the start date.");
        if (to.DayNumber - from.DayNumber > ReportMath.MaxRefreshDays)
            throw ReservationService.Field(nameof(r.To), $"Refresh at most {ReportMath.MaxRefreshDays} days at a time (about two and a half years).");
        await refresher.RefreshAsync(propertyId, from, to, r.From is null && r.To is null, ct);
        return await FreshnessAsync(propertyId, ct);
    }

    public async Task<IReadOnlyList<CommissionRateDto>> CommissionAsync(Guid propertyId, CancellationToken ct)
    {
        await RequirePropertyAsync(propertyId, ct);
        var orgId = caller.RequireOrganizationId();
        var set = await CommissionSetAsync(propertyId, ct);
        return (await db.ReservationSources.AsNoTracking()
                .Where(s => s.IsActive && (s.OrganizationId == null || s.OrganizationId == orgId))
                .OrderBy(s => s.SortOrder).ThenBy(s => s.Name).ToListAsync(ct))
            .Select(s =>
            {
                var def = s.CommissionPercent ?? ReportMath.DefaultCommission(s.Channel);
                var mine = set.TryGetValue(s.Code, out var v) ? v : (decimal?)null;
                return new CommissionRateDto(s.Id, s.Code, s.Name, s.Channel, mine ?? def, def, mine is not null);
            }).ToList();
    }

    /// <summary>Sets the commission rates of the booking sources (property_settings.update; audited), then adds the last
    /// financial year up again so the OTA net uses them.</summary>
    public async Task<IReadOnlyList<CommissionRateDto>> SaveCommissionAsync(Guid propertyId, SaveCommissionRatesRequest r, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var current = await CommissionAsync(propertyId, ct);
        var set = await CommissionSetAsync(propertyId, ct);
        var changes = new List<string>();
        for (var i = 0; i < r.Rates.Count; i++)
        {
            var x = r.Rates[i];
            var src = current.SingleOrDefault(c => c.SourceId == x.SourceId)
                      ?? throw ReservationService.Field($"Rates[{i}].SourceId", "That booking source isn't in use here. Reload the page.");
            if (x.Percent is { } p)
            {
                if (p is < 0 or > 50 || decimal.Round(p, 2) != p)
                    throw ReservationService.Field($"Rates[{i}].Percent", $"Enter {src.Name}'s commission as 0 to 50 % with at most two decimals, e.g. 18 or 17.5.");
                if (!set.TryGetValue(src.Code, out var old) || old != p) changes.Add($"{src.Name} {p:0.##} %");
                set[src.Code] = p;
            }
            else if (set.Remove(src.Code)) changes.Add($"{src.Name} back to {src.DefaultPercent:0.##} %");
        }
        if (changes.Count == 0) return current;
        var row = await db.PropertySettings.SingleOrDefaultAsync(s => s.PropertyId == propertyId && s.Key == CommissionSetting, ct);
        var json = JsonSerializer.Serialize(set.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Value));
        if (row is null)
            db.PropertySettings.Add(new PropertySetting { OrganizationId = property.OrganizationId, PropertyId = propertyId, Key = CommissionSetting, Value = json });
        else row.Value = json;
        audit.Reason = "Commission: " + string.Join(", ", changes);
        await db.SaveChangesAsync(ct);
        var (from, to) = ReportMath.FullRange(property.LocalDate(clock.GetUtcNow()));
        await refresher.RefreshAsync(propertyId, from, to, true, ct);
        return await CommissionAsync(propertyId, ct);
    }

    private async Task<Dictionary<string, decimal>> CommissionSetAsync(Guid propertyId, CancellationToken ct)
    {
        var json = await db.PropertySettings.AsNoTracking().Where(s => s.PropertyId == propertyId && s.Key == CommissionSetting)
            .Select(s => s.Value).SingleOrDefaultAsync(ct);
        var set = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (json is null) return set;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return set;
        foreach (var p in doc.RootElement.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDecimal(out var v)) set[p.Name] = v;
        return set;
    }

    // ------------------------------------------------------------------ shared

    internal async Task<ReportAccessDto> AccessAsync(Guid propertyId, CancellationToken ct)
    {
        var a = await access.GetAsync(ct);
        var financial = a.Has(Perm.FinancialReportRead, propertyId);
        return new ReportAccessDto(financial || a.Has(Perm.ReportOperationalRead, propertyId), financial,
            a.Has(Perm.EmployeeSalaryRead, propertyId), a.Has(Perm.BudgetRead, propertyId));
    }

    private async Task<Property> RequirePropertyAsync(Guid propertyId, CancellationToken ct) =>
        await db.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == propertyId, ct) ?? throw new NotFoundAppException("Property", propertyId);

    private async Task<ReportFreshnessDto> FreshnessAsync(Guid propertyId, CancellationToken ct)
    {
        var s = await db.ReportRefreshStates.AsNoTracking().SingleOrDefaultAsync(x => x.PropertyId == propertyId, ct);
        return new ReportFreshnessDto(s?.RefreshedAt, s?.RefreshedFrom, s?.RefreshedTo, s?.DurationMs, options.Value.RefreshMinutes);
    }

    private Task<List<DailyStat>> DaysAsync(Guid propertyId, DateOnly from, DateOnly to, CancellationToken ct) =>
        db.DailyStats.AsNoTracking().Where(d => d.PropertyId == propertyId && d.BusinessDate >= from && d.BusinessDate <= to)
            .OrderBy(d => d.BusinessDate).ToListAsync(ct);

    private Task<List<DailyCost>> CostsAsync(Guid propertyId, DateOnly from, DateOnly to, CancellationToken ct) =>
        db.DailyCosts.AsNoTracking().Where(d => d.PropertyId == propertyId && d.BusinessDate >= from && d.BusinessDate <= to).ToListAsync(ct);

    private static ReportDayPointDto Point(DailyStat d, bool financial) =>
        new(d.BusinessDate, d.UnitsAvailable, d.UnitsSold, ReportMath.Percent(d.UnitsSold, d.UnitsAvailable), financial ? d.TotalRevenue : null);

    internal static ReportKpisDto Kpis(List<DailyStat> all, List<DailyCost> costs, DateOnly from, DateOnly to, ReportAccessDto can)
    {
        var d = all.Where(x => x.BusinessDate >= from && x.BusinessDate <= to).ToList();
        var avail = d.Sum(x => x.UnitsAvailable);
        var sold = d.Sum(x => x.UnitsSold);
        var room = d.Sum(x => x.RoomRevenue);
        var revenue = d.Sum(x => x.TotalRevenue);
        var sales = d.Sum(x => x.RestaurantSales);
        var food = d.Sum(x => x.FoodCost);
        var labour = costs.Where(c => c.BusinessDate >= from && c.BusinessDate <= to).Sum(c => c.Labour);
        var f = can.Financial;
        return new ReportKpisDto(from, to, avail, sold, ReportMath.Percent(sold, avail), d.Sum(x => x.GuestsInHouse), d.Sum(x => x.Arrivals),
            f ? ReportMath.PerUnit(room, sold) : null, f ? ReportMath.PerUnit(room, avail) : null,
            f ? room : null, f ? d.Sum(x => x.FnbRevenue) : null, f ? d.Sum(x => x.ActivityRevenue) : null, f ? d.Sum(x => x.TransportRevenue) : null,
            f ? d.Sum(x => x.OtherRevenue) : null, f ? revenue : null, f ? d.Sum(x => x.PaymentsReceived) : null,
            sales, food, ReportMath.Percent(food, sales),
            f && can.Pay ? labour : null, f && can.Pay ? ReportMath.Percent(labour, revenue) : null);
    }

    private async Task<DashboardBudgetDto> BudgetForMonthAsync(Guid propertyId, DateOnly month, DateOnly to, ReportAccessDto can, CancellationToken ct)
    {
        var planned = await db.BudgetLines.AsNoTracking().Where(l => l.PropertyId == propertyId && l.Month == month).SumAsync(l => (decimal?)l.Amount, ct) ?? 0;
        var spent = await db.DailyCosts.AsNoTracking().Where(c => c.PropertyId == propertyId && c.BusinessDate >= month && c.BusinessDate <= to)
            .SumAsync(c => (decimal?)(c.Expenses + c.SupplierBills + (can.Pay ? c.Labour : 0)), ct) ?? 0;
        return new DashboardBudgetDto(month, planned, spent, can.Pay);
    }

    private sealed record Group(DateOnly Period, string Label, List<DailyStat> Days);

    private static IEnumerable<Group> Groups(List<DailyStat> days, DateOnly from, DateOnly to, string by) => by == "month"
        ? ReportMath.MonthsBetween(from, to).Select(m => new Group(m, ReportMath.MonthLabel(m), days.Where(d => ReportMath.MonthStart(d.BusinessDate) == m).ToList()))
        : Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(from.AddDays)
            .Select(day => new Group(day, ReportMath.DayLabel(day), days.Where(d => d.BusinessDate == day).ToList()));

    /// <summary>The period of a day or month report: by day, this month to today and at most 366 days; by month, the last
    /// twelve months and at most 25.</summary>
    private (DateOnly From, DateOnly To, string By) Period(Property property, DateOnly? from, DateOnly? to, string? by)
    {
        var grouping = string.IsNullOrWhiteSpace(by) ? "day" : by.Trim().ToLowerInvariant();
        if (grouping is not ("day" or "month"))
            throw ReservationService.Field("by", "Show the report by day or by month.");
        if (grouping == "month")
        {
            var (f, t) = MonthPeriod(property, from, to);
            return (f, t, grouping);
        }
        var today = property.LocalDate(clock.GetUtcNow());
        var end = to ?? today;
        var start = from ?? ReportMath.MonthStart(end);
        if (start > end) throw ReservationService.Field("from", "The start date must be on or before the end date.");
        if (end.DayNumber - start.DayNumber > 366) throw ReservationService.Field("to", "Choose at most a year by day, or show it by month.");
        return (start, end, grouping);
    }

    private (DateOnly From, DateOnly To) MonthPeriod(Property property, DateOnly? from, DateOnly? to)
    {
        var today = property.LocalDate(clock.GetUtcNow());
        var end = to ?? ReportMath.MonthEnd(today);
        var start = from ?? ReportMath.MonthStart(end).AddMonths(-11);
        if (start > end) throw ReservationService.Field("from", "The start date must be on or before the end date.");
        if (ReportMath.MonthsBetween(start, end).Count > 25) throw ReservationService.Field("to", "Choose at most 25 months.");
        return (start, end);
    }
}
