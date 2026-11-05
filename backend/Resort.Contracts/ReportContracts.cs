namespace Resort.Contracts;

// ---------------------------------------------------------------- Sprint 23: reports and dashboards
//
// Every figure comes from the reporting summary (one row per property and day), refreshed every few minutes. Money is
// before GST unless the name says GST or Tax. A dorm bed counts as one unit, like a room: occupancy = units sold / units
// available (active, not out of order); ADR = room revenue / units sold; RevPAR = room revenue / units available.
// Financial figures (revenue, ADR, RevPAR, GST, costs) are null without financial_report.read, pay without
// employee_salary.read.

/// <summary>When the numbers were last added up, which days, and how often the job runs. RefreshedAt null: never yet.</summary>
public sealed record ReportFreshnessDto(DateTimeOffset? RefreshedAt, DateOnly? From, DateOnly? To, int? DurationMs, int EveryMinutes);

/// <summary>What the caller may see: operational (occupancy, arrivals), financial (revenue, GST, costs), pay, budget.</summary>
public sealed record ReportAccessDto(bool Operational, bool Financial, bool Pay, bool Budget);

/// <summary>The headline numbers for a period. Revenue fields null without financial access; Labour null without pay access.</summary>
public sealed record ReportKpisDto(
    DateOnly From, DateOnly To, int UnitsAvailable, int UnitsSold, decimal? OccupancyPercent, int GuestNights, int Arrivals,
    decimal? Adr, decimal? RevPar, decimal? RoomRevenue, decimal? FnbRevenue, decimal? ActivityRevenue, decimal? TransportRevenue,
    decimal? OtherRevenue, decimal? TotalRevenue, decimal? PaymentsReceived, decimal RestaurantSales, decimal FoodCost,
    decimal? FoodCostPercent, decimal? Labour, decimal? LabourPercent);

/// <summary>Tonight at the property.</summary>
public sealed record DashboardTodayDto(DateOnly Date, int UnitsAvailable, int UnitsSold, decimal? OccupancyPercent, int GuestsInHouse,
    int Arrivals, int Departures, int OutOfOrder);

/// <summary>One day of a chart: occupancy and (financial) revenue.</summary>
public sealed record ReportDayPointDto(DateOnly Date, int UnitsAvailable, int UnitsSold, decimal? OccupancyPercent, decimal? Revenue);

/// <summary>One month of the trend.</summary>
public sealed record ReportMonthPointDto(DateOnly Month, string Label, decimal? OccupancyPercent, decimal? Adr, decimal? Revenue);

/// <summary>This month's budget against what was spent (approved expenses, supplier bills and, with pay access, pay).</summary>
public sealed record DashboardBudgetDto(DateOnly Month, decimal Budget, decimal Spent, bool PayIncluded);

/// <summary>
/// The role-aware dashboard. Today and Ahead (the next 14 nights on the books) need report.operational.read;
/// MonthToDate with its comparisons (the same days of last month and of the month a year ago), Days (this month), Months
/// (the last 12), Channels and Gst need financial_report.read; Budget needs budget.read.
/// </summary>
public sealed record DashboardDto(
    DateOnly Today, ReportAccessDto Access, ReportFreshnessDto Freshness, DashboardTodayDto TodayStats,
    IReadOnlyList<ReportDayPointDto> Ahead, ReportKpisDto MonthToDate, ReportKpisDto? LastMonth, ReportKpisDto? LastYear,
    IReadOnlyList<ReportDayPointDto> Days, IReadOnlyList<ReportMonthPointDto> Months, IReadOnlyList<ChannelRowDto>? Channels,
    GstRowDto? Gst, DashboardBudgetDto? Budget);

// ---------------------------------------------------------------- the reports

/// <summary>One row of the occupancy report: a day or a month (Period = its first day).</summary>
public sealed record OccupancyRowDto(DateOnly Period, string Label, int RoomsAvailable, int RoomsSold, int BedsAvailable, int BedsSold,
    int UnitsAvailable, int UnitsSold, int OutOfOrder, decimal? OccupancyPercent, int GuestNights, int Arrivals, int Departures,
    decimal? RoomRevenue, decimal? Adr, decimal? RevPar);

public sealed record OccupancyReportDto(DateOnly From, DateOnly To, string By, IReadOnlyList<OccupancyRowDto> Rows, OccupancyRowDto Total,
    ReportFreshnessDto Freshness);

public sealed record RevenueRowDto(DateOnly Period, string Label, decimal Room, decimal Fnb, decimal Activity, decimal Transport, decimal Other,
    decimal Total, decimal Tax, decimal PaymentsReceived, decimal RefundsPaid);

public sealed record RevenueReportDto(DateOnly From, DateOnly To, string By, IReadOnlyList<RevenueRowDto> Rows, RevenueRowDto Total,
    ReportFreshnessDto Freshness);

/// <summary>A booking source over the period: units sold, room revenue, commission (at CommissionPercent) and the net.</summary>
public sealed record ChannelRowDto(Guid SourceId, string Code, string Name, string Channel, int UnitsSold, decimal? SharePercent,
    decimal RoomRevenue, decimal CommissionPercent, decimal Commission, decimal Net, decimal? Adr);

/// <summary>Totals per kind of channel: DIRECT, OTA, TRAVEL_AGENT, CORPORATE, OTHER.</summary>
public sealed record ChannelGroupDto(string Channel, int UnitsSold, decimal RoomRevenue, decimal Commission, decimal Net);

public sealed record ChannelReportDto(DateOnly From, DateOnly To, IReadOnlyList<ChannelRowDto> Rows, IReadOnlyList<ChannelGroupDto> Groups,
    ChannelGroupDto Total, ReportFreshnessDto Freshness);

/// <summary>
/// GST for a month: output on GST invoices and debit notes less credit notes; GST on restaurant bills paid at the table
/// (no GST invoice yet, Sprint 14); input that can be claimed on supplier bills and approved expenses; and the estimate
/// payable in cash (output − input; Credit when input is more). ChargedOnBills: the GST on the guests' bills by the day
/// of the service (an invoice is issued at check-out, so the two differ until every bill is invoiced). For the accountant
/// to check, not a return.
/// </summary>
public sealed record GstRowDto(DateOnly Month, string Label, decimal OutputTaxable, decimal OutputCgst, decimal OutputSgst, decimal OutputIgst,
    decimal OutputCess, decimal PosUninvoiced, decimal OutputTotal, decimal InputCgst, decimal InputSgst, decimal InputIgst, decimal InputTotal,
    decimal Payable, decimal Credit, decimal ChargedOnBills);

public sealed record GstReportDto(DateOnly From, DateOnly To, IReadOnlyList<GstRowDto> Rows, GstRowDto Total, ReportFreshnessDto Freshness);

/// <summary>Food and labour cost for a month: restaurant sales, the stock their dishes used, food cost %, stock written
/// off; pay (null without pay access) and pay as a % of revenue; approved expenses and supplier bills.</summary>
public sealed record CostRowDto(DateOnly Month, string Label, decimal Revenue, decimal RestaurantSales, decimal FoodCost, decimal? FoodCostPercent,
    decimal StockWrittenOff, decimal? Labour, decimal? LabourPercent, decimal Expenses, decimal SupplierBills);

/// <summary>A budget department over the period: approved expenses, supplier bills and pay (null without pay access).</summary>
public sealed record DepartmentCostDto(Guid? CostCentreId, string Code, string Name, decimal Expenses, decimal SupplierBills, decimal? Labour, decimal Total);

public sealed record CostReportDto(DateOnly From, DateOnly To, IReadOnlyList<CostRowDto> Rows, CostRowDto Total,
    IReadOnlyList<DepartmentCostDto> Departments, bool PayIncluded, ReportFreshnessDto Freshness);

/// <summary>One line of the profit and loss: an amount per month and the total.</summary>
public sealed record PnlLineDto(string Key, string Label, string Kind, IReadOnlyList<decimal> Months, decimal Total);

/// <summary>
/// Profit and loss by month (management figures, before GST): revenue by kind; cost of sales (the stock the dishes used and
/// stock written off); operating costs (approved expenses by department and, with pay access, pay); and the operating
/// profit. Supplier bills are not a cost here: what they bought is counted when it is used (food cost) or written off.
/// </summary>
public sealed record PnlReportDto(DateOnly From, DateOnly To, IReadOnlyList<DateOnly> Months, IReadOnlyList<string> Labels,
    IReadOnlyList<PnlLineDto> Lines, bool PayIncluded, ReportFreshnessDto Freshness);

/// <summary>
/// The month at a glance: the month's numbers beside the month before and the same month a year ago, the profit and loss,
/// the booking sources, GST, spending by department and the budget.
/// </summary>
public sealed record MonthReportDto(DateOnly Month, string Label, ReportAccessDto Access, ReportKpisDto This, ReportKpisDto Previous,
    ReportKpisDto LastYear, PnlReportDto? Pnl, IReadOnlyList<ChannelRowDto>? Channels, GstRowDto? Gst,
    IReadOnlyList<DepartmentCostDto>? Departments, DashboardBudgetDto? Budget, ReportFreshnessDto Freshness);

// ---------------------------------------------------------------- refresh and commission

/// <summary>Refresh now: adds up the days From..To again (at most 800; default the last financial year to 90 days ahead).</summary>
public sealed record RefreshReportsRequest(DateOnly? From = null, DateOnly? To = null);

/// <summary>A booking source's commission: Percent as set for this property, else the source's own, else the default
/// (18 % for an OTA, 10 % for a travel agent). IsSet: set here.</summary>
public sealed record CommissionRateDto(Guid SourceId, string Code, string Name, string Channel, decimal Percent, decimal DefaultPercent, bool IsSet);

/// <summary>Sets commission rates; Percent null puts a source back to its default.</summary>
public sealed record SaveCommissionRatesRequest(IReadOnlyList<CommissionRateRequest> Rates);

public sealed record CommissionRateRequest(Guid SourceId, decimal? Percent);
