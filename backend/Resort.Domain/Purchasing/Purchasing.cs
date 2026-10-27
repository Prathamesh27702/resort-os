using Resort.Domain.Common;

namespace Resort.Domain.Purchasing;

public static class PurchaseRequestStatuses
{
    public const string Open = "OPEN";
    public const string Ordered = "ORDERED";
    public const string Cancelled = "CANCELLED";
}

/// <summary>
/// DRAFT → (submit) PENDING_APPROVAL → (approve) APPROVED → PARTLY_RECEIVED → RECEIVED. An order at or under the approval
/// limit is APPROVED when it is submitted. Reject sends it back to DRAFT. CANCELLED: nothing was received; CLOSED: part
/// received and the rest is not coming.
/// </summary>
public static class PurchaseOrderStatuses
{
    public const string Draft = "DRAFT";
    public const string PendingApproval = "PENDING_APPROVAL";
    public const string Approved = "APPROVED";
    public const string PartlyReceived = "PARTLY_RECEIVED";
    public const string Received = "RECEIVED";
    public const string Closed = "CLOSED";
    public const string Cancelled = "CANCELLED";

    /// <summary>Goods can come in against these.</summary>
    public static readonly string[] Receivable = [Approved, PartlyReceived];
    /// <summary>The supplier can bill these (something has come in).</summary>
    public static readonly string[] Billable = [PartlyReceived, Received, Closed];

    public static string Words(string status) => status switch
    {
        Draft => "a draft",
        PendingApproval => "waiting for approval",
        Approved => "approved",
        PartlyReceived => "partly received",
        Received => "fully received",
        Closed => "closed",
        _ => "cancelled",
    };
}

public static class SupplierInvoiceStatuses
{
    public const string Unpaid = "UNPAID";
    public const string PartPaid = "PART_PAID";
    public const string Paid = "PAID";
}

public static class SupplierPaymentMethods
{
    public const string Cash = "CASH";
    public const string BankTransfer = "BANK_TRANSFER";
    public const string Upi = "UPI";
    public const string Cheque = "CHEQUE";
    public static readonly string[] All = [Cash, BankTransfer, Upi, Cheque];

    public static string Words(string method) => method switch
    {
        Cash => "cash",
        BankTransfer => "bank transfer",
        Upi => "UPI",
        _ => "cheque",
    };
}

/// <summary>Who the property buys from. Gstin null: unregistered (charges no GST). StateCode: the state they supply from
/// (the GSTIN's first two digits when there is one), which decides CGST + SGST or IGST on their bills.</summary>
public class Supplier : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Gstin { get; set; }
    public string StateCode { get; set; } = default!;
    public string? ContactName { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public int PaymentTermsDays { get; set; }
    public string? Note { get; set; }
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }
}

/// <summary>What a store needs, numbered PR00001 … Becomes one purchase order.</summary>
public class PurchaseRequest : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Number { get; set; } = default!;
    public Guid LocationId { get; set; }
    public DateOnly? NeededBy { get; set; }
    public string? Note { get; set; }
    public string Status { get; set; } = PurchaseRequestStatuses.Open;
    public Guid? PurchaseOrderId { get; set; }
    public string? CancelReason { get; set; }
    public uint Version { get; set; }
    public List<PurchaseRequestItem> Items { get; set; } = [];
}

public class PurchaseRequestItem : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; set; }
    public Guid RequestId { get; set; }
    public Guid ItemId { get; set; }
    /// <summary>In the stock item's unit.</summary>
    public decimal Quantity { get; set; }
    public string? Note { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>An order to one supplier for one store, numbered PO00001 … Totals: before GST, GST, with GST.</summary>
public class PurchaseOrder : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Number { get; set; } = default!;
    public Guid SupplierId { get; set; }
    public Guid? RequestId { get; set; }
    public Guid LocationId { get; set; }
    public DateOnly OrderDate { get; set; }
    public DateOnly? ExpectedDate { get; set; }
    public string Status { get; set; } = PurchaseOrderStatuses.Draft;
    /// <summary>Supplier and property in the same state: CGST + SGST; otherwise IGST.</summary>
    public bool IntraState { get; set; }
    public string? Note { get; set; }
    public decimal Subtotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    /// <summary>The approval limit when it was submitted (the order needed approval when its total was over it).</summary>
    public decimal? ApprovalLimit { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public Guid? SubmittedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    /// <summary>Null when it was within the limit (approved on submit).</summary>
    public Guid? ApprovedBy { get; set; }
    public string? ApprovalNote { get; set; }
    /// <summary>The last rejection's reason (the order went back to DRAFT).</summary>
    public string? RejectedReason { get; set; }
    /// <summary>Why it was cancelled or closed.</summary>
    public string? EndReason { get; set; }
    public uint Version { get; set; }
    public List<PurchaseOrderItem> Items { get; set; } = [];
}

public class PurchaseOrderItem : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; set; }
    public Guid OrderId { get; set; }
    public Guid ItemId { get; set; }
    /// <summary>In the stock item's unit.</summary>
    public decimal Quantity { get; set; }
    /// <summary>One unit before GST.</summary>
    public decimal UnitPrice { get; set; }
    public decimal GstRate { get; set; }
    public decimal TaxableValue { get; set; }
    public decimal Tax { get; set; }
    public decimal ReceivedQuantity { get; set; }
    public decimal BilledQuantity { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A delivery against an order into a store, numbered GRN00001 … Never changed.</summary>
public class GoodsReceivedNote : Entity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Number { get; set; } = default!;
    public Guid OrderId { get; set; }
    public Guid SupplierId { get; set; }
    public Guid LocationId { get; set; }
    public DateOnly ReceivedOn { get; set; }
    public string? ChallanNumber { get; set; }
    public string? Note { get; set; }
    public decimal Value { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public List<GrnItem> Items { get; set; } = [];
}

/// <summary>What came in for one order line; one RECEIVED stock movement at the order's price.</summary>
public class GrnItem : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; set; }
    public Guid GrnId { get; set; }
    public Guid OrderItemId { get; set; }
    public Guid ItemId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal Value { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>The supplier's bill against an order, numbered SI00001 … (SupplierNumber: theirs). Fixed once saved except
/// what has been paid. Input tax = Cgst + Sgst + Igst.</summary>
public class SupplierInvoice : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Number { get; set; } = default!;
    public Guid SupplierId { get; set; }
    public Guid OrderId { get; set; }
    public string SupplierNumber { get; set; } = default!;
    public DateOnly InvoiceDate { get; set; }
    public DateOnly DueDate { get; set; }
    public string? SupplierGstin { get; set; }
    public string SupplierStateCode { get; set; } = default!;
    public string? BuyerStateCode { get; set; }
    public bool IntraState { get; set; }
    /// <summary>The property was a regular GST registrant on the invoice date, so the input tax can be claimed.</summary>
    public bool ItcEligible { get; set; }
    public decimal TaxableValue { get; set; }
    public decimal Cgst { get; set; }
    public decimal Sgst { get; set; }
    public decimal Igst { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public decimal AmountPaid { get; set; }
    public string Status { get; set; } = SupplierInvoiceStatuses.Unpaid;
    public string? Note { get; set; }
    public uint Version { get; set; }
    public List<SupplierInvoiceItem> Items { get; set; } = [];
}

public class SupplierInvoiceItem : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; set; }
    public Guid InvoiceId { get; set; }
    public Guid OrderItemId { get; set; }
    public Guid ItemId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal GstRate { get; set; }
    public decimal TaxableValue { get; set; }
    public decimal Cgst { get; set; }
    public decimal Sgst { get; set; }
    public decimal Igst { get; set; }
    public decimal Total { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Money paid against a supplier invoice, numbered SP00001 … Never changed. Cash leaves the open cash drawer.</summary>
public class SupplierPayment : Entity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Number { get; set; } = default!;
    public Guid SupplierId { get; set; }
    public Guid InvoiceId { get; set; }
    public DateOnly PaidOn { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = default!;
    public string? Reference { get; set; }
    public string? Note { get; set; }
    public Guid? CashDrawerSessionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

/// <summary>Purchase arithmetic that needs no database. Pure, unit-tested.</summary>
public static class PurchaseMath
{
    /// <summary>The GST rates a purchase line can carry (percent).</summary>
    public static readonly decimal[] GstRates = [0m, 0.25m, 3m, 5m, 12m, 18m, 28m, 40m];

    /// <summary>The default limit above which an order needs a second person's approval (₹10,000 with GST).</summary>
    public const decimal DefaultApprovalLimit = 10_000m;

    public static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Quantity × price before GST, to the paisa.</summary>
    public static decimal Taxable(decimal quantity, decimal unitPrice) => Round(quantity * unitPrice);

    /// <summary>
    /// The GST on a line: (CGST, SGST, IGST). Inside one state the rate is split evenly into CGST and SGST, each rounded to
    /// the paisa; across states it is all IGST.
    /// </summary>
    public static (decimal Cgst, decimal Sgst, decimal Igst) Tax(decimal taxable, decimal rate, bool intraState)
    {
        if (!intraState) return (0m, 0m, Round(taxable * rate / 100m));
        var each = Round(taxable * rate / 2m / 100m);
        return (each, each, 0m);
    }

    /// <summary>What is still to come in on a line.</summary>
    public static decimal Remaining(decimal ordered, decimal received) => ordered - received;

    /// <summary>An order needs a second person's approval when its total is over the limit.</summary>
    public static bool NeedsApproval(decimal total, decimal limit) => total > limit;

    /// <summary>The status an order takes after goods came in: RECEIVED when every line is complete.</summary>
    public static string StatusAfterReceipt(IEnumerable<(decimal Ordered, decimal Received)> lines) =>
        lines.All(l => l.Received >= l.Ordered) ? PurchaseOrderStatuses.Received : PurchaseOrderStatuses.PartlyReceived;

    /// <summary>The status of an invoice once <paramref name="paid"/> of <paramref name="total"/> is paid.</summary>
    public static string InvoiceStatus(decimal total, decimal paid) =>
        paid >= total ? SupplierInvoiceStatuses.Paid : paid == 0 ? SupplierInvoiceStatuses.Unpaid : SupplierInvoiceStatuses.PartPaid;
}
