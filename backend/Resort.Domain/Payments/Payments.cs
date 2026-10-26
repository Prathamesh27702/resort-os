using Resort.Domain.Common;

namespace Resort.Domain.Payments;

/// <summary>How the money came in (or went back). Card means the property's own terminal: we store its approval code at
/// most, never card data (brief §3). Online means paid through Razorpay (Sprint 11): only the webhook records it.</summary>
public static class PaymentMethods
{
    public const string Cash = "CASH";
    public const string Upi = "UPI";
    public const string Card = "CARD";
    public const string BankTransfer = "BANK_TRANSFER";
    public const string Online = "ONLINE";

    public static readonly string[] All = [Cash, Upi, Card, BankTransfer, Online];

    /// <summary>The methods staff record at the desk. Online payments come from Razorpay's webhook only.</summary>
    public static readonly string[] Desk = [Cash, Upi, Card, BankTransfer];

    /// <summary>"Cash", "UPI", "Card", "Bank transfer", "Online" — for descriptions and messages.</summary>
    public static string Words(string method) => method switch
    {
        Cash => "Cash",
        Upi => "UPI",
        Card => "Card",
        BankTransfer => "Bank transfer",
        Online => "Online (Razorpay)",
        _ => method,
    };
}

/// <summary>
/// The payment status machine (database table payment_status_transitions; docs/RAZORPAY.md). A payment taken at the desk
/// is created CAPTURED. An online payment (Sprint 11) is created AUTHORIZED or CAPTURED by Razorpay's webhook, and an
/// authorised one becomes CAPTURED or FAILED. CANCELLED is a voided payment: it counts for nothing. A gateway refund
/// that fails gives the money back, so an online payment may move from REFUNDED or PARTIALLY_REFUNDED back to
/// PARTIALLY_REFUNDED or CAPTURED (<see cref="GatewayReturns"/>; the database allows it for ONLINE payments only).
/// </summary>
public static class PaymentStatuses
{
    public const string Pending = "PENDING";
    public const string Authorized = "AUTHORIZED";
    public const string Captured = "CAPTURED";
    public const string Failed = "FAILED";
    public const string Refunded = "REFUNDED";
    public const string PartiallyRefunded = "PARTIALLY_REFUNDED";
    public const string Cancelled = "CANCELLED";

    public static readonly string[] All = [Pending, Authorized, Captured, Failed, Refunded, PartiallyRefunded, Cancelled];

    /// <summary>The allowed moves; null = the statuses a payment may be created in. Same graph as the database.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Transitions = new Dictionary<string, string[]>
    {
        [""] = [Pending, Authorized, Captured],
        [Pending] = [Authorized, Captured, Failed, Cancelled],
        [Authorized] = [Captured, Failed, Cancelled],
        [Captured] = [PartiallyRefunded, Refunded, Cancelled],
        [PartiallyRefunded] = [Refunded],
        [Refunded] = [],
        [Failed] = [],
        [Cancelled] = [],
    };

    public static bool CanMove(string? from, string to) => from == to || Transitions[from ?? ""].Contains(to);

    /// <summary>The moves a failed gateway refund makes (online payments only).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> GatewayReturns = new Dictionary<string, string[]>
    {
        [Refunded] = [PartiallyRefunded, Captured],
        [PartiallyRefunded] = [Captured],
    };

    /// <summary>
    /// How far along an online payment is, so webhooks that arrive out of order can't move it backwards: a "captured"
    /// that came before "authorized" stays captured, and a "failed" after "captured" changes nothing (docs/RAZORPAY.md).
    /// </summary>
    public static int GatewayRank(string? status) => status switch
    {
        null => 0,
        Pending => 1,
        Authorized => 2,
        Failed => 3,
        _ => 4,   // captured, refunded in part or in full, voided: the money arrived
    };

    /// <summary>Money that was received and still counts on the bill (refunds are subtracted separately).</summary>
    public static readonly string[] Counted = [Captured, PartiallyRefunded, Refunded];

    /// <summary>Money in hand that can still be allocated or refunded.</summary>
    public static readonly string[] Refundable = [Captured, PartiallyRefunded];

    public static string Words(string status) => status switch
    {
        PartiallyRefunded => "partly refunded",
        Cancelled => "voided",
        _ => status.ToLowerInvariant(),
    };
}

public static class RefundStatuses
{
    /// <summary>Sent to Razorpay, not confirmed yet (Sprint 11). Counts as refunded until it fails.</summary>
    public const string Pending = "PENDING";
    /// <summary>The money has gone back. Every refund made at the desk.</summary>
    public const string Processed = "PROCESSED";
    public const string Failed = "FAILED";
}

/// <summary>Money received for a booking. Numbered P00001 … per property. Never deleted: a mistake is voided (with a
/// reason) or refunded; a database trigger refuses any change but the status and the void.</summary>
public class Payment : AuditableEntity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Number { get; set; } = default!;
    public Guid ReservationId { get; set; }
    /// <summary>Who paid (default: the booking's guest).</summary>
    public Guid? GuestId { get; set; }
    public string Method { get; set; } = default!;
    public string Status { get; set; } = PaymentStatuses.Captured;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "INR";
    /// <summary>UPI transaction id, card terminal approval code, bank UTR … Never a card number.</summary>
    public string? Reference { get; set; }
    public string? Note { get; set; }
    /// <summary>The drawer session the cash went into (cash only).</summary>
    public Guid? CashDrawerSessionId { get; set; }
    /// <summary>The payment link it paid (online payments only). <see cref="Reference"/> is then Razorpay's pay_… id.</summary>
    public Guid? GatewayOrderId { get; set; }
    /// <summary>How the guest paid at Razorpay: upi, card, netbanking, wallet … (online payments only; never card details).</summary>
    public string? GatewayMethod { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public Guid? ReceivedBy { get; set; }
    public DateTimeOffset? CapturedAt { get; set; }
    public DateTimeOffset? VoidedAt { get; set; }
    public Guid? VoidedBy { get; set; }
    public string? VoidReason { get; set; }
}

public static class GatewayOrderStatuses
{
    /// <summary>Waiting for the guest to pay.</summary>
    public const string Created = "CREATED";
    /// <summary>Razorpay's webhook confirmed a captured payment.</summary>
    public const string Paid = "PAID";
    /// <summary>Cancelled by staff. A payment that still arrives is recorded (the money is real) and the link becomes PAID.</summary>
    public const string Cancelled = "CANCELLED";
}

/// <summary>
/// A payment link (Sprint 11): an order created at Razorpay for a booking and an amount, and the private link
/// /pay/<see cref="PublicToken"/> the guest opens to pay it. Only the webhook marks it paid. Never deleted.
/// </summary>
public class PaymentGatewayOrder : AuditableEntity, IPropertyScoped, IAudited
{
    public const string Razorpay = "RAZORPAY";
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid ReservationId { get; set; }
    public string Gateway { get; set; } = Razorpay;
    /// <summary>Razorpay's order_… id.</summary>
    public string GatewayOrderId { get; set; } = default!;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "INR";
    public string Status { get; set; } = GatewayOrderStatuses.Created;
    public string PublicToken { get; set; } = default!;
    public string? Note { get; set; }
    /// <summary>Failed tries by the guest (card declined, UPI timed out …), and the last reason Razorpay gave.</summary>
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    /// <summary>When the guest's browser came back from Checkout. It only changes what the pay page shows.</summary>
    public DateTimeOffset? CallbackAt { get; set; }
    public Guid? PaymentId { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledBy { get; set; }
    public string? CancelReason { get; set; }
}

public static class WebhookEventStatuses
{
    public const string Processed = "PROCESSED";
    /// <summary>Verified, but nothing to do (not our order, already captured, an event we don't use …).</summary>
    public const string Ignored = "IGNORED";
    /// <summary>Our error while processing; processed again when Razorpay retries. Also the state of the row while the
    /// event is being processed, inside the transaction that then marks it PROCESSED or IGNORED.</summary>
    public const string Failed = "FAILED";
}

/// <summary>One verified webhook delivery, stored once per Razorpay event id. Not tenant-scoped: the webhook has no
/// signed-in caller, and an event is matched to a property only through its payment link.</summary>
public class WebhookEvent : Entity
{
    public string Provider { get; set; } = PaymentGatewayOrder.Razorpay;
    public string EventId { get; set; } = default!;
    public string EventType { get; set; } = default!;
    public Guid? OrganizationId { get; set; }
    public Guid? PropertyId { get; set; }
    public Guid? GatewayOrderId { get; set; }
    public Guid? PaymentId { get; set; }
    public string Status { get; set; } = WebhookEventStatuses.Failed;
    public string? Outcome { get; set; }
    public string Payload { get; set; } = "{}";
    public int Attempts { get; set; } = 1;
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}

/// <summary>Part of a payment put on a folio. Immutable.</summary>
public class PaymentAllocation : Entity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid PaymentId { get; set; }
    public Guid FolioId { get; set; }
    public decimal Amount { get; set; }
    public DateTimeOffset AllocatedAt { get; set; }
    public Guid? AllocatedBy { get; set; }
}

/// <summary>Money given back from a payment: from a folio it settled, or (FolioId null) from what is still on account.</summary>
public class Refund : AuditableEntity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid PaymentId { get; set; }
    public Guid? FolioId { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = default!;
    public string Status { get; set; } = RefundStatuses.Processed;
    public string Reason { get; set; } = default!;
    public string? Reference { get; set; }
    public Guid? CashDrawerSessionId { get; set; }
    /// <summary>Razorpay's rfnd_… id when the money went back online (one Razorpay refund can cover several rows).</summary>
    public string? GatewayRefundId { get; set; }
    public DateTimeOffset RefundedAt { get; set; }
    public Guid? RefundedBy { get; set; }
}

/// <summary>A cash box at a desk. Every property has a "Front desk" drawer.</summary>
public class CashDrawer : AuditableEntity, IPropertyScoped, IAudited
{
    public const string DefaultName = "Front desk";
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Name { get; set; } = DefaultName;
    public bool IsActive { get; set; } = true;
}

public static class DrawerSessionStatuses
{
    public const string Open = "OPEN";
    /// <summary>Closed short or over; waits for someone with cash_drawer.approve_variance.</summary>
    public const string VariancePending = "VARIANCE_PENDING";
    public const string Closed = "CLOSED";
}

/// <summary>One shift of a cash drawer. Expected cash is the sum of its movements; the variance (counted − expected) is
/// fixed at the close.</summary>
public class CashDrawerSession : AuditableEntity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid CashDrawerId { get; set; }
    public string Status { get; set; } = DrawerSessionStatuses.Open;
    public DateTimeOffset OpenedAt { get; set; }
    public Guid? OpenedBy { get; set; }
    public decimal OpeningFloat { get; set; }
    public string? OpenNote { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public Guid? ClosedBy { get; set; }
    public decimal? ExpectedAmount { get; set; }
    public decimal? CountedAmount { get; set; }
    public decimal? Variance { get; set; }
    public string? CloseNote { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public Guid? ApprovedBy { get; set; }
    public string? ApprovalNote { get; set; }
}

public static class DrawerMovementTypes
{
    public const string OpeningFloat = "OPENING_FLOAT";
    public const string Payment = "PAYMENT";
    public const string Refund = "REFUND";
    public const string Void = "VOID";
    /// <summary>Cash taken for a restaurant bill (Sprint 14); OrderId names the order.</summary>
    public const string PosSale = "POS_SALE";
}

/// <summary>One line of a drawer session's ledger: + money in, − money out. Immutable.</summary>
public class CashDrawerMovement : Entity, IPropertyScoped
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid SessionId { get; set; }
    public string MovementType { get; set; } = default!;
    public decimal Amount { get; set; }
    public Guid? PaymentId { get; set; }
    public Guid? RefundId { get; set; }
    /// <summary>The restaurant order a POS_SALE was for (Sprint 14).</summary>
    public Guid? OrderId { get; set; }
    public string Description { get; set; } = default!;
    public DateTimeOffset MovedAt { get; set; }
    public Guid? MovedBy { get; set; }
}

/// <summary>Payment arithmetic that needs no database. Pure, unit-tested; the database checks the same limits.</summary>
public static class PaymentMath
{
    /// <summary>What can still be refunded: captured − already refunded (never below 0).</summary>
    public static decimal Refundable(decimal amount, decimal refunded) => Math.Max(0m, amount - refunded);

    /// <summary>What is still on account (not on any folio): amount − allocated − refunded from the account.</summary>
    public static decimal OnAccount(decimal amount, decimal allocated, decimal refundedFromAccount) =>
        Math.Max(0m, amount - allocated - refundedFromAccount);

    /// <summary>The status after a refund: REFUNDED when nothing is left, otherwise PARTIALLY_REFUNDED.</summary>
    public static string StatusAfterRefund(decimal amount, decimal refundedIncludingThis) =>
        refundedIncludingThis >= amount ? PaymentStatuses.Refunded : PaymentStatuses.PartiallyRefunded;

    /// <summary>Counted − expected: negative when the drawer is short, positive when it is over.</summary>
    public static decimal Variance(decimal expected, decimal counted) => counted - expected;

    /// <summary>
    /// True if <paramref name="text"/> holds something that looks like a card number: a run of 13 to 19 digits (spaces and
    /// dashes inside it ignored) that passes the Luhn check. Used to refuse a reference or note that would store card data.
    /// </summary>
    public static bool LooksLikeCardNumber(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var run = new System.Text.StringBuilder();
        foreach (var c in text + "!")
        {
            if (char.IsAsciiDigit(c)) { run.Append(c); continue; }
            if (c is ' ' or '-' && run.Length > 0) continue;
            if (run.Length is >= 13 and <= 19 && Luhn(run.ToString())) return true;
            run.Clear();
        }
        return false;
    }

    /// <summary>The longest run of digits in <paramref name="text"/>, spaces and dashes ignored (a card terminal's
    /// approval code or RRN has at most 12).</summary>
    public static int LongestDigitRun(string? text)
    {
        int best = 0, run = 0;
        foreach (var c in text ?? "")
        {
            if (char.IsAsciiDigit(c)) best = Math.Max(best, ++run);
            else if (c is not (' ' or '-')) run = 0;
        }
        return best;
    }

    /// <summary>Rupees to paise for the gateway (amounts are whole paise; checked by the validators).</summary>
    public static long ToPaise(decimal rupees) => (long)decimal.Round(rupees * 100m, 0, MidpointRounding.AwayFromZero);

    public static decimal FromPaise(long paise) => paise / 100m;

    public static bool Luhn(ReadOnlySpan<char> digits)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 1) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        return sum % 10 == 0;
    }
}
