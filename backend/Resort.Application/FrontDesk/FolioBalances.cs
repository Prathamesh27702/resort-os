namespace Resort.Application.FrontDesk;

/// <summary>What a reservation still owes, for the check-out rule. Folios arrive in Sprint 8, which replaces
/// <see cref="NoFolioBalances"/> with the real balance (charges minus payments).</summary>
public interface IFolioBalances
{
    Task<decimal> BalanceAsync(Guid reservationId, CancellationToken ct);
}

/// <summary>Until folios exist nothing is owed, so check-out is never blocked by a balance.</summary>
public sealed class NoFolioBalances : IFolioBalances
{
    public Task<decimal> BalanceAsync(Guid reservationId, CancellationToken ct) => Task.FromResult(0m);
}
