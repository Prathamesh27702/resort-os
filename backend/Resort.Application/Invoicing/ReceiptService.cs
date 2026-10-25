using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Contracts;
using Resort.Domain.Invoicing;
using Resort.Domain.Organizations;
using Resort.Domain.Payments;
using Resort.Domain.Reservations;

namespace Resort.Application.Invoicing;

/// <summary>
/// Payment receipts (Sprint 12): one numbered receipt per payment that counts, issued when the guest checks out (for
/// every payment of the stay) or when reception prints one. Issuing is idempotent: a payment's receipt is created once
/// (unique in the database, under a lock on the payment row) and then returned as it is. A receipt is never changed; if
/// its payment is voided later, the receipt says so.
/// </summary>
public sealed class ReceiptService(IAppDbContext db, ICurrentUser caller, TimeProvider clock, FolioBilling billing, InvoicePrinter printer)
{
    /// <summary>The payment's receipt, issued now if it has none.</summary>
    public async Task<ReceiptDto> IssueForPaymentAsync(Guid propertyId, Guid paymentId, CancellationToken ct)
    {
        var property = await db.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == propertyId, ct)
                       ?? throw new NotFoundAppException("Property", propertyId);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM payments WHERE id = {paymentId} AND property_id = {propertyId} FOR UPDATE", ct);
        var payment = await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.Id == paymentId && p.PropertyId == propertyId, ct)
                      ?? throw new NotFoundAppException("Payment", paymentId);
        var receipt = await db.Receipts.AsNoTracking().SingleOrDefaultAsync(r => r.PaymentId == payment.Id, ct);
        if (receipt is null)
        {
            if (!PaymentStatuses.Counted.Contains(payment.Status))
                throw new BusinessRuleAppException("NO_RECEIPT_FOR_PAYMENT", payment.Status == PaymentStatuses.Cancelled
                    ? $"{payment.Number} was voided ({payment.VoidReason}), so it gets no receipt."
                    : $"{payment.Number} is {PaymentStatuses.Words(payment.Status)}: the money hasn't arrived, so there is nothing to give a receipt for yet.");
            var reservation = await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == payment.ReservationId, ct);
            receipt = await AddAsync(property, reservation, payment, ct);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return (await DtosAsync([receipt], ct)).Single();
    }

    /// <summary>Receipts for every payment of the booking that counts and has none (check-out). Not saved.</summary>
    public async Task IssueMissingAsync(Property property, Reservation reservation, CancellationToken ct)
    {
        var payments = await db.Payments.IgnoreQueryFilters()
            .Where(p => p.ReservationId == reservation.Id && PaymentStatuses.Counted.Contains(p.Status)
                        && !db.Receipts.IgnoreQueryFilters().Any(r => r.PaymentId == p.Id))
            .OrderBy(p => p.ReceivedAt).ThenBy(p => p.Number).ToListAsync(ct);
        foreach (var payment in payments) await AddAsync(property, reservation, payment, ct);
        if (payments.Count > 0) await db.SaveChangesAsync(ct);
    }

    public async Task<List<ReceiptDto>> ForReservationAsync(Guid reservationId, CancellationToken ct) =>
        await DtosAsync(await db.Receipts.AsNoTracking().Where(r => r.ReservationId == reservationId).OrderBy(r => r.Number).ToListAsync(ct), ct);

    public async Task<PrintDocument> PdfAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        var receipt = await db.Receipts.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id && r.PropertyId == propertyId, ct)
                      ?? throw new NotFoundAppException("Receipt", id);
        var property = await db.Properties.AsNoTracking().SingleAsync(p => p.Id == propertyId, ct);
        var registration = await db.GstRegistrations.AsNoTracking()
            .Where(r => r.PropertyId == propertyId && r.EffectiveFrom <= receipt.ReceiptDate && (r.EffectiveTo == null || r.EffectiveTo >= receipt.ReceiptDate))
            .SingleOrDefaultAsync(ct);
        var dto = (await DtosAsync([receipt], ct)).Single();
        var invoices = await db.Invoices.AsNoTracking().Where(i => i.ReservationId == receipt.ReservationId).OrderBy(i => i.Number)
            .Select(i => i.Number).ToListAsync(ct);
        return printer.Receipt(dto, property, registration, invoices);
    }

    private async Task<Receipt> AddAsync(Property property, Reservation reservation, Payment payment, CancellationToken ct)
    {
        var today = property.LocalDate(clock.GetUtcNow());
        var (seriesId, number, fy) = await billing.NextNumberAsync(property, DocumentTypes.Receipt, today, ct);
        var payerId = payment.GuestId ?? reservation.PrimaryGuestId;
        var payer = await db.Guests.IgnoreQueryFilters().AsNoTracking().Where(g => g.Id == payerId).Select(g => new { g.FirstName, g.LastName })
            .SingleAsync(ct);
        var receipt = new Receipt
        {
            OrganizationId = payment.OrganizationId,
            PropertyId = payment.PropertyId,
            SeriesId = seriesId,
            Number = number,
            FinancialYear = fy,
            ReceiptDate = today,
            PaymentId = payment.Id,
            ReservationId = payment.ReservationId,
            ReceivedFrom = string.IsNullOrWhiteSpace(payer.LastName) ? payer.FirstName : $"{payer.FirstName} {payer.LastName}",
            Amount = payment.Amount,
            Method = payment.Method,
            Currency = payment.Currency,
            IssuedAt = clock.GetUtcNow(),
            IssuedBy = caller.UserId,
        };
        db.Receipts.Add(receipt);
        return receipt;
    }

    private async Task<List<ReceiptDto>> DtosAsync(List<Receipt> receipts, CancellationToken ct)
    {
        var paymentIds = receipts.Select(r => r.PaymentId).ToList();
        var payments = await db.Payments.IgnoreQueryFilters().AsNoTracking().Where(p => paymentIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var reservationIds = receipts.Select(r => r.ReservationId).Distinct().ToList();
        var references = await db.Reservations.IgnoreQueryFilters().AsNoTracking().Where(r => reservationIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.Reference, ct);
        var userIds = receipts.Where(r => r.IssuedBy != null).Select(r => r.IssuedBy!.Value).Distinct().ToList();
        var users = await db.Users.IgnoreQueryFilters().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        return receipts.Select(r =>
        {
            var p = payments[r.PaymentId];
            return new ReceiptDto(r.Id, r.Number, r.ReceiptDate, p.Id, p.Number, r.ReservationId, references[r.ReservationId], r.ReceivedFrom, r.Amount,
                r.Method, p.Reference, p.ReceivedAt, p.Status, p.Status == PaymentStatuses.Cancelled, r.IssuedAt,
                r.IssuedBy is { } u ? users.GetValueOrDefault(u) : null);
        }).ToList();
    }
}
