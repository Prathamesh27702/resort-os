using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Resort.Application.Folios;

namespace Resort.Tests.Support;

/// <summary>The real API with a clock a test can pin (to run the nightly room charge "tomorrow"). Balances are the real
/// folio sums, unlike <see cref="FrontDeskFactory"/>.</summary>
public sealed class FolioFactory : ResortApiFactory
{
    public PinnedClock Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Clock);
        });
    }

    /// <summary>One run of the nightly room charge job, as the hosted service does it.</summary>
    public async Task<int> RunRoomChargesAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RoomChargeRun>().RunAsync(CancellationToken.None);
    }
}
