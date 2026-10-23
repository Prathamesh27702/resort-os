using Resort.Application.Invoicing;
using Resort.Contracts;
using Resort.Domain.Invoicing;
using Resort.Domain.Tax;

namespace Resort.Tests.Unit;

/// <summary>Sprint 12 — invoice arithmetic and wording: the financial year, series prefixes within GST's 16 characters,
/// amounts in words (lakh and crore), the tax a part credit takes off a line, document totals with IGST, and what is
/// printed.</summary>
public class InvoiceUnitTests
{
    // ------------------------------------------------------------------ numbering

    [Theory]
    [InlineData("2026-04-01", "2026-27")]
    [InlineData("2027-03-31", "2026-27")]
    [InlineData("2027-04-01", "2027-28")]
    [InlineData("2026-01-15", "2025-26")]
    [InlineData("2099-12-31", "2099-00")]
    public void The_financial_year_runs_from_1_April_to_31_March(string day, string fy) =>
        Assert.Equal(fy, InvoiceMath.FinancialYear(DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("BEACH01", "INVOICE", "BEACH/2627/", 5)]
    [InlineData("BEACH01", "BILL_OF_SUPPLY", "BEACH/B2627/", 4)]
    [InlineData("BEACH01", "CREDIT_NOTE", "BEACH/C2627/", 4)]
    [InlineData("BEACH01", "DEBIT_NOTE", "BEACH/D2627/", 4)]
    [InlineData("BEACH01", "RECEIPT", "BEACH/R2627/", 5)]
    [InlineData("SEA_VIEW_2", "INVOICE", "SEAVI/2627/", 5)]       // underscores aren't allowed in a GST invoice number
    [InlineData("GOA", "CREDIT_NOTE", "GOA/C2627/", 4)]
    [InlineData("__", "INVOICE", "P/2627/", 5)]
    public void A_series_prefix_is_the_property_code_a_letter_for_the_type_and_the_year(string code, string type, string prefix, short width)
    {
        Assert.Equal(prefix, InvoiceMath.Prefix(code, type, "2026-27"));
        Assert.Equal(width, InvoiceMath.Width(type));
    }

    [Fact]
    public void GST_document_numbers_stay_within_16_characters_and_use_only_letters_digits_slash_and_dash()
    {
        foreach (var type in new[] { DocumentTypes.Invoice, DocumentTypes.BillOfSupply, DocumentTypes.CreditNote, DocumentTypes.DebitNote })
        {
            var prefix = InvoiceMath.Prefix("LONGPROPERTYCODE", type, "2026-27");
            var last = InvoiceMath.Number(prefix, InvoiceMath.Width(type), type == DocumentTypes.Invoice ? 99999 : 9999);
            Assert.True(last.Length <= 16, $"{last} is {last.Length} characters");
            Assert.Matches("^[A-Z0-9/-]+$", last);
        }
        Assert.Equal("BEACH/2627/00001", InvoiceMath.Number("BEACH/2627/", 5, 1));
        Assert.Equal("BEACH/C2627/0042", InvoiceMath.Number("BEACH/C2627/", 4, 42));
    }

    // ------------------------------------------------------------------ amount in words

    [Theory]
    [InlineData("0", "Rupees Zero Only")]
    [InlineData("1", "Rupees One Only")]
    [InlineData("15", "Rupees Fifteen Only")]
    [InlineData("100000", "Rupees One Lakh Only")]
    [InlineData("8872.50", "Rupees Eight Thousand Eight Hundred Seventy Two and Fifty Paise Only")]
    [InlineData("12650.05", "Rupees Twelve Thousand Six Hundred Fifty and Five Paise Only")]
    [InlineData("12345678", "Rupees One Crore Twenty Three Lakh Forty Five Thousand Six Hundred Seventy Eight Only")]
    [InlineData("1000000000", "Rupees One Hundred Crore Only")]
    [InlineData("-90", "Rupees Ninety Only")]
    public void Amounts_are_written_in_words_with_lakh_and_crore(string amount, string words) =>
        Assert.Equal(words, InvoiceMath.AmountInWords(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)));

    // ------------------------------------------------------------------ credit note tax

    private static readonly (decimal Rate, decimal TaxableValue, decimal Amount)[] RoomNight = [(2.5m, 3500m, 87.50m), (2.5m, 3500m, 87.50m)];

    [Fact]
    public void Crediting_part_of_a_line_takes_each_tax_at_its_own_rate_on_the_part()
    {
        var part = InvoiceMath.CreditTaxes(RoomNight, 3500m, 1000m);
        Assert.Equal(new[] { (0, 1000m, 25.00m), (1, 1000m, 25.00m) }, part);
    }

    [Fact]
    public void Crediting_what_is_left_takes_exactly_what_is_left_so_a_line_always_credits_to_the_paisa()
    {
        // ₹3,500 at 2.5%: a third (₹1,166.67) rounds to ₹29.17 a tax; after two thirds, the rest is exactly what remains
        var first = InvoiceMath.CreditTaxes(RoomNight, 3500m, 1166.67m);
        Assert.Equal(29.17m, first[0].Amount);
        var left = RoomNight.Select(r => (r.Rate, r.TaxableValue - 2 * 1166.67m, r.Amount - 2 * 29.17m)).ToList();
        var last = InvoiceMath.CreditTaxes(left, 1166.66m, 1166.66m);
        Assert.Equal(new[] { (0, 1166.66m, 29.16m), (1, 1166.66m, 29.16m) }, last);
        Assert.Equal(87.50m, 2 * 29.17m + last[0].Amount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3500.01)]
    public void A_credit_of_nothing_or_more_than_is_left_is_refused(decimal credit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => InvoiceMath.CreditTaxes(RoomNight, 3500m, credit));

    // ------------------------------------------------------------------ totals (required: B2B intra- and inter-state)

    [Fact]
    public void Document_totals_add_up_CGST_and_SGST_inside_the_state()
    {
        var invoice = new Invoice();
        InvoiceMath.Total(invoice, [
            (3500m, [("CGST", 87.50m), ("SGST", 87.50m)]),
            (3500m, [("CGST", 87.50m), ("SGST", 87.50m)]),
            (1450m, [("CGST", 36.25m), ("SGST", 36.25m)]),
            (300m, []),    // laundry: no GST
        ]);
        Assert.Equal((8750m, 211.25m, 211.25m, 0m, 0m, 422.50m, 9172.50m),
            (invoice.TaxableValue, invoice.Cgst, invoice.Sgst, invoice.Igst, invoice.Cess, invoice.TaxTotal, invoice.Total));
    }

    [Fact]
    public void Document_totals_add_up_IGST_across_states()
    {
        var note = new DebitNote();
        InvoiceMath.Total(note, [(9000m, [("IGST", 1620m)]), (1450m, [("IGST", 72.50m)])]);
        Assert.Equal((10450m, 0m, 0m, 1692.50m, 1692.50m, 12142.50m),
            (note.TaxableValue, note.Cgst, note.Sgst, note.Igst, note.TaxTotal, note.Total));
    }

    // ------------------------------------------------------------------ what is printed

    private static InvoiceDto Sample(string? gstin, string status = InvoiceStatuses.Issued, string type = DocumentTypes.Invoice) => new(
        Guid.NewGuid(), "BEACH/2627/00007", type, DocumentTypes.Words(type), "2026-27", new DateOnly(2026, 10, 23), Guid.NewGuid(), "F00012",
        Guid.NewGuid(), "R00031",
        new DocumentPartyDto("Test Resorts Pvt Ltd", "Main Resort", "30AABCT1234C1Z9", "Calangute, Goa", "30", "Goa (30)"),
        new DocumentPartyDto(gstin is null ? "Asha Rao" : "Acme Travels Pvt Ltd", null, gstin, "Pune", gstin?[..2] ?? "27", "Maharashtra (27)"),
        gstin is not null, "30", "Goa (30)",
        [new DocumentLineDto(Guid.NewGuid(), 1, Guid.NewGuid(), "ROOM", "Room night: Room 101", "996311", new DateOnly(2026, 10, 22), 1, 3500m,
            3500m, 0m, 3500m, 175m, 3675m, [new("CGST", 2.5m, 3500m, 87.5m), new("SGST", 2.5m, 3500m, 87.5m)], 0m, 3500m, null)],
        [new TaxSummaryDto("996311", "CGST", 2.5m, 3500m, 87.5m), new TaxSummaryDto("996311", "SGST", 2.5m, 3500m, 87.5m)],
        3500m, 87.5m, 87.5m, 0m, 0m, 175m, 3675m, InvoiceMath.AmountInWords(3675m), "INR", null, DateTimeOffset.UtcNow, "Owner", status,
        3675m, 0m, 3675m, [], [], [], null, 0, 0m);

    [Fact]
    public void A_B2B_invoice_prints_both_GSTINs_the_place_of_supply_the_SAC_and_each_tax_with_its_rate()
    {
        var doc = new InvoicePrinter().Invoice(Sample("27AAPFU0939F1ZV"), ("+91 832 000 0000", "desk@example.com"));
        Assert.Equal(("TAX INVOICE", "BEACH/2627/00007", "23 Oct 2026", "BEACH-2627-00007.pdf"), (doc.Title, doc.Number, doc.Date, doc.FileName));
        Assert.Contains("GSTIN 30AABCT1234C1Z9", doc.Supplier.Lines);
        Assert.Contains("GSTIN 27AAPFU0939F1ZV", doc.Customer!.Lines);
        Assert.Contains(("Place of supply", "Goa (30)"), doc.Meta);
        var line = doc.Tables[0].Rows.Single();
        Assert.Equal("996311", line[2]);
        Assert.Equal("CGST 2.5% 87.50\nSGST 2.5% 87.50", line[6]);
        Assert.Equal(("Invoice total", "₹3,675.00", true), doc.Totals[^1]);
        Assert.Equal("Rupees Three Thousand Six Hundred Seventy Five Only", doc.AmountInWords);
        Assert.Null(doc.Watermark);
        Assert.Equal("Test Resorts Pvt Ltd", doc.Signatory);
    }

    [Fact]
    public void A_B2C_invoice_says_unregistered_and_a_cancelled_one_says_cancelled_and_a_bill_of_supply_shows_no_GST()
    {
        var b2c = new InvoicePrinter().Invoice(Sample(null, InvoiceStatuses.Cancelled), (null, null));
        Assert.Contains("Unregistered (B2C)", b2c.Customer!.Lines);
        Assert.Equal("CANCELLED", b2c.Watermark);

        var supply = new InvoicePrinter().Invoice(Sample(null, type: DocumentTypes.BillOfSupply), (null, null));
        Assert.Equal("BILL OF SUPPLY", supply.Title);
        Assert.DoesNotContain(supply.Tables[0].Columns, c => c.Header == "GST");
        Assert.Contains("Composition taxable person, not eligible to collect tax on supplies.", supply.Notes);
    }
}
