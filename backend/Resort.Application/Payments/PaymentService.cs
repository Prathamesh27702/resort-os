using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Folios;
using Resort.Application.Reservations;
using Resort.Application.Validation;
using Resort.Contracts;
using Resort.Domain.Folios;
using Resort.Domain.Organizations;
using Resort.Domain.Payments;
using Resort.Domain.Reservations;

namespace Resort.Application.Payments;

/// <summary>
/// Payments (Sprint 10, brief §21): money taken at the desk in cash, UPI, card (the property's own terminal) or bank
/// transfer, put on a booking's folios, refunded with a reason, or voided when entered by mistake. Nothing is edited or
/// deleted. No card data is ever stored (brief §3): the only card detail kept is the terminal's approval code.
///
/// Locks, always in this order: the booking's reservation row (the lock check-in, check-out and folio changes take, so a
/// balance can't change under the check-out rule), then the payment row, then the cash drawer session. Two refunds of the
/// same payment therefore run one after the other, and the second sees the first; the database checks the same limit
/// under its own lock on the payment row (V012).
///
/// Sprint 11: online payments (method ONLINE) are created only by Razorpay's webhook (<see cref="RazorpayWebhookService"/>).
/// They are refunded like any other, and by default back through Razorpay; they are never voided (the money really came).
/// </summary>
public sealed class PaymentService(
    IAppDbContext db, ICurrentUser caller, AuditContext audit, TimeProvider clock, ReservationService reservations,
    CashDrawerService drawers, PaymentLedger ledger, IPaymentGateway gateway)
{
    /// <summary>Booking statuses that take money on account. A folio takes payments while it takes charges.</summary>
    public static readonly string[] TakesPayments =
        [ReservationStatuses.Pending, ReservationStatuses.Confirmed, ReservationStatuses.CheckedIn, ReservationStatuses.CheckedOut];

    public sealed record ListQuery(string? Q, string? Method, string? Status, DateOnly? From, DateOnly? To, Guid? ReservationId, int Page, int PageSize);

    // ------------------------------------------------------------------ queries

    /// <summary>Payments of the property, newest first. From/To: days at the property the money was received. Q: the
    /// payment number, the booking reference, the payment reference or the guest (name, phone).</summary>
    public async Task<PaymentListDto> ListAsync(Guid propertyId, ListQuery q, CancellationToken ct)
    {
        var property = await LoadPropertyAsync(propertyId, ct);
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 200);
        var rows = db.Payments.AsNoTracking().Where(p => p.PropertyId == propertyId);
        if (!string.IsNullOrWhiteSpace(q.Method))
        {
            var m = q.Method.Trim().ToUpperInvariant();
            if (!PaymentMethods.All.Contains(m))
                throw ReservationService.Field("Method", $"'{q.Method}' isn't a payment method. Use {string.Join(", ", PaymentMethods.All)}.");
            rows = rows.Where(p => p.Method == m);
        }
        if (!string.IsNullOrWhiteSpace(q.Status))
        {
            var st = q.Status.Trim().ToUpperInvariant();
            if (!PaymentStatuses.All.Contains(st))
                throw ReservationService.Field("Status", $"'{q.Status}' isn't a payment status. Use {string.Join(", ", PaymentStatuses.All)}.");
            rows = rows.Where(p => p.Status == st);
        }
        if (q.From is { } from) { var start = property.StartOfDay(from); rows = rows.Where(p => p.ReceivedAt >= start); }
        if (q.To is { } to) { var end = property.StartOfDay(to.AddDays(1)); rows = rows.Where(p => p.ReceivedAt < end); }
        if (q.ReservationId is { } rid) rows = rows.Where(p => p.ReservationId == rid);
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var text = q.Q.Trim();
            var code = text.ToUpperInvariant();
            var like = "%" + text.ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            var digits = new string(text.Where(char.IsDigit).ToArray());
            var guestIds = db.Guests.Where(g => EF.Functions.Like(g.SearchText, like) || (digits.Length >= 4 && g.PhoneDigits.Contains(digits)))
                .Select(g => g.Id);
            var reservationIds = db.Reservations.Where(r => r.Reference == code || guestIds.Contains(r.PrimaryGuestId)).Select(r => r.Id);
            rows = rows.Where(p => p.Number == code || p.Reference == text || reservationIds.Contains(p.ReservationId));
        }

        var total = await rows.CountAsync(ct);
        var counted = rows.Where(p => PaymentStatuses.Counted.Contains(p.Status));
        var received = await counted.GroupBy(p => p.Method).Select(g => new { g.Key, Count = g.Count(), Sum = g.Sum(p => p.Amount) }).ToListAsync(ct);
        var refunded = await (from r in db.Refunds.AsNoTracking()
                              join p in counted on r.PaymentId equals p.Id
                              where r.Status != RefundStatuses.Failed
                              group r by p.Method into g
                              select new { g.Key, Sum = g.Sum(r => r.Amount) }).ToListAsync(ct);
        var byMethod = PaymentMethods.All.Where(m => received.Any(x => x.Key == m))
            .Select(m =>
            {
                var rec = received.Single(x => x.Key == m);
                var back = refunded.SingleOrDefault(x => x.Key == m)?.Sum ?? 0m;
                return new PaymentMethodTotalDto(m, rec.Count, rec.Sum, back, rec.Sum - back);
            }).ToList();

        var list = await rows.OrderByDescending(p => p.ReceivedAt).ThenByDescending(p => p.Number).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var ids = list.Select(p => p.Id).ToList();
        var resIds = list.Select(p => p.ReservationId).Distinct().ToList();
        var bookings = await db.Reservations.AsNoTracking().Where(r => resIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var guests = await GuestNamesAsync(list.Select(p => p.GuestId ?? bookings[p.ReservationId].PrimaryGuestId), ct);
        var allocations = await (from a in db.PaymentAllocations.AsNoTracking()
                                 join f in db.Folios.AsNoTracking() on a.FolioId equals f.Id
                                 where ids.Contains(a.PaymentId)
                                 select new { a.PaymentId, a.Amount, f.Number }).ToListAsync(ct);
        var refunds = await db.Refunds.AsNoTracking().Where(r => ids.Contains(r.PaymentId) && r.Status != RefundStatuses.Failed).ToListAsync(ct);
        var users = await ledger.UserNamesAsync(list.Where(p => p.ReceivedBy != null).Select(p => p.ReceivedBy!.Value).Distinct().ToList(), ct);
        var items = list.Select(p =>
        {
            var mine = refunds.Where(r => r.PaymentId == p.Id).ToList();
            var allocated = allocations.Where(a => a.PaymentId == p.Id).Sum(a => a.Amount);
            var folios = allocations.Where(a => a.PaymentId == p.Id).Select(a => a.Number).Distinct().Order(StringComparer.Ordinal).ToList();
            return new PaymentListItemDto(p.Id, p.Number, p.ReservationId, bookings[p.ReservationId].Reference,
                guests.GetValueOrDefault(p.GuestId ?? bookings[p.ReservationId].PrimaryGuestId, ""), p.Method, p.Status, p.Amount,
                mine.Sum(r => r.Amount), Counts(p) ? PaymentMath.OnAccount(p.Amount, allocated, mine.Where(r => r.FolioId == null).Sum(r => r.Amount)) : 0m,
                p.Reference, p.ReceivedAt, p.ReceivedBy is { } u ? users.GetValueOrDefault(u) : null, folios.Count == 0 ? null : string.Join(", ", folios));
        }).ToList();
        var receivedTotal = byMethod.Sum(m => m.Received);
        var refundedTotal = byMethod.Sum(m => m.Refunded);
        return new PaymentListDto(items, page, size, total, byMethod, receivedTotal, refundedTotal, receivedTotal - refundedTotal);
    }

    public async Task<PaymentDto> GetAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        var payment = await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && p.PropertyId == propertyId, ct)
                      ?? throw new NotFoundAppException("Payment", id);
        return await ToDtoAsync(payment, ct);
    }

    // ------------------------------------------------------------------ take a payment

    /// <summary>
    /// Records money received. With a folio, the whole amount goes on it (at most what it owes); without one the money
    /// stays on account for the booking (an advance) until it is allocated. Cash goes into the open drawer session. Under
    /// an Idempotency-Key: a retry with the same key and body returns the first payment, without taking it twice.
    /// </summary>
    public async Task<(PaymentDto Payment, bool Replayed)> CreateAsync(Guid propertyId, string? idempotencyKey, CreatePaymentRequest r,
        CancellationToken ct)
    {
        var key = Idempotency.RequireKey(idempotencyKey);
        var property = await LoadPropertyAsync(propertyId, ct);
        var orgId = caller.RequireOrganizationId();
        var scope = $"POST /api/v1/properties/{propertyId}/payments";
        var method = r.Method!.Trim().ToUpperInvariant();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await Idempotency.ClaimAsync(db, orgId, caller.UserId, scope, key, Idempotency.Hash(r), "payment", ct) is { } stored)
            return (JsonSerializer.Deserialize<PaymentDto>(stored, Idempotency.Json)!, true);

        Folio? folio = null;
        Guid reservationId;
        if (r.FolioId is { } folioId)
        {
            folio = await db.Folios.AsNoTracking().SingleOrDefaultAsync(f => f.Id == folioId && f.PropertyId == propertyId, ct)
                    ?? throw ReservationService.Field(nameof(r.FolioId), "That folio isn't at this property. Reload and choose again.");
            if (r.ReservationId is { } given && given != folio.ReservationId)
                throw ReservationService.Field(nameof(r.FolioId), $"{folio.Number} is the bill of another booking. Choose a folio of this booking.");
            reservationId = folio.ReservationId;
        }
        else reservationId = r.ReservationId!.Value;

        var reservation = await LockReservationAsync(propertyId, reservationId, nameof(r.ReservationId), ct);
        if (folio is null && !TakesPayments.Contains(reservation.Status))
            throw new BusinessRuleAppException("BOOKING_CLOSED",
                $"{reservation.Reference} is {ReservationStatuses.Words(reservation.Status)}, so no payment can be taken for it.");
        if (folio is not null)
        {
            if (!FolioPosting.Billable.Contains(reservation.Status))
                throw new BusinessRuleAppException("FOLIO_CLOSED",
                    $"{reservation.Reference} is {ReservationStatuses.Words(reservation.Status)}, so {folio.Number} takes no payments.");
            var owed = await FolioBalanceAsync(folio.Id, ct);
            if (r.Amount > owed)
                throw ReservationService.Field(nameof(r.Amount), owed <= 0
                    ? $"{folio.Number} owes nothing{(owed < 0 ? $" (it is {Money.Rupees(-owed)} in credit)" : "")}. To take an advance, record it on account for the booking instead."
                    : $"{folio.Number} owes {Money.Rupees(owed)}, so it can take at most that. Take {Money.Rupees(owed)}, or record the rest on account for the booking.");
        }

        var number = await NextNumberAsync(propertyId, ct);
        var now = clock.GetUtcNow();
        var payment = new Payment
        {
            OrganizationId = reservation.OrganizationId,
            PropertyId = propertyId,
            Number = number,
            ReservationId = reservation.Id,
            GuestId = folio?.BillToGuestId ?? reservation.PrimaryGuestId,
            Method = method,
            Status = PaymentStatuses.Captured,
            Amount = r.Amount,
            Currency = reservation.Currency,
            Reference = ReservationService.Clean(r.PaymentReference),
            Note = ReservationService.Clean(r.Note),
            ReceivedAt = now,
            ReceivedBy = caller.UserId,
            CapturedAt = now,
        };
        if (method == PaymentMethods.Cash)
        {
            var session = await drawers.RequireOpenSessionAsync(property, nameof(r.Method), "a payment", ct);
            payment.CashDrawerSessionId = session.Id;
            drawers.AddMovement(session, DrawerMovementTypes.Payment, payment.Amount, $"{payment.Number} · {reservation.Reference}", payment.Id, null);
        }
        db.Payments.Add(payment);
        if (folio is not null)
            db.PaymentAllocations.Add(Allocation(payment, folio.Id, payment.Amount, now));
        await db.SaveChangesAsync(ct);

        var dto = await ToDtoAsync(payment, ct);
        await Idempotency.CompleteAsync(db, orgId, scope, key, 201, dto, payment.Id, ct);
        await tx.CommitAsync(ct);
        return (dto, false);
    }

    // ------------------------------------------------------------------ allocate money on account

    /// <summary>Puts money that is on account onto folios of the same booking, each at most what that folio owes.</summary>
    public async Task<PaymentDto> AllocateAsync(Guid propertyId, Guid id, AllocatePaymentRequest r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (payment, reservation) = await LockAsync(propertyId, id, ct);
        RequireRefundable(payment, "allocated");
        var onAccount = await OnAccountAsync(payment, ct);
        var lines = r.Allocations!;
        var sum = lines.Sum(l => l.Amount);
        if (sum > onAccount)
            throw ReservationService.Field(nameof(r.Allocations), onAccount == 0
                ? $"All of {payment.Number} is already on folios or refunded; nothing is left on account to allocate."
                : $"{payment.Number} has {Money.Rupees(onAccount)} on account; {Money.Rupees(sum)} can't be allocated. Allocate at most {Money.Rupees(onAccount)}.");

        var folioIds = lines.Select(l => l.FolioId).ToList();
        var folios = await db.Folios.AsNoTracking().Where(f => folioIds.Contains(f.Id) && f.ReservationId == reservation.Id).ToDictionaryAsync(f => f.Id, ct);
        if (folios.Count != folioIds.Distinct().Count())
            throw ReservationService.Field(nameof(r.Allocations),
                $"One of the folios isn't a bill of {reservation.Reference}. A payment can only go on the folios of its own booking.");
        if (!FolioPosting.Billable.Contains(reservation.Status))
            throw new BusinessRuleAppException("FOLIO_CLOSED",
                $"{reservation.Reference} is {ReservationStatuses.Words(reservation.Status)}, so its folios take no payments. Refund the money instead.");
        var balances = await FolioBalancesAsync(folioIds, ct);
        var now = clock.GetUtcNow();
        foreach (var line in lines)
        {
            var folio = folios[line.FolioId];
            if (line.Amount > balances[folio.Id])
                throw ReservationService.Field(nameof(r.Allocations), balances[folio.Id] <= 0
                    ? $"{folio.Number} owes nothing, so no payment can go on it."
                    : $"{folio.Number} owes {Money.Rupees(balances[folio.Id])}; {Money.Rupees(line.Amount)} would put it in credit. Allocate at most {Money.Rupees(balances[folio.Id])}.");
            db.PaymentAllocations.Add(Allocation(payment, folio.Id, line.Amount, now));
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ToDtoAsync(payment, ct);
    }

    // ------------------------------------------------------------------ refund

    /// <summary>
    /// Gives money back (payment.refund, checked by the endpoint) with a reason. At most what is left to refund, checked
    /// under the payment's lock. Without a folio the money comes off what is on account first, then off the folios the
    /// payment is on, in the order it was put on them. Cash comes out of the open drawer, which must hold enough.
    /// </summary>
    public async Task<(PaymentDto Payment, bool Replayed)> RefundAsync(Guid propertyId, Guid id, string? idempotencyKey, RefundPaymentRequest r,
        CancellationToken ct)
    {
        var key = Idempotency.RequireKey(idempotencyKey);
        var property = await LoadPropertyAsync(propertyId, ct);
        var orgId = caller.RequireOrganizationId();
        var scope = $"POST /api/v1/properties/{propertyId}/payments/{id}/refund";
        var reason = ReservationService.Clean(r.Reason)!;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await Idempotency.ClaimAsync(db, orgId, caller.UserId, scope, key, Idempotency.Hash(r), "refund", ct) is { } stored)
            return (JsonSerializer.Deserialize<PaymentDto>(stored, Idempotency.Json)!, true);

        var (payment, _) = await LockAsync(propertyId, id, ct);
        RequireRefundable(payment, "refunded");
        var refunds = await db.Refunds.AsNoTracking().Where(x => x.PaymentId == payment.Id && x.Status != RefundStatuses.Failed).ToListAsync(ct);
        var refunded = refunds.Sum(x => x.Amount);
        var left = PaymentMath.Refundable(payment.Amount, refunded);
        if (r.Amount > left)
            throw ReservationService.Field(nameof(r.Amount),
                $"{payment.Number} was {Money.Rupees(payment.Amount)} and {Money.Rupees(refunded)} of it is already refunded, so at most {Money.Rupees(left)} can be refunded.");

        // where the money comes off: on account first, then the payment's folios in order (or only the folio asked for)
        var allocations = await (from a in db.PaymentAllocations.AsNoTracking()
                                 join f in db.Folios.AsNoTracking() on a.FolioId equals f.Id
                                 where a.PaymentId == payment.Id
                                 orderby a.AllocatedAt, a.Id
                                 select new { a.FolioId, a.Amount, f.Number }).ToListAsync(ct);
        var parts = new List<(Guid? FolioId, decimal Amount)>();
        if (r.FolioId is { } fid)
        {
            var on = allocations.Where(a => a.FolioId == fid).ToList();
            if (on.Count == 0)
                throw ReservationService.Field(nameof(r.FolioId), $"{payment.Number} isn't on that folio. Choose one of the folios it paid, or leave it empty.");
            var onFolio = on.Sum(a => a.Amount) - refunds.Where(x => x.FolioId == fid).Sum(x => x.Amount);
            if (r.Amount > onFolio)
                throw ReservationService.Field(nameof(r.Amount),
                    $"{payment.Number} has {Money.Rupees(onFolio)} on {on[0].Number}, so at most that can be refunded from it.");
            parts.Add((fid, r.Amount));
        }
        else
        {
            var rest = r.Amount;
            var account = PaymentMath.OnAccount(payment.Amount, allocations.Sum(a => a.Amount), refunds.Where(x => x.FolioId == null).Sum(x => x.Amount));
            if (account > 0) { var take = Math.Min(account, rest); parts.Add((null, take)); rest -= take; }
            foreach (var g in allocations.GroupBy(a => a.FolioId))
            {
                if (rest == 0) break;
                var onFolio = g.Sum(a => a.Amount) - refunds.Where(x => x.FolioId == g.Key).Sum(x => x.Amount);
                if (onFolio <= 0) continue;
                var take = Math.Min(onFolio, rest);
                parts.Add((g.Key, take));
                rest -= take;
            }
        }

        var method = string.IsNullOrWhiteSpace(r.Method) ? payment.Method : r.Method.Trim().ToUpperInvariant();
        if (method == PaymentMethods.Online && payment.Method != PaymentMethods.Online)
            throw ReservationService.Field(nameof(r.Method),
                $"{payment.Number} was paid {PaymentMethods.Words(payment.Method).ToLowerInvariant()}, not online, so it can't go back through Razorpay. " +
                $"Choose {string.Join(", ", PaymentMethods.Desk.Select(PaymentMethods.Words))}.");
        CashDrawerSession? session = null;
        if (method == PaymentMethods.Cash)
        {
            session = await drawers.RequireOpenSessionAsync(property, nameof(r.Method), "a refund", ct);
            var inDrawer = await drawers.ExpectedAsync(session.Id, ct);
            if (r.Amount > inDrawer)
                throw ReservationService.Field(nameof(r.Method),
                    $"The drawer should hold {Money.Rupees(inDrawer)}, less than {Money.Rupees(r.Amount)}. Refund by another method, or add cash to the drawer first.");
        }

        var reservation = await db.Reservations.AsNoTracking().SingleAsync(x => x.Id == payment.ReservationId, ct);
        var now = clock.GetUtcNow();
        var ids = parts.Select(_ => Guid.CreateVersion7()).ToList();
        GatewayRefund? online = null;
        if (method == PaymentMethods.Online)
            online = await RefundOnlineAsync(payment, r.Amount, ids[0], reason, ct);
        foreach (var ((folioId, amount), refundId) in parts.Zip(ids))
        {
            var refund = new Refund
            {
                Id = refundId,
                Status = online is null ? RefundStatuses.Processed : online.Status == "processed" ? RefundStatuses.Processed : RefundStatuses.Pending,
                GatewayRefundId = online?.Id,
                OrganizationId = payment.OrganizationId,
                PropertyId = payment.PropertyId,
                PaymentId = payment.Id,
                FolioId = folioId,
                Amount = amount,
                Method = method,
                Reason = reason,
                Reference = ReservationService.Clean(r.Reference),
                CashDrawerSessionId = session?.Id,
                RefundedAt = now,
                RefundedBy = caller.UserId,
            };
            db.Refunds.Add(refund);
            if (session is not null)
                drawers.AddMovement(session, DrawerMovementTypes.Refund, -amount, $"Refund of {payment.Number} · {reservation.Reference}", null, refund.Id);
        }
        audit.Reason = reason;
        await db.SaveChangesAsync(ct);   // the refunds first: the database checks them against the payment while it is still captured
        payment.Status = PaymentMath.StatusAfterRefund(payment.Amount, refunded + r.Amount);
        await db.SaveChangesAsync(ct);

        var dto = await ToDtoAsync(payment, ct);
        await Idempotency.CompleteAsync(db, orgId, scope, key, 200, dto, payment.Id, ct);
        await tx.CommitAsync(ct);
        return (dto, false);
    }

    /// <summary>Sends the money back through Razorpay (one Razorpay refund, however many rows it is split into here). Our
    /// first refund row's id is the receipt. A clear refusal means nothing went back; an unclear failure (no answer) might
    /// have refunded, so the message says to check Razorpay's dashboard before trying again.</summary>
    private async Task<GatewayRefund> RefundOnlineAsync(Payment payment, decimal amount, Guid receipt, string reason, CancellationToken ct)
    {
        try
        {
            var refund = await gateway.RefundAsync(payment.Reference!, PaymentMath.ToPaise(amount), receipt.ToString("N"),
                new Dictionary<string, string> { ["payment"] = payment.Number, ["reason"] = reason.Length > 200 ? reason[..200] : reason }, ct);
            if (refund.Status == "failed")
                throw new BusinessRuleAppException("GATEWAY_REFUND_FAILED",
                    $"Razorpay refused to refund {Money.Rupees(amount)} of {payment.Number}, so nothing was refunded. Refund by another method, or try again later.");
            return refund;
        }
        catch (PaymentGatewayException ex) when (ex.StatusCode is null or >= 500)
        {
            throw new BusinessRuleAppException("GATEWAY_REFUND_UNCLEAR",
                $"{ex.Message} The refund may or may not have gone through: look at {payment.Reference} in Razorpay's dashboard before trying again. Nothing was recorded here.");
        }
        catch (PaymentGatewayException ex)
        {
            throw new BusinessRuleAppException("GATEWAY_REFUND_FAILED", $"{ex.Message} Nothing was refunded.");
        }
    }

    /// <summary>The property's next payment number: P00001, P00002 … (a counter row, so two at once get different ones).</summary>
    internal async Task<string> NextNumberAsync(Guid propertyId, CancellationToken ct)
    {
        var number = (await db.Database.SqlQuery<long>(
            $"""
             INSERT INTO property_counters (property_id, name, last_value) VALUES ({propertyId}, 'payment', 1)
             ON CONFLICT (property_id, name) DO UPDATE SET last_value = property_counters.last_value + 1
             RETURNING last_value AS "Value"
             """).ToListAsync(ct)).Single();
        return $"P{number:D5}";
    }

    // ------------------------------------------------------------------ void

    /// <summary>Voids a payment entered by mistake (payment.void, checked by the endpoint) with a reason: it stays on record,
    /// marked voided, and counts for nothing. Only while nothing of it is refunded; cash only while its drawer shift is
    /// open (the cash goes back out of the drawer's ledger). After that, refund it instead.</summary>
    public async Task<PaymentDto> VoidAsync(Guid propertyId, Guid id, VoidPaymentRequest r, CancellationToken ct)
    {
        var reason = ReservationService.Clean(r.Reason)!;
        var property = await LoadPropertyAsync(propertyId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (payment, reservation) = await LockAsync(propertyId, id, ct);
        if (await CantVoidAsync(property, payment, lockSession: true, ct) is { } why)
        {
            if (payment.Status == PaymentStatuses.Cancelled)
                throw new ConflictAppException("ALREADY_VOIDED", why);
            throw new BusinessRuleAppException("NOT_VOIDABLE", why);
        }
        if (payment.CashDrawerSessionId is { } sid)
        {
            var session = (await drawers.LockSessionIfOpenAsync(sid, ct))!;
            drawers.AddMovement(session, DrawerMovementTypes.Void, -payment.Amount, $"Void of {payment.Number} · {reservation.Reference}", payment.Id, null);
        }
        payment.Status = PaymentStatuses.Cancelled;
        payment.VoidedAt = clock.GetUtcNow();
        payment.VoidedBy = caller.UserId;
        payment.VoidReason = reason;
        audit.Reason = reason;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ToDtoAsync(payment, ct);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>What a folio owes now: its lines − what it has been paid.</summary>
    public async Task<decimal> FolioBalanceAsync(Guid folioId, CancellationToken ct) => (await FolioBalancesAsync([folioId], ct))[folioId];

    private async Task<Dictionary<Guid, decimal>> FolioBalancesAsync(IReadOnlyCollection<Guid> folioIds, CancellationToken ct)
    {
        var totals = await db.FolioItems.AsNoTracking().Where(i => folioIds.Contains(i.FolioId)).GroupBy(i => i.FolioId)
            .Select(g => new { g.Key, Sum = g.Sum(i => i.Total) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        var paid = await ledger.PaidByFolioAsync(folioIds, ct);
        return folioIds.Distinct().ToDictionary(id => id, id => totals.GetValueOrDefault(id) - paid[id]);
    }

    private static bool Counts(Payment p) => PaymentStatuses.Counted.Contains(p.Status);

    private static void RequireRefundable(Payment payment, string what)
    {
        if (PaymentStatuses.Refundable.Contains(payment.Status)) return;
        var message = payment.Status switch
        {
            PaymentStatuses.Refunded => $"{payment.Number} is already refunded in full, so nothing of it can be {what}.",
            PaymentStatuses.Cancelled => $"{payment.Number} was voided ({payment.VoidReason}), so nothing of it can be {what}.",
            _ => $"{payment.Number} is {PaymentStatuses.Words(payment.Status)}, so the money isn't in hand and can't be {what}.",
        };
        throw new BusinessRuleAppException("PAYMENT_NOT_CAPTURED", message);
    }

    /// <summary>Why a payment can't be voided, or null if it can.</summary>
    private async Task<string?> CantVoidAsync(Property property, Payment p, bool lockSession, CancellationToken ct)
    {
        if (p.Status == PaymentStatuses.Cancelled)
        {
            var who = p.VoidedBy is { } u ? (await ledger.UserNamesAsync([u], ct)).GetValueOrDefault(u) : null;
            return $"{p.Number} was already voided at {CashDrawerService.When(property, p.VoidedAt!.Value)}{(who is null ? "" : $" by {who}")} " +
                   $"(reason: {p.VoidReason}). Reload to see it.";
        }
        if (p.Method == PaymentMethods.Online)
            return $"{p.Number} was paid online through Razorpay, so the money really came in and it can't be voided. Refund it instead.";
        if (p.Status != PaymentStatuses.Captured)
            return p.Status is PaymentStatuses.PartiallyRefunded or PaymentStatuses.Refunded
                ? $"Part of {p.Number} has been refunded, so it can't be voided. Refund the rest instead."
                : $"{p.Number} is {PaymentStatuses.Words(p.Status)}; only a captured payment can be voided.";
        if (p.CashDrawerSessionId is { } sid)
        {
            var open = lockSession
                ? await drawers.LockSessionIfOpenAsync(sid, ct) is not null
                : await db.CashDrawerSessions.AsNoTracking().AnyAsync(s => s.Id == sid && s.Status == DrawerSessionStatuses.Open, ct);
            if (!open)
            {
                var closedAt = await db.CashDrawerSessions.AsNoTracking().Where(s => s.Id == sid).Select(s => s.ClosedAt).SingleAsync(ct);
                return $"{p.Number} was cash in the drawer shift closed at {CashDrawerService.When(property, closedAt!.Value)}, and that cash has been " +
                       "counted. Refund it instead.";
            }
        }
        return null;
    }

    private async Task<decimal> OnAccountAsync(Payment p, CancellationToken ct)
    {
        var allocated = await db.PaymentAllocations.Where(a => a.PaymentId == p.Id).SumAsync(a => (decimal?)a.Amount, ct) ?? 0m;
        var fromAccount = await db.Refunds.Where(x => x.PaymentId == p.Id && x.FolioId == null && x.Status != RefundStatuses.Failed)
            .SumAsync(x => (decimal?)x.Amount, ct) ?? 0m;
        return PaymentMath.OnAccount(p.Amount, allocated, fromAccount);
    }

    internal PaymentAllocation Allocation(Payment payment, Guid folioId, decimal amount, DateTimeOffset at) => new()
    {
        OrganizationId = payment.OrganizationId,
        PropertyId = payment.PropertyId,
        PaymentId = payment.Id,
        FolioId = folioId,
        Amount = amount,
        AllocatedAt = at,
        AllocatedBy = caller.UserId,
    };

    private async Task<Reservation> LockReservationAsync(Guid propertyId, Guid reservationId, string field, CancellationToken ct)
    {
        if (!await db.Reservations.AsNoTracking().AnyAsync(x => x.Id == reservationId && x.PropertyId == propertyId, ct))
            throw ReservationService.Field(field, "That booking isn't at this property. Search for it again.");
        return await reservations.LockAsync(propertyId, reservationId, ct);
    }

    /// <summary>Locks the payment's booking, then the payment row, and loads both (the payment tracked).</summary>
    private async Task<(Payment Payment, Reservation Reservation)> LockAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        var reservationId = await db.Payments.AsNoTracking().Where(p => p.Id == id && p.PropertyId == propertyId).Select(p => (Guid?)p.ReservationId)
                                .SingleOrDefaultAsync(ct)
                            ?? throw new NotFoundAppException("Payment", id);
        var reservation = await reservations.LockAsync(propertyId, reservationId, ct);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM payments WHERE id = {id} FOR UPDATE", ct);
        return (await db.Payments.SingleAsync(p => p.Id == id, ct), reservation);
    }

    private async Task<Property> LoadPropertyAsync(Guid propertyId, CancellationToken ct) =>
        await db.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == propertyId, ct) ?? throw new NotFoundAppException("Property", propertyId);

    private async Task<Dictionary<Guid, string>> GuestNamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Guests.AsNoTracking().Where(g => list.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.FullName, ct);
    }

    private async Task<PaymentDto> ToDtoAsync(Payment p, CancellationToken ct)
    {
        var property = await db.Properties.AsNoTracking().SingleAsync(x => x.Id == p.PropertyId, ct);
        var reservation = await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == p.ReservationId, ct);
        var guests = await GuestNamesAsync([p.GuestId ?? reservation.PrimaryGuestId], ct);
        var folios = await db.Folios.AsNoTracking().Where(f => f.ReservationId == reservation.Id)
            .OrderByDescending(f => f.IsPrimary).ThenBy(f => f.Number).ToListAsync(ct);
        var numbers = folios.ToDictionary(f => f.Id);
        var allocations = await db.PaymentAllocations.AsNoTracking().Where(a => a.PaymentId == p.Id).OrderBy(a => a.AllocatedAt).ThenBy(a => a.Id).ToListAsync(ct);
        var refunds = await db.Refunds.AsNoTracking().Where(r => r.PaymentId == p.Id).OrderBy(r => r.RefundedAt).ThenBy(r => r.Id).ToListAsync(ct);
        var live = refunds.Where(r => r.Status != RefundStatuses.Failed).ToList();
        var users = await ledger.UserNamesAsync(new[] { p.ReceivedBy, p.VoidedBy }.Concat(allocations.Select(a => a.AllocatedBy))
            .Concat(refunds.Select(r => r.RefundedBy)).Where(u => u != null).Select(u => u!.Value).Distinct().ToList(), ct);
        string? Name(Guid? u) => u is { } x ? users.GetValueOrDefault(x) : null;
        var balances = await FolioBalancesAsync(folios.Select(f => f.Id).ToList(), ct);
        var allocated = allocations.Sum(a => a.Amount);
        var refunded = live.Sum(r => r.Amount);
        var cantVoid = await CantVoidAsync(property, p, lockSession: false, ct);

        return new PaymentDto(p.Id, p.Number, reservation.Id, reservation.Reference, guests.GetValueOrDefault(p.GuestId ?? reservation.PrimaryGuestId, ""),
            p.Method, p.Status, p.Amount, allocated, refunded,
            Counts(p) ? PaymentMath.OnAccount(p.Amount, allocated, live.Where(r => r.FolioId == null).Sum(r => r.Amount)) : 0m,
            PaymentStatuses.Refundable.Contains(p.Status) ? PaymentMath.Refundable(p.Amount, refunded) : 0m, p.Currency,
            p.Reference, p.Note, p.CashDrawerSessionId, p.ReceivedAt, Name(p.ReceivedBy), p.VoidedAt, Name(p.VoidedBy), p.VoidReason,
            cantVoid is null, cantVoid,
            allocations.GroupBy(a => a.FolioId).Select(g =>
            {
                var f = numbers[g.Key];
                var first = g.First();
                return new PaymentAllocationDto(first.Id, f.Id, f.Number, f.Name, g.Sum(a => a.Amount),
                    live.Where(r => r.FolioId == f.Id).Sum(r => r.Amount), first.AllocatedAt, Name(first.AllocatedBy));
            }).ToList(),
            refunds.Select(r => new RefundDto(r.Id, p.Id, p.Number, r.FolioId, r.FolioId is { } fid ? numbers[fid].Number : null, r.Amount, r.Method,
                r.Status, r.Reason, r.Reference, r.RefundedAt, Name(r.RefundedBy))).ToList(),
            folios.Select(f => new PaymentFolioDto(f.Id, f.Number, f.Name, f.IsPrimary, balances[f.Id])).ToList(),
            p.GatewayMethod, p.GatewayOrderId);
    }
}
