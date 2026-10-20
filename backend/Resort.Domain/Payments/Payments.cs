using Resort.Domain.Common;

namespace Resort.Domain.Payments;

/// <summary>How the money came in (or went back). Card means the property's own terminal: we store its approval code at
/// most, never card data (brief §3).</summary>
public static class PaymentMethods
{
    public const string Cash = "CASH";
    public const string Upi = "UPI";
    public const string Card = "CARD";
    public const string BankTransfer = "BANK_TRANSFER";

    public static readonly string[] All = [Cash, Upi, Card, BankTransfer];

    /// <summary>"Cash", "UPI", "Card", "Bank transfer" — for descriptions and messages.</summary>
    public static string Words(string method) => method switch
    {
        Cash => "Cash",
        Upi => "UPI",
        Card => "Card",
        BankTransfer => "Bank transfer",
        _ => method,
    };
}

/// <summary>
/// The payment status machine (database table payment_status_transitions; docs/RAZORPAY.md). A payment taken at the desk
/// is created CAPTURED; PENDING, AUTHORIZED and FAILED are for the online gateway (Sprint 11). CANCELLED is a voided
/// payment: it counts for nothing.
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
        [""] = [Pending, Captured],
        [Pending] = [Authorized, Captured, Failed, Cancelled],
        [Authorized] = [Captured, Failed, Cancelled],
        [Captured] = [PartiallyRefunded, Refunded, Cancelled],
        [PartiallyRefunded] = [Refunded],
        [Refunded] = [],
        [Failed] = [],
        [Cancelled] = [],
    };

    public static bool CanMove(string? from, string to) => from == to || Transitions[from ?? ""].Contains(to);

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
    /// <summary>Sent to the gateway, not confirmed yet (Sprint 11).</summary>
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
    public DateTimeOffset ReceivedAt { get; set; }
    public Guid? ReceivedBy { get; set; }
    public DateTimeOffset? CapturedAt { get; set; }
    public DateTimeOffset? VoidedAt { get; set; }
    public Guid? VoidedBy { get; set; }
    public string? VoidReason { get; set; }
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
