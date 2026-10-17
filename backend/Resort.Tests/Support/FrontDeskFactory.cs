using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Resort.Application.FrontDesk;

namespace Resort.Tests.Support;

/// <summary>The real API with two things a test can set: the clock (to stand at 23:30 or 00:30 at the property) and what
/// a reservation owes (folios arrive in Sprint 8). Everything else — database, locks, permissions — is the real thing.</summary>
public sealed class FrontDeskFactory : ResortApiFactory
{
    public PinnedClock Clock { get; } = new();
    public TestBalances Balances { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Clock);
            s.RemoveAll<IFolioBalances>();
            s.AddSingleton<IFolioBalances>(Balances);
        });
    }
}

/// <summary>The system clock, or a fixed instant while <see cref="At"/> is set. Sign in before pinning: access tokens are
/// checked against the real time.</summary>
public sealed class PinnedClock : TimeProvider
{
    public DateTimeOffset? At { get; set; }
    public override DateTimeOffset GetUtcNow() => At ?? base.GetUtcNow();
}

public sealed class TestBalances : IFolioBalances
{
    public System.Collections.Concurrent.ConcurrentDictionary<Guid, decimal> Owed { get; } = new();
    public Task<decimal> BalanceAsync(Guid reservationId, CancellationToken ct) => Task.FromResult(Owed.GetValueOrDefault(reservationId));
}
