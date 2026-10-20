namespace Resort.Contracts;

// ---------------------------------------------------------------- payments, refunds and the cash drawer (Sprint 10)

/// <summary>
/// A payment with what became of it. Method CASH, UPI, CARD or BANK_TRANSFER; Status CAPTURED, PARTIALLY_REFUNDED,
/// REFUNDED or CANCELLED (voided) for a payment taken at the desk. Method ONLINE (Sprint 11): paid through Razorpay,
/// recorded by its webhook only — PaymentReference is Razorpay's pay_… id, GatewayMethod how the guest paid (upi, card …),
/// PaymentLinkId the link it paid; it may also be AUTHORIZED (not in hand yet) or FAILED. Allocated: put on folios; OnAccount: still not on any
/// folio (an advance for the booking); Refundable: captured − already refunded. CanVoid / CantVoidReason: whether it can
/// still be voided (nothing refunded, and cash only while its drawer session is open).
/// </summary>
public sealed record PaymentDto(
    Guid Id, string Number, Guid ReservationId, string Reference, string GuestName, string Method, string Status,
    decimal Amount, decimal Allocated, decimal Refunded, decimal OnAccount, decimal Refundable, string Currency,
    string? PaymentReference, string? Note, Guid? CashDrawerSessionId, DateTimeOffset ReceivedAt, string? ReceivedByName,
    DateTimeOffset? VoidedAt, string? VoidedByName, string? VoidReason, bool CanVoid, string? CantVoidReason,
    IReadOnlyList<PaymentAllocationDto> Allocations, IReadOnlyList<RefundDto> Refunds, IReadOnlyList<PaymentFolioDto> Folios,
    string? GatewayMethod = null, Guid? PaymentLinkId = null);

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
/// Method: how the money goes back (default the payment's method; CASH comes out of the open drawer; ONLINE, only for an
/// online payment, sends it back through Razorpay and stays PENDING until Razorpay confirms it).
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

// ---------------------------------------------------------------- Razorpay payment links (Sprint 11)

/// <summary>Creates a payment link for a booking (needs payment.create and an Idempotency-Key header): an order at
/// Razorpay for Amount (₹1 up to what the booking still owes, less what open links already ask for). Note: shown to staff.</summary>
public sealed record CreatePaymentLinkRequest(Guid ReservationId, decimal Amount, string? Note = null);

/// <summary>Cancels an unpaid link (needs payment.create and a reason). The guest's page then says so; a payment that
/// still arrives is recorded, because the money is real.</summary>
public sealed record CancelPaymentLinkRequest(string? Reason);

/// <summary>
/// A payment link. Status CREATED (waiting for the guest), PAID (Razorpay's webhook confirmed a captured payment) or
/// CANCELLED. PayPath: the guest's page, /pay/&lt;token&gt; on the staff app's address. Attempts / LastError: the guest's
/// failed tries and Razorpay's reason for the last one. CallbackAt: when the guest's browser came back from Checkout (it
/// changes only what the page shows). Events: the webhooks received for it, oldest first (on the link's own page only).
/// </summary>
public sealed record PaymentLinkDto(
    Guid Id, Guid ReservationId, string Reference, string GuestName, string GatewayOrderId, decimal Amount, string Currency, string Status,
    string PayPath, string? Note, int Attempts, string? LastError, DateTimeOffset? CallbackAt, Guid? PaymentId, string? PaymentNumber,
    string? PaymentStatus, DateTimeOffset? PaidAt, DateTimeOffset CreatedAt, string? CreatedByName, DateTimeOffset? CancelledAt,
    string? CancelledByName, string? CancelReason, IReadOnlyList<WebhookEventDto>? Events);

/// <summary>A Razorpay webhook as received. Status PROCESSED, IGNORED (verified, nothing to do) or FAILED (our error; Razorpay
/// retries it). Outcome: what it did, or why it changed nothing.</summary>
public sealed record WebhookEventDto(Guid Id, string EventId, string EventType, string Status, string? Outcome, int Attempts, DateTimeOffset ReceivedAt);

/// <summary>
/// A booking's payment links and what it still owes: Due = the larger of the booking's price and its bills' total; Paid =
/// payments that count, net of refunds; Owed = Due − Paid; Open = what unpaid links ask for. CanCreate / CantCreateReason:
/// whether a new link can be sent now (and why not). Fake: the server uses the built-in fake Razorpay (no keys set).
/// </summary>
public sealed record BookingPaymentLinksDto(
    Guid ReservationId, string Reference, decimal Due, decimal Paid, decimal Owed, decimal Open, bool CanCreate, string? CantCreateReason,
    bool Fake, IReadOnlyList<PaymentLinkDto> Links);

/// <summary>
/// What the guest's pay page shows (anonymous, by the link's token). Status AWAITING_PAYMENT, CONFIRMING (the guest's
/// browser says it paid, or Razorpay authorised it; waiting for the webhook), PAID, CANCELLED or CLOSED (the booking was
/// cancelled). Message: the same, in words. KeyId and GatewayOrderId: what Razorpay Checkout needs (only while it can be
/// paid). Fake: the built-in fake Razorpay is in use, so the page offers test buttons instead of Checkout.
/// </summary>
public sealed record PayPageDto(
    string PropertyName, string BookingReference, string GuestName, DateOnly CheckIn, DateOnly CheckOut, decimal Amount, string Currency,
    string Status, string Message, string? KeyId, string? GatewayOrderId, bool Fake, string? LastError, string? PaymentNumber,
    DateTimeOffset? PaidAt);

/// <summary>What Razorpay Checkout hands the browser after a payment. Only changes what the page shows: the webhook
/// records the payment.</summary>
public sealed record PayCallbackRequest(string? RazorpayPaymentId, string? RazorpayOrderId, string? RazorpaySignature);

/// <summary>The fake Razorpay's checkout (development and tests only): pay the link, or fail with a reason.</summary>
public sealed record FakeCheckoutRequest(bool Succeed = true, string? Method = null);

/// <summary>The answer to Razorpay's webhook: processed, duplicate (already processed once) or ignored, and why.</summary>
public sealed record WebhookAckDto(string Status, string Message);
