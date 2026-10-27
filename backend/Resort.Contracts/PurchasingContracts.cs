namespace Resort.Contracts;

// ---------------------------------------------------------------- suppliers (Sprint 16)

/// <summary>A supplier. Gstin null: unregistered (their bills carry no GST). StateName: "Goa (30)". Outstanding: what is
/// still to be paid on their invoices; Overdue: the part of it past its due date.</summary>
public sealed record SupplierDto(
    Guid Id, string Code, string Name, string? Gstin, string StateCode, string StateName, string? ContactName, string? Phone, string? Email,
    string? Address, int PaymentTermsDays, string? Note, bool IsActive, decimal Outstanding, decimal Overdue, uint Version);

/// <summary>Creates or edits a supplier. StateCode is taken from the GSTIN when there is one; without a GSTIN it is
/// required (the state they supply from).</summary>
public sealed record SaveSupplierRequest(
    string Code, string Name, string? Gstin, string? StateCode, string? ContactName, string? Phone, string? Email, string? Address,
    int PaymentTermsDays, string? Note, bool IsActive = true, uint? Version = null);

// ---------------------------------------------------------------- purchase requests

public sealed record PurchaseRequestItemDto(Guid Id, Guid ItemId, string ItemCode, string ItemName, string Unit, decimal Quantity, string? Note,
    decimal OnHand, decimal UnitCost);

/// <summary>What a store needs. Status OPEN, ORDERED (PurchaseOrderNumber) or CANCELLED (CancelReason).</summary>
public sealed record PurchaseRequestDto(
    Guid Id, string Number, Guid LocationId, string LocationName, DateOnly? NeededBy, string? Note, string Status, Guid? PurchaseOrderId,
    string? PurchaseOrderNumber, string? CancelReason, IReadOnlyList<PurchaseRequestItemDto> Items, string? ByName, DateTimeOffset CreatedAt, uint Version);

public sealed record CreatePurchaseRequestRequest(Guid LocationId, DateOnly? NeededBy, string? Note, IReadOnlyList<PurchaseRequestLine> Items);

/// <summary>Quantity in the stock item's unit.</summary>
public sealed record PurchaseRequestLine(Guid ItemId, decimal Quantity, string? Note);

/// <summary>A step that needs a reason: cancel a request, reject, cancel or close an order. Version: what the person last saw.</summary>
public sealed record PurchaseReasonRequest(string Reason, uint? Version);

// ---------------------------------------------------------------- purchase orders

/// <summary>
/// One order line. Quantity, Received, Remaining (still to come in) and Billed are in the item's unit; UnitPrice is one
/// unit before GST; Tax the line's GST; Total with GST.
/// </summary>
public sealed record PurchaseOrderLineDto(
    Guid Id, Guid ItemId, string ItemCode, string ItemName, string Unit, decimal Quantity, decimal UnitPrice, decimal GstRate, decimal TaxableValue,
    decimal Tax, decimal Total, decimal Received, decimal Remaining, decimal Billed, decimal ToBill);

public sealed record GrnLineDto(Guid OrderItemId, string ItemName, string Unit, decimal Quantity, decimal UnitCost, decimal Value);

/// <summary>A delivery (goods received note). Never changed.</summary>
public sealed record GrnDto(
    Guid Id, string Number, DateOnly ReceivedOn, string LocationName, string? ChallanNumber, string? Note, decimal Value,
    IReadOnlyList<GrnLineDto> Lines, string? ByName, DateTimeOffset CreatedAt);

/// <summary>
/// A purchase order with its lines, deliveries and supplier invoices. NeedsApproval: its total is over ApprovalLimit (the
/// limit when it was submitted, or today's while it is a draft), so someone with purchase.approve other than the person
/// who raised it must approve it. ApprovedByName null with ApprovedAt set: approved on submit (within the limit).
/// </summary>
public sealed record PurchaseOrderDto(
    Guid Id, string Number, Guid SupplierId, string SupplierName, string? SupplierGstin, string SupplierState, Guid? RequestId, string? RequestNumber,
    Guid LocationId, string LocationName, DateOnly OrderDate, DateOnly? ExpectedDate, string Status, bool IntraState, string? Note,
    decimal Subtotal, decimal TaxTotal, decimal Total, decimal ApprovalLimit, bool NeedsApproval,
    DateTimeOffset? SubmittedAt, string? SubmittedByName, Guid? SubmittedBy, DateTimeOffset? ApprovedAt, string? ApprovedByName, string? ApprovalNote,
    string? RejectedReason, string? EndReason, IReadOnlyList<PurchaseOrderLineDto> Lines, IReadOnlyList<GrnDto> Receipts,
    IReadOnlyList<SupplierInvoiceDto> Invoices, string? CreatedByName, DateTimeOffset CreatedAt, uint Version);

/// <summary>An order in the list. Received: share of the ordered value that has come in (0–100).</summary>
public sealed record PurchaseOrderSummaryDto(
    Guid Id, string Number, Guid SupplierId, string SupplierName, string LocationName, DateOnly OrderDate, DateOnly? ExpectedDate, string Status,
    decimal Total, bool NeedsApproval, decimal Received, string? CreatedByName, DateTimeOffset CreatedAt);

/// <summary>Orders newest first. ApprovalLimit: today's limit; Waiting: orders waiting for approval.</summary>
public sealed record PurchaseOrderListDto(IReadOnlyList<PurchaseOrderSummaryDto> Items, decimal ApprovalLimit, int Waiting);

/// <summary>Creates or edits a draft order. RequestId: the request it is raised from (that request becomes ORDERED).
/// Quantity in the item's unit; UnitPrice one unit before GST; GstRate one of 0, 0.25, 3, 5, 12, 18, 28, 40.</summary>
public sealed record SavePurchaseOrderRequest(
    Guid SupplierId, Guid LocationId, Guid? RequestId, DateOnly? ExpectedDate, string? Note, IReadOnlyList<SavePurchaseOrderLine> Lines, uint? Version);

public sealed record SavePurchaseOrderLine(Guid ItemId, decimal Quantity, decimal UnitPrice, decimal GstRate);

/// <summary>Submit or approve an order, with an optional note.</summary>
public sealed record PurchaseOrderStepRequest(string? Note, uint? Version);

/// <summary>
/// Goods received against an order. Each line: an order line and how much came in (in the item's unit), no more than is
/// still to come. LocationId: the store it went into (the order's when left out). ReceivedOn: the property's today when
/// left out; not in the future.
/// </summary>
public sealed record ReceiveGoodsRequest(DateOnly? ReceivedOn, Guid? LocationId, string? ChallanNumber, string? Note, IReadOnlyList<ReceiveGoodsLine> Lines);

public sealed record ReceiveGoodsLine(Guid OrderItemId, decimal Quantity);

// ---------------------------------------------------------------- supplier invoices and payments

public sealed record SupplierPaymentDto(
    Guid Id, string Number, DateOnly PaidOn, decimal Amount, string Method, string? Reference, string? Note, string? ByName, DateTimeOffset CreatedAt);

/// <summary>One billed line. OrderPrice: the order's price; PriceDiffers when the supplier billed another price.</summary>
public sealed record SupplierInvoiceLineDto(
    Guid OrderItemId, string ItemName, string Unit, decimal Quantity, decimal UnitPrice, decimal OrderPrice, bool PriceDiffers, decimal GstRate,
    decimal TaxableValue, decimal Cgst, decimal Sgst, decimal Igst, decimal Total);

/// <summary>
/// A supplier's bill. Number: ours (SI00001); SupplierNumber: theirs. InputTax = Cgst + Sgst + Igst; ItcEligible: the
/// property was a regular GST registrant on the invoice date. Balance: still to pay; Overdue: unpaid after DueDate.
/// </summary>
public sealed record SupplierInvoiceDto(
    Guid Id, string Number, Guid SupplierId, string SupplierName, string? SupplierGstin, Guid OrderId, string OrderNumber, string SupplierNumber,
    DateOnly InvoiceDate, DateOnly DueDate, bool IntraState, bool ItcEligible, decimal TaxableValue, decimal Cgst, decimal Sgst, decimal Igst,
    decimal InputTax, decimal Total, decimal AmountPaid, decimal Balance, string Status, bool Overdue, string? Note,
    IReadOnlyList<SupplierInvoiceLineDto> Lines, IReadOnlyList<SupplierPaymentDto> Payments, string? ByName, DateTimeOffset CreatedAt, uint Version);

/// <summary>Supplier invoices, latest first. Outstanding / Overdue: still to pay (of the listed); InputTax: GST on the
/// listed invoices that can be claimed.</summary>
public sealed record SupplierInvoiceListDto(IReadOnlyList<SupplierInvoiceDto> Items, decimal Outstanding, decimal Overdue, decimal InputTax);

/// <summary>
/// Records the supplier's bill against an order. Each line bills an order line: quantity no more than has come in and
/// not been billed, the supplier's price before GST and GST rate. An unregistered supplier's bill carries no GST.
/// DueDate: the invoice date plus the supplier's payment terms when left out.
/// </summary>
public sealed record RecordSupplierInvoiceRequest(
    string SupplierNumber, DateOnly InvoiceDate, DateOnly? DueDate, string? Note, IReadOnlyList<SupplierInvoiceLineRequest> Lines);

public sealed record SupplierInvoiceLineRequest(Guid OrderItemId, decimal Quantity, decimal UnitPrice, decimal GstRate);

/// <summary>A payment against a supplier invoice: CASH (from the open cash drawer), BANK_TRANSFER, UPI or CHEQUE (with
/// its UTR or cheque number). No more than the balance. PaidOn: today when left out; not in the future.</summary>
public sealed record PaySupplierRequest(decimal Amount, DateOnly? PaidOn, string Method, string? Reference, string? Note, uint? Version);
