using System.Globalization;
using System.Text;
using Resort.Domain.Common;
using Resort.Domain.Tax;

namespace Resort.Domain.Invoicing;

/// <summary>The kinds of numbered document (one series of each per property and financial year).</summary>
public static class DocumentTypes
{
    /// <summary>A GST tax invoice (the property charges GST).</summary>
    public const string Invoice = "INVOICE";
    /// <summary>A bill of supply: the property is under the composition scheme and charges no GST.</summary>
    public const string BillOfSupply = "BILL_OF_SUPPLY";
    public const string CreditNote = "CREDIT_NOTE";
    public const string DebitNote = "DEBIT_NOTE";
    public const string Receipt = "RECEIPT";

    public static string Words(string type) => type switch
    {
        Invoice => "Tax invoice",
        BillOfSupply => "Bill of supply",
        CreditNote => "Credit note",
        DebitNote => "Debit note",
        Receipt => "Receipt",
        _ => type,
    };
}

public static class CreditNoteTypes
{
    /// <summary>Credits the whole invoice (and its debit notes); the bill stays as it is, so it can be invoiced again
    /// with corrected details.</summary>
    public const string Cancel = "CANCEL";
    /// <summary>Credits chosen lines in full or in part; the folio gets a CREDIT line, so the guest owes less.</summary>
    public const string Reduce = "REDUCE";
    public static readonly string[] All = [Cancel, Reduce];
}

/// <summary>What an invoice has become through its credit notes (derived, never stored).</summary>
public static class InvoiceStatuses
{
    public const string Issued = "ISSUED";
    public const string PartlyCredited = "PARTLY_CREDITED";
    /// <summary>Credited in full by REDUCE notes: the guest owes nothing on it.</summary>
    public const string Credited = "CREDITED";
    /// <summary>A CANCEL credit note: its charges can be invoiced again.</summary>
    public const string Cancelled = "CANCELLED";
}

/// <summary>One running number per property, document type and financial year. Only <see cref="LastNumber"/> changes,
/// always by one, under the row lock of the upsert that takes it (gap-free).</summary>
public class InvoiceSeries : Entity, IPropertyScoped
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string DocumentType { get; set; } = default!;
    /// <summary>2026-27.</summary>
    public string FinancialYear { get; set; } = default!;
    /// <summary>BEACH/2627/ — the number follows.</summary>
    public string Prefix { get; set; } = default!;
    public short Width { get; set; }
    public int LastNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>The supplier and customer copied onto a document, and its totals. Shared by invoices and notes.</summary>
public interface IDocumentTotals
{
    decimal TaxableValue { get; set; }
    decimal Cgst { get; set; }
    decimal Sgst { get; set; }
    decimal Igst { get; set; }
    decimal Cess { get; set; }
    decimal TaxTotal { get; set; }
    decimal Total { get; set; }
}

/// <summary>A GST tax invoice (or bill of supply) for one folio. Immutable: corrected only by credit notes.</summary>
public class Invoice : Entity, IPropertyScoped, IAudited, IDocumentTotals
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid SeriesId { get; set; }
    public string DocumentType { get; set; } = DocumentTypes.Invoice;
    public string Number { get; set; } = default!;
    public string FinancialYear { get; set; } = default!;
    public DateOnly InvoiceDate { get; set; }
    public Guid FolioId { get; set; }
    public Guid ReservationId { get; set; }
    public Guid GstRegistrationId { get; set; }
    public string SupplierGstin { get; set; } = default!;
    public string SupplierLegalName { get; set; } = default!;
    public string? SupplierTradeName { get; set; }
    public string? SupplierAddress { get; set; }
    public string SupplierStateCode { get; set; } = default!;
    public Guid? BillToGuestId { get; set; }
    public string CustomerName { get; set; } = default!;
    /// <summary>Null: a B2C invoice.</summary>
    public string? CustomerGstin { get; set; }
    public string? CustomerAddress { get; set; }
    public string? CustomerStateCode { get; set; }
    /// <summary>The state where the supply is taxed. For a hotel stay and anything served at the property it is the
    /// property's state (IGST Act s.12(3)), whoever the customer is.</summary>
    public string PlaceOfSupply { get; set; } = default!;
    public decimal TaxableValue { get; set; }
    public decimal Cgst { get; set; }
    public decimal Sgst { get; set; }
    public decimal Igst { get; set; }
    public decimal Cess { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public string Currency { get; set; } = "INR";
    public string? Notes { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public Guid? IssuedBy { get; set; }
    public List<InvoiceItem> Items { get; set; } = [];

    public bool IsB2B => CustomerGstin is not null;
}

/// <summary>A billed line (invoice or debit note): one folio charge, its amounts and its tax rows as posted.</summary>
public abstract class BilledLine : Entity, IPropertyScoped
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public short LineNo { get; set; }
    public Guid FolioItemId { get; set; }
    public string Category { get; set; } = default!;
    public string Description { get; set; } = default!;
    public string? HsnSac { get; set; }
    public DateOnly ServiceDate { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal GrossAmount { get; set; }
    /// <summary>What credit notes had already taken off the charge before it was billed here.</summary>
    public decimal Discount { get; set; }
    public decimal TaxableValue { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal Total { get; set; }
}

public class InvoiceItem : BilledLine
{
    public Guid InvoiceId { get; set; }
    public List<InvoiceItemTax> Taxes { get; set; } = [];
}

/// <summary>A tax row of a document line: the rule and rate copied from the folio, never recalculated.</summary>
public abstract class DocumentTax : Entity, IPropertyScoped
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid? TaxRuleId { get; set; }
    public string TaxCode { get; set; } = default!;
    public decimal Rate { get; set; }
    public decimal TaxableValue { get; set; }
    public decimal Amount { get; set; }
}

public class InvoiceItemTax : DocumentTax
{
    public Guid InvoiceItemId { get; set; }
}

/// <summary>Charges posted to a folio after its invoice. Immutable.</summary>
public class DebitNote : Entity, IPropertyScoped, IAudited, IDocumentTotals
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid SeriesId { get; set; }
    public string Number { get; set; } = default!;
    public string FinancialYear { get; set; } = default!;
    public DateOnly NoteDate { get; set; }
    public Guid InvoiceId { get; set; }
    public string Reason { get; set; } = default!;
    public decimal TaxableValue { get; set; }
    public decimal Cgst { get; set; }
    public decimal Sgst { get; set; }
    public decimal Igst { get; set; }
    public decimal Cess { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public string Currency { get; set; } = "INR";
    public DateTimeOffset IssuedAt { get; set; }
    public Guid? IssuedBy { get; set; }
    public List<DebitNoteItem> Items { get; set; } = [];
}

public class DebitNoteItem : BilledLine
{
    public Guid DebitNoteId { get; set; }
    public List<DebitNoteItemTax> Taxes { get; set; } = [];
}

public class DebitNoteItemTax : DocumentTax
{
    public Guid DebitNoteItemId { get; set; }
}

/// <summary>Cancels or reduces an invoice, with a reason. Immutable.</summary>
public class CreditNote : Entity, IPropertyScoped, IAudited, IDocumentTotals
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid SeriesId { get; set; }
    public string Number { get; set; } = default!;
    public string FinancialYear { get; set; } = default!;
    public DateOnly NoteDate { get; set; }
    public Guid InvoiceId { get; set; }
    public string NoteType { get; set; } = CreditNoteTypes.Reduce;
    public string Reason { get; set; } = default!;
    public decimal TaxableValue { get; set; }
    public decimal Cgst { get; set; }
    public decimal Sgst { get; set; }
    public decimal Igst { get; set; }
    public decimal Cess { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public string Currency { get; set; } = "INR";
    public DateTimeOffset IssuedAt { get; set; }
    public Guid? IssuedBy { get; set; }
    public List<CreditNoteItem> Items { get; set; } = [];
}

public class CreditNoteItem : Entity, IPropertyScoped
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid CreditNoteId { get; set; }
    public short LineNo { get; set; }
    /// <summary>The line credited: on the invoice, or on one of its debit notes.</summary>
    public Guid? InvoiceItemId { get; set; }
    public Guid? DebitNoteItemId { get; set; }
    /// <summary>The folio charge behind the line.</summary>
    public Guid FolioItemId { get; set; }
    /// <summary>The CREDIT line the note put on the folio (REDUCE only).</summary>
    public Guid? FolioCreditItemId { get; set; }
    public string Category { get; set; } = default!;
    public string Description { get; set; } = default!;
    public string? HsnSac { get; set; }
    public decimal TaxableValue { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal Total { get; set; }
    public List<CreditNoteItemTax> Taxes { get; set; } = [];
}

public class CreditNoteItemTax : DocumentTax
{
    public Guid CreditNoteItemId { get; set; }
}

/// <summary>Acknowledges one payment. Numbered in its own series. Immutable.</summary>
public class Receipt : Entity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid SeriesId { get; set; }
    public string Number { get; set; } = default!;
    public string FinancialYear { get; set; } = default!;
    public DateOnly ReceiptDate { get; set; }
    public Guid PaymentId { get; set; }
    public Guid ReservationId { get; set; }
    public string ReceivedFrom { get; set; } = default!;
    public decimal Amount { get; set; }
    public string Method { get; set; } = default!;
    public string Currency { get; set; } = "INR";
    public DateTimeOffset IssuedAt { get; set; }
    public Guid? IssuedBy { get; set; }
}

public static class EInvoiceStatuses
{
    public const string Pending = "PENDING";
    public const string Generated = "GENERATED";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";
}

/// <summary>A document's registration with the invoice registration portal (IRN, acknowledgement, signed QR). Filled by
/// the e-invoicing integration (Sprint 25).</summary>
public class EInvoiceRecord : Entity, IPropertyScoped
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string DocumentType { get; set; } = default!;
    public Guid DocumentId { get; set; }
    public string Status { get; set; } = EInvoiceStatuses.Pending;
    public string? Irn { get; set; }
    public string? AckNo { get; set; }
    public DateTimeOffset? AckDate { get; set; }
    public string? SignedQr { get; set; }
    public string? SignedInvoice { get; set; }
    public string? Provider { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Invoice arithmetic and wording that needs no database. Pure and unit-tested.</summary>
public static class InvoiceMath
{
    /// <summary>The Indian financial year (1 April – 31 March) a day falls in: "2026-27".</summary>
    public static string FinancialYear(DateOnly day)
    {
        var start = day.Month >= 4 ? day.Year : day.Year - 1;
        return $"{start}-{(start + 1) % 100:D2}";
    }

    /// <summary>
    /// The prefix of a series: the property's code (letters and digits, at most 5), a letter for the document type
    /// (none for invoices) and the financial year as four digits: BEACH/2627/, BEACH/C2627/. With the number this keeps
    /// a GST document within 16 characters (CGST rule 46(b)).
    /// </summary>
    public static string Prefix(string propertyCode, string documentType, string financialYear)
    {
        var code = new string(propertyCode.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).Take(5).ToArray());
        if (code.Length == 0) code = "P";
        var letter = documentType switch
        {
            DocumentTypes.BillOfSupply => "B",
            DocumentTypes.CreditNote => "C",
            DocumentTypes.DebitNote => "D",
            DocumentTypes.Receipt => "R",
            _ => "",
        };
        return $"{code}/{letter}{financialYear[2..4]}{financialYear[5..7]}/";
    }

    /// <summary>Digits of the running number: 5 for invoices and receipts, 4 for the rest.</summary>
    public static short Width(string documentType) =>
        documentType is DocumentTypes.Invoice or DocumentTypes.Receipt ? (short)5 : (short)4;

    public static string Number(string prefix, short width, int n) => prefix + n.ToString(new string('0', width), CultureInfo.InvariantCulture);

    public static decimal Round(decimal v) => decimal.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The tax rows a credit of <paramref name="credit"/> (taxable value) takes from a line whose rows still have
    /// <paramref name="remaining"/> to credit. Crediting everything that is left takes exactly what is left (so a line is
    /// always credited to the paisa); a part takes each row's share of the value, taxed at the row's own rate.
    /// </summary>
    public static IReadOnlyList<(int Index, decimal TaxableValue, decimal Amount)> CreditTaxes(
        IReadOnlyList<(decimal Rate, decimal TaxableValue, decimal Amount)> remaining, decimal remainingValue, decimal credit)
    {
        if (credit <= 0 || credit > remainingValue) throw new ArgumentOutOfRangeException(nameof(credit));
        if (credit == remainingValue) return remaining.Select((r, i) => (i, r.TaxableValue, r.Amount)).ToList();
        return remaining.Select((r, i) =>
        {
            var value = Round(r.TaxableValue * credit / remainingValue);
            return (i, value, Math.Min(r.Amount, Round(value * r.Rate / 100m)));
        }).ToList();
    }

    /// <summary>Rupees in words, Indian style (lakh and crore): 12,650.50 → "Rupees Twelve Thousand Six Hundred Fifty and
    /// Fifty Paise Only".</summary>
    public static string AmountInWords(decimal amount)
    {
        amount = Math.Abs(Round(amount));
        var rupees = (long)decimal.Truncate(amount);
        var paise = (int)((amount - rupees) * 100);
        var sb = new StringBuilder("Rupees ").Append(rupees == 0 ? "Zero" : Words(rupees));
        if (paise > 0) sb.Append(" and ").Append(Words(paise)).Append(" Paise");
        return sb.Append(" Only").ToString();
    }

    private static readonly string[] Ones =
    [
        "", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve", "Thirteen",
        "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen",
    ];

    private static readonly string[] Tens = ["", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety"];

    private static string Words(long n)
    {
        var parts = new List<string>();
        void Add(long count, string unit)
        {
            if (count > 0) parts.Add($"{Below1000((int)count)} {unit}");
        }
        if (n >= 10_000_000)
        {
            parts.Add($"{Words(n / 10_000_000)} Crore");
            n %= 10_000_000;
        }
        Add(n / 100_000, "Lakh");
        n %= 100_000;
        Add(n / 1000, "Thousand");
        n %= 1000;
        if (n > 0) parts.Add(Below1000((int)n));
        return string.Join(" ", parts);
    }

    private static string Below1000(int n)
    {
        var parts = new List<string>();
        if (n >= 100)
        {
            parts.Add($"{Ones[n / 100]} Hundred");
            n %= 100;
        }
        if (n >= 20)
        {
            parts.Add(n % 10 == 0 ? Tens[n / 10] : $"{Tens[n / 10]} {Ones[n % 10]}");
        }
        else if (n > 0) parts.Add(Ones[n]);
        return string.Join(" ", parts);
    }

    /// <summary>Fills a document's totals from its lines' tax rows.</summary>
    public static void Total(IDocumentTotals doc, IEnumerable<(decimal TaxableValue, IEnumerable<(string Code, decimal Amount)> Taxes)> lines)
    {
        var list = lines.Select(l => (l.TaxableValue, Taxes: l.Taxes.ToList())).ToList();
        decimal Sum(string code) => list.Sum(l => l.Taxes.Where(t => t.Code == code).Sum(t => t.Amount));
        doc.TaxableValue = list.Sum(l => l.TaxableValue);
        doc.Cgst = Sum(TaxCodes.Cgst);
        doc.Sgst = Sum(TaxCodes.Sgst);
        doc.Igst = Sum(TaxCodes.Igst);
        doc.Cess = Sum(TaxCodes.Cess);
        doc.TaxTotal = doc.Cgst + doc.Sgst + doc.Igst + doc.Cess;
        doc.Total = doc.TaxableValue + doc.TaxTotal;
    }
}
