using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Resort.Application.Common;
using Resort.Application.Folios;
using Resort.Application.Reservations;
using Resort.Application.Validation;
using Resort.Contracts;
using Resort.Domain.Payments;
using Resort.Domain.Reservations;

namespace Resort.Application.Payments;

/// <summary>
/// Razorpay's webhook (Sprint 11, brief §22; docs/RAZORPAY.md): the only thing that records an online payment, marks a
/// payment link paid and confirms a pending booking. In order:
///
///  1. Verify X-Razorpay-Signature over the raw body (HMAC-SHA256 with the webhook secret). Wrong: 401, nothing stored.
///  2. Already processed this event id? 200, nothing changes (Razorpay delivers at least once).
///  3. Match it to one of our payment links (by Razorpay's order id, or a refund by its id). Not ours: stored as IGNORED.
///  4. Verify then trust: fetch the payment from Razorpay and check its order, amount and currency. Its status there
///     decides, not the event's, so events that arrive out of order (captured before authorized, failed after captured)
///     can't move a payment backwards.
///  5. In one transaction: claim the event (webhook_events UNIQUE (provider, event_id); a second delivery at the same
///     moment waits, then finds it done), lock the booking, the link and the payment (the order PaymentService uses), apply
///     the change, and mark the event PROCESSED or IGNORED with what it did.
///  6. Our own error: rolled back, the event stored as FAILED with the reason, and 500 so Razorpay delivers it again.
/// </summary>
public sealed class RazorpayWebhookService(
    IAppDbContext db, SystemActor actor, IPaymentGateway gateway, TimeProvider clock, AuditContext audit, ReservationService reservations,
    PaymentService payments, ILogger<RazorpayWebhookService> log)
{
    public const string SignatureHeader = "X-Razorpay-Signature";
    public const string EventIdHeader = "X-Razorpay-Event-Id";
    private const string Provider = PaymentGatewayOrder.Razorpay;

    /// <summary>The webhook's answer: 200 processed / duplicate / ignored, or 500 failed (Razorpay retries).</summary>
    public sealed record Result(int StatusCode, WebhookAckDto Ack);

    private sealed record Parsed(string EventId, string Type, JsonElement? Payment, JsonElement? Refund, string? OrderId, string Payload);

    private sealed record Outcome(string Status, string Text, Guid? PaymentId = null);

    public async Task<Result> HandleAsync(byte[] rawBody, string? signature, string? eventIdHeader, CancellationToken ct)
    {
        if (!gateway.VerifyWebhook(rawBody, signature))
            throw new UnauthorizedAppException("BAD_SIGNATURE",
                $"The {SignatureHeader} header doesn't match this body for the webhook secret, so the event was refused and nothing was stored. " +
                "Check that Razorpay__WebhookSecret is the secret set on the webhook in Razorpay's dashboard.");
        var ev = Parse(rawBody, eventIdHeader);

        if (await db.WebhookEvents.AsNoTracking().AnyAsync(e => e.Provider == Provider && e.EventId == ev.EventId && e.Status != WebhookEventStatuses.Failed, ct))
            return Duplicate(ev);

        PaymentGatewayOrder? link = null;
        List<Refund> refunds = [];
        GatewayPayment? fetched = null;
        try
        {
            if (ev.Type.StartsWith("refund.", StringComparison.Ordinal) && Str(ev.Refund, "id") is { } refundId)
            {
                refunds = await db.Refunds.IgnoreQueryFilters().AsNoTracking().Where(r => r.GatewayRefundId == refundId).ToListAsync(ct);
                if (refunds.Count > 0)
                {
                    actor.ActFor(refunds[0].OrganizationId);
                    var orderId = await db.Payments.AsNoTracking().Where(p => p.Id == refunds[0].PaymentId).Select(p => p.GatewayOrderId).SingleAsync(ct);
                    link = await db.PaymentGatewayOrders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == orderId, ct);
                }
            }
            else if (ev.OrderId is { } orderId)
            {
                link = await db.PaymentGatewayOrders.IgnoreQueryFilters().AsNoTracking()
                    .SingleOrDefaultAsync(o => o.Gateway == Provider && o.GatewayOrderId == orderId, ct);
                if (link is not null)
                {
                    actor.ActFor(link.OrganizationId);
                    if (IsPaymentEvent(ev.Type) && Str(ev.Payment, "id") is { } paymentId)
                        fetched = await gateway.FetchPaymentAsync(paymentId, ct);
                }
            }

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (!await ClaimAsync(ev, ct))
                return Duplicate(ev);
            var outcome = link is null && refunds.Count == 0
                ? new Outcome(WebhookEventStatuses.Ignored, ev.Type.StartsWith("refund.", StringComparison.Ordinal)
                    ? $"Refund {Str(ev.Refund, "id")} wasn't made from Resort OS (perhaps in Razorpay's dashboard), so nothing changed here."
                    : $"Order {ev.OrderId ?? "(none)"} isn't a Resort OS payment link, so nothing changed here.")
                : ev.Type.StartsWith("refund.", StringComparison.Ordinal) ? await ApplyRefundAsync(ev, refunds, ct)
                : IsPaymentEvent(ev.Type) ? await ApplyPaymentAsync(ev, link!, fetched!, ct)
                : new Outcome(WebhookEventStatuses.Ignored, $"Resort OS doesn't use {ev.Type} events; nothing changed.");
            await db.Database.ExecuteSqlAsync(
                $"""
                 UPDATE webhook_events SET status = {outcome.Status}, outcome = {Cut(outcome.Text)}, processed_at = {clock.GetUtcNow()},
                        organization_id = {link?.OrganizationId ?? refunds.FirstOrDefault()?.OrganizationId},
                        property_id = {link?.PropertyId ?? refunds.FirstOrDefault()?.PropertyId},
                        gateway_order_id = {link?.Id}, payment_id = {outcome.PaymentId ?? refunds.FirstOrDefault()?.PaymentId}
                  WHERE provider = {Provider} AND event_id = {ev.EventId}
                 """, ct);
            await tx.CommitAsync(ct);
            log.LogInformation("Razorpay webhook {EventId} {Type}: {Status}. {Outcome}", ev.EventId, ev.Type, outcome.Status, outcome.Text);
            return new Result(200, new WebhookAckDto(outcome.Status == WebhookEventStatuses.Processed ? "processed" : "ignored", outcome.Text));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var reason = ex switch
            {
                PaymentGatewayException or AppException => ex.Message,
                DbUpdateException { InnerException: { } inner } => $"the database refused it: {inner.Message}",
                _ => $"{ex.GetType().Name}: {ex.Message}",
            };
            log.LogError(ex, "Razorpay webhook {EventId} {Type} failed; Razorpay will deliver it again", ev.EventId, ev.Type);
            await db.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO webhook_events (id, provider, event_id, event_type, organization_id, property_id, gateway_order_id, status, outcome, payload, received_at)
                 VALUES ({Guid.CreateVersion7()}, {Provider}, {ev.EventId}, {ev.Type}, {link?.OrganizationId}, {link?.PropertyId}, {link?.Id},
                         'FAILED', {Cut("Failed: " + reason)}, {ev.Payload}::jsonb, {clock.GetUtcNow()})
                 ON CONFLICT (provider, event_id) DO UPDATE SET attempts = webhook_events.attempts + 1, outcome = EXCLUDED.outcome
                  WHERE webhook_events.status = 'FAILED'
                 """, CancellationToken.None);
            return new Result(500, new WebhookAckDto("failed",
                $"The event couldn't be processed ({reason}). It is stored as failed and is processed again when Razorpay retries it."));
        }
    }

    // ------------------------------------------------------------------ payments

    private static bool IsPaymentEvent(string type) => type is "payment.authorized" or "payment.captured" or "payment.failed" or "order.paid";

    private async Task<Outcome> ApplyPaymentAsync(Parsed ev, PaymentGatewayOrder linkSeen, GatewayPayment p, CancellationToken ct)
    {
        if (p.OrderId != linkSeen.GatewayOrderId)
            return new(WebhookEventStatuses.Ignored, $"Razorpay says {p.Id} belongs to order {p.OrderId ?? "(none)"}, not {linkSeen.GatewayOrderId}; nothing recorded.");
        if (p.AmountPaise != PaymentMath.ToPaise(linkSeen.Amount) || p.Currency != linkSeen.Currency)
        {
            log.LogWarning("Razorpay payment {PaymentId} is {Amount} {Currency} but link {Link} is for {LinkAmount}", p.Id, p.AmountPaise, p.Currency,
                linkSeen.Id, linkSeen.Amount);
            return new(WebhookEventStatuses.Ignored,
                $"Razorpay says {p.Id} is {Money.Rupees(PaymentMath.FromPaise(p.AmountPaise))} {p.Currency}, but the link is for {Money.Rupees(linkSeen.Amount)} " +
                $"{linkSeen.Currency}; nothing recorded. Check the payment in Razorpay's dashboard.");
        }
        var target = p.Status switch
        {
            "captured" or "refunded" => PaymentStatuses.Captured,
            "authorized" => PaymentStatuses.Authorized,
            "failed" => PaymentStatuses.Failed,
            _ => null,
        };
        if (target is null)
            return new(WebhookEventStatuses.Ignored, $"Razorpay has {p.Id} as {p.Status}; nothing to record yet.");

        // locks in PaymentService's order: the booking, then the link, then the payment
        var reservation = await reservations.LockAsync(linkSeen.PropertyId, linkSeen.ReservationId, ct);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM payment_gateway_orders WHERE id = {linkSeen.Id} FOR UPDATE", ct);
        var link = await db.PaymentGatewayOrders.SingleAsync(o => o.Id == linkSeen.Id, ct);
        var paymentId = await db.Payments.AsNoTracking().Where(x => x.Method == PaymentMethods.Online && x.Reference == p.Id).Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(ct);
        Payment? payment = null;
        if (paymentId is { } pid)
        {
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM payments WHERE id = {pid} FOR UPDATE", ct);
            payment = await db.Payments.SingleAsync(x => x.Id == pid, ct);
        }

        if (PaymentStatuses.GatewayRank(target) <= PaymentStatuses.GatewayRank(payment?.Status) && payment is not null)
            return new(WebhookEventStatuses.Ignored,
                $"{payment.Number} is already {PaymentStatuses.Words(payment.Status)}; Razorpay has it as {p.Status}, so this {ev.Type} changes nothing.",
                payment.Id);
        if (payment is { Status: PaymentStatuses.Failed })
        {
            log.LogWarning("Razorpay payment {PaymentId} was failed here but Razorpay now has it {Status}", p.Id, p.Status);
            return new(WebhookEventStatuses.Ignored,
                $"{payment.Number} was recorded as failed, but Razorpay now has {p.Id} as {p.Status}. A failed payment isn't reopened here: " +
                "check it in Razorpay's dashboard and refund it there if the money arrived.", payment.Id);
        }
        var now = clock.GetUtcNow();

        if (target == PaymentStatuses.Failed)
        {
            link.Attempts++;
            link.LastError = Cut(p.ErrorDescription ?? "Razorpay gave no reason.", 500);
            var words = $"The guest's payment {p.Id} failed: {link.LastError}";
            if (payment is not null)
            {
                payment.Status = PaymentStatuses.Failed;
                words = $"{payment.Number} ({p.Id}) failed after it was authorised: {link.LastError}";
            }
            await db.SaveChangesAsync(ct);
            return new(WebhookEventStatuses.Processed, words + (link.Status == GatewayOrderStatuses.Created ? " The link stays open to try again." : ""), payment?.Id);
        }

        payment ??= await NewPaymentAsync(link, reservation, p, target, ct);
        if (target == PaymentStatuses.Authorized)
        {
            await db.SaveChangesAsync(ct);
            return new(WebhookEventStatuses.Processed, $"{payment.Number} ({p.Id}, {Money.Rupees(payment.Amount)}) authorised by Razorpay; it counts once captured.", payment.Id);
        }

        // captured: the money is in. Mark the payment, put it on the bill, mark the link paid, confirm a pending booking.
        payment.Status = PaymentStatuses.Captured;
        payment.CapturedAt = now;
        var said = new List<string> { $"{payment.Number} ({p.Id}, {Money.Rupees(payment.Amount)} by {p.Method ?? "online"}) captured" };
        if (link.PaymentId is null)
        {
            link.Status = GatewayOrderStatuses.Paid;
            link.PaidAt = now;
            link.PaymentId = payment.Id;
            if (link.CancelledAt is not null) said.Add("the link had been cancelled, but the money came, so it is recorded");
        }
        else said.Add("the link was already paid by another payment, so this one stays on account: refund it");
        audit.Reason = $"Paid online: {payment.Number} {Money.Rupees(payment.Amount)} (Razorpay {p.Id})";
        await db.SaveChangesAsync(ct);

        if (reservation.Status == ReservationStatuses.Pending)
        {
            await reservations.ConfirmLockedAsync(reservation, audit.Reason, ct);
            said.Add($"{reservation.Reference} confirmed");
        }
        else if (!PaymentService.TakesPayments.Contains(reservation.Status))
            said.Add($"{reservation.Reference} is {ReservationStatuses.Words(reservation.Status)}: the money is on account, refund it");

        if (FolioPosting.Billable.Contains(reservation.Status))
        {
            var folio = await db.Folios.AsNoTracking().SingleOrDefaultAsync(f => f.ReservationId == reservation.Id && f.IsPrimary, ct);
            var owes = folio is null ? 0m : await payments.FolioBalanceAsync(folio.Id, ct);
            if (folio is not null && owes > 0)
            {
                var put = Math.Min(owes, payment.Amount);
                db.PaymentAllocations.Add(payments.Allocation(payment, folio.Id, put, now));
                await db.SaveChangesAsync(ct);
                said.Add($"{Money.Rupees(put)} put on {folio.Number}");
            }
        }
        if (said.Count == 1 || !said.Any(s => s.Contains(" put on ", StringComparison.Ordinal)))
            said.Add("on account for " + reservation.Reference);
        return new(WebhookEventStatuses.Processed, string.Join("; ", said) + ".", payment.Id);
    }

    private async Task<Payment> NewPaymentAsync(PaymentGatewayOrder link, Reservation reservation, GatewayPayment p, string status, CancellationToken ct)
    {
        var payment = new Payment
        {
            OrganizationId = link.OrganizationId,
            PropertyId = link.PropertyId,
            Number = await payments.NextNumberAsync(link.PropertyId, ct),
            ReservationId = reservation.Id,
            GuestId = reservation.PrimaryGuestId,
            Method = PaymentMethods.Online,
            Status = status,
            Amount = link.Amount,
            Currency = link.Currency,
            Reference = p.Id,
            GatewayOrderId = link.Id,
            GatewayMethod = p.Method is { } m && System.Text.RegularExpressions.Regex.IsMatch(m, "^[a-z_]{1,20}$") ? m : null,
            ReceivedAt = p.CreatedAt,
            CapturedAt = status == PaymentStatuses.Captured ? clock.GetUtcNow() : null,
        };
        db.Payments.Add(payment);
        return payment;
    }

    // ------------------------------------------------------------------ refunds

    private async Task<Outcome> ApplyRefundAsync(Parsed ev, List<Refund> seen, CancellationToken ct)
    {
        var processed = ev.Type == "refund.processed";
        if (!processed && ev.Type != "refund.failed")
            return new(WebhookEventStatuses.Ignored, $"Resort OS doesn't use {ev.Type} events; nothing changed.", seen[0].PaymentId);
        var paymentId = seen[0].PaymentId;
        var reservationId = await db.Payments.AsNoTracking().Where(x => x.Id == paymentId).Select(x => x.ReservationId).SingleAsync(ct);
        await reservations.LockAsync(seen[0].PropertyId, reservationId, ct);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM payments WHERE id = {paymentId} FOR UPDATE", ct);
        var payment = await db.Payments.SingleAsync(x => x.Id == paymentId, ct);
        var ids = seen.Select(r => r.Id).ToList();
        var rows = await db.Refunds.Where(r => ids.Contains(r.Id)).ToListAsync(ct);
        var amount = rows.Sum(r => r.Amount);
        var gatewayId = rows[0].GatewayRefundId;
        if (rows.All(r => r.Status != RefundStatuses.Pending))
            return new(WebhookEventStatuses.Ignored,
                $"The refund of {Money.Rupees(amount)} from {payment.Number} ({gatewayId}) is already {rows[0].Status.ToLowerInvariant()}; nothing changed.", payment.Id);
        foreach (var r in rows.Where(r => r.Status == RefundStatuses.Pending))
            r.Status = processed ? RefundStatuses.Processed : RefundStatuses.Failed;
        await db.SaveChangesAsync(ct);
        if (processed)
            return new(WebhookEventStatuses.Processed, $"The refund of {Money.Rupees(amount)} from {payment.Number} ({gatewayId}) reached the guest.", payment.Id);

        // the money didn't go back: it counts on the payment again
        var refunded = await db.Refunds.Where(r => r.PaymentId == payment.Id && r.Status != RefundStatuses.Failed).SumAsync(r => (decimal?)r.Amount, ct) ?? 0m;
        payment.Status = refunded == 0 ? PaymentStatuses.Captured : PaymentMath.StatusAfterRefund(payment.Amount, refunded);
        audit.Reason = $"Razorpay refund {gatewayId} failed";
        await db.SaveChangesAsync(ct);
        return new(WebhookEventStatuses.Processed,
            $"The refund of {Money.Rupees(amount)} from {payment.Number} ({gatewayId}) failed at Razorpay, so the money counts on the payment again " +
            $"({PaymentStatuses.Words(payment.Status)}). Refund it again, or by another method.", payment.Id);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Claims the event inside the transaction: a new row, or a FAILED one being retried. False: it was processed
    /// already (a second delivery at the same moment waits on the first one's row until it commits).</summary>
    private async Task<bool> ClaimAsync(Parsed ev, CancellationToken ct)
    {
        var claimed = await db.Database.SqlQuery<Guid>(
            $"""
             INSERT INTO webhook_events (id, provider, event_id, event_type, status, payload, received_at)
             VALUES ({Guid.CreateVersion7()}, {Provider}, {ev.EventId}, {ev.Type}, 'FAILED', {ev.Payload}::jsonb, {clock.GetUtcNow()})
             ON CONFLICT (provider, event_id) DO UPDATE SET attempts = webhook_events.attempts + 1
              WHERE webhook_events.status = 'FAILED'
             RETURNING id AS "Value"
             """).ToListAsync(ct);
        return claimed.Count == 1;
    }

    private static Result Duplicate(Parsed ev) =>
        new(200, new WebhookAckDto("duplicate", $"Event {ev.EventId} ({ev.Type}) was already processed; nothing changed."));

    private static Parsed Parse(byte[] raw, string? eventIdHeader)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(raw).RootElement.Clone();
        }
        catch (JsonException)
        {
            throw ReservationService.Field("body", "The webhook body isn't JSON.");
        }
        var type = Str(root, "event") ?? throw ReservationService.Field("event", "The webhook has no \"event\" name.");
        var eventId = eventIdHeader?.Trim();
        if (string.IsNullOrEmpty(eventId) || eventId.Length > 100)
            throw ReservationService.Field(EventIdHeader, $"The {EventIdHeader} header is missing, so the event can't be told apart from a repeat of it.");
        JsonElement? Entity(string name) =>
            root.TryGetProperty("payload", out var pl) && pl.TryGetProperty(name, out var e) && e.TryGetProperty("entity", out var en) ? en : null;
        var payment = Entity("payment");
        var refund = Entity("refund");
        var order = Entity("order");
        var orderId = Str(payment, "order_id") ?? Str(order, "id");

        // keep what we need of it; never the card, the guest's contact or UPI id
        JsonObject? Pick(JsonElement? e, params string[] names)
        {
            if (e is not { } x) return null;
            var o = new JsonObject();
            foreach (var n in names)
                if (x.TryGetProperty(n, out var v)) o[n] = JsonNode.Parse(v.GetRawText());
            return o;
        }
        var kept = new JsonObject
        {
            ["event"] = type,
            ["account_id"] = Str(root, "account_id"),
            ["created_at"] = root.TryGetProperty("created_at", out var c) ? JsonNode.Parse(c.GetRawText()) : null,
            ["payment"] = Pick(payment, "id", "order_id", "amount", "currency", "status", "method", "captured", "amount_refunded", "error_code",
                "error_description", "created_at"),
            ["refund"] = Pick(refund, "id", "payment_id", "amount", "currency", "status", "receipt", "created_at"),
            ["order"] = Pick(order, "id", "amount", "amount_paid", "currency", "status", "receipt"),
        };
        return new Parsed(eventId, Cut(type, 100), payment, refund, orderId, kept.ToJsonString());
    }

    private static string? Str(JsonElement? e, string name) =>
        e is { } x && x.ValueKind == JsonValueKind.Object && x.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Cut(string text, int max = 1000) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
