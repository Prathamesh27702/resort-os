using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Resort.Contracts;
using Resort.Tests.Support;
using static Resort.Tests.Support.BookingKit;

namespace Resort.Tests.Integration;

/// <summary>Sprint 12 — GST invoices, credit and debit notes, receipts. Check-out turns the bill into a GST invoice with a
/// gap-free number and gives every payment a receipt; the amounts and tax rates are copied from the folio; an invoice is
/// corrected only by a credit note (cancel, or reduce lines) and late charges go on a debit note; nothing issued can be
/// changed or deleted, not even directly in the database. Real API, real PostgreSQL. Tests in this class run one at a
/// time (xUnit), so series numbers can be checked exactly.</summary>
public class InvoiceTests(FolioFactory app) : IClassFixture<FolioFactory>
{
    private const string DemoGstin = "30AABCT1234C1Z9";   // the property: Goa
    private const string MaharashtraGstin = "27AAPFU0939F1ZV";
    private static readonly string GoaCustomerGstin = Gst("30AAACR5055K1Z");

    // ------------------------------------------------------------------ the sprint review demo (acceptance)

    [Fact]
    public async Task Check_out_issues_a_GST_invoice_and_a_receipt_as_PDFs_and_a_credit_note_cancels_it_so_it_can_be_issued_again()
    {
        var (owner, pid) = await OwnerAsync();
        var desk = await StaffAsync("RECEPTIONIST", pid);
        var accountant = await StaffAsync("ACCOUNTANT", pid);
        var (folio, booking) = await InHouseAsync(owner, pid, nights: 1);               // ₹3,500 + 5% GST
        await ChargeAsync(desk, pid, folio.Id, "FOOD", "Dinner for two", 1450m);         // + 5% GST
        await PayAsync(desk, pid, new CreatePaymentRequest("UPI", 5197.50m, folio.Id, PaymentReference: "412345678901"));

        await FrontDeskTests.OkAsync(await desk.PostAsync(FrontDeskTests.CheckOutUrl(pid, booking), new CheckOutRequest(null)));

        var listed = Assert.Single((await ListAsync(desk, pid, $"reservationId={booking.Id}")).Items);
        var invoice = await GetAsync(desk, pid, listed.Id);
        Assert.Matches(@"^[A-Z0-9]{1,5}/\d{4}/\d{5}$", invoice.Number);
        Assert.True(invoice.Number.Length <= 16);
        Assert.Equal(("INVOICE", "Tax invoice", "ISSUED", false, folio.Number, booking.Reference),
            (invoice.DocumentType, invoice.Title, invoice.Status, invoice.IsB2B, invoice.FolioNumber, invoice.Reference));
        Assert.Equal((DemoGstin, "30", "Goa (30)"), (invoice.Supplier.Gstin, invoice.PlaceOfSupply, invoice.PlaceOfSupplyName));
        Assert.Equal(new (string, string?, decimal, decimal)[] { ("ROOM", "996311", 3500m, 175m), ("FOOD", "996331", 1450m, 72.50m) },
            invoice.Items.Select(i => (i.Category, i.HsnSac, i.TaxableValue, i.TaxAmount)));
        Assert.Equal((4950m, 123.75m, 123.75m, 0m, 247.50m, 5197.50m),
            (invoice.TaxableValue, invoice.Cgst, invoice.Sgst, invoice.Igst, invoice.TaxTotal, invoice.Total));
        Assert.Equal("Rupees Five Thousand One Hundred Ninety Seven and Fifty Paise Only", invoice.AmountInWords);
        Assert.Equal(new (string?, string, decimal, decimal, decimal)[] { ("996311", "CGST", 2.5m, 3500m, 87.50m), ("996311", "SGST", 2.5m, 3500m, 87.50m),
                ("996331", "CGST", 2.5m, 1450m, 36.25m), ("996331", "SGST", 2.5m, 1450m, 36.25m) },
            invoice.TaxSummary.Select(s => (s.HsnSac, s.Code, s.Rate, s.TaxableValue, s.Amount)));
        var receipt = Assert.Single(invoice.Receipts);
        Assert.Matches(@"/R\d{4}/\d{5}$", receipt.Number);
        Assert.Equal((5197.50m, "UPI", false), (receipt.Amount, receipt.Method, receipt.Voided));

        // the copies the guest takes away
        await PdfAsync(desk, $"/api/v1/properties/{pid}/invoices/{invoice.Id}/pdf", invoice.Number.Replace('/', '-') + ".pdf");
        await PdfAsync(desk, $"/api/v1/properties/{pid}/receipts/{receipt.Id}/pdf", receipt.Number.Replace('/', '-') + ".pdf");

        // the bill now says which invoice each charge is on, and the charges can't be reversed any more
        folio = await GetFolioAsync(desk, pid, folio.Id);
        Assert.All(folio.Items, i => Assert.Equal((invoice.Number, false), (i.InvoiceNumber, i.Adjustable)));
        var reverse = await owner.PostAsync($"/api/v1/properties/{pid}/folios/{folio.Id}/charges/{folio.Items.Single(i => i.Category == "FOOD").Id}/reverse", new ReverseChargeRequest("Wrong table"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reverse.StatusCode);
        Assert.Equal(("INVOICED", $"\"Dinner for two\" is on invoice {invoice.Number}, so it can't be reversed or moved. Issue a credit note on {invoice.Number} instead."),
            await FrontDeskTests.ProblemAsync(reverse));

        // the guest wanted it in their company's name: reception can't cancel an invoice, the accountant can (with a reason)
        var cancelUrl = $"/api/v1/properties/{pid}/invoices/{invoice.Id}/credit-notes";
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PostAsync(cancelUrl, new CreateCreditNoteRequest("CANCEL", "Company name"), Key("cn"))).StatusCode);
        Assert.Equal("Say why the credit note is issued (at least 3 characters), e.g. Guest was charged for a dinner they didn't have.",
            await FieldErrorAsync(await accountant.PostAsync(cancelUrl, new CreateCreditNoteRequest("CANCEL", " "), Key("cn")), "Reason"));
        var cancelled = await CreatedJsonAsync<InvoiceDto>(await accountant.PostAsync(cancelUrl,
            new CreateCreditNoteRequest("CANCEL", "Guest asked for the invoice in the company's name"), Key("cn")));
        var note = Assert.Single(cancelled.CreditNotes);
        Assert.Matches(@"/C\d{4}/\d{4}$", note.Number);
        Assert.Equal(("CANCELLED", "CANCEL", 5197.50m, 2, 0m), (cancelled.Status, note.NoteType, note.Total, note.Lines.Count, cancelled.Outstanding));
        await PdfAsync(accountant, $"/api/v1/properties/{pid}/credit-notes/{note.Id}/pdf", note.Number.Replace('/', '-') + ".pdf");
        var again = await accountant.PostAsync(cancelUrl, new CreateCreditNoteRequest("CANCEL", "Again"), Key("cn"));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.StartsWith($"{invoice.Number} was already cancelled by credit note {note.Number} on ", (await FrontDeskTests.ProblemAsync(again)).Message);

        // the bill didn't change (the guest still owes nothing) and is invoiced again, B2B, with the next number
        folio = await GetFolioAsync(desk, pid, folio.Id);
        Assert.Equal((5197.50m, 0m), (folio.Total, folio.Balance));
        Assert.All(folio.Items, i => Assert.Null(i.InvoiceNumber));
        var reissued = await IssueAsync(desk, pid, folio.Id, new IssueInvoiceRequest("Acme Travels Pvt Ltd", MaharashtraGstin, "Baner, Pune"));
        Assert.Equal(Seq(invoice.Number) + 1, Seq(reissued.Number));
        Assert.Equal((true, "Acme Travels Pvt Ltd", MaharashtraGstin, "27", 5197.50m), (reissued.IsB2B, reissued.Customer.Name, reissued.Customer.Gstin,
            reissued.Customer.StateCode, reissued.Total));
        Assert.Equal(5197.50m, reissued.Total);
        Assert.Single(reissued.Receipts);   // the payment keeps its one receipt

        // and it is all in the audit trail
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE entity_id = '{note.Id}' AND reason = 'Guest asked for the invoice in the company''s name'"));
    }

    // ------------------------------------------------------------------ gap-free numbering (required test)

    [Fact]
    public async Task Twenty_invoices_issued_at_the_same_moment_get_the_next_twenty_numbers_with_no_gaps_or_repeats()
    {
        var (owner, pid) = await OwnerAsync();
        await RegisterMainAsync(owner, pid);
        var stay = await SetupAsync(owner, pid, rooms: 20);
        var source = await WalkInAsync(owner, pid);
        var folios = new List<FolioDto>();
        for (var i = 0; i < 20; i++)
        {
            var guest = await CreateGuestAsync(owner);
            var booked = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, Today, Today.AddDays(1))));
            await FrontDeskTests.OkAsync(await owner.PostAsync(FrontDeskTests.CheckInUrl(pid, booked), new CheckInRequest(null)));
            folios.Add(await MainFolioAsync(owner, pid, booked.Id));
        }

        // a refused invoice takes no number
        var before = await LastNumberAsync(pid, "INVOICE");
        var empty = await EmptyFolioAsync(owner, pid);
        var refused = await owner.PostAsync($"/api/v1/properties/{pid}/folios/{empty.Id}/invoice", new IssueInvoiceRequest(), Key("inv"));
        Assert.Equal(("NOTHING_TO_INVOICE", $"{empty.Number} has no charges yet, so there is nothing to invoice."), await FrontDeskTests.ProblemAsync(refused));
        Assert.Equal(before, await LastNumberAsync(pid, "INVOICE"));

        using var barrier = new Barrier(20);
        var results = await Task.WhenAll(folios.Select(f => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await owner.PostAsync($"/api/v1/properties/{pid}/folios/{f.Id}/invoice", new IssueInvoiceRequest(), Key("inv"));
        })));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var numbers = new List<int>();
        foreach (var r in results) numbers.Add(Seq((await r.Content.ReadFromJsonAsync<InvoiceDto>())!.Number));
        Assert.Equal(Enumerable.Range(before + 1, 20), numbers.Order());
        Assert.Equal(before + 20, await LastNumberAsync(pid, "INVOICE"));
        Assert.Equal(20L, await ScalarAsync($"SELECT count(DISTINCT number) FROM invoices WHERE folio_id IN ({string.Join(",", folios.Select(f => $"'{f.Id}'"))})"));
    }

    // ------------------------------------------------------------------ B2B intra- and inter-state (required test)

    [Fact]
    public async Task B2B_invoices_inside_and_outside_the_state_carry_CGST_and_SGST_because_a_stay_is_supplied_where_the_property_is()
    {
        var (owner, pid) = await OwnerAsync();
        foreach (var (gstin, state) in new[] { (GoaCustomerGstin, "30"), (MaharashtraGstin, "27") })
        {
            // the company's details are on the guest profile, so check-out bills the company
            var guest = await CreateCompanyGuestAsync(owner, "Acme Travels Pvt Ltd", gstin);
            var (folio, booking) = await InHouseAsync(owner, pid, guest.Id, nights: 1);
            await ChargeAsync(owner, pid, folio.Id, "FOOD", "Lunch", 1000m);
            await PayAsync(owner, pid, new CreatePaymentRequest("UPI", 4725m, folio.Id));
            await FrontDeskTests.OkAsync(await owner.PostAsync(FrontDeskTests.CheckOutUrl(pid, booking), new CheckOutRequest(null)));
            var invoice = await GetAsync(owner, pid, Assert.Single((await ListAsync(owner, pid, $"reservationId={booking.Id}")).Items).Id);

            Assert.Equal((true, "Acme Travels Pvt Ltd", gstin, state, "30"),
                (invoice.IsB2B, invoice.Customer.Name, invoice.Customer.Gstin, invoice.Customer.StateCode, invoice.PlaceOfSupply));
            Assert.Equal((4500m, 112.50m, 112.50m, 0m, 225m, 4725m),
                (invoice.TaxableValue, invoice.Cgst, invoice.Sgst, invoice.Igst, invoice.TaxTotal, invoice.Total));
            Assert.Equal(invoice.Total, invoice.Items.Sum(i => i.Total));
            Assert.Equal(invoice.Cgst, invoice.Items.SelectMany(i => i.Taxes).Where(t => t.Code == "CGST").Sum(t => t.Amount));
            Assert.Equal(invoice.TaxTotal, invoice.TaxSummary.Sum(s => s.Amount));
        }

        // a GSTIN typed at the desk is checked, check digit included, and the state must be the GSTIN's
        var (other, _) = await InHouseAsync(owner, pid);
        var url = $"/api/v1/properties/{pid}/folios/{other.Id}/invoice";
        Assert.Equal("The last character (check digit) should be V for this GSTIN; check it for a typing mistake.",
            await FieldErrorAsync(await owner.PostAsync(url, new IssueInvoiceRequest("Acme", "27AAPFU0939F1ZX"), Key("inv")), "CustomerGstin"));
        Assert.Equal($"The GSTIN {MaharashtraGstin} is registered in Maharashtra (27), not Goa (30). A B2B customer's state is its GSTIN's state.",
            await FieldErrorAsync(await owner.PostAsync(url, new IssueInvoiceRequest("Acme", MaharashtraGstin, null, "30"), Key("inv")), "CustomerStateCode"));
        Assert.Equal("Enter the customer's name (the company's legal name for a B2B invoice) with the GSTIN.",
            await FieldErrorAsync(await owner.PostAsync(url, new IssueInvoiceRequest(null, MaharashtraGstin), Key("inv")), "CustomerGstin"));
    }

    // ------------------------------------------------------------------ immutability (required test)

    [Fact]
    public async Task Changing_or_deleting_an_invoice_its_lines_notes_receipts_or_skipping_a_number_is_rejected_by_the_database()
    {
        var (owner, pid) = await OwnerAsync();
        var (folio, _) = await InHouseAsync(owner, pid);
        var payment = await PayAsync(owner, pid, new CreatePaymentRequest("UPI", 1000m, folio.Id));
        var invoice = await IssueAsync(owner, pid, folio.Id, new IssueInvoiceRequest());
        var reduced = await CreatedJsonAsync<InvoiceDto>(await owner.PostAsync($"/api/v1/properties/{pid}/invoices/{invoice.Id}/credit-notes",
            new CreateCreditNoteRequest("REDUCE", "Goodwill", [new CreditNoteLineRequest(invoice.Items[0].Id!.Value, 100m)]), Key("cn")));
        var note = Assert.Single(reduced.CreditNotes);
        var receipt = await OkAsync<ReceiptDto>(await owner.PostAsync($"/api/v1/properties/{pid}/payments/{payment.Id}/receipt", new { }));

        foreach (var sql in new[]
                 {
                     $"UPDATE invoices SET total = 1 WHERE id = '{invoice.Id}'",
                     $"UPDATE invoices SET customer_name = 'Someone else' WHERE id = '{invoice.Id}'",
                     $"DELETE FROM invoices WHERE id = '{invoice.Id}'",
                     $"UPDATE invoice_items SET taxable_value = 1, total = 1 WHERE invoice_id = '{invoice.Id}'",
                     $"DELETE FROM invoice_items WHERE invoice_id = '{invoice.Id}'",
                     $"UPDATE invoice_item_taxes SET amount = 0 WHERE invoice_item_id = '{invoice.Items[0].Id}'",
                     $"DELETE FROM invoice_item_taxes WHERE invoice_item_id = '{invoice.Items[0].Id}'",
                     $"UPDATE credit_notes SET total = 1 WHERE id = '{note.Id}'",
                     $"DELETE FROM credit_notes WHERE id = '{note.Id}'",
                     $"DELETE FROM credit_note_items WHERE credit_note_id = '{note.Id}'",
                     $"UPDATE receipts SET amount = 1 WHERE id = '{receipt.Id}'",
                     $"DELETE FROM receipts WHERE id = '{receipt.Id}'",
                     $"DELETE FROM invoice_series WHERE property_id = '{pid}'",
                 })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        // a series only moves on by one: no skipping a number, no going back
        foreach (var step in new[] { "+ 2", "- 1", "+ 0" })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
                $"UPDATE invoice_series SET last_number = last_number {step} WHERE property_id = '{pid}' AND document_type = 'INVOICE'"));
            Assert.Equal("ck_invoice_series_gap_free", ex.ConstraintName);
        }

        // an invoice whose totals don't add up to its lines can't be committed
        var bad = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"""
             BEGIN;
             INSERT INTO invoices (organization_id, property_id, series_id, document_type, number, financial_year, invoice_date, folio_id,
                                   reservation_id, gst_registration_id, supplier_gstin, supplier_legal_name, supplier_state_code, customer_name,
                                   place_of_supply, taxable_value, cgst, sgst, igst, cess, tax_total, total, currency)
             SELECT organization_id, property_id, series_id, document_type, 'X/9999/99999', financial_year, invoice_date, folio_id,
                    reservation_id, gst_registration_id, supplier_gstin, supplier_legal_name, supplier_state_code, customer_name,
                    place_of_supply, 1, 0, 0, 0, 0, 0, 1, currency FROM invoices WHERE id = '{invoice.Id}';
             COMMIT;
             """));
        Assert.Equal("ck_invoices_add_up", bad.ConstraintName);

        // and a credit note can't credit more than the line billed
        var over = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"""
             INSERT INTO credit_note_items (organization_id, property_id, credit_note_id, line_no, invoice_item_id, folio_item_id, category,
                                            description, taxable_value, tax_amount, total)
             SELECT organization_id, property_id, '{note.Id}', 99, id, folio_item_id, category, description, taxable_value, 0, taxable_value
               FROM invoice_items WHERE id = '{invoice.Items[0].Id}'
             """));
        Assert.Equal("ck_credit_note_items_limit", over.ConstraintName);
        Assert.Equal(invoice.Total, (await GetAsync(owner, pid, invoice.Id)).Total);
    }

    // ------------------------------------------------------------------ credit notes that reduce, debit notes

    [Fact]
    public async Task Reducing_an_invoice_takes_the_credit_off_the_bill_at_the_invoice_rates_and_never_more_than_was_billed()
    {
        var (owner, pid) = await OwnerAsync();
        var (folio, _) = await InHouseAsync(owner, pid);
        await PayAsync(owner, pid, new CreatePaymentRequest("CARD", 7350m, folio.Id, PaymentReference: "AP1234"));
        var invoice = await IssueAsync(owner, pid, folio.Id, new IssueInvoiceRequest());
        var url = $"/api/v1/properties/{pid}/invoices/{invoice.Id}/credit-notes";
        var night = invoice.Items[0];

        Assert.Equal("Choose at least one line to credit.",
            await FieldErrorAsync(await owner.PostAsync(url, new CreateCreditNoteRequest("REDUCE", "Goodwill"), Key("cn")), "Lines"));
        Assert.Equal("\"Room night: " + folio.Items[0].Description["Room night: ".Length..] + "\" has ₹3,500 left to credit (before GST); ₹3,600 is more than that.",
            await FieldErrorAsync(await owner.PostAsync(url, new CreateCreditNoteRequest("REDUCE", "Goodwill", [new(night.Id!.Value, 3600m)]), Key("cn")), "Lines[0].Amount"));
        Assert.Equal("Cancelling credits the whole invoice, so don't choose lines. To credit some lines, reduce the invoice instead.",
            await FieldErrorAsync(await owner.PostAsync(url, new CreateCreditNoteRequest("CANCEL", "Oops", [new(night.Id!.Value)]), Key("cn")), "Lines"));

        // ₹1,000 off the first night: GST comes back at the rate of the invoice (2.5% + 2.5%)
        var reduced = await CreatedJsonAsync<InvoiceDto>(await owner.PostAsync(url,
            new CreateCreditNoteRequest("REDUCE", "Air conditioning failed on the first night", [new(night.Id!.Value, 1000m)]), Key("cn")));
        var note = Assert.Single(reduced.CreditNotes);
        Assert.Equal(("REDUCE", 1000m, 50m, 1050m), (note.NoteType, note.TaxableValue, note.TaxTotal, note.Total));
        Assert.Equal(new[] { ("CGST", 2.5m, 1000m, 25m), ("SGST", 2.5m, 1000m, 25m) }, note.Lines[0].Taxes.Select(t => (t.Code, t.Rate, t.TaxableValue, t.Amount)));
        Assert.Equal(("PARTLY_CREDITED", 1050m, 6300m, 1000m, 2500m), (reduced.Status, reduced.Credited, reduced.Outstanding, reduced.Items[0].Credited, reduced.Items[0].Creditable));

        // the bill shows the credit and owes the guest ₹1,050 (refunded through payments as usual)
        folio = await GetFolioAsync(owner, pid, folio.Id);
        var credit = Assert.Single(folio.Items, i => i.ItemType == "CREDIT");
        Assert.Equal(($"Credit note {note.Number}: {night.Description}", -1000m, -50m, -1050m, "Air conditioning failed on the first night"),
            (credit.Description, credit.Amount, credit.TaxAmount, credit.Total, credit.Reason));
        Assert.Equal((6300m, -1050m), (folio.Total, folio.Balance));

        // crediting the rest of both nights credits the invoice in full, to the paisa
        var rest = await CreatedJsonAsync<InvoiceDto>(await owner.PostAsync(url,
            new CreateCreditNoteRequest("REDUCE", "Stay comped by the manager", [new(night.Id!.Value), new(invoice.Items[1].Id!.Value)]), Key("cn")));
        Assert.Equal(("CREDITED", 7350m, 0m), (rest.Status, rest.Credited, rest.Outstanding));
        Assert.Equal("\"" + night.Description + "\" has already been credited in full.",
            await FieldErrorAsync(await owner.PostAsync(url, new CreateCreditNoteRequest("REDUCE", "Again", [new(night.Id!.Value, 1m)]), Key("cn")), "Lines[0].Amount"));
        Assert.Equal(0m, (await GetFolioAsync(owner, pid, folio.Id)).Total);
    }

    [Fact]
    public async Task Charges_posted_after_the_invoice_go_on_a_debit_note_and_a_reissued_invoice_shows_earlier_credits_as_a_discount()
    {
        var (owner, pid) = await OwnerAsync();
        var (folio, _) = await InHouseAsync(owner, pid);
        var invoice = await IssueAsync(owner, pid, folio.Id, new IssueInvoiceRequest());

        // the minibar is found after the invoice
        await ChargeAsync(owner, pid, folio.Id, "FOOD", "Minibar", 400m);
        var withLate = await GetAsync(owner, pid, invoice.Id);
        Assert.Equal((1, 420m), (withLate.UnbilledCharges, withLate.UnbilledAmount));
        var twice = await owner.PostAsync($"/api/v1/properties/{pid}/folios/{folio.Id}/invoice", new IssueInvoiceRequest(), Key("inv"));
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Equal(("ALREADY_INVOICED", $"{folio.Number} already has invoice {invoice.Number}. Charges posted after it go on a debit note: open {invoice.Number} and issue one."),
            await FrontDeskTests.ProblemAsync(twice));

        var debitUrl = $"/api/v1/properties/{pid}/invoices/{invoice.Id}/debit-notes";
        Assert.Equal("Say why the debit note is issued (at least 3 characters), e.g. Minibar used on the last night, found after check-out.",
            await FieldErrorAsync(await owner.PostAsync(debitUrl, new CreateDebitNoteRequest(""), Key("dn")), "Reason"));
        var debited = await CreatedJsonAsync<InvoiceDto>(await owner.PostAsync(debitUrl, new CreateDebitNoteRequest("Minibar found after the invoice"), Key("dn")));
        var debit = Assert.Single(debited.DebitNotes);
        Assert.Matches(@"/D\d{4}/\d{4}$", debit.Number);
        Assert.Equal((400m, 20m, 420m, 0, 7770m), (debit.TaxableValue, debit.TaxTotal, debit.Total, debited.UnbilledCharges, debited.Billed));
        Assert.Equal(debit.Number, debited.Items[^1].DebitNoteNumber);
        await PdfAsync(owner, $"/api/v1/properties/{pid}/debit-notes/{debit.Id}/pdf", debit.Number.Replace('/', '-') + ".pdf");
        var nothing = await owner.PostAsync(debitUrl, new CreateDebitNoteRequest("Again"), Key("dn"));
        Assert.Equal("NOTHING_TO_BILL", (await FrontDeskTests.ProblemAsync(nothing)).Code);

        // ₹500 off a night, then the invoice is cancelled and issued again: the night is billed net, the ₹500 as a discount
        var url = $"/api/v1/properties/{pid}/invoices/{invoice.Id}/credit-notes";
        await CreatedJsonAsync<InvoiceDto>(await owner.PostAsync(url, new CreateCreditNoteRequest("REDUCE", "Late check-in", [new(invoice.Items[0].Id!.Value, 500m)]), Key("cn")));
        var cancelled = await CreatedJsonAsync<InvoiceDto>(await owner.PostAsync(url, new CreateCreditNoteRequest("CANCEL", "Wrong guest name"), Key("cn")));
        Assert.Equal(("CANCELLED", 7770m), (cancelled.Status, cancelled.Credited));
        var again = await IssueAsync(owner, pid, folio.Id, new IssueInvoiceRequest());
        Assert.Equal((3500m, 500m, 3000m, 150m), (again.Items[0].GrossAmount, again.Items[0].Discount, again.Items[0].TaxableValue, again.Items[0].TaxAmount));
        Assert.Equal((3, 7245m), (again.Items.Count, again.Total));
        Assert.Equal((await GetFolioAsync(owner, pid, folio.Id)).Total, again.Total);
    }

    // ------------------------------------------------------------------ check-out without an invoice, receipts, permissions

    [Fact]
    public async Task A_wrong_GSTIN_on_the_profile_doesnt_stop_the_check_out_and_the_booking_says_why_no_invoice_was_issued()
    {
        var (owner, pid) = await OwnerAsync();
        var guest = await CreateCompanyGuestAsync(owner, "Typo Traders", "27AAPFU0939F1ZX");   // wrong check digit
        var (folio, booking) = await InHouseAsync(owner, pid, guest.Id, nights: 1);
        await PayAsync(owner, pid, new CreatePaymentRequest("UPI", 3675m, folio.Id));

        var preview = await owner.GetJsonAsync<InvoicePreviewDto>($"/api/v1/properties/{pid}/folios/{folio.Id}/invoice-preview");
        Assert.Equal(("Typo Traders", "27AAPFU0939F1ZX", 3675m, (string?)null), (preview.CustomerName, preview.CustomerGstin, preview.Total, preview.CantIssueReason));
        Assert.StartsWith($"The GSTIN on {guest.FullName}'s profile (27AAPFU0939F1ZX) isn't valid: The last character", preview.GstinProblem);

        var out_ = await FrontDeskTests.OkAsync(await owner.PostAsync(FrontDeskTests.CheckOutUrl(pid, booking), new CheckOutRequest(null)));
        Assert.Equal("CHECKED_OUT", out_.Status);
        Assert.Empty((await ListAsync(owner, pid, $"reservationId={booking.Id}")).Items);
        Assert.Contains(out_.Notes, n => n.Note.StartsWith($"No invoice was issued for {folio.Number} at check-out: The GSTIN on {guest.FullName}'s profile", StringComparison.Ordinal));

        // reception issues it as B2C from the bill instead
        var b2c = await IssueAsync(owner, pid, folio.Id, new IssueInvoiceRequest(guest.FullName));
        Assert.Equal((false, (string?)null, 3675m), (b2c.IsB2B, b2c.Customer.Gstin, b2c.Total));
    }

    [Fact]
    public async Task A_payment_gets_one_receipt_a_voided_one_gets_none_and_a_retried_invoice_is_returned_not_issued_twice()
    {
        var (owner, pid) = await OwnerAsync();
        var (folio, _) = await InHouseAsync(owner, pid);
        var paid = await PayAsync(owner, pid, new CreatePaymentRequest("UPI", 500m, folio.Id));
        var first = await OkAsync<ReceiptDto>(await owner.PostAsync($"/api/v1/properties/{pid}/payments/{paid.Id}/receipt", new { }));
        var second = await OkAsync<ReceiptDto>(await owner.PostAsync($"/api/v1/properties/{pid}/payments/{paid.Id}/receipt", new { }));
        Assert.Equal((first.Id, first.Number, 500m, paid.Number), (second.Id, second.Number, second.Amount, second.PaymentNumber));

        var wrong = await PayAsync(owner, pid, new CreatePaymentRequest("UPI", 50m, folio.Id));
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/v1/properties/{pid}/payments/{wrong.Id}/void", new VoidPaymentRequest("Typed twice"))).StatusCode);
        var none = await owner.PostAsync($"/api/v1/properties/{pid}/payments/{wrong.Id}/receipt", new { });
        Assert.Equal(("NO_RECEIPT_FOR_PAYMENT", $"{wrong.Number} was voided (Typed twice), so it gets no receipt."), await FrontDeskTests.ProblemAsync(none));

        var key = Key("inv");
        var a = await owner.PostAsync($"/api/v1/properties/{pid}/folios/{folio.Id}/invoice", new IssueInvoiceRequest(), key);
        var b = await owner.PostAsync($"/api/v1/properties/{pid}/folios/{folio.Id}/invoice", new IssueInvoiceRequest(), key);
        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Created), (a.StatusCode, b.StatusCode));
        Assert.True(b.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal((await a.Content.ReadFromJsonAsync<InvoiceDto>())!.Number, (await b.Content.ReadFromJsonAsync<InvoiceDto>())!.Number);
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM invoices WHERE folio_id = '{folio.Id}'"));
    }

    [Fact]
    public async Task Housekeeping_cant_see_invoices_reception_cant_issue_credit_notes_and_another_organisation_sees_nothing()
    {
        var (owner, pid) = await OwnerAsync();
        var (folio, _) = await InHouseAsync(owner, pid);
        var invoice = await IssueAsync(owner, pid, folio.Id, new IssueInvoiceRequest());
        var hk = await StaffAsync("HOUSEKEEPER", pid);
        var desk = await StaffAsync("RECEPTIONIST", pid);
        var manager = await StaffAsync("MANAGER", pid);

        Assert.Equal(HttpStatusCode.Forbidden, (await hk.GetAsync($"/api/v1/properties/{pid}/invoices")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await hk.GetAsync($"/api/v1/properties/{pid}/invoices/{invoice.Id}/pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await desk.GetAsync($"/api/v1/properties/{pid}/invoices/{invoice.Id}")).StatusCode);
        foreach (var c in new[] { desk, manager })
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync($"/api/v1/properties/{pid}/invoices/{invoice.Id}/credit-notes",
                new CreateCreditNoteRequest("REDUCE", "Goodwill", [new(invoice.Items[0].Id!.Value, 10m)]), Key("cn"))).StatusCode);

        var other = await app.CreateSecondTenantAsync();
        var stranger = await app.SignInAsync(other.OwnerEmail, other.Password);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/properties/{pid}/invoices/{invoice.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/properties/{other.PropertyId}/invoices/{invoice.Id}")).StatusCode);
        Assert.Empty((await stranger.GetJsonAsync<PagedResult<InvoiceListItemDto>>($"/api/v1/properties/{other.PropertyId}/invoices")).Items);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(TestClient Owner, Guid PropertyId)> OwnerAsync() => (await app.SignInOwnerAsync(), await app.MainPropertyIdAsync());

    private async Task<TestClient> StaffAsync(string role, Guid pid)
    {
        var (email, pw) = await app.CreateUserAsync(role, pid);
        return await app.SignInAsync(email, pw);
    }

    private static string Gst(string first14) => first14 + Resort.Domain.Tax.Gstin.CheckDigit(first14);

    private static Dictionary<string, string> Key(string what) => new() { ["Idempotency-Key"] = $"{what}-{Guid.NewGuid()}" };

    /// <summary>The property's sequence number of a document: BEACH/2627/00012 → 12.</summary>
    private static int Seq(string number) => int.Parse(number[(number.LastIndexOf('/') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Registers the main property (Goa) from 1 Jul 2017, once per test class.</summary>
    private static async Task RegisterMainAsync(TestClient owner, Guid pid)
    {
        var list = await owner.GetJsonAsync<List<GstRegistrationDto>>($"/api/v1/properties/{pid}/gst-registrations");
        if (list.Count > 0) return;
        var res = await owner.PostAsync($"/api/v1/properties/{pid}/gst-registrations",
            new CreateGstRegistrationRequest(DemoGstin, "Test Resorts Pvt Ltd", "Main Resort", "REGULAR", new DateOnly(2017, 7, 1)));
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
    }

    /// <summary>A guest checked in today for two nights at ₹3,500 (5% GST), and their main folio: ₹7,350. A one-night stay
    /// (₹3,675) can be checked out today.</summary>
    private static async Task<(FolioDto Folio, ReservationDto Booking)> InHouseAsync(TestClient owner, Guid pid, Guid? guestId = null, int nights = 2)
    {
        await RegisterMainAsync(owner, pid);
        var stay = await SetupAsync(owner, pid, rooms: 1);
        guestId ??= (await CreateGuestAsync(owner)).Id;
        var booked = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guestId.Value, await WalkInAsync(owner, pid), Today, Today.AddDays(nights))));
        await FrontDeskTests.OkAsync(await owner.PostAsync(FrontDeskTests.CheckInUrl(pid, booked), new CheckInRequest(null)));
        var folio = await MainFolioAsync(owner, pid, booked.Id);
        Assert.Equal(3675m * nights, folio.Total);
        return (folio, booked);
    }

    /// <summary>A confirmed booking's main folio: nothing charged yet.</summary>
    private static async Task<FolioDto> EmptyFolioAsync(TestClient owner, Guid pid)
    {
        var stay = await SetupAsync(owner, pid, rooms: 1);
        var guest = await CreateGuestAsync(owner);
        var booked = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, await WalkInAsync(owner, pid), Today.AddDays(3), Today.AddDays(4))));
        return await MainFolioAsync(owner, pid, booked.Id);
    }

    private static async Task<GuestDto> CreateCompanyGuestAsync(TestClient c, string company, string gstin)
    {
        var res = await c.PostAsync("/api/v1/guests", new CreateGuestRequest("Ravi", "K" + Suffix().ToLowerInvariant(), null, null, null, "IN",
            company, gstin, null, false, new GuestAddressDto("Baner Road", null, "Pune", "Maharashtra", "27", "411045", "IN"), null, IgnoreDuplicates: true));
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<GuestDto>())!;
    }

    private static async Task<FolioDto> MainFolioAsync(TestClient c, Guid pid, Guid reservationId)
    {
        var list = await c.GetJsonAsync<List<FolioListItemDto>>($"/api/v1/properties/{pid}/reservations/{reservationId}/folios");
        return await GetFolioAsync(c, pid, list.Single(f => f.IsPrimary).Id);
    }

    private static Task<FolioDto> GetFolioAsync(TestClient c, Guid pid, Guid id) => c.GetJsonAsync<FolioDto>($"/api/v1/properties/{pid}/folios/{id}");

    private static async Task ChargeAsync(TestClient c, Guid pid, Guid folioId, string category, string description, decimal price) =>
        await CreatedJsonAsync<FolioDto>(await c.PostAsync($"/api/v1/properties/{pid}/folios/{folioId}/charges",
            new PostChargeRequest(category, description, 1, price), Key("charge")));

    private static async Task<PaymentDto> PayAsync(TestClient c, Guid pid, CreatePaymentRequest body) =>
        await CreatedJsonAsync<PaymentDto>(await c.PostAsync($"/api/v1/properties/{pid}/payments", body, Key("pay")));

    private static async Task<InvoiceDto> IssueAsync(TestClient c, Guid pid, Guid folioId, IssueInvoiceRequest body) =>
        await CreatedJsonAsync<InvoiceDto>(await c.PostAsync($"/api/v1/properties/{pid}/folios/{folioId}/invoice", body, Key("inv")));

    private static Task<InvoiceDto> GetAsync(TestClient c, Guid pid, Guid id) => c.GetJsonAsync<InvoiceDto>($"/api/v1/properties/{pid}/invoices/{id}");

    private static Task<PagedResult<InvoiceListItemDto>> ListAsync(TestClient c, Guid pid, string query) =>
        c.GetJsonAsync<PagedResult<InvoiceListItemDto>>($"/api/v1/properties/{pid}/invoices?{query}");

    private static async Task PdfAsync(TestClient c, string url, string fileName)
    {
        var res = await c.GetAsync(url);
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        Assert.Equal("application/pdf", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal(fileName, res.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 2000, $"{bytes.Length} bytes");
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
    }

    private static async Task<T> CreatedJsonAsync<T>(HttpResponseMessage res)
    {
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<T> OkAsync<T>(HttpResponseMessage res)
    {
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<int> LastNumberAsync(Guid pid, string type) =>
        (int)((await ScalarAsync($"SELECT coalesce(max(last_number), 0) FROM invoice_series WHERE property_id = '{pid}' AND document_type = '{type}'"))!);

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
