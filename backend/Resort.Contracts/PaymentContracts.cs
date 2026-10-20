namespace Resort.Contracts;

// ---------------------------------------------------------------- payments, refunds and the cash drawer (Sprint 10)

/// <summary>
/// A payment with what became of it. Method CASH, UPI, CARD or BANK_TRANSFER; Status CAPTURED, PARTIALLY_REFUNDED,
/// REFUNDED or CANCELLED (voided) for a payment taken at the desk. Allocated: put on folios; OnAccount: still not on any
/// folio (an advance for the booking); Refundable: captured − already refunded. CanVoid / CantVoidReason: whether it can
/// still be voided (nothing refunded, and cash only while its drawer session is open).
/// </summary>
public sealed record PaymentDto(
    Guid Id, string Number, Guid ReservationId, string Reference, string GuestName, string Method, string Status,
    decimal Amount, decimal Allocated, decimal Refunded, decimal OnAccount, decimal Refundable, string Currency,
    string? PaymentReference, string? Note, Guid? CashDrawerSessionId, DateTimeOffset ReceivedAt, string? ReceivedByName,
    DateTimeOffset? VoidedAt, string? VoidedByName, string? VoidReason, bool CanVoid, string? CantVoidReason,
    IReadOnlyList<PaymentAllocationDto> Allocations, IReadOnlyList<RefundDto> Refunds, IReadOnlyList<PaymentFolioDto> Folios);

/// <summary>Part of a payment on a folio. Refunded: what was refunded from that folio out of this payment.</summary>
public sealed record PaymentAllocationDto(
    Guid Id, Guid FolioId, string FolioNumber, string FolioName, decimal Amount, decimal Refunded, DateTimeOffset AllocatedAt, string? AllocatedByName);

/// <summary>Money given back. FolioNumber null: refunded from what was on account.</summary>
public sealed record RefundDto(
    Guid Id, Guid PaymentId, string PaymentNumber, Guid? FolioId, string? FolioNumber, decimal Amount, string Method, string Status,
    string Reason, string? Reference, DateTimeOffset RefundedAt, string? RefundedByName);

/// <summary>A folio of the payment's booking, with its balance, for the allocate and refund forms.</summary>
public sealed record PaymentFolioDto(Guid Id, string Number, string Name, bool IsPrimary, decimal Balance);

/// <summary>A row on the Payments screen.</summary>
public sealed record PaymentListItemDto(
    Guid Id, string Number, Guid ReservationId, string Reference, string GuestName, string Method, string Status,
    decimal Amount, decimal Refunded, decimal OnAccount, string? PaymentReference, DateTimeOffset ReceivedAt, string? ReceivedByName,
    string? Folios);

/// <summary>The Payments screen: one page of payments and what the filtered payments add up to by method (net of
/// refunds; voided payments left out).</summary>
public sealed record PaymentListDto(
    IReadOnlyList<PaymentListItemDto> Items, int Page, int PageSize, long Total, IReadOnlyList<PaymentMethodTotalDto> ByMethod,
    decimal Received, decimal RefundedTotal, decimal Net);

public sealed record PaymentMethodTotalDto(string Method, int Count, decimal Received, decimal Refunded, decimal Net);

/// <summary>A payment on a folio page: money in (PAYMENT, positive) or out (REFUND, negative). Voided: the payment was
/// voided and counts for nothing.</summary>
public sealed record FolioPaymentDto(
    Guid PaymentId, string PaymentNumber, Guid? RefundId, string Kind, string Method, decimal Amount, DateTimeOffset At, string? ByName,
    string? Detail, bool Voided);

/// <summary>
/// Records money received for a booking (needs an Idempotency-Key header). FolioId: the bill it pays (the whole amount
/// goes on it); without one, give ReservationId and the money stays on account until it is allocated. Cash goes into the
/// open cash drawer session. PaymentReference: the UPI transaction id, the card terminal's approval code or the bank
/// UTR, never a card number.
/// </summary>
public sealed record CreatePaymentRequest(
    string? Method, decimal Amount, Guid? FolioId = null, Guid? ReservationId = null, string? PaymentReference = null, string? Note = null);

/// <summary>Puts money on account onto folios of the same booking.</summary>
public sealed record AllocatePaymentRequest(IReadOnlyList<AllocationLine>? Allocations);

public sealed record AllocationLine(Guid FolioId, decimal Amount);

/// <summary>
/// Gives money back (needs payment.refund, an Idempotency-Key header and a reason). Amount ≤ what is left to refund.
/// FolioId: the folio it comes off (needed when the payment is on more than one folio and on account is not enough);
/// Method: how the money goes back (default the payment's method; CASH comes out of the open drawer).
/// </summary>
public sealed record RefundPaymentRequest(decimal Amount, string? Reason, string? Method = null, Guid? FolioId = null, string? Reference = null);

/// <summary>Voids a payment entered by mistake (needs payment.void and a reason). Only while nothing is refunded; a cash
/// payment only while its drawer session is open.</summary>
public sealed record VoidPaymentRequest(string? Reason);

// ---------------------------------------------------------------- cash drawer

/// <summary>A property's cash drawer: its open session (if any), sessions waiting for variance approval, and the last
/// closed ones.</summary>
public sealed record CashDrawerDto(
    Guid Id, string Name, CashDrawerSessionDto? Current, IReadOnlyList<CashDrawerSessionDto> PendingApproval,
    IReadOnlyList<CashDrawerSessionDto> Recent);

/// <summary>
/// One shift. Status OPEN, VARIANCE_PENDING or CLOSED. Expected: the float + cash taken − cash refunded − voided cash
/// (live while open, fixed at the close). Variance = Counted − Expected: negative when short. Movements only on the
/// session's own page (null in lists).
/// </summary>
public sealed record CashDrawerSessionDto(
    Guid Id, Guid CashDrawerId, string DrawerName, string Status, DateTimeOffset OpenedAt, string? OpenedByName, decimal OpeningFloat,
    string? OpenNote, decimal CashIn, decimal CashOut, decimal Expected, int Payments, DateTimeOffset? ClosedAt, string? ClosedByName,
    decimal? Counted, decimal? Variance, string? CloseNote, DateTimeOffset? ApprovedAt, string? ApprovedByName, string? ApprovalNote,
    IReadOnlyList<CashDrawerMovementDto>? Movements);

/// <summary>A line of a session's ledger: + in, − out. PaymentNumber / Reference: the payment and booking it came from.</summary>
public sealed record CashDrawerMovementDto(
    Guid Id, string MovementType, decimal Amount, string Description, Guid? PaymentId, string? PaymentNumber, string? Reference,
    DateTimeOffset MovedAt, string? ByName);

/// <summary>Opens a shift with the cash counted in the drawer (the float). DrawerId: default the property's only drawer.</summary>
public sealed record OpenCashDrawerRequest(decimal OpeningFloat, string? Note = null, Guid? DrawerId = null);

/// <summary>Closes the open shift with the cash counted now. A count that differs from what is expected needs a note
/// saying why; the session then waits for a manager to approve the variance.</summary>
public sealed record CloseCashDrawerRequest(decimal CountedAmount, string? Note = null, Guid? DrawerId = null);

/// <summary>Approves a short or over drawer (needs cash_drawer.approve_variance and a note, 3–500 characters).</summary>
public sealed record ApproveVarianceRequest(string? Note);
