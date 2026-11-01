using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Leave;

namespace Resort.Api.Infrastructure;

/// <summary>
/// The leave accrual (Sprint 19): every few hours, posts this month's leave for every property that has its leave set up
/// (the month in the property's time zone). Posting is idempotent — one entry per person, leave type and period, also
/// enforced by a unique index — so running often, by hand at the same time, or on several API instances is safe.
/// Leave:AccrualJob:Enabled (default true) and Leave:AccrualJob:IntervalMinutes (default 360).
/// </summary>
public sealed class LeaveAccrualJob(IServiceScopeFactory scopes, IConfiguration config, TimeProvider clock, ILogger<LeaveAccrualJob> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Leave:AccrualJob:Enabled", true)) return;
        var every = TimeSpan.FromMinutes(Math.Max(1, config.GetValue("Leave:AccrualJob:IntervalMinutes", 360)));
        using var timer = new PeriodicTimer(every);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
                var run = scope.ServiceProvider.GetRequiredService<LeaveAccrual>();
                var withLeave = await db.LeaveTypes.IgnoreQueryFilters().AsNoTracking().Select(t => t.PropertyId).Distinct().ToListAsync(stoppingToken);
                var properties = await db.Properties.IgnoreQueryFilters().AsNoTracking().Where(p => withLeave.Contains(p.Id)).ToListAsync(stoppingToken);
                foreach (var p in properties)
                {
                    var today = p.LocalDate(clock.GetUtcNow());
                    var result = await run.RunAsync(p, today.Year, today.Month, null, stoppingToken);
                    if (result.Posted > 0) log.LogInformation("Leave accrual {Month} at {Property}: {Posted} entries, {Days} days", result.Month, p.Code, result.Posted, result.Days);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Leave accrual job failed; it runs again in {Minutes} minutes", every.TotalMinutes);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
