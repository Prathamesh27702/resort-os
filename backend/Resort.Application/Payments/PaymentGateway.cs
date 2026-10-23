namespace Resort.Application.Payments;

/// <summary>
/// An online payment gateway (Sprint 11; Razorpay today, docs/RAZORPAY.md "Adding Cashfree / Stripe later"). Amounts are
/// whole paise at this boundary. The implementation retries what is safe to retry, with jitter, and times out each try;
/// a failure that is left is a <see cref="PaymentGatewayException"/>.
/// </summary>
public interface IPaymentGateway
{
    /// <summary>RAZORPAY.</summary>
    string Name { get; }

    /// <summary>The public key id Checkout needs in the guest's browser (rzp_test_… / rzp_live_…). Never the secret.</summary>
    string KeyId { get; }

    /// <summary>True when the gateway is the built-in fake Razorpay (development, browser tests and integration tests):
    /// the pay page then shows "Pay (test)" buttons instead of Razorpay Checkout. Never in production.</summary>
    bool IsFake { get; }

    /// <summary>Creates an order. <paramref name="receipt"/> is our payment link's id; notes are shown in Razorpay's
    /// dashboard (booking reference, property).</summary>
    Task<GatewayOrder> CreateOrderAsync(long amountPaise, string currency, string receipt, IReadOnlyDictionary<string, string> notes,
        CancellationToken ct);

    /// <summary>The payment as Razorpay has it now: what the webhook says is checked against this before it is trusted.</summary>
    Task<GatewayPayment> FetchPaymentAsync(string paymentId, CancellationToken ct);

    /// <summary>Sends money back to the guest. Not retried on an unclear failure (it could refund twice).</summary>
    Task<GatewayRefund> RefundAsync(string paymentId, long amountPaise, string receipt, IReadOnlyDictionary<string, string> notes,
        CancellationToken ct);

    /// <summary>True if <paramref name="signature"/> (X-Razorpay-Signature) is the HMAC-SHA256 of the raw body with the
    /// webhook secret. Constant-time comparison.</summary>
    bool VerifyWebhook(ReadOnlySpan<byte> rawBody, string? signature);

    /// <summary>True if Checkout's razorpay_signature is the HMAC-SHA256 of "order_id|payment_id" with the key secret.</summary>
    bool VerifyCheckout(string orderId, string paymentId, string? signature);
}

/// <summary>An order at the gateway. Status: created, attempted, paid.</summary>
public sealed record GatewayOrder(string Id, long AmountPaise, string Currency, string Status);

/// <summary>A payment at the gateway. Status: created, authorized, captured, refunded, failed. Method: upi, card,
/// netbanking, wallet, emi …; ErrorDescription is the reason a failed payment failed, in Razorpay's words.</summary>
public sealed record GatewayPayment(
    string Id, string? OrderId, long AmountPaise, string Currency, string Status, string? Method, string? ErrorCode,
    string? ErrorDescription, DateTimeOffset CreatedAt);

/// <summary>A refund at the gateway. Status: pending, processed, failed.</summary>
public sealed record GatewayRefund(string Id, string PaymentId, long AmountPaise, string Status);

/// <summary>The gateway refused or couldn't be reached. Message: what happened, for the error shown to staff.</summary>
public sealed class PaymentGatewayException(string message, int? statusCode = null, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The gateway's HTTP status, or null when it didn't answer (timeout, network).</summary>
    public int? StatusCode { get; } = statusCode;
}
