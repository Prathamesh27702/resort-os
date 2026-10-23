using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Reservations;
using Resort.Application.Validation;
using Resort.Contracts;
using Resort.Domain.Payments;
using Resort.Domain.Reservations;

namespace Resort.Application.Payments;

/// <summary>
/// Payment links (Sprint 11, brief §22; docs/RAZORPAY.md). Staff create a link for a booking and an amount: an order at
/// Razorpay and a private page /pay/&lt;token&gt; the guest opens to pay it with Razorpay Checkout (UPI, cards, netbanking).
/// The guest's page only ever shows the status: the payment is recorded, the link marked paid and a pending booking
/// confirmed only by Razorpay's webhook (<see cref="RazorpayWebhookService"/>).
///
/// The pay page is anonymous: it finds the link by its token (32 random bytes) across tenants and then acts for the
/// link's organisation (<see cref="SystemActor"/>). It shows the property, the booking reference, the guest's first name,
/// the dates and the amount, and nothing else.
/// </summary>
public sealed class PaymentLinkService(
    IAppDbContext db, ICurrentUser caller, SystemActor actor, AuditContext audit, TimeProvider clock, ReservationService reservations,
    PaymentLedger ledger, IPaymentGateway gateway)
{
    /// <summary>How long after the guest's browser says it paid the page shows "confirming" while waiting for the webhook.</summary>
    public static readonly TimeSpan ConfirmingFor = TimeSpan.FromMinutes(30);

    // ------------------------------------------------------------------ staff

    /// <summary>
    /// Creates a link for <see cref="CreatePaymentLinkRequest.Amount"/> under an Idempotency-Key (a retry returns the
    /// first link and makes no second order). At most what the booking still owes, less what open links already ask for,
    /// so two links can't both be paid for the same money.
    /// </summary>
    public async Task<(PaymentLinkDto Link, bool Replayed)> CreateAsync(Guid propertyId, string? idempotencyKey, CreatePaymentLinkRequest r,
        CancellationToken ct)
    {
        var key = Idempotency.RequireKey(idempotencyKey);
        var property = await db.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == propertyId, ct)
                       ?? throw new NotFoundAppException("Property", propertyId);
        var orgId = caller.RequireOrganizationId();
        var scope = $"POST /api/v1/properties/{propertyId}/payments/razorpay/orders";

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await Idempotency.ClaimAsync(db, orgId, caller.UserId, scope, key, Idempotency.Hash(r), "payment link", ct) is { } stored)
            return (JsonSerializer.Deserialize<PaymentLinkDto>(stored, Idempotency.Json)!, true);

        if (!await db.Reservations.AsNoTracking().AnyAsync(x => x.Id == r.ReservationId && x.PropertyId == propertyId, ct))
            throw ReservationService.Field(nameof(r.ReservationId), "That booking isn't at this property. Search for it again.");
        var reservation = await reservations.LockAsync(propertyId, r.ReservationId, ct);
        var sums = await SumsAsync(reservation, ct);
        if (!PaymentService.TakesPayments.Contains(reservation.Status))
            throw new BusinessRuleAppException("BOOKING_CLOSED", WhyNot(reservation, sums)!);
        var room = sums.Owed - sums.Open;
        if (r.Amount > room)
            throw ReservationService.Field(nameof(r.Amount), sums.Owed <= 0
                ? $"{reservation.Reference} is paid in full ({Money.Rupees(sums.Paid)} of {Money.Rupees(sums.Due)}), so there is nothing to ask for."
                : sums.Open > 0
                    ? $"{reservation.Reference} still owes {Money.Rupees(sums.Owed)} and open links already ask for {Money.Rupees(sums.Open)}, " +
                      (room > 0 ? $"so a new link can be for at most {Money.Rupees(room)}. " : "so there is nothing left to ask for. ") +
                      "Cancel an open link to ask for more."
                    : $"{reservation.Reference} owes {Money.Rupees(sums.Owed)}, so the link can be for at most that.");

        var link = new PaymentGatewayOrder
        {
            OrganizationId = reservation.OrganizationId,
            PropertyId = propertyId,
            ReservationId = reservation.Id,
            Amount = r.Amount,
            Currency = reservation.Currency,
            PublicToken = NewToken(),
            Note = ReservationService.Clean(r.Note),
        };
        GatewayOrder order;
        try
        {
            order = await gateway.CreateOrderAsync(PaymentMath.ToPaise(r.Amount), reservation.Currency, link.Id.ToString("N"),
                new Dictionary<string, string> { ["booking"] = reservation.Reference, ["property"] = property.Code, ["link"] = link.Id.ToString() }, ct);
        }
        catch (PaymentGatewayException ex)
        {
            throw new BusinessRuleAppException("GATEWAY_UNAVAILABLE", $"{ex.Message} No link was created; try again in a minute.");
        }
        link.GatewayOrderId = order.Id;
        db.PaymentGatewayOrders.Add(link);
        await db.SaveChangesAsync(ct);

        var dto = await ToDtoAsync(link, withEvents: false, ct);
        await Idempotency.CompleteAsync(db, orgId, scope, key, 201, dto, link.Id, ct);
        await tx.CommitAsync(ct);
        return (dto, false);
    }

    /// <summary>A booking's links, newest first, and what it still owes.</summary>
    public async Task<BookingPaymentLinksDto> ListAsync(Guid propertyId, Guid? reservationId, CancellationToken ct)
    {
        if (reservationId is not { } rid || rid == Guid.Empty)
            throw ReservationService.Field("ReservationId", "Choose the booking whose payment links to show (reservationId).");
        var reservation = await db.Reservations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == rid && x.PropertyId == propertyId, ct)
                          ?? throw ReservationService.Field("ReservationId", "That booking isn't at this property. Search for it again.");
        var links = await db.PaymentGatewayOrders.AsNoTracking().Where(o => o.ReservationId == rid)
            .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id).ToListAsync(ct);
        var sums = await SumsAsync(reservation, ct);
        var why = WhyNot(reservation, sums);
        var dtos = new List<PaymentLinkDto>();
        foreach (var l in links) dtos.Add(await ToDtoAsync(l, withEvents: false, ct));
        return new BookingPaymentLinksDto(reservation.Id, reservation.Reference, sums.Due, sums.Paid, sums.Owed, sums.Open, why is null, why,
            gateway.IsFake, dtos);
    }

    public async Task<PaymentLinkDto> GetAsync(Guid propertyId, Guid id, CancellationToken ct)
    {
        var link = await db.PaymentGatewayOrders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id && o.PropertyId == propertyId, ct)
                   ?? throw new NotFoundAppException("Payment link", id);
        return await ToDtoAsync(link, withEvents: true, ct);
    }

    /// <summary>Cancels an unpaid link with a reason. Razorpay can't cancel an order, so the guest's page refuses to open
    /// Checkout; a payment already under way that still arrives is recorded (the money is real) and the link becomes paid.</summary>
    public async Task<PaymentLinkDto> CancelAsync(Guid propertyId, Guid id, CancelPaymentLinkRequest r, CancellationToken ct)
    {
        var reason = ReservationService.Clean(r.Reason)!;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.PaymentGatewayOrders.AsNoTracking().AnyAsync(o => o.Id == id && o.PropertyId == propertyId, ct))
            throw new NotFoundAppException("Payment link", id);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM payment_gateway_orders WHERE id = {id} FOR UPDATE", ct);
        var link = await db.PaymentGatewayOrders.SingleAsync(o => o.Id == id, ct);
        if (link.Status == GatewayOrderStatuses.Paid)
        {
            var number = await db.Payments.AsNoTracking().Where(p => p.Id == link.PaymentId).Select(p => p.Number).SingleAsync(ct);
            throw new ConflictAppException("LINK_ALREADY_PAID",
                $"The guest already paid this link ({number}, {Money.Rupees(link.Amount)}), so it can't be cancelled. Refund the payment instead.");
        }
        if (link.Status == GatewayOrderStatuses.Cancelled)
            throw new ConflictAppException("LINK_ALREADY_CANCELLED", $"This link was already cancelled ({link.CancelReason}). Reload to see it.");
        link.Status = GatewayOrderStatuses.Cancelled;
        link.CancelledAt = clock.GetUtcNow();
        link.CancelledBy = caller.UserId;
        link.CancelReason = reason;
        audit.Reason = reason;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ToDtoAsync(link, withEvents: true, ct);
    }

    // ------------------------------------------------------------------ the guest's page (anonymous)

    /// <summary>What the guest's page shows. Unknown token: 404 with a message the guest understands.</summary>
    public async Task<PayPageDto> PageAsync(string token, CancellationToken ct) => await PageOfAsync(await FindAsync(token, ct), ct);

    /// <summary>
    /// The guest's browser is back from Checkout. If Checkout's signature checks out (HMAC of order|payment with the key
    /// secret) the page shows "confirming" until the webhook arrives; it never records or captures a payment.
    /// </summary>
    public async Task<PayPageDto> CallbackAsync(string token, PayCallbackRequest r, CancellationToken ct)
    {
        var link = await FindAsync(token, ct);
        if (string.IsNullOrWhiteSpace(r.RazorpayPaymentId) || r.RazorpayOrderId != link.GatewayOrderId
            || !gateway.VerifyCheckout(link.GatewayOrderId, r.RazorpayPaymentId, r.RazorpaySignature))
            throw ReservationService.Field(nameof(r.RazorpaySignature),
                "Razorpay's confirmation didn't check out, so this page can't tell whether you paid. If money left your account, the resort will see it within a few minutes; otherwise try again.");
        if (link.CallbackAt is null)
        {
            await db.Database.ExecuteSqlAsync(
                $"UPDATE payment_gateway_orders SET callback_at = {clock.GetUtcNow()} WHERE id = {link.Id} AND callback_at IS NULL", ct);
            link.CallbackAt = clock.GetUtcNow();
        }
        return await PageOfAsync(link, ct);
    }

    /// <summary>The link behind a token (for the fake checkout, which then sends the fake's webhooks).</summary>
    public async Task<PaymentGatewayOrder> FindAsync(string token, CancellationToken ct)
    {
        var t = token?.Trim() ?? "";
        var link = t.Length is >= 32 and <= 64
            ? await db.PaymentGatewayOrders.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(o => o.PublicToken == t, ct)
            : null;
        if (link is null)
            throw new NotFoundAppException("This payment link isn't valid. Check you opened the whole link, or ask the resort for a new one.");
        actor.ActFor(link.OrganizationId);
        return link;
    }

    private async Task<PayPageDto> PageOfAsync(PaymentGatewayOrder link, CancellationToken ct)
    {
        var property = await db.Properties.AsNoTracking().SingleAsync(p => p.Id == link.PropertyId, ct);
        var reservation = await db.Reservations.AsNoTracking().SingleAsync(x => x.Id == link.ReservationId, ct);
        var guest = await db.Guests.AsNoTracking().Where(g => g.Id == reservation.PrimaryGuestId).Select(g => g.FullName).FirstOrDefaultAsync(ct) ?? "Guest";
        var first = guest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? guest;
        var payment = link.PaymentId is { } pid ? await db.Payments.AsNoTracking().SingleAsync(p => p.Id == pid, ct)
            : await db.Payments.AsNoTracking().Where(p => p.GatewayOrderId == link.Id && p.Status == PaymentStatuses.Authorized).FirstOrDefaultAsync(ct);
        var now = clock.GetUtcNow();
        var money = Money.Rupees(link.Amount);
        var (status, message) = link.Status switch
        {
            GatewayOrderStatuses.Paid => ("PAID", $"Paid. Thank you, {first}: {property.Name} has received {money} for booking {reservation.Reference}."),
            GatewayOrderStatuses.Cancelled => ("CANCELLED", $"This link was cancelled by {property.Name}, so it can't be paid. Ask them for a new one if you still need to pay."),
            _ when !PaymentService.TakesPayments.Contains(reservation.Status) =>
                ("CLOSED", $"Booking {reservation.Reference} is {ReservationStatuses.Words(reservation.Status)}, so this link can't be paid. Contact {property.Name} if this is a mistake."),
            _ when payment is { Status: PaymentStatuses.Authorized } || (link.CallbackAt is { } at && now - at < ConfirmingFor) =>
                ("CONFIRMING", $"Payment received by Razorpay; {property.Name} is confirming it. This page updates by itself, and you don't need to pay again."),
            _ => ("AWAITING_PAYMENT", $"{property.Name} asks you to pay {money} for booking {reservation.Reference}." +
                                      (link.LastError is null ? "" : $" Your last try didn't go through: {link.LastError}")),
        };
        var payable = status is "AWAITING_PAYMENT";
        return new PayPageDto(property.Name, reservation.Reference, first, reservation.CheckIn, reservation.CheckOut, link.Amount, link.Currency,
            status, message, payable ? gateway.KeyId : null, payable ? link.GatewayOrderId : null, gateway.IsFake,
            payable ? link.LastError : null, status == "PAID" ? payment?.Number : null, link.PaidAt);
    }

    // ------------------------------------------------------------------ helpers

    internal sealed record Sums(decimal Due, decimal Paid, decimal Owed, decimal Open);

    /// <summary>Due = the larger of the booking's price and what its bills add up to (extras); Paid = what counts net of
    /// refunds; Open = what unpaid links ask for.</summary>
    private async Task<Sums> SumsAsync(Reservation reservation, CancellationToken ct)
    {
        var billed = await (from i in db.FolioItems.AsNoTracking()
                            join f in db.Folios.AsNoTracking() on i.FolioId equals f.Id
                            where f.ReservationId == reservation.Id
                            select (decimal?)i.Total).SumAsync(ct) ?? 0m;
        var due = Math.Max(reservation.TotalAmount, billed);
        var paid = await ledger.PaidForReservationAsync(reservation.Id, ct);
        var open = await db.PaymentGatewayOrders.AsNoTracking()
            .Where(o => o.ReservationId == reservation.Id && o.Status == GatewayOrderStatuses.Created).SumAsync(o => (decimal?)o.Amount, ct) ?? 0m;
        return new Sums(due, paid, Math.Max(0m, due - paid), open);
    }

    /// <summary>Why no link can be sent for the booking now, or null if one can.</summary>
    private static string? WhyNot(Reservation reservation, Sums sums)
    {
        if (!PaymentService.TakesPayments.Contains(reservation.Status))
            return $"{reservation.Reference} is {ReservationStatuses.Words(reservation.Status)}, so no payment link can be sent for it.";
        if (sums.Owed <= 0)
            return $"{reservation.Reference} is paid in full, so there is nothing to ask for.";
        if (sums.Owed - sums.Open <= 0)
            return $"Open links already ask for all {Money.Rupees(sums.Owed)} {reservation.Reference} owes. Cancel one to send a new link.";
        return null;
    }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<PaymentLinkDto> ToDtoAsync(PaymentGatewayOrder l, bool withEvents, CancellationToken ct)
    {
        var reservation = await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == l.ReservationId, ct);
        var guest = await db.Guests.AsNoTracking().Where(g => g.Id == reservation.PrimaryGuestId).Select(g => g.FullName).FirstOrDefaultAsync(ct) ?? "";
        var payment = l.PaymentId is { } pid ? await db.Payments.AsNoTracking().SingleAsync(p => p.Id == pid, ct)
            : await db.Payments.AsNoTracking().Where(p => p.GatewayOrderId == l.Id).OrderByDescending(p => p.ReceivedAt).FirstOrDefaultAsync(ct);
        var users = await ledger.UserNamesAsync(new[] { l.CreatedBy, l.CancelledBy }.Where(u => u != null).Select(u => u!.Value).Distinct().ToList(), ct);
        string? Name(Guid? u) => u is { } x ? users.GetValueOrDefault(x) : null;
        List<WebhookEventDto>? events = null;
        if (withEvents)
            events = await db.WebhookEvents.AsNoTracking().Where(e => e.GatewayOrderId == l.Id).OrderBy(e => e.ReceivedAt).ThenBy(e => e.Id)
                .Select(e => new WebhookEventDto(e.Id, e.EventId, e.EventType, e.Status, e.Outcome, e.Attempts, e.ReceivedAt)).ToListAsync(ct);
        return new PaymentLinkDto(l.Id, reservation.Id, reservation.Reference, guest, l.GatewayOrderId, l.Amount, l.Currency, l.Status,
            $"/pay/{l.PublicToken}", l.Note, l.Attempts, l.LastError, l.CallbackAt, payment?.Id, payment?.Number, payment?.Status, l.PaidAt,
            l.CreatedAt, Name(l.CreatedBy), l.CancelledAt, Name(l.CancelledBy), l.CancelReason, events);
    }
}
