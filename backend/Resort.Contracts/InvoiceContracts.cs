namespace Resort.Contracts;

// ---------------------------------------------------------------- invoices, credit and debit notes, receipts (Sprint 12)

/// <summary>A supplier or customer as printed on a document. StateName: "Goa (30)".</summary>
public sealed record DocumentPartyDto(string Name, string? TradeName, string? Gstin, string? Address, string? StateCode, string? StateName);

/// <summary>One tax on a document line: CGST 2.5% on ₹3,500 = ₹87.50, as posted on the folio.</summary>
public sealed record DocumentTaxDto(string Code, decimal Rate, decimal TaxableValue, decimal Amount);

/// <summary>
/// A billed line (invoice or debit note): one folio charge. Gross = quantity × unit price; Discount = what credit notes had
/// already taken off the charge before it was billed; TaxableValue = Gross − Discount. Credited / Creditable: the taxable
/// value credit notes have taken off this line since, and what is left to credit. Id is null on a preview.
/// </summary>
public sealed record DocumentLineDto(
    Guid? Id, int LineNo, Guid FolioItemId, string Category, string Description, string? HsnSac, DateOnly ServiceDate,
    decimal Quantity, decimal UnitPrice, decimal GrossAmount, decimal Discount, decimal TaxableValue, decimal TaxAmount, decimal Total,
    IReadOnlyList<DocumentTaxDto> Taxes, decimal Credited, decimal Creditable, string? DebitNoteNumber);

/// <summary>Tax by HSN/SAC and rate (the summary printed under the lines).</summary>
public sealed record TaxSummaryDto(string? HsnSac, string Code, decimal Rate, decimal TaxableValue, decimal Amount);

/// <summary>A line of a credit note: what it took off an invoice (or debit note) line.</summary>
public sealed record NoteLineDto(int LineNo, string Description, string? HsnSac, decimal TaxableValue, decimal TaxAmount, decimal Total,
    IReadOnlyList<DocumentTaxDto> Taxes);

/// <summary>A credit note (Kind CREDIT_NOTE; NoteType CANCEL or REDUCE) or a debit note (Kind DEBIT_NOTE) of an invoice.</summary>
public sealed record NoteDto(
    Guid Id, string Kind, string Number, string? NoteType, DateOnly NoteDate, string Reason, decimal TaxableValue, decimal TaxTotal,
    decimal Total, DateTimeOffset IssuedAt, string? IssuedByName, IReadOnlyList<NoteLineDto> Lines);

/// <summary>A payment receipt. Voided: the payment was voided after the receipt was issued (the PDF says so).</summary>
public sealed record ReceiptDto(
    Guid Id, string Number, DateOnly ReceiptDate, Guid PaymentId, string PaymentNumber, Guid ReservationId, string Reference,
    string ReceivedFrom, decimal Amount, string Method, string? PaymentReference, DateTimeOffset ReceivedAt, string PaymentStatus,
    bool Voided, DateTimeOffset IssuedAt, string? IssuedByName);

/// <summary>The invoice registration portal's answer for a document (filled from Sprint 25).</summary>
public sealed record EInvoiceDto(string Status, string? Irn, string? AckNo, DateTimeOffset? AckDate, string? LastError);

/// <summary>
/// An invoice with everything about it. Status: ISSUED, PARTLY_CREDITED, CREDITED (reduced to nothing) or CANCELLED (a
/// CANCEL credit note: its charges can be invoiced again). Billed = the invoice plus its debit notes; Credited = its
/// credit notes; Outstanding = Billed − Credited. UnbilledCharges / UnbilledAmount: charges on the folio posted after it,
/// for a debit note.
/// </summary>
public sealed record InvoiceDto(
    Guid Id, string Number, string DocumentType, string Title, string FinancialYear, DateOnly InvoiceDate, Guid FolioId, string FolioNumber,
    Guid ReservationId, string Reference, DocumentPartyDto Supplier, DocumentPartyDto Customer, bool IsB2B, string PlaceOfSupply,
    string PlaceOfSupplyName, IReadOnlyList<DocumentLineDto> Items, IReadOnlyList<TaxSummaryDto> TaxSummary,
    decimal TaxableValue, decimal Cgst, decimal Sgst, decimal Igst, decimal Cess, decimal TaxTotal, decimal Total, string AmountInWords,
    string Currency, string? Notes, DateTimeOffset IssuedAt, string? IssuedByName, string Status,
    decimal Billed, decimal Credited, decimal Outstanding, IReadOnlyList<NoteDto> CreditNotes, IReadOnlyList<NoteDto> DebitNotes,
    IReadOnlyList<ReceiptDto> Receipts, EInvoiceDto? EInvoice, int UnbilledCharges, decimal UnbilledAmount);

/// <summary>A row on the Invoices screen.</summary>
public sealed record InvoiceListItemDto(
    Guid Id, string Number, string DocumentType, DateOnly InvoiceDate, Guid FolioId, string FolioNumber, Guid ReservationId, string Reference,
    string CustomerName, string? CustomerGstin, decimal TaxableValue, decimal TaxTotal, decimal Total, decimal Credited, string Status,
    int CreditNotes, int DebitNotes, string Currency);

/// <summary>
/// What issuing an invoice for a folio would produce, for the Issue invoice dialog: the customer it would default to
/// (the folio's bill-to guest, else the booking's guest; their company and GSTIN make it B2B), the lines and totals.
/// CantIssueReason says why it can't be issued now (already invoiced, nothing to invoice, no GST registration …).
/// GstinProblem: the guest's GSTIN on file isn't valid (the dialog shows it under the GSTIN field).
/// </summary>
public sealed record InvoicePreviewDto(
    Guid FolioId, string FolioNumber, Guid ReservationId, string Reference, string DocumentType, DocumentPartyDto? Supplier,
    Guid? BillToGuestId, string CustomerName, string? CustomerGstin, string? CustomerAddress, string? CustomerStateCode, string? GstinProblem,
    IReadOnlyList<DocumentLineDto> Lines, decimal TaxableValue, decimal TaxTotal, decimal Total, Guid? InvoiceId, string? InvoiceNumber,
    string? CantIssueReason);

/// <summary>
/// Issues the GST invoice of a folio (needs an Idempotency-Key). Leave CustomerName empty to bill the folio's bill-to
/// guest as on file (their company and GSTIN make it B2B). With CustomerName, the customer is exactly what is sent: a
/// CustomerGstin makes it B2B (validated, check digit included; its state is the customer's state), none makes it B2C.
/// </summary>
public sealed record IssueInvoiceRequest(
    string? CustomerName = null, string? CustomerGstin = null, string? CustomerAddress = null, string? CustomerStateCode = null,
    string? Notes = null);

/// <summary>A line to credit: an invoice line or a debit note line (LineId), and the taxable value to take off it (null =
/// everything still on it). Tax is credited at the line's own rates.</summary>
public sealed record CreditNoteLineRequest(Guid LineId, decimal? Amount = null);

/// <summary>
/// Issues a credit note on an invoice (needs an Idempotency-Key and a reason). Type CANCEL credits the whole invoice and its
/// debit notes (needs invoice.cancel; Lines must be empty): the bill stays as it is and can be invoiced again. Type REDUCE
/// credits the chosen Lines; the folio gets a CREDIT line for each, so the guest owes that much less.
/// </summary>
public sealed record CreateCreditNoteRequest(string? Type, string? Reason, IReadOnlyList<CreditNoteLineRequest>? Lines = null);

/// <summary>Bills charges posted to the folio after its invoice (needs an Idempotency-Key and a reason). FolioItemIds:
/// which of them (null = all of them).</summary>
public sealed record CreateDebitNoteRequest(string? Reason, IReadOnlyList<Guid>? FolioItemIds = null);
