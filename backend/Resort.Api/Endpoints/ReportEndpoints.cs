using Resort.Api.Infrastructure;
using Resort.Application.Common;
using Resort.Application.Reports;
using Resort.Contracts;

namespace Resort.Api.Endpoints;

/// <summary>Sprint 23 — reports and dashboards: the role-aware dashboard, the reports (occupancy, revenue, booking sources
/// and the OTA net, GST, food and labour cost, profit and loss, the month at a glance), Refresh now and commission rates.
/// All read the reporting summary, never the raw transaction tables.</summary>
public static class ReportEndpoints
{
    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var r = app.MapGroup("/api/v1/properties/{propertyId:guid}/reports").WithTags("Reports");

        r.MapGet("/dashboard", (Guid propertyId, ReportService s, CancellationToken ct) => s.DashboardAsync(propertyId, ct))
            .RequirePermission(Perm.ReportOperationalRead);
        r.MapGet("/occupancy", (Guid propertyId, DateOnly? from, DateOnly? to, string? by, ReportService s, CancellationToken ct) =>
                s.OccupancyAsync(propertyId, from, to, by, ct))
            .RequirePermission(Perm.ReportOperationalRead);
        r.MapGet("/revenue", (Guid propertyId, DateOnly? from, DateOnly? to, string? by, ReportService s, CancellationToken ct) =>
                s.RevenueAsync(propertyId, from, to, by, ct))
            .RequirePermission(Perm.FinancialReportRead);
        r.MapGet("/channels", (Guid propertyId, DateOnly? from, DateOnly? to, ReportService s, CancellationToken ct) =>
                s.ChannelsAsync(propertyId, from, to, ct))
            .RequirePermission(Perm.FinancialReportRead);
        r.MapGet("/gst", (Guid propertyId, DateOnly? from, DateOnly? to, ReportService s, CancellationToken ct) => s.GstAsync(propertyId, from, to, ct))
            .RequirePermission(Perm.FinancialReportRead);
        r.MapGet("/costs", (Guid propertyId, DateOnly? from, DateOnly? to, ReportService s, CancellationToken ct) => s.CostsAsync(propertyId, from, to, ct))
            .RequirePermission(Perm.FinancialReportRead);
        r.MapGet("/pnl", (Guid propertyId, DateOnly? from, DateOnly? to, ReportService s, CancellationToken ct) => s.PnlAsync(propertyId, from, to, ct))
            .RequirePermission(Perm.FinancialReportRead);
        r.MapGet("/month", (Guid propertyId, DateOnly? month, ReportService s, CancellationToken ct) => s.MonthAsync(propertyId, month, ct))
            .RequirePermission(Perm.ReportOperationalRead);
        r.MapPost("/refresh", (Guid propertyId, RefreshReportsRequest? body, ReportService s, CancellationToken ct) =>
                s.RefreshAsync(propertyId, body ?? new RefreshReportsRequest(), ct))
            .RequirePermission(Perm.ReportOperationalRead);
        r.MapGet("/commission", (Guid propertyId, ReportService s, CancellationToken ct) => s.CommissionAsync(propertyId, ct))
            .RequirePermission(Perm.FinancialReportRead);
        r.MapPut("/commission", (Guid propertyId, SaveCommissionRatesRequest body, ReportService s, CancellationToken ct) =>
                s.SaveCommissionAsync(propertyId, body, ct))
            .Validate<SaveCommissionRatesRequest>().RequirePermission(Perm.PropertySettingsUpdate);
    }
}
