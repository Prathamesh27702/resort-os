using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Resort.Application.Common;
using Resort.Application.Reports;

namespace Resort.Api.Infrastructure;

/// <summary>
/// Keeps the reporting summary current (Sprint 23). Every Reporting:RefreshMinutes (default 15) it adds up again, for
/// every property, the days from the 1st of last month to 90 days ahead; once a night (the first run after midnight at the
/// property) it rebuilds from the start of last financial year, so a late bill or a correction to an old day is picked up
/// by the next morning. reporting.refresh replaces the days it covers in one transaction under a lock per property, so
/// running on several API instances, or at the same time as Refresh now, is safe. Reporting:JobEnabled (default true).
/// </summary>
public sealed class ReportRefreshJob(IServiceScopeFactory scopes, IOptions<ReportingOptions> options, TimeProvider clock, ILogger<ReportRefreshJob> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.JobEnabled) return;
        var every = TimeSpan.FromMinutes(Math.Max(1, options.Value.RefreshMinutes));
        using var timer = new PeriodicTimer(every);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
                var refresher = scope.ServiceProvider.GetRequiredService<ReportRefresher>();
                var properties = await db.Properties.IgnoreQueryFilters().AsNoTracking().Where(p => p.DeletedAt == null).ToListAsync(stoppingToken);
                var states = await db.ReportRefreshStates.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(s => s.PropertyId, stoppingToken);
                foreach (var p in properties)
                {
                    var now = clock.GetUtcNow();
                    var today = p.LocalDate(now);
                    var full = !states.TryGetValue(p.Id, out var s) || s.FullRefreshedAt is not { } last || p.LocalDate(last) < today;
                    var (from, to) = full ? ReportMath.FullRange(today) : ReportMath.RecentRange(today);
                    var days = await refresher.RefreshAsync(p.Id, from, to, full, stoppingToken);
                    if (full) log.LogInformation("Reports rebuilt at {Property}: {Days} days from {From} to {To}", p.Code, days, from, to);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Report refresh failed; it runs again in {Minutes} minutes", every.TotalMinutes);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
