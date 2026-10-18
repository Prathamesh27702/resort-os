namespace Resort.Application.FrontDesk;

/// <summary>What a reservation still owes, for the check-out rule and the front desk lists. The real one sums the
/// booking's folios (<see cref="Folios.FolioBalances"/>, Sprint 8); tests can set it.</summary>
public interface IFolioBalances
{
    Task<decimal> BalanceAsync(Guid reservationId, CancellationToken ct);
}
