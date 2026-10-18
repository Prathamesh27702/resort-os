using Resort.Application.Folios;

namespace Resort.Api.Infrastructure;

/// <summary>
/// The nightly room charge (Sprint 8): every few minutes, puts the nights that are due on the bills of checked-in guests
/// (tonight's, at properties that post nightly; any night still missing otherwise). Posting is idempotent — one charge per
/// booking and night, also enforced by a unique index — so running often, or on several API instances, is safe.
/// Folio:RoomChargeJob:Enabled (default true) and Folio:RoomChargeJob:IntervalMinutes (default 15).
/// </summary>
public sealed class RoomChargeJob(IServiceScopeFactory scopes, IConfiguration config, ILogger<RoomChargeJob> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Folio:RoomChargeJob:Enabled", true)) return;
        var every = TimeSpan.FromMinutes(Math.Max(1, config.GetValue("Folio:RoomChargeJob:IntervalMinutes", 15)));
        using var timer = new PeriodicTimer(every);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var posted = await scope.ServiceProvider.GetRequiredService<RoomChargeRun>().RunAsync(stoppingToken);
                if (posted > 0) log.LogInformation("Room charge job: {Nights} room nights posted", posted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Room charge job failed; it runs again in {Minutes} minutes", every.TotalMinutes);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
