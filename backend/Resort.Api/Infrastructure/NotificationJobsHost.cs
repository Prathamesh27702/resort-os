using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Resort.Application.Common;
using Resort.Application.Notifications;

namespace Resort.Api.Infrastructure;

/// <summary>
/// The scheduler (Sprint 24): every minute, for every property, runs the scheduled jobs that are due (day-before reminder,
/// pre-arrival, check-out and payment reminders, low stock and leave reminders). Each property runs in its own scope acting
/// for its organisation. Claiming a job and every job itself are idempotent (scheduled_jobs compare-and-set, job_runs
/// unique keys), so several API instances can run this at once. Notifications:JobsEnabled (default true).
/// </summary>
public sealed class NotificationSchedulerJob(IServiceScopeFactory scopes, IOptions<NotificationOptions> options, ILogger<NotificationSchedulerJob> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.JobsEnabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                List<Domain.Organizations.Property> properties;
                await using (var scope = scopes.CreateAsyncScope())
                    properties = await scope.ServiceProvider.GetRequiredService<IAppDbContext>().Properties.IgnoreQueryFilters().AsNoTracking()
                        .Where(p => p.DeletedAt == null && p.IsActive).ToListAsync(stoppingToken);
                foreach (var p in properties)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    scope.ServiceProvider.GetRequiredService<SystemActor>().ActFor(p.OrganizationId);
                    await scope.ServiceProvider.GetRequiredService<NotificationJobs>().RunDueAsync(p, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "The job scheduler failed; it looks again in a minute");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>
/// The dispatcher (Sprint 24): every few seconds (Notifications:DispatchIntervalSeconds, 10) sends what the outbox holds,
/// up to 50 at a time. Rows are claimed with FOR UPDATE SKIP LOCKED, so several API instances share the work without
/// sending a message twice. Notifications:DispatchEnabled (default true).
/// </summary>
public sealed class OutboxDispatchJob(IServiceScopeFactory scopes, IOptions<NotificationOptions> options, ILogger<OutboxDispatchJob> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.DispatchEnabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.DispatchIntervalSeconds)));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sent = await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchAsync(50, stoppingToken);
                if (sent > 0) log.LogInformation("Dispatcher handled {Count} messages", sent);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "The message dispatcher failed; it tries again shortly");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
