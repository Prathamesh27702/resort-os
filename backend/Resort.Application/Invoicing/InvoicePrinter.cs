using System.Globalization;
using Resort.Contracts;
using Resort.Domain.Invoicing;
using Resort.Domain.Organizations;
using Resort.Domain.Payments;
using Resort.Domain.Tax;

namespace Resort.Application.Invoicing;

/// <summary>
/// What goes on a printed invoice, bill of supply, credit or debit note and receipt (GST.md, "Fields printed on an
/// invoice"; CGST rules 46, 49 and 53): supplier and customer with GSTINs and states, place of supply, per line the
/// HSN/SAC, quantity, rate, discount, taxable value and each tax with its rate, the tax summary by HSN/SAC and rate,
/// totals in figures and words, the IRN when e-invoiced, and a place for the authorised signatory. Pure: no database.
/// </summary>
public sealed class InvoicePrinter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public PrintDocument Invoice(InvoiceDto i, (string? Phone, string? Email) contact)
    {
        var supply = i.DocumentType == DocumentTypes.BillOfSupply;
        var meta = new List<(string, string)>
        {
            ("Place of supply", i.PlaceOfSupplyName),
            ("Booking", $"{i.Reference} · folio {i.FolioNumber}"),
            ("Reverse charge", "No"),
        };
        var lines = i.Items.Where(l => l.DebitNoteNumber is null).ToList();
        var tables = new List<PrintTable> { LinesTable(lines, supply) };
        if (!supply && i.TaxSummary.Count > 0) tables.Add(SummaryTable(i.TaxSummary));
        if (i.Receipts.Count > 0)
            tables.Add(new PrintTable("Payments received", [new("Receipt", 3.8), new("Date", 2.4), new("Payment", 4.8), new("Amount", 3, true)],
                i.Receipts.Select(r => (IReadOnlyList<string>)
                [
                    r.Number, Day(r.ReceiptDate),
                    $"{r.PaymentNumber} · {PaymentMethods.Words(r.Method)}{(r.PaymentReference is { } p ? $" · {p}" : "")}{(r.Voided ? " · voided" : "")}",
                    Money(r.Amount),
                ]).ToList()));

        var notes = new List<string>();
        if (supply) notes.Add("Composition taxable person, not eligible to collect tax on supplies.");
        if (i.Notes is { } n) notes.Add(n);
        if (i.EInvoice is { Irn: { } irn } e) notes.Add($"IRN {irn} · Ack. no. {e.AckNo} · Ack. date {e.AckDate:dd MMM yyyy}");
        foreach (var c in i.CreditNotes)
            notes.Add($"Credit note {c.Number} of {Day(c.NoteDate)}: {Money(c.Total)} — {c.Reason}");
        foreach (var d in i.DebitNotes)
            notes.Add($"Debit note {d.Number} of {Day(d.NoteDate)}: {Money(d.Total)} — {d.Reason}");

        return new PrintDocument(
            supply ? "BILL OF SUPPLY" : "TAX INVOICE", "Original for recipient", i.Number, Day(i.InvoiceDate), meta,
            SupplierBlock(i.Supplier, contact), CustomerBlock(i.Customer, i.IsB2B),
            tables, Totals(i.TaxableValue, i.Cgst, i.Sgst, i.Igst, i.Cess, i.Total, "Invoice total"), i.AmountInWords, notes,
            i.Status == InvoiceStatuses.Cancelled ? "CANCELLED" : null, i.Supplier.Name, FileName(i.Number));
    }

    public PrintDocument Note(InvoiceDto i, NoteDto note, (string? Phone, string? Email) contact)
    {
        var credit = note.Kind == DocumentTypes.CreditNote;
        var meta = new List<(string, string)>
        {
            ("Against invoice", $"{i.Number} of {Day(i.InvoiceDate)}"),
            ("Place of supply", i.PlaceOfSupplyName),
            ("Booking", $"{i.Reference} · folio {i.FolioNumber}"),
            ("Reason", note.Reason),
        };
        if (note.NoteType == CreditNoteTypes.Cancel) meta.Insert(1, ("Type", "Cancels the invoice in full"));
        var table = new PrintTable(null,
            [new("#", 0.8), new("Description", 7.2), new("HSN/SAC", 1.8), new("Taxable ₹", 2.4, true), new("GST", 3.4), new("Total ₹", 2.4, true)],
            note.Lines.Select(l => (IReadOnlyList<string>)
                [l.LineNo.ToString(Inv), l.Description, l.HsnSac ?? "", Amount(l.TaxableValue), TaxText(l.Taxes), Amount(l.Total)]).ToList());
        var taxes = note.Lines.SelectMany(l => l.Taxes).ToList();
        decimal Sum(string code) => taxes.Where(t => t.Code == code).Sum(t => t.Amount);
        return new PrintDocument(credit ? "CREDIT NOTE" : "DEBIT NOTE", "Original for recipient", note.Number, Day(note.NoteDate), meta,
            SupplierBlock(i.Supplier, contact), CustomerBlock(i.Customer, i.IsB2B), [table],
            Totals(note.TaxableValue, Sum(TaxCodes.Cgst), Sum(TaxCodes.Sgst), Sum(TaxCodes.Igst), Sum(TaxCodes.Cess), note.Total,
                credit ? "Credit total" : "Debit total"),
            InvoiceMath.AmountInWords(note.Total), [], null, i.Supplier.Name, FileName(note.Number));
    }

    public PrintDocument Receipt(ReceiptDto r, Property property, GstRegistration? registration, IReadOnlyList<string> invoices)
    {
        var supplier = new DocumentPartyDto(registration?.LegalName ?? property.LegalName ?? property.Name, registration?.TradeName ?? property.Name,
            registration?.Gstin, InvoiceService.Address(property), registration?.StateCode, registration is null ? null : GstStates.Words(registration.StateCode));
        var meta = new List<(string, string)>
        {
            ("Booking", r.Reference),
            ("Payment", $"{r.PaymentNumber} · {PaymentMethods.Words(r.Method)}"),
            ("Received on", TimeZoneInfo.ConvertTime(r.ReceivedAt, TimeZoneInfo.FindSystemTimeZoneById(property.Timezone)).ToString("d MMM yyyy, HH:mm", Inv)),
        };
        if (r.PaymentReference is { } reference) meta.Add(("Reference", reference));
        if (invoices.Count > 0) meta.Add(("Invoice", string.Join(", ", invoices)));
        var notes = new List<string>();
        if (r.Voided) notes.Add($"Payment {r.PaymentNumber} was voided after this receipt was issued, so this receipt no longer counts.");
        return new PrintDocument("RECEIPT", null, r.Number, Day(r.ReceiptDate), meta, SupplierBlock(supplier, (property.Phone, property.Email)),
            new PrintParty("Received from", [r.ReceivedFrom]), [],
            [("Amount received", Money(r.Amount), true)], InvoiceMath.AmountInWords(r.Amount), notes,
            r.Voided ? "VOID" : null, supplier.Name, FileName(r.Number));
    }

    // ------------------------------------------------------------------ pieces

    private static PrintTable LinesTable(IReadOnlyList<DocumentLineDto> lines, bool supply)
    {
        var discount = lines.Any(l => l.Discount != 0);
        var columns = new List<PrintColumn> { new("#", 0.7), new("Description", (supply ? 7.0 : 4.0) + (discount ? 0 : 1.6)), new("HSN/SAC", 1.9), new("Qty", 0.9, true), new("Rate ₹", 1.8, true) };
        if (discount) columns.Add(new("Disc. ₹", 1.6, true));
        columns.Add(new("Taxable ₹", 1.9, true));
        if (!supply) columns.Add(new("GST", 3.3));
        columns.Add(new("Total ₹", 1.9, true));
        var rows = lines.Select(l =>
        {
            var row = new List<string> { l.LineNo.ToString(Inv), $"{l.Description}\n{Day(l.ServiceDate)}", l.HsnSac ?? "", Qty(l.Quantity), Amount(l.UnitPrice) };
            if (discount) row.Add(l.Discount == 0 ? "" : Amount(l.Discount));
            row.Add(Amount(l.TaxableValue));
            if (!supply) row.Add(TaxText(l.Taxes));
            row.Add(Amount(l.Total));
            return (IReadOnlyList<string>)row;
        }).ToList();
        return new PrintTable(null, columns, rows);
    }

    private static PrintTable SummaryTable(IReadOnlyList<TaxSummaryDto> summary) =>
        new("Tax summary",
            [new("HSN/SAC", 2.6), new("Tax", 2.2), new("Rate", 2.0, true), new("Taxable ₹", 3.4, true), new("Tax ₹", 3.0, true)],
            summary.Select(s => (IReadOnlyList<string>)[s.HsnSac ?? "—", s.Code, $"{s.Rate.ToString("0.##", Inv)}%", Amount(s.TaxableValue), Amount(s.Amount)])
                .ToList());

    private static List<(string, string, bool)> Totals(decimal taxable, decimal cgst, decimal sgst, decimal igst, decimal cess, decimal total, string label)
    {
        var rows = new List<(string, string, bool)> { ("Taxable value", Money(taxable), false) };
        if (cgst != 0) rows.Add(("CGST", Money(cgst), false));
        if (sgst != 0) rows.Add(("SGST", Money(sgst), false));
        if (igst != 0) rows.Add(("IGST", Money(igst), false));
        if (cess != 0) rows.Add(("Cess", Money(cess), false));
        rows.Add((label, Money(total), true));
        return rows;
    }

    private static PrintParty SupplierBlock(DocumentPartyDto s, (string? Phone, string? Email) contact)
    {
        var lines = new List<string>();
        if (s.TradeName is { } t && t != s.Name) lines.Add(t);
        lines.Add(s.Name);
        if (s.Address is { } a) lines.Add(a);
        if (s.Gstin is { } g) lines.Add($"GSTIN {g}");
        if (s.StateName is { } st) lines.Add($"State: {st}");
        var reach = string.Join(" · ", new[] { contact.Phone, contact.Email }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (reach.Length > 0) lines.Add(reach);
        return new PrintParty("Supplier", lines);
    }

    private static PrintParty CustomerBlock(DocumentPartyDto c, bool b2b)
    {
        var lines = new List<string> { c.Name };
        if (c.Address is { } a) lines.Add(a);
        lines.Add(b2b ? $"GSTIN {c.Gstin}" : "Unregistered (B2C)");
        if (c.StateName is { } st) lines.Add($"State: {st}");
        return new PrintParty("Bill to", lines);
    }

    private static string TaxText(IEnumerable<DocumentTaxDto> taxes)
    {
        var list = taxes.ToList();
        return list.Count == 0 ? "No GST" : string.Join("\n", list.Select(t => $"{t.Code} {t.Rate.ToString("0.##", Inv)}% {Amount(t.Amount)}"));
    }

    private static string Day(DateOnly d) => d.ToString("d MMM yyyy", Inv);
    private static string Qty(decimal q) => q.ToString("0.###", Inv);
    private static string Amount(decimal v) => v.ToString("#,##0.00", Inv);
    private static string Money(decimal v) => "₹" + Amount(v);
    private static string FileName(string number) => number.Replace('/', '-') + ".pdf";
}
