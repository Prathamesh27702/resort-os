using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Contracts;
using Resort.Domain.Payments;

namespace Resort.Application.Payments;

/// <summary>
/// What has been paid, summed from the rows every time (Sprint 10): nothing is stored twice.
///   paid on a folio      = its allocations from payments that count − the refunds taken from it
///   paid for a booking   = its payments that count − every refund of them (so money on account counts too)
/// A payment counts when it is CAPTURED, PARTIALLY_REFUNDED or REFUNDED; a voided (CANCELLED) one counts for nothing,
/// and so do its refunds (it has none: only a payment with nothing refunded can be voided).
///
/// Queries filter by folio or reservation id and ignore the tenant filter on purpose, as <see cref="Folios.FolioPosting"/>
/// does: the check-out rule and the nightly job pass in ids they have already loaded.
/// </summary>
public sealed class PaymentLedger(IAppDbContext db)
{
    /// <summary>What each folio has been paid.</summary>
    public async Task<Dictionary<Guid, decimal>> PaidByFolioAsync(IReadOnlyCollection<Guid> folioIds, CancellationToken ct)
    {
        var allocated = await (from a in db.PaymentAllocations.IgnoreQueryFilters()
                               join p in db.Payments.IgnoreQueryFilters() on a.PaymentId equals p.Id
                               where folioIds.Contains(a.FolioId) && PaymentStatuses.Counted.Contains(p.Status)
                               group a by a.FolioId into g
                               select new { g.Key, Sum = g.Sum(x => x.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        var refunded = await db.Refunds.IgnoreQueryFilters()
            .Where(r => r.FolioId != null && folioIds.Contains(r.FolioId.Value) && r.Status != RefundStatuses.Failed)
            .GroupBy(r => r.FolioId!.Value).Select(g => new { g.Key, Sum = g.Sum(x => x.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        return folioIds.Distinct().ToDictionary(id => id, id => allocated.GetValueOrDefault(id) - refunded.GetValueOrDefault(id));
    }

    /// <summary>What each booking has been paid in all: on its folios and on account.</summary>
    public async Task<Dictionary<Guid, decimal>> PaidByReservationAsync(IReadOnlyCollection<Guid> reservationIds, CancellationToken ct)
    {
        var received = await db.Payments.IgnoreQueryFilters()
            .Where(p => reservationIds.Contains(p.ReservationId) && PaymentStatuses.Counted.Contains(p.Status))
            .GroupBy(p => p.ReservationId).Select(g => new { g.Key, Sum = g.Sum(x => x.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        var refunded = await (from r in db.Refunds.IgnoreQueryFilters()
                              join p in db.Payments.IgnoreQueryFilters() on r.PaymentId equals p.Id
                              where reservationIds.Contains(p.ReservationId) && r.Status != RefundStatuses.Failed
                                    && PaymentStatuses.Counted.Contains(p.Status)
                              group r by p.ReservationId into g
                              select new { g.Key, Sum = g.Sum(x => x.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        return reservationIds.Distinct().ToDictionary(id => id, id => received.GetValueOrDefault(id) - refunded.GetValueOrDefault(id));
    }

    public async Task<decimal> PaidForReservationAsync(Guid reservationId, CancellationToken ct) =>
        (await PaidByReservationAsync([reservationId], ct))[reservationId];

    /// <summary>Money of the booking not on any folio yet (advances), net of refunds from it.</summary>
    public async Task<decimal> OnAccountAsync(Guid reservationId, CancellationToken ct)
    {
        var payments = await db.Payments.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.ReservationId == reservationId && PaymentStatuses.Counted.Contains(p.Status)).Select(p => new { p.Id, p.Amount })
            .ToListAsync(ct);
        if (payments.Count == 0) return 0m;
        var ids = payments.Select(p => p.Id).ToList();
        var allocated = await db.PaymentAllocations.IgnoreQueryFilters().Where(a => ids.Contains(a.PaymentId)).SumAsync(a => (decimal?)a.Amount, ct) ?? 0m;
        var fromAccount = await db.Refunds.IgnoreQueryFilters()
            .Where(r => ids.Contains(r.PaymentId) && r.FolioId == null && r.Status != RefundStatuses.Failed).SumAsync(r => (decimal?)r.Amount, ct) ?? 0m;
        return payments.Sum(p => p.Amount) - allocated - fromAccount;
    }

    /// <summary>The payments and refunds of a folio, oldest first, for the bill.</summary>
    public async Task<List<FolioPaymentDto>> FolioLinesAsync(Guid folioId, CancellationToken ct)
    {
        var allocations = await (from a in db.PaymentAllocations.IgnoreQueryFilters().AsNoTracking()
                                 join p in db.Payments.IgnoreQueryFilters().AsNoTracking() on a.PaymentId equals p.Id
                                 where a.FolioId == folioId
                                 select new { a, p }).ToListAsync(ct);
        var refunds = await (from r in db.Refunds.IgnoreQueryFilters().AsNoTracking()
                             join p in db.Payments.IgnoreQueryFilters().AsNoTracking() on r.PaymentId equals p.Id
                             where r.FolioId == folioId
                             select new { r, p }).ToListAsync(ct);
        var userIds = allocations.Select(x => x.a.AllocatedBy).Concat(refunds.Select(x => x.r.RefundedBy))
            .Where(u => u != null).Select(u => u!.Value).Distinct().ToList();
        var users = await UserNamesAsync(userIds, ct);
        return allocations.Select(x => new FolioPaymentDto(x.p.Id, x.p.Number, null, "PAYMENT", x.p.Method, x.a.Amount, x.a.AllocatedAt,
                    x.a.AllocatedBy is { } u ? users.GetValueOrDefault(u) : null,
                    x.p.Status == PaymentStatuses.Cancelled ? $"Voided: {x.p.VoidReason}" : x.p.Reference,
                    !PaymentStatuses.Counted.Contains(x.p.Status)))
            .Concat(refunds.Select(x => new FolioPaymentDto(x.p.Id, x.p.Number, x.r.Id, "REFUND", x.r.Method, -x.r.Amount, x.r.RefundedAt,
                x.r.RefundedBy is { } u ? users.GetValueOrDefault(u) : null, x.r.Reason, x.r.Status == RefundStatuses.Failed)))
            .OrderBy(l => l.At).ThenBy(l => l.PaymentNumber, StringComparer.Ordinal).ToList();
    }

    public async Task<Dictionary<Guid, string>> UserNamesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        ids.Count == 0 ? [] : await db.Users.IgnoreQueryFilters().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
}
