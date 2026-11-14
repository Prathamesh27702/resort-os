namespace Resort.Application.Invoicing;

/// <summary>A table on a printed document. Right-aligned columns hold amounts.</summary>
public sealed record PrintColumn(string Header, double Width, bool Right = false);

public sealed record PrintTable(string? Title, IReadOnlyList<PrintColumn> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>A block of lines under a heading: "Supplier", "Bill to".</summary>
public sealed record PrintParty(string Heading, IReadOnlyList<string> Lines);

/// <summary>
/// A document laid out for printing (invoice, bill of supply, credit or debit note, receipt), in plain strings with the
/// amounts already formatted. The application decides what is printed; <see cref="IPdfRenderer"/> only draws it.
/// </summary>
public sealed record PrintDocument(
    string Title, string? Subtitle, string Number, string Date, IReadOnlyList<(string Label, string Value)> Meta,
    PrintParty Supplier, PrintParty? Customer, IReadOnlyList<PrintTable> Tables,
    IReadOnlyList<(string Label, string Value, bool Bold)> Totals, string? AmountInWords, IReadOnlyList<string> Notes,
    string? Watermark, string? Signatory, string FileName, PrintQr? Qr = null);

/// <summary>A QR code printed at the foot of a document (the UPI QR on an invoice with money still to pay): the PNG and the
/// lines beside it.</summary>
public sealed record PrintQr(byte[] Png, string Caption, IReadOnlyList<string> Lines);

/// <summary>What the property prints about itself besides its GST registration: how to reach it and its FSSAI number.</summary>
public sealed record PropertyPrint(string? Phone, string? Email, string? FssaiLicenceNo = null);

/// <summary>Draws a <see cref="PrintDocument"/> as an A4 PDF (Infrastructure: PDFsharp/MigraDoc, MIT-licensed).</summary>
public interface IPdfRenderer
{
    byte[] Render(PrintDocument document);
}
