using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using Resort.Application.Invoicing;

namespace Resort.Infrastructure.Documents;

/// <summary>
/// Draws invoices, notes and receipts as A4 PDFs with PDFsharp/MigraDoc (MIT licence; QuestPDF would need a paid licence
/// above USD 1M revenue). Every font request is served the embedded DejaVu Sans, which has the rupee sign, so the output
/// is the same on Windows, Linux and in containers with no fonts installed.
/// </summary>
public sealed class PdfRenderer : IPdfRenderer
{
    private const string Font = "DejaVu Sans";
    private static readonly Color Ink = new(0x1f, 0x29, 0x37);
    private static readonly Color Muted = new(0x6b, 0x72, 0x80);
    private static readonly Color Rule = new(0xd1, 0xd5, 0xdb);
    private static readonly Color Shade = new(0xf3, 0xf4, 0xf6);
    private static readonly Color Alert = new(0xb9, 0x1c, 0x1c);
    private static readonly Lock FontLock = new();

    public PdfRenderer()
    {
        lock (FontLock)
        {
            GlobalFontSettings.FontResolver ??= new EmbeddedFontResolver();
        }
    }

    public byte[] Render(PrintDocument d)
    {
        var doc = new Document();
        doc.Info.Title = $"{d.Title} {d.Number}";
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = Font;
        normal.Font.Size = 8.5;
        normal.Font.Color = Ink;

        var section = doc.AddSection();
        var page = section.PageSetup;
        page.PageFormat = PageFormat.A4;
        page.LeftMargin = page.RightMargin = Unit.FromCentimeter(1.5);
        page.TopMargin = Unit.FromCentimeter(1.4);
        page.BottomMargin = Unit.FromCentimeter(1.6);
        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 7;
        footer.Format.Font.Color = Muted;
        footer.AddText($"{System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(d.Title.ToLowerInvariant())} {d.Number} · page ");
        footer.AddPageField();
        footer.AddText(" of ");
        footer.AddNumPagesField();

        // title, number and date
        var head = section.AddTable();
        head.AddColumn(Unit.FromCentimeter(10));
        head.AddColumn(Unit.FromCentimeter(8));
        var hr = head.AddRow();
        var title = hr.Cells[0].AddParagraph(d.Title);
        title.Format.Font.Size = 15;
        title.Format.Font.Bold = true;
        if (d.Subtitle is { } sub) Small(hr.Cells[0].AddParagraph(sub));
        var num = hr.Cells[1].AddParagraph(d.Number);
        num.Format.Alignment = ParagraphAlignment.Right;
        num.Format.Font.Size = 12;
        num.Format.Font.Bold = true;
        var date = hr.Cells[1].AddParagraph($"Date: {d.Date}");
        date.Format.Alignment = ParagraphAlignment.Right;
        if (d.Watermark is { } mark)
        {
            var w = section.AddParagraph(mark);
            w.Format.Font.Size = 20;
            w.Format.Font.Bold = true;
            w.Format.Font.Color = Alert;
            w.Format.SpaceBefore = Unit.FromPoint(4);
        }
        Gap(section, 8);

        // supplier and customer side by side, then the meta rows
        var parties = section.AddTable();
        parties.AddColumn(Unit.FromCentimeter(9));
        parties.AddColumn(Unit.FromCentimeter(9));
        var pr = parties.AddRow();
        Party(pr.Cells[0], d.Supplier);
        if (d.Customer is { } c) Party(pr.Cells[1], c);
        Gap(section, 6);
        if (d.Meta.Count > 0)
        {
            var meta = section.AddTable();
            meta.AddColumn(Unit.FromCentimeter(3.4));
            meta.AddColumn(Unit.FromCentimeter(14.6));
            foreach (var (label, value) in d.Meta)
            {
                var row = meta.AddRow();
                Small(row.Cells[0].AddParagraph(label));
                row.Cells[1].AddParagraph(value);
            }
            Gap(section, 8);
        }

        foreach (var t in d.Tables) DrawTable(section, t);

        // totals on the right
        if (d.Totals.Count > 0)
        {
            var totals = section.AddTable();
            totals.AddColumn(Unit.FromCentimeter(11));
            totals.AddColumn(Unit.FromCentimeter(4.2));
            totals.AddColumn(Unit.FromCentimeter(2.8));
            totals.RightPadding = Unit.FromPoint(3);
            totals.TopPadding = totals.BottomPadding = Unit.FromPoint(1.5);
            foreach (var (label, value, bold) in d.Totals)
            {
                var row = totals.AddRow();
                var l = row.Cells[1].AddParagraph(label);
                var v = row.Cells[2].AddParagraph(value);
                l.Format.Alignment = v.Format.Alignment = ParagraphAlignment.Right;
                l.Format.Font.Bold = v.Format.Font.Bold = bold;
                if (bold) row.Cells[1].Borders.Top.Color = row.Cells[2].Borders.Top.Color = Ink;
            }
        }
        if (d.AmountInWords is { } words)
        {
            var p = section.AddParagraph(words);
            p.Format.SpaceBefore = Unit.FromPoint(6);
            p.Format.Font.Italic = true;
        }
        if (d.Notes.Count > 0)
        {
            Gap(section, 8);
            foreach (var n in d.Notes) Small(section.AddParagraph(n));
        }
        if (d.Signatory is { } who)
        {
            var s = section.AddParagraph($"For {who}");
            s.Format.Alignment = ParagraphAlignment.Right;
            s.Format.SpaceBefore = Unit.FromCentimeter(1);
            var sig = section.AddParagraph("Authorised signatory");
            sig.Format.Alignment = ParagraphAlignment.Right;
            sig.Format.SpaceBefore = Unit.FromCentimeter(1.2);
            Small(sig);
        }

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }

    private static void DrawTable(Section section, PrintTable t)
    {
        if (t.Title is { } title)
        {
            var p = section.AddParagraph(title);
            p.Format.Font.Bold = true;
            p.Format.SpaceAfter = Unit.FromPoint(3);
        }
        var table = section.AddTable();
        table.Borders.Color = Rule;
        table.Borders.Width = 0.5;
        table.LeftPadding = table.RightPadding = Unit.FromPoint(3);
        table.TopPadding = table.BottomPadding = Unit.FromPoint(3);
        foreach (var c in t.Columns)
        {
            var col = table.AddColumn(Unit.FromCentimeter(c.Width));
            col.Format.Alignment = c.Right ? ParagraphAlignment.Right : ParagraphAlignment.Left;
        }
        var head = table.AddRow();
        head.HeadingFormat = true;
        head.Shading.Color = Shade;
        head.Format.Font.Bold = true;
        for (var i = 0; i < t.Columns.Count; i++) Text(head.Cells[i], t.Columns[i].Header);
        foreach (var r in t.Rows)
        {
            var row = table.AddRow();
            for (var i = 0; i < t.Columns.Count && i < r.Count; i++) Text(row.Cells[i], r[i]);
        }
        Gap(section, 8);
    }

    private static void Party(Cell cell, PrintParty party)
    {
        Small(cell.AddParagraph(party.Heading.ToUpperInvariant()));
        for (var i = 0; i < party.Lines.Count; i++)
        {
            var p = cell.AddParagraph(party.Lines[i]);
            if (i == 0) p.Format.Font.Bold = true;
        }
    }

    /// <summary>Text with line breaks ("\n") in a cell.</summary>
    private static void Text(Cell cell, string text)
    {
        var p = cell.AddParagraph();
        var parts = text.Split('\n');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) p.AddLineBreak();
            if (i == 0) p.AddText(parts[i]);
            else p.AddFormattedText(parts[i]).Font.Color = Muted;
        }
    }

    private static void Small(Paragraph p)
    {
        p.Format.Font.Size = 7.5;
        p.Format.Font.Color = Muted;
    }

    private static void Gap(Section section, double points) => section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(points);

    /// <summary>Serves the embedded DejaVu Sans for every family asked for (regular and bold; italic is synthesised).</summary>
    private sealed class EmbeddedFontResolver : IFontResolver
    {
        public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic) =>
            new(bold ? "DejaVuSans-Bold" : "DejaVuSans", false, italic);

        public byte[] GetFont(string faceName)
        {
            using var s = typeof(PdfRenderer).Assembly.GetManifestResourceStream($"fonts/{faceName}.ttf")
                          ?? throw new InvalidOperationException($"Font {faceName} isn't embedded in Resort.Infrastructure.");
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
