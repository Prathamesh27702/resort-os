using Resort.Domain.Common;

namespace Resort.Domain.Reports;

/// <summary>
/// One property's day in the reporting summary (Sprint 23, table reporting.daily_stats). Written only by the database
/// function reporting.refresh (the refresh job, the nightly rebuild or Refresh now); the app reads it and never saves it.
/// Money is before GST. A dorm bed counts as one unit, like a room.
/// </summary>
public class DailyStat : IPropertyScoped
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public DateOnly BusinessDate { get; set; }
    public int RoomsAvailable { get; set; }
    public int RoomsSold { get; set; }
    public int BedsAvailable { get; set; }
    public int BedsSold { get; set; }
    public int OutOfOrder { get; set; }
    public int GuestsInHouse { get; set; }
    public int Arrivals { get; set; }
    public int Departures { get; set; }
    public decimal RoomRevenue { get; set; }
    public decimal FnbRevenue { get; set; }
    public decimal ActivityRevenue { get; set; }
    public decimal TransportRevenue { get; set; }
    public decimal OtherRevenue { get; set; }
    public decimal TotalRevenue { get; set; }
    public decimal TaxCharged { get; set; }
    public decimal PaymentsReceived { get; set; }
    public decimal RefundsPaid { get; set; }
    public decimal RestaurantSales { get; set; }
    public int RestaurantOrders { get; set; }
    public decimal FoodCost { get; set; }
    public decimal StockWrittenOff { get; set; }
    public decimal OutputTaxable { get; set; }
    public decimal OutputCgst { get; set; }
    public decimal OutputSgst { get; set; }
    public decimal OutputIgst { get; set; }
    public decimal OutputCess { get; set; }
    public decimal PosGstUninvoiced { get; set; }
    public decimal InputCgst { get; set; }
    public decimal InputSgst { get; set; }
    public decimal InputIgst { get; set; }
    public DateTimeOffset RefreshedAt { get; set; }

    public int UnitsAvailable => RoomsAvailable + BedsAvailable;
    public int UnitsSold => RoomsSold + BedsSold;
}

/// <summary>Rooms and beds sold and room revenue by booking source for one day (reporting.daily_channel_stats).</summary>
public class DailyChannelStat : IPropertyScoped
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public DateOnly BusinessDate { get; set; }
    public Guid SourceId { get; set; }
    public int UnitsSold { get; set; }
    public decimal RoomRevenue { get; set; }
    public decimal Commission { get; set; }
}

/// <summary>What a budget department spent on a day (reporting.daily_costs): approved expenses and supplier bills at cost,
/// and the estimated pay bill. CostCentreId null: not given a department.</summary>
public class DailyCost : IPropertyScoped
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public DateOnly BusinessDate { get; set; }
    public Guid? CostCentreId { get; set; }
    public decimal Expenses { get; set; }
    public decimal SupplierBills { get; set; }
    public decimal Labour { get; set; }
}

/// <summary>When a property's reporting numbers were last refreshed (reporting.refresh_state).</summary>
public class ReportRefreshState : IPropertyScoped
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public DateTimeOffset RefreshedAt { get; set; }
    public DateOnly RefreshedFrom { get; set; }
    public DateOnly RefreshedTo { get; set; }
    public int DurationMs { get; set; }
    public DateTimeOffset? FullRefreshedAt { get; set; }
}
