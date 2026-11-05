using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Resort.Contracts;
using Resort.Domain.Employees;
using Resort.Domain.Tax;
using Resort.Tests.Support;
using static Resort.Tests.Support.BookingKit;

namespace Resort.Tests.Integration;

/// <summary>Sprint 22 — expenses and budgets: an expense counts in the budget's actuals only once it is approved (never while
/// it waits, nor when rejected or cancelled); GST on a vendor's bill is split CGST + SGST or IGST and, when the input tax can
/// be claimed, only the taxable value is a cost; approval needs the receipt over ₹500, someone other than the person who
/// recorded it (unless they manage the budget) and is in the audit trail; a decided expense is frozen, even against SQL;
/// receipts are checked by their bytes and stored encrypted; the budget by department and month, its variance and
/// forecast. Each test uses a property of its own. Real API, real PostgreSQL.</summary>
public class FinanceTests(FrontDeskFactory app) : IClassFixture<FrontDeskFactory>
{
    private static readonly TimeZoneInfo Ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    // ------------------------------------------------------------------ required: unapproved expense excluded from actuals

    [Fact]
    public async Task An_unapproved_expense_is_left_out_of_the_actuals_until_it_is_approved_and_rejected_or_cancelled_ones_never_count()
    {
        var s = await SetupAsync();
        var year = YearOf(Today);
        var budget = await OkAsync<BudgetDto>(await s.Owner.PostAsync($"{Url(s.Pid)}/budgets", new CreateBudgetRequest(year)), HttpStatusCode.Created);
        Assert.Equal(($"Budget {year}-{(year + 1) % 100:00}", 0m, 8), (budget.Name, budget.Total, budget.Rows.Count));
        budget = await OkAsync<BudgetDto>(await s.Owner.PutAsync($"{Url(s.Pid)}/budgets/{budget.Id}/rows",
            new SaveBudgetRowRequest(s.Hk, Enumerable.Repeat(10_000m, 12).ToList(), budget.Version)));
        Assert.Equal(120_000m, budget.Total);

        // Recorded by the manager: it waits for approval and is not an actual yet.
        var diesel = await CreateOkAsync(s.Manager, s, Expense(s, 4_000m, "Diesel for the laundry generator"));
        Assert.Equal(("EX00001", "SUBMITTED", 4_000m, 4_000m, false), (diesel.Code, diesel.Status, diesel.Amount, diesel.CostAmount, diesel.ItcEligible));
        var report = await ReportAsync(s);
        var hk = report.Rows.Single(r => r.CostCentreId == s.Hk);
        Assert.Equal((0m, 4_000m, 1), (hk.ActualToDate, hk.Waiting, hk.WaitingCount));
        Assert.Equal(0m, report.Total.ActualToDate);

        // A rejected one and a cancelled one never count.
        var towels = await CreateOkAsync(s.Manager, s, Expense(s, 300m, "Towels from the market"));
        var rejected = await OkAsync<ExpenseDto>(await s.Accountant.PostAsync($"{Url(s.Pid)}/expenses/{towels.Id}/reject",
            new ExpenseDecisionRequest("Bought from petty cash already", towels.Version)));
        Assert.Equal(("REJECTED", "Bought from petty cash already", "Accounts user"), (rejected.Status, rejected.DecisionReason, rejected.DecidedBy));
        var soap = await CreateOkAsync(s.Manager, s, Expense(s, 200m, "Soap"));
        Assert.Equal("CANCELLED", (await OkAsync<ExpenseDto>(await s.Manager.PostAsync($"{Url(s.Pid)}/expenses/{soap.Id}/cancel",
            new ExpenseDecisionRequest("Entered twice", soap.Version)))).Status);

        // Approved (the receipt first: it is over ₹500): now it is an actual.
        await ReceiptOkAsync(s.Manager, s, diesel.Id, Jpeg(30_000), "bill.jpg", "image/jpeg");
        var approved = await OkAsync<ExpenseDto>(await s.Accountant.PostAsync($"{Url(s.Pid)}/expenses/{diesel.Id}/approve", new ExpenseDecisionRequest()));
        Assert.Equal(("APPROVED", "Accounts user"), (approved.Status, approved.DecidedBy));
        Assert.Empty(approved.Actions);
        report = await ReportAsync(s);
        hk = report.Rows.Single(r => r.CostCentreId == s.Hk);
        Assert.Equal((4_000m, 0m, 0), (hk.ActualToDate, hk.Waiting, hk.WaitingCount));
        Assert.Equal(4_000m, report.Total.ActualToDate);
        Assert.Equal(4_000m, hk.Categories.Single().Actual);

        // The database's view agrees: only APPROVED rows are actuals.
        Assert.Equal(4_000m, await ScalarAsync($"SELECT sum(cost_amount) FROM expenses WHERE property_id = '{s.Pid}' AND status = 'APPROVED'"));
        Assert.Equal(3L, await ScalarAsync($"SELECT count(*) FROM expenses WHERE property_id = '{s.Pid}'"));
    }

    // ------------------------------------------------------------------ GST

    [Fact]
    public async Task Gst_on_a_bill_is_split_by_the_vendors_state_and_only_the_taxable_value_is_a_cost_when_the_tax_can_be_claimed()
    {
        var s = await SetupAsync(registered: true);
        var goa = Gstin("30ABCDE1234F1Z");
        var mumbai = Gstin("27PQRSX6789K1Z");

        // Same state: CGST + SGST halves (SGST takes the odd paisa); claimable, so the cost is the taxable value.
        var plumber = await CreateOkAsync(s.Manager, s, Expense(s, 1_180.01m, "Plumber: bathroom tap", gstin: goa, bill: "INV-77", gst: 180.01m));
        Assert.Equal((1_000m, 90m, 90.01m, 0m, true, 1_000m, goa),
            (plumber.TaxableValue, plumber.Cgst, plumber.Sgst, plumber.Igst, plumber.ItcEligible, plumber.CostAmount, plumber.VendorGstin));
        // Another state: IGST.
        var ads = await CreateOkAsync(s.Manager, s, Expense(s, 5_900m, "Online ads", gstin: " " + mumbai.ToLowerInvariant() + " ", bill: "A/2026/19", gst: 900m, category: s.Mkt));
        Assert.Equal((5_000m, 0m, 0m, 900m, true, 5_000m, mumbai), (ads.TaxableValue, ads.Cgst, ads.Sgst, ads.Igst, ads.ItcEligible, ads.CostAmount, ads.VendorGstin));
        // Staff food: the tax can't be claimed (section 17(5)), so everything paid is the cost.
        var lunch = await CreateOkAsync(s.Manager, s, Expense(s, 1_050m, "Staff lunch on Diwali", gstin: goa, bill: "INV-78", gst: 50m, category: s.StaffWelfare));
        Assert.Equal((false, 1_050m, 25m, 25m), (lunch.ItcEligible, lunch.CostAmount, lunch.Cgst, lunch.Sgst));

        // GST only on a GST bill; a bill once; a GSTIN that isn't one.
        Assert.Equal("GST can only be entered for a GST bill. Enter the vendor's GSTIN from the bill, or set the GST to ₹0.",
            await FieldErrorAsync(await Create(s.Manager, s, Expense(s, 1_180m, "Tap", gst: 180m)), "VendorGstin"));
        Assert.Equal("Enter the bill number printed on the GST bill.",
            await FieldErrorAsync(await Create(s.Manager, s, Expense(s, 1_180m, "Tap", gstin: goa, gst: 180m)), "BillNumber"));
        Assert.Equal($"Bill inv-77 of {goa} is already recorded as {plumber.Code}. A bill is recorded once.",
            await FieldErrorAsync(await Create(s.Manager, s, Expense(s, 1_180m, "Tap again", gstin: goa, bill: "inv-77", gst: 180m)), "BillNumber"));
        Assert.Equal("The GST is part of the amount paid, so it must be less than the amount.",
            await FieldErrorAsync(await Create(s.Manager, s, Expense(s, 100m, "Tap", gstin: goa, bill: "X1", gst: 100m)), "GstAmount"));
        Assert.StartsWith("The last character (check digit) should be",
            await FieldErrorAsync(await Create(s.Manager, s, Expense(s, 100m, "Tap", gstin: goa[..14] + (goa[14] == 'A' ? 'B' : 'A'), bill: "X1", gst: 10m)), "VendorGstin"));
        Assert.Equal($"{Day(Today.AddDays(1))} is in the future. Enter the day the money was spent.",
            await FieldErrorAsync(await Create(s.Manager, s, Expense(s, 100m, "Tap") with { ExpenseDate = Today.AddDays(1) }), "ExpenseDate"));

        // A property that isn't GST registered can't claim anything back.
        var u = await SetupAsync();
        var unregistered = await CreateOkAsync(u.Manager, u, Expense(u, 1_180m, "Plumber", gstin: goa, bill: "INV-90", gst: 180m));
        Assert.Equal((false, 1_180m, 90m, 90m), (unregistered.ItcEligible, unregistered.CostAmount, unregistered.Cgst, unregistered.Sgst));

        // The database refuses GST that doesn't add up, even without the service.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"UPDATE expenses SET cgst = 0 WHERE id = '{plumber.Id}'"));
        Assert.Equal("ck_expenses_gst", ex.ConstraintName);
    }

    // ------------------------------------------------------------------ approval

    [Fact]
    public async Task Approval_needs_the_receipt_over_500_and_someone_else_and_is_audited_and_an_approved_expense_is_frozen()
    {
        var s = await SetupAsync();
        var paint = await CreateOkAsync(s.Accountant, s, Expense(s, 2_400m, "Paint for the cottages", category: s.Repair, centre: s.Mnt));
        // The accountant recorded it: they can't approve it themselves, and it has no receipt yet.
        Assert.Equal(["edit", "receipt", "cancel", "reject"], paint.Actions);
        Assert.True(paint.NeedsReceipt);
        Assert.Equal("You recorded EX00001 yourself, so someone else who approves expenses has to approve it.", paint.Blocked["approve"]);
        Assert.Equal(("OWN_EXPENSE", "You recorded EX00001 yourself, so someone else who approves expenses has to approve it."),
            await ProblemAsync(await s.Accountant.PostAsync($"{Url(s.Pid)}/expenses/{paint.Id}/approve", new ExpenseDecisionRequest())));
        // The manager records expenses but doesn't approve them.
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Manager.PostAsync($"{Url(s.Pid)}/expenses/{paint.Id}/approve", new ExpenseDecisionRequest())).StatusCode);
        // The owner (who manages the budget) may, but not before the receipt.
        Assert.Equal(("RECEIPT_REQUIRED", "EX00001 is ₹2,400, over ₹500, so it needs a photo of the receipt before it can be approved. Add the receipt first."),
            await ProblemAsync(await s.Owner.PostAsync($"{Url(s.Pid)}/expenses/{paint.Id}/approve", new ExpenseDecisionRequest())));
        var pdf = "%PDF-1.4\n%receipt\n"u8.ToArray();
        var receipt = await ReceiptOkAsync(s.Accountant, s, paint.Id, pdf, "Paint bill.pdf", "application/pdf");
        Assert.Equal(("Paint bill.pdf", "application/pdf"), (receipt.FileName, receipt.ContentType));
        var owner = await OkAsync<ExpenseDto>(await s.Owner.GetAsync($"{Url(s.Pid)}/expenses/{paint.Id}"));
        Assert.Contains("approve", owner.Actions);
        var approved = await OkAsync<ExpenseDto>(await s.Owner.PostAsync($"{Url(s.Pid)}/expenses/{paint.Id}/approve",
            new ExpenseDecisionRequest(null, owner.Version)));
        Assert.Equal("APPROVED", approved.Status);

        // The audit trail has the approval: SUBMITTED → APPROVED, by whom, with the reason.
        Assert.Equal("EX00001 approved: ₹2,400 for Maintenance", await ScalarAsync(
            $"SELECT reason FROM audit_logs WHERE entity_id = '{paint.Id}' AND changes ? 'Status' ORDER BY id DESC LIMIT 1"));
        Assert.Equal("{\"new\": \"APPROVED\", \"old\": \"SUBMITTED\"}", await ScalarAsync(
            $"SELECT changes->>'Status' FROM audit_logs WHERE entity_id = '{paint.Id}' AND changes ? 'Status' ORDER BY id DESC LIMIT 1"));

        // Frozen: no edit, no new receipt, no second decision; the database refuses a change even without the service.
        Assert.Equal(("EXPENSE_DECIDED", "EX00001 is approved, so it can't be changed. Record a new expense if something was wrong."),
            await ProblemAsync(await s.Owner.PutAsync($"{Url(s.Pid)}/expenses/{paint.Id}", Expense(s, 1m, "Paint", category: s.Repair, centre: s.Mnt))));
        Assert.Equal("EXPENSE_DECIDED", (await ProblemAsync(await UploadAsync(s.Owner, s, paint.Id, Png(), "late.png", "image/png"))).Code);
        Assert.Equal("EXPENSE_DECIDED", (await ProblemAsync(await s.Owner.PostAsync($"{Url(s.Pid)}/expenses/{paint.Id}/reject", new ExpenseDecisionRequest("Too much")))).Code);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"UPDATE expenses SET amount = 1, taxable_value = 1, cost_amount = 1 WHERE id = '{paint.Id}'"));
        Assert.Equal("ck_expenses_frozen", ex.ConstraintName);
        ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"DELETE FROM expenses WHERE id = '{paint.Id}'"));
        Assert.Equal("42501", ex.SqlState);

        // Small spends need no receipt; someone else's waiting expense: the manager can't change it, an approver can.
        var tea = await CreateOkAsync(s.Accountant, s, Expense(s, 450m, "Tea for the painters"));
        Assert.False(tea.NeedsReceipt);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Manager.PutAsync($"{Url(s.Pid)}/expenses/{tea.Id}", Expense(s, 400m, "Tea"))).StatusCode);
        var edited = await OkAsync<ExpenseDto>(await s.Owner.PutAsync($"{Url(s.Pid)}/expenses/{tea.Id}", Expense(s, 480m, "Tea and biscuits") with { Version = tea.Version }));
        Assert.Equal((480m, "Tea and biscuits"), (edited.Amount, edited.Description));
        Assert.Equal("APPROVED", (await OkAsync<ExpenseDto>(await s.Owner.PostAsync($"{Url(s.Pid)}/expenses/{tea.Id}/approve", new ExpenseDecisionRequest()))).Status);
        // Rejecting says why.
        var x = await CreateOkAsync(s.Manager, s, Expense(s, 100m, "Pens"));
        Assert.Equal("Say why (3–300 characters), e.g. No bill, or Paid from the owner's account.",
            await FieldErrorAsync(await s.Owner.PostAsync($"{Url(s.Pid)}/expenses/{x.Id}/reject", new ExpenseDecisionRequest(" ")), "Reason"));
        // The receipt limit is a property setting.
        await SettingAsync(s, "expenses.receipt_required_over", 5000);
        Assert.False((await OkAsync<ExpenseDto>(await s.Owner.GetAsync($"{Url(s.Pid)}/expenses/{x.Id}"))).NeedsReceipt);
    }

    [Fact]
    public async Task Receipts_are_checked_by_their_bytes_kept_encrypted_and_limited_to_four_and_recording_is_idempotent()
    {
        var s = await SetupAsync();
        var key = "ex-" + Guid.NewGuid();
        var body = Expense(s, 1_200m, "Printer paper", category: s.Office, centre: s.Fo);
        var first = await Create(s.Manager, s, body, key);
        var retry = await Create(s.Manager, s, body, key);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.True(retry.Headers.Contains("Idempotent-Replayed"));
        var e = (await first.Content.ReadFromJsonAsync<ExpenseDto>())!;
        Assert.Equal(e.Id, (await retry.Content.ReadFromJsonAsync<ExpenseDto>())!.Id);
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM expenses WHERE property_id = '{s.Pid}'"));

        var jpeg = Jpeg(50_000);
        var r = await ReceiptOkAsync(s.Manager, s, e.Id, jpeg, "IMG_1001.jpg", "image/jpeg");
        var storage = (string)(await ScalarAsync($"SELECT storage_key FROM expense_attachments WHERE id = '{r.Id}'"))!;
        var onDisk = await File.ReadAllBytesAsync(Path.Combine(app.FileRoot, storage));
        Assert.NotEqual(jpeg, onDisk);
        Assert.DoesNotContain("JFIF", System.Text.Encoding.ASCII.GetString(onDisk));
        var back = await s.Accountant.GetAsync($"{Url(s.Pid)}/expenses/{e.Id}/receipts/{r.Id}");
        Assert.Equal("image/jpeg", back.Content.Headers.ContentType!.MediaType);
        Assert.Equal(jpeg, await back.Content.ReadAsByteArrayAsync());

        Assert.Equal("That file isn't a receipt photo or a PDF. Send a JPEG, PNG or WebP photo, or a PDF bill.",
            await FieldErrorAsync(await UploadAsync(s.Manager, s, e.Id, "hello"u8.ToArray(), "bill.pdf", "application/pdf"), "File"));
        Assert.Equal("The receipt is 2.4 MB; at most 2 MB. Take the photo again or choose a smaller file.",
            await FieldErrorAsync(await UploadAsync(s.Manager, s, e.Id, Jpeg(2_500_000), "big.jpg", "image/jpeg"), "File"));
        for (var i = 0; i < 3; i++) await ReceiptOkAsync(s.Manager, s, e.Id, Png(), $"p{i}.png", "image/png");
        Assert.Equal(("TOO_MANY_RECEIPTS", "EX00001 already has 4 receipts, the most it can have."),
            await ProblemAsync(await UploadAsync(s.Manager, s, e.Id, Png(), "p5.png", "image/png")));
        var full = await OkAsync<ExpenseDto>(await s.Manager.GetAsync($"{Url(s.Pid)}/expenses/{e.Id}"));
        Assert.Equal(["IMG_1001.jpg", "p0.png", "p1.png", "p2.png"], full.Receipts.Select(x => x.FileName));
        Assert.DoesNotContain("receipt", full.Actions);

        // Who sees what: a housekeeper has no expenses; the manager doesn't manage budgets.
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Housekeeper.GetAsync($"{Url(s.Pid)}/expenses")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Manager.PostAsync($"{Url(s.Pid)}/budgets", new CreateBudgetRequest(YearOf(Today)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Manager.GetAsync($"{Url(s.Pid)}/budgets/report")).StatusCode);
    }

    // ------------------------------------------------------------------ budgets

    [Fact]
    public async Task The_budget_report_shows_each_departments_variance_and_a_run_rate_forecast()
    {
        var s = await SetupAsync();
        // 31 October 2026 at noon: 214 days of the 365 in 2026-27 have passed.
        app.Clock.At = new DateTimeOffset(2026, 10, 31, 6, 30, 0, TimeSpan.Zero);
        try
        {
            var budget = await OkAsync<BudgetDto>(await s.Owner.PostAsync($"{Url(s.Pid)}/budgets", new CreateBudgetRequest(2026, "Plan 2026-27")), HttpStatusCode.Created);
            Assert.Equal(("2026-27", new DateOnly(2026, 4, 1), new DateOnly(2027, 3, 1)), (budget.Label, budget.Months[0], budget.Months[11]));
            budget = await OkAsync<BudgetDto>(await s.Owner.PutAsync($"{Url(s.Pid)}/budgets/{budget.Id}/rows",
                new SaveBudgetRowRequest(s.Hk, Enumerable.Repeat(3_000m, 12).ToList(), budget.Version)));
            var stale = budget.Version;
            budget = await OkAsync<BudgetDto>(await s.Owner.PutAsync($"{Url(s.Pid)}/budgets/{budget.Id}/rows",
                new SaveBudgetRowRequest(s.Fb, Enumerable.Repeat(5_000m, 12).ToList(), budget.Version)));
            Assert.Equal(96_000m, budget.Total);
            Assert.Equal("CONCURRENCY_CONFLICT", (await ProblemAsync(await s.Owner.PutAsync($"{Url(s.Pid)}/budgets/{budget.Id}/rows",
                new SaveBudgetRowRequest(s.Fb, Enumerable.Repeat(1m, 12).ToList(), stale)))).Code);
            Assert.Equal("Give twelve amounts, April to March.", await FieldErrorAsync(await s.Owner.PutAsync($"{Url(s.Pid)}/budgets/{budget.Id}/rows",
                new SaveBudgetRowRequest(s.Fb, [1m, 2m], budget.Version)), "Months"));
            Assert.Equal("There is already a budget for 2026-27. Open it to change it.",
                await FieldErrorAsync(await s.Owner.PostAsync($"{Url(s.Pid)}/budgets", new CreateBudgetRequest(2026)), "Year"));

            // Housekeeping spent 21,400 by the end of October against 21,000 planned: over. F&B 10,700 of 35,000: on track.
            // Maintenance spent 500 with nothing planned.
            await ApprovedAsync(s, new DateOnly(2026, 6, 10), 20_000m, s.Hk);
            await ApprovedAsync(s, new DateOnly(2026, 10, 30), 1_400m, s.Hk);
            await ApprovedAsync(s, new DateOnly(2026, 9, 1), 10_700m, s.Fb);
            await ApprovedAsync(s, new DateOnly(2026, 8, 1), 500m, s.Mnt);
            await CreateOkAsync(s.Manager, s, Expense(s, 300m, "Waiting", centre: s.Fb) with { ExpenseDate = new DateOnly(2026, 10, 2) });

            var report = await OkAsync<BudgetReportDto>(await s.Manager.GetAsync($"{Url(s.Pid)}/budgets/report?year=2026"));
            Assert.Equal((budget.Id, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)), (report.BudgetId, report.Through, report.AsOf));
            var hk = report.Rows.Single(r => r.CostCentreId == s.Hk);
            Assert.Equal((3_000m, 1_400m, 21_000m, 21_400m, -400m, -1.9m, 36_000m, 36_500m, -500m, "OVER"),
                (hk.MonthBudget, hk.MonthActual, hk.BudgetToDate, hk.ActualToDate, hk.Variance, hk.VariancePercent, hk.AnnualBudget, hk.Forecast, hk.ForecastVariance, hk.Standing));
            var fb = report.Rows.Single(r => r.CostCentreId == s.Fb);
            Assert.Equal((35_000m, 10_700m, 24_300m, 18_250m, "ON_TRACK", 300m, 1), (fb.BudgetToDate, fb.ActualToDate, fb.Variance, fb.Forecast, fb.Standing, fb.Waiting, fb.WaitingCount));
            Assert.Equal(("NO_BUDGET", 500m, (decimal?)null), (report.Rows.Single(r => r.CostCentreId == s.Mnt).Standing,
                report.Rows.Single(r => r.CostCentreId == s.Mnt).ActualToDate, report.Rows.Single(r => r.CostCentreId == s.Mnt).VariancePercent));
            Assert.Equal((56_000m, 32_600m, 96_000m), (report.Total.BudgetToDate, report.Total.ActualToDate, report.Total.AnnualBudget));
            Assert.Equal((8_000m, 20_000m), (report.Months[2].Budget, report.Months[2].Actual));   // June

            // Through June only: the forecast runs to 30 June (91 days).
            var june = await OkAsync<BudgetReportDto>(await s.Owner.GetAsync($"{Url(s.Pid)}/budgets/report?year=2026&through=2026-06-01"));
            hk = june.Rows.Single(r => r.CostCentreId == s.Hk);
            Assert.Equal((new DateOnly(2026, 6, 30), 9_000m, 20_000m, 80_219.78m, "OVER"), (june.AsOf, hk.BudgetToDate, hk.ActualToDate, hk.Forecast, hk.Standing));
            Assert.Equal("Choose a month of 2026-27 (April 2026 to March 2027).",
                await FieldErrorAsync(await s.Owner.GetAsync($"{Url(s.Pid)}/budgets/report?year=2026&through=2027-04-01"), "Through"));

            // Next year's budget copied from this one with 10 % added.
            var next = await OkAsync<BudgetDto>(await s.Owner.PostAsync($"{Url(s.Pid)}/budgets",
                new CreateBudgetRequest(2027, null, null, budget.Id, 10)), HttpStatusCode.Created);
            Assert.Equal(("Budget 2027-28", 105_600m, 3_300m, new DateOnly(2027, 4, 1)),
                (next.Name, next.Total, next.Rows.Single(r => r.CostCentreId == s.Hk).Months[0], next.Months[0]));
            Assert.Equal("A budget can be made for last year, this year or next year (2025-26 to 2027-28).",
                await FieldErrorAsync(await s.Owner.PostAsync($"{Url(s.Pid)}/budgets", new CreateBudgetRequest(2028)), "Year"));
            var list = await OkAsync<List<BudgetSummaryDto>>(await s.Owner.GetAsync($"{Url(s.Pid)}/budgets"));
            Assert.Equal(["2027-28", "2026-27"], list.Select(b => b.Label));

            // A line outside its budget's year is refused by the database.
            var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
                $"UPDATE budget_lines SET month = '2027-04-01' WHERE budget_id = '{budget.Id}' AND month = '2027-03-01' AND cost_centre_id = '{s.Hk}'"));
            Assert.Equal("ck_budget_lines_in_year", ex.ConstraintName);
        }
        finally { app.Clock.At = null; }
    }

    // ------------------------------------------------------------------ setup and helpers

    private sealed record Setup(Guid Pid, TestClient Owner, TestClient Manager, TestClient Accountant, TestClient Housekeeper,
        Guid Fuel, Guid Repair, Guid Mkt, Guid Office, Guid StaffWelfare, Guid Fo, Guid Hk, Guid Fb, Guid Mnt);

    private async Task<Setup> SetupAsync(bool registered = false)
    {
        app.Clock.At = null;
        var owner = await app.SignInOwnerAsync();
        var code = "X" + Suffix();
        var res = await owner.PostAsync("/api/v1/properties", new CreatePropertyRequest(code, $"Spend Shack {code}", null, "RESORT", "Asia/Kolkata", "INR",
            null, null, null, null, null, "30", null, "IN", null, null, new TimeOnly(14, 0), new TimeOnly(11, 0), null, null));
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        var pid = (await res.Content.ReadFromJsonAsync<PropertyDto>())!.Id;
        if (registered)
            Assert.Equal(HttpStatusCode.Created, (await owner.PostAsync($"/api/v1/properties/{pid}/gst-registrations",
                new CreateGstRegistrationRequest("30AABCT1234C1Z9", "Test Resorts Pvt Ltd", null, "REGULAR", new DateOnly(2017, 7, 1)))).StatusCode);
        var manager = await StaffAsync("MANAGER", pid);
        var accountant = await StaffAsync("ACCOUNTANT", pid);
        var housekeeper = await StaffAsync("HOUSEKEEPER", pid);
        await ExecuteAsync($"UPDATE users SET full_name = 'Accounts user' WHERE id = '{accountant.Me!.UserId}'");

        var setup = await OkAsync<ExpenseSetupDto>(await manager.GetAsync($"{Url(pid)}/expenses/setup"));
        Assert.Equal((11, 8, false, 500m, false), (setup.Categories.Count, setup.CostCentres.Count, setup.CanApprove, setup.ReceiptRequiredOver, setup.CanManage));
        Guid Cat(string c) => setup.Categories.Single(x => x.Code == c).Id;
        Guid Cc(string c) => setup.CostCentres.Single(x => x.Code == c).Id;
        return new Setup(pid, owner, manager, accountant, housekeeper, Cat("FUEL"), Cat("REPAIR"), Cat("MKT"), Cat("OFFICE"), Cat("STAFFW"),
            Cc("FO"), Cc("HK"), Cc("FB"), Cc("MNT"));
    }

    private static SaveExpenseRequest Expense(Setup s, decimal amount, string what, string? gstin = null, string? bill = null, decimal gst = 0,
        Guid? category = null, Guid? centre = null) =>
        new(Today, category ?? s.Fuel, centre ?? s.Hk, what, "Malvan Traders", "UPI", amount, "UPI 4021", gstin, bill, gst);

    /// <summary>An expense recorded by the manager on <paramref name="day"/> and approved by the accountant (no receipt
    /// needed: the limit is raised for these).</summary>
    private async Task ApprovedAsync(Setup s, DateOnly day, decimal amount, Guid centre)
    {
        await SettingAsync(s, "expenses.receipt_required_over", 1_000_000);
        var e = await CreateOkAsync(s.Manager, s, Expense(s, amount, "Spend", centre: centre) with { ExpenseDate = day });
        Assert.Equal("APPROVED", (await OkAsync<ExpenseDto>(await s.Accountant.PostAsync($"{Url(s.Pid)}/expenses/{e.Id}/approve", new ExpenseDecisionRequest()))).Status);
    }

    private static Task<HttpResponseMessage> Create(TestClient c, Setup s, SaveExpenseRequest body, string? key = null) =>
        c.PostAsync($"{Url(s.Pid)}/expenses", body, new Dictionary<string, string> { ["Idempotency-Key"] = key ?? "ex-" + Guid.NewGuid() });

    private static async Task<ExpenseDto> CreateOkAsync(TestClient c, Setup s, SaveExpenseRequest body) =>
        await OkAsync<ExpenseDto>(await Create(c, s, body), HttpStatusCode.Created);

    private static async Task<BudgetReportDto> ReportAsync(Setup s) =>
        await OkAsync<BudgetReportDto>(await s.Owner.GetAsync($"{Url(s.Pid)}/budgets/report"));

    private static Task<HttpResponseMessage> UploadAsync(TestClient c, Setup s, Guid id, byte[] bytes, string name, string type)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        content.Add(file, "file", name);
        return c.PostContentAsync($"{Url(s.Pid)}/expenses/{id}/receipts", content);
    }

    private static async Task<ExpenseReceiptDto> ReceiptOkAsync(TestClient c, Setup s, Guid id, byte[] bytes, string name, string type) =>
        await OkAsync<ExpenseReceiptDto>(await UploadAsync(c, s, id, bytes, name, type), HttpStatusCode.Created);

    private static async Task SettingAsync(Setup s, string key, object value) =>
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.PutAsync($"/api/v1/properties/{s.Pid}/settings/{key}",
            new PutPropertySettingRequest(JsonSerializer.SerializeToElement(value)))).StatusCode);

    private async Task<TestClient> StaffAsync(string role, Guid pid)
    {
        var (email, pw) = await app.CreateUserAsync(role, pid);
        return await app.SignInAsync(email, pw);
    }

    private static int YearOf(DateOnly d) => d.Month >= 4 ? d.Year : d.Year - 1;
    private static string Gstin(string first14) => first14 + Resort.Domain.Tax.Gstin.CheckDigit(first14);
    private static string Url(Guid pid) => $"/api/v1/properties/{pid}";
    private static string Day(DateOnly d) => Resort.Domain.Rates.QuoteEngine.Day(d);

    private static byte[] Jpeg(int size)
    {
        var b = new byte[size];
        new Random(size).NextBytes(b);
        byte[] head = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00];
        head.CopyTo(b, 0);
        return b;
    }

    private static byte[] Png() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 1, 2, 3, 4];

    private static async Task<(string Code, string Message)> ProblemAsync(HttpResponseMessage res)
    {
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("code", out var code), $"{(int)res.StatusCode}: {body}");
        return (code.GetString()!, body.GetProperty("message").GetString()!);
    }

    private static async Task<T> OkAsync<T>(HttpResponseMessage res, HttpStatusCode status = HttpStatusCode.OK)
    {
        Assert.True(res.StatusCode == status, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

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
