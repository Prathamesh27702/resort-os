using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Resort.Contracts;
using Resort.Tests.Support;
using static Resort.Tests.Support.BookingKit;

namespace Resort.Tests.Integration;

/// <summary>Sprint 10 — payments, refunds and the cash drawer. Taking money onto a bill, refunding it safely (never more than
/// was captured, even when two refunds arrive at once), voiding a mistake, and closing the drawer with a variance that a
/// manager approves. Nothing about money is ever edited or deleted. Real API, real PostgreSQL. The tests share one cash
/// drawer (the property's), so each one that uses it starts its own shift.</summary>
public class PaymentTests(FolioFactory app) : IClassFixture<FolioFactory>
{
    // ------------------------------------------------------------------ the sprint review demo (acceptance)

    [Fact]
    public async Task The_demo_cash_and_UPI_then_a_partial_refund_and_the_drawer_closes_50_short_and_a_manager_approves()
    {
        var (owner, pid) = await OwnerAsync();
        var desk = await StaffAsync("RECEPTIONIST", pid);
        var accountant = await StaffAsync("ACCOUNTANT", pid);
        var (folio, booking) = await InHouseFolioAsync(owner, pid);   // ₹7,000 of room nights
        await FreshShiftAsync(desk, pid, 2000m);

        // ₹3,000 cash and ₹4,000 UPI pay the bill
        var cash = await PayAsync(desk, pid, new CreatePaymentRequest("CASH", 3000m, folio.Id, Note: "Paid at the desk"));
        var upi = await PayAsync(desk, pid, new CreatePaymentRequest("UPI", 4000m, folio.Id, PaymentReference: "412345678901"));
        Assert.Matches("^P[0-9]{5}$", cash.Number);
        Assert.Equal(("CAPTURED", 3000m, 3000m, 0m, 3000m), (cash.Status, cash.Amount, cash.Allocated, cash.OnAccount, cash.Refundable));
        Assert.Equal((booking.Reference, "RECEPTIONIST user"), (upi.Reference, upi.ReceivedByName));
        folio = await GetFolioAsync(desk, pid, folio.Id);
        Assert.Equal((7000m, 7000m, 0m), (folio.Total, folio.Payments, folio.Balance));
        Assert.Equal(new[] { ("PAYMENT", "CASH", 3000m), ("PAYMENT", "UPI", 4000m) }, folio.PaymentLines.Select(l => (l.Kind, l.Method, l.Amount)));

        // the guest didn't use the second night's dinner credit: ₹1,500 of the UPI goes back, with a reason
        var refunded = await RefundAsync(accountant, pid, upi.Id, new RefundPaymentRequest(1500m, "Dinner credit not used"));
        Assert.Equal(("PARTIALLY_REFUNDED", 1500m, 2500m), (refunded.Status, refunded.Refunded, refunded.Refundable));
        var refund = Assert.Single(refunded.Refunds);
        Assert.Equal(("UPI", "Dinner credit not used", folio.Number, "ACCOUNTANT user"), (refund.Method, refund.Reason, refund.FolioNumber, refund.RefundedByName));
        folio = await GetFolioAsync(desk, pid, folio.Id);
        Assert.Equal((5500m, 1500m), (folio.Payments, folio.Balance));
        Assert.Equal(-1500m, folio.PaymentLines.Single(l => l.Kind == "REFUND").Amount);

        // the drawer should hold ₹2,000 + ₹3,000; reception counts ₹4,950
        var drawer = await desk.GetJsonAsync<CashDrawerDto>($"/api/v1/properties/{pid}/cash-drawer");
        Assert.Equal((5000m, 3000m, 0m, 1), (drawer.Current!.Expected, drawer.Current.CashIn, drawer.Current.CashOut, drawer.Current.Payments));
        Assert.Equal("The drawer should hold ₹5,000 but ₹4,950 was counted: ₹50 short. Count again, or say why (at least 3 characters) so a manager can approve it.",
            await FieldErrorAsync(await desk.PostAsync($"/api/v1/properties/{pid}/cash-drawer/close", new CloseCashDrawerRequest(4950m)), "Note"));
        var closed = await OkAsync<CashDrawerSessionDto>(await desk.PostAsync($"/api/v1/properties/{pid}/cash-drawer/close",
            new CloseCashDrawerRequest(4950m, "Gave ₹50 too much change, I think")));
        Assert.Equal(("VARIANCE_PENDING", 5000m, 4950m, -50m), (closed.Status, closed.Expected, closed.Counted, closed.Variance));
        Assert.Equal(new[] { "OPENING_FLOAT", "PAYMENT" }, closed.Movements!.Select(m => m.MovementType));

        // reception can't approve its own shortfall; the accountant can, with a note
        var approveUrl = $"/api/v1/properties/{pid}/cash-drawer/sessions/{closed.Id}/approve-variance";
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PostAsync(approveUrl, new ApproveVarianceRequest("ok"))).StatusCode);
        drawer = await accountant.GetJsonAsync<CashDrawerDto>($"/api/v1/properties/{pid}/cash-drawer");
        Assert.Null(drawer.Current);
        Assert.Contains(drawer.PendingApproval, s => s.Id == closed.Id);
        Assert.Equal("Say why the variance is approved (at least 3 characters), e.g. Change given wrongly to a guest; staff told.",
            await FieldErrorAsync(await accountant.PostAsync(approveUrl, new ApproveVarianceRequest(" ")), "Note"));
        var approved = await OkAsync<CashDrawerSessionDto>(await accountant.PostAsync(approveUrl, new ApproveVarianceRequest("Counted twice; staff briefed on change")));
        Assert.Equal(("CLOSED", -50m, "ACCOUNTANT user"), (approved.Status, approved.Variance, approved.ApprovedByName));
        var again = await accountant.PostAsync(approveUrl, new ApproveVarianceRequest("Again"));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.StartsWith("This shift's variance was already approved at ", (await ProblemAsync(again)).Message);

        // and the approval is in the audit trail with its note
        Assert.Equal(1L, await ScalarAsync(
            $"SELECT count(*) FROM audit_logs WHERE entity_id = '{closed.Id}' AND reason = 'Counted twice; staff briefed on change'"));
    }

    // ------------------------------------------------------------------ concurrency (required test)

    [Fact]
    public async Task Two_refunds_at_the_same_time_can_never_exceed_what_was_captured()
    {
        var (owner, pid) = await OwnerAsync();
        var manager = await StaffAsync("MANAGER", pid);
        var (folio, _) = await InHouseFolioAsync(owner, pid);
        var payment = await PayAsync(owner, pid, new CreatePaymentRequest("UPI", 5000m, folio.Id));

        for (var run = 0; run < 5; run++)
        {
            // each refund alone fits (₹3,000 ≤ what is left), both together don't
            var left = (await GetPaymentAsync(owner, pid, payment.Id)).Refundable;
            var each = run < 1 ? 3000m : decimal.Round(left / 2, 2, MidpointRounding.ToZero) + 0.01m;
            if (each > left) break;
            using var barrier = new Barrier(2);
            Task<HttpResponseMessage> Refund(TestClient c) => Task.Run(() =>
            {
                barrier.SignalAndWait();
                return c.PostAsync($"/api/v1/properties/{pid}/payments/{payment.Id}/refund", new RefundPaymentRequest(each, "Overcharged"),
                    Key("refund"));
            });
            var results = await Task.WhenAll(Refund(owner), Refund(manager));
            Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
            var refused = Assert.Single(results, r => r.StatusCode != HttpStatusCode.OK);
            Assert.StartsWith($"{payment.Number} was ₹5,000 and ", await FieldErrorAsync(refused, "Amount"));
        }

        Assert.True((decimal)(await ScalarAsync($"SELECT sum(amount) FROM refunds WHERE payment_id = '{payment.Id}'"))! <= 5000m);
        var after = await GetPaymentAsync(owner, pid, payment.Id);
        Assert.Equal(after.Refunded, after.Refunds.Sum(r => r.Amount));
        Assert.Equal(5000m - after.Refunded, after.Refundable);

        // the database refuses an over-refund on its own too, under its lock on the payment
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"""
             INSERT INTO refunds (organization_id, property_id, payment_id, folio_id, amount, method, reason, refunded_at)
             SELECT organization_id, property_id, id, '{folio.Id}', {after.Refundable + 1}, 'UPI', 'too much', now() FROM payments WHERE id = '{payment.Id}'
             """));
        Assert.Equal("ck_refunds_limit", ex.ConstraintName);
    }

    // ------------------------------------------------------------------ immutability (required test)

    [Fact]
    public async Task Deleting_or_editing_payments_refunds_allocations_and_drawer_lines_is_rejected_by_the_database()
    {
        var (owner, pid) = await OwnerAsync();
        var (folio, _) = await InHouseFolioAsync(owner, pid);
        var session = await FreshShiftAsync(owner, pid, 1000m);
        var payment = await PayAsync(owner, pid, new CreatePaymentRequest("CASH", 1200m, folio.Id));
        payment = await RefundAsync(owner, pid, payment.Id, new RefundPaymentRequest(200m, "Change owed to the guest"));

        foreach (var sql in new[]
                 {
                     $"DELETE FROM payments WHERE id = '{payment.Id}'",
                     $"UPDATE payments SET amount = 1 WHERE id = '{payment.Id}'",
                     $"UPDATE payments SET method = 'UPI', cash_drawer_session_id = NULL WHERE id = '{payment.Id}'",
                     $"DELETE FROM payment_allocations WHERE payment_id = '{payment.Id}'",
                     $"UPDATE payment_allocations SET amount = 1 WHERE payment_id = '{payment.Id}'",
                     $"DELETE FROM refunds WHERE payment_id = '{payment.Id}'",
                     $"UPDATE refunds SET amount = 1 WHERE payment_id = '{payment.Id}'",
                     $"DELETE FROM cash_drawer_movements WHERE session_id = '{session.Id}'",
                     $"UPDATE cash_drawer_movements SET amount = 1 WHERE session_id = '{session.Id}'",
                     $"DELETE FROM cash_drawer_sessions WHERE id = '{session.Id}'",
                 })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        // a status move that isn't in the graph, a card number on a card payment, or cash outside a drawer are refused too
        var back = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"UPDATE payments SET status = 'CAPTURED' WHERE id = '{payment.Id}'"));
        Assert.Equal("ck_payments_status_transition", back.ConstraintName);
        var card = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"""
             INSERT INTO payments (organization_id, property_id, number, reservation_id, method, status, amount, currency, reference, received_at)
             SELECT organization_id, property_id, 'P99999', reservation_id, 'CARD', 'CAPTURED', 10, currency, '4111 1111 1111 1111', now()
               FROM payments WHERE id = '{payment.Id}'
             """));
        Assert.Equal("ck_payments_no_card_number", card.ConstraintName);
        var loose = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"""
             INSERT INTO payments (organization_id, property_id, number, reservation_id, method, status, amount, currency, received_at)
             SELECT organization_id, property_id, 'P99998', reservation_id, 'CASH', 'CAPTURED', 10, currency, now() FROM payments WHERE id = '{payment.Id}'
             """));
        Assert.Equal("ck_payments_drawer", loose.ConstraintName);

        var unchanged = await GetPaymentAsync(owner, pid, payment.Id);
        Assert.Equal((1200m, 200m, "PARTIALLY_REFUNDED"), (unchanged.Amount, unchanged.Refunded, unchanged.Status));
    }

    // ------------------------------------------------------------------ taking a payment

    [Fact]
    public async Task A_payment_needs_an_idempotency_key_a_retry_takes_it_once_and_every_mistake_is_named_under_its_field()
    {
        var (owner, pid) = await OwnerAsync();
        var (folio, _) = await InHouseFolioAsync(owner, pid);
        var url = $"/api/v1/properties/{pid}/payments";
        var body = new CreatePaymentRequest("UPI", 1000m, folio.Id, PaymentReference: "UTR998877665544");

        Assert.StartsWith("Send an Idempotency-Key header", await FieldErrorAsync(await owner.PostAsync(url, body), "Idempotency-Key"));
        var key = Key("pay");
        var first = await owner.PostAsync(url, body, key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var second = await owner.PostAsync(url, body, key);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal((await first.Content.ReadFromJsonAsync<PaymentDto>())!.Id, (await second.Content.ReadFromJsonAsync<PaymentDto>())!.Id);
        Assert.Equal(1000m, (await GetFolioAsync(owner, pid, folio.Id)).Payments);

        Assert.Equal("Choose how the money was paid: Cash, UPI, Card, Bank transfer.",
            await FieldErrorAsync(await owner.PostAsync(url, body with { Method = "CHEQUE" }, Key("pay")), "Method"));
        Assert.Equal("The amount must be more than ₹0.", await FieldErrorAsync(await owner.PostAsync(url, body with { Amount = 0 }, Key("pay")), "Amount"));
        Assert.Equal("Choose the folio this pays, or the booking to put the money on account for.",
            await FieldErrorAsync(await owner.PostAsync(url, body with { FolioId = null }, Key("pay")), "ReservationId"));

        // no card data, ever: a card number is refused wherever it is typed
        Assert.StartsWith("Don't type the card number: card details are never stored.", await FieldErrorAsync(
            await owner.PostAsync(url, body with { Method = "CARD", PaymentReference = "4111 1111 1111 1111" }, Key("pay")), "PaymentReference"));
        Assert.StartsWith("This looks like a card number", await FieldErrorAsync(
            await owner.PostAsync(url, body with { PaymentReference = "5555-5555-5555-4444" }, Key("pay")), "PaymentReference"));
        Assert.StartsWith("This looks like a card number", await FieldErrorAsync(
            await owner.PostAsync(url, body with { Note = "card 4111111111111111 exp 12/29" }, Key("pay")), "Note"));
        var approval = await PayAsync(owner, pid, body with { Method = "CARD", Amount = 100m, PaymentReference = "Approval 0A12BC RRN 123456789012" });
        Assert.Equal("Approval 0A12BC RRN 123456789012", approval.PaymentReference);

        // a folio takes at most what it owes; the rest goes on account
        var owed = (await GetFolioAsync(owner, pid, folio.Id)).Balance;
        Assert.Equal($"{folio.Number} owes {Rupees(owed)}, so it can take at most that. Take {Rupees(owed)}, or record the rest on account for the booking.",
            await FieldErrorAsync(await owner.PostAsync(url, body with { Amount = owed + 1 }, Key("pay")), "Amount"));
    }

    [Fact]
    public async Task Cash_needs_an_open_drawer_and_the_drawer_opens_once()
    {
        var (owner, pid) = await OwnerAsync();
        var desk = await StaffAsync("RECEPTIONIST", pid);
        var (folio, _) = await InHouseFolioAsync(owner, pid);
        await CloseAnyShiftAsync(owner, pid);

        Assert.StartsWith("The Front desk cash drawer isn't open, so a payment in cash can't be recorded. Open it on the front desk",
            await FieldErrorAsync(await desk.PostAsync($"/api/v1/properties/{pid}/payments", new CreatePaymentRequest("CASH", 500m, folio.Id), Key("pay")), "Method"));
        Assert.Equal("DRAWER_NOT_OPEN", (await ProblemAsync(await desk.PostAsync($"/api/v1/properties/{pid}/cash-drawer/close",
            new CloseCashDrawerRequest(0m)))).Code);
        Assert.Equal("The opening float can't be negative.",
            await FieldErrorAsync(await desk.PostAsync($"/api/v1/properties/{pid}/cash-drawer/open", new OpenCashDrawerRequest(-1m)), "OpeningFloat"));

        var shift = await FreshShiftAsync(desk, pid, 1500m);
        Assert.Equal(("OPEN", 1500m, 1500m, "RECEPTIONIST user"), (shift.Status, shift.OpeningFloat, shift.Expected, shift.OpenedByName));
        var twice = await desk.PostAsync($"/api/v1/properties/{pid}/cash-drawer/open", new OpenCashDrawerRequest(100m));
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        var (code, message) = await ProblemAsync(twice);
        Assert.Equal("DRAWER_ALREADY_OPEN", code);
        Assert.Matches(@"^The Front desk drawer is already open since \d\d:\d\d on .+ \(RECEPTIONIST user\)\. Close that shift before opening a new one\.$", message);

        await PayAsync(desk, pid, new CreatePaymentRequest("CASH", 500m, folio.Id));
        // a shift that balances closes at once, with nothing to approve
        var closed = await OkAsync<CashDrawerSessionDto>(await desk.PostAsync($"/api/v1/properties/{pid}/cash-drawer/close", new CloseCashDrawerRequest(2000m)));
        Assert.Equal(("CLOSED", 2000m, 0m, (DateTimeOffset?)null), (closed.Status, closed.Expected, closed.Variance, closed.ApprovedAt));
        Assert.Equal("NO_VARIANCE_PENDING", (await ProblemAsync(await owner.PostAsync(
            $"/api/v1/properties/{pid}/cash-drawer/sessions/{closed.Id}/approve-variance", new ApproveVarianceRequest("Fine")))).Code);
    }

    // ------------------------------------------------------------------ on account and allocation

    [Fact]
    public async Task An_advance_stays_on_account_counts_for_check_out_and_is_allocated_to_the_bill_later()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 1);
        var guest = await CreateGuestAsync(owner, "Meera", "Iyer");
        var booked = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, await WalkInAsync(owner, pid), Today, Today.AddDays(1))));

        // before check-in the bill is empty, so the advance goes on account for the booking
        var folio = await MainFolioAsync(owner, pid, booked.Id);
        Assert.StartsWith($"{folio.Number} owes nothing. To take an advance", await FieldErrorAsync(await owner.PostAsync(
            $"/api/v1/properties/{pid}/payments", new CreatePaymentRequest("BANK_TRANSFER", 3500m, folio.Id), Key("pay")), "Amount"));
        var advance = await PayAsync(owner, pid, new CreatePaymentRequest("BANK_TRANSFER", 3500m, ReservationId: booked.Id, PaymentReference: "HDFCR52026102012345678"));
        Assert.Equal((0m, 3500m), (advance.Allocated, advance.OnAccount));

        // check-in posts the night; the folio owes it, but the booking as a whole owes nothing, so check-out is allowed
        await FrontDeskTests.OkAsync(await owner.PostAsync(FrontDeskTests.CheckInUrl(pid, booked), new CheckInRequest(null)));
        folio = await GetFolioAsync(owner, pid, folio.Id);
        Assert.Equal((3500m, 0m, 3500m), (folio.Total, folio.Payments, folio.Balance));
        var inHouse = await owner.GetJsonAsync<FrontDeskListDto>($"/api/v1/properties/{pid}/front-desk/in-house");
        Assert.Equal(0m, inHouse.Items.Single(i => i.ReservationId == booked.Id).Balance);

        // allocate: at most what is on account and what the folio owes, onto this booking's folios only
        var url = $"/api/v1/properties/{pid}/payments/{advance.Id}/allocate";
        Assert.Equal($"{advance.Number} has ₹3,500 on account; ₹4,000 can't be allocated. Allocate at most ₹3,500.",
            await FieldErrorAsync(await owner.PostAsync(url, new AllocatePaymentRequest([new AllocationLine(folio.Id, 4000m)])), "Allocations"));
        var (other, _) = await InHouseFolioAsync(owner, pid);
        Assert.StartsWith($"One of the folios isn't a bill of {booked.Reference}.",
            await FieldErrorAsync(await owner.PostAsync(url, new AllocatePaymentRequest([new AllocationLine(other.Id, 100m)])), "Allocations"));
        var allocated = await OkAsync<PaymentDto>(await owner.PostAsync(url, new AllocatePaymentRequest([new AllocationLine(folio.Id, 3500m)])));
        Assert.Equal((3500m, 0m), (allocated.Allocated, allocated.OnAccount));
        Assert.Equal(0m, (await GetFolioAsync(owner, pid, folio.Id)).Balance);

        // the calendar shows the booking as paid
        var calendar = await owner.GetJsonAsync<CalendarDto>($"/api/v1/properties/{pid}/calendar?from={Today:yyyy-MM-dd}&to={Today.AddDays(1):yyyy-MM-dd}");
        Assert.Equal("PAID", calendar.Bookings.First(b => b.ReservationId == booked.Id).PaymentStatus);
    }

    [Fact]
    public async Task A_guest_who_paid_in_full_and_leaves_a_night_early_is_refunded_the_night_from_the_bill()
    {
        var (owner, pid) = await OwnerAsync();
        var (folio, booking) = await InHouseFolioAsync(owner, pid);
        var paid = await PayAsync(owner, pid, new CreatePaymentRequest("UPI", 7000m, folio.Id));

        // leaving today, a night early: the second night comes off and the folio is ₹3,500 in credit
        await FrontDeskTests.OkAsync(await owner.PostAsync($"/api/v1/properties/{pid}/reservations/{booking.Id}/check-out",
            new CheckOutRequest(null, "Flight moved up")));
        folio = await GetFolioAsync(owner, pid, folio.Id);
        Assert.Equal((3500m, 7000m, -3500m), (folio.Total, folio.Payments, folio.Balance));

        // more than is left is refused under the amount; the night is refunded from the bill
        Assert.Equal($"{paid.Number} was ₹7,000 and ₹0 of it is already refunded, so at most ₹7,000 can be refunded.",
            await FieldErrorAsync(await owner.PostAsync($"/api/v1/properties/{pid}/payments/{paid.Id}/refund", new RefundPaymentRequest(7000.5m, "Night not used"),
                Key("refund")), "Amount"));
        Assert.Equal("Say why the money is refunded (at least 3 characters), e.g. Guest cancelled the airport transfer.",
            await FieldErrorAsync(await owner.PostAsync($"/api/v1/properties/{pid}/payments/{paid.Id}/refund", new RefundPaymentRequest(3500m, ""),
                Key("refund")), "Reason"));
        var refunded = await RefundAsync(owner, pid, paid.Id, new RefundPaymentRequest(3500m, "Night not used", FolioId: folio.Id));
        Assert.Equal(("PARTIALLY_REFUNDED", 3500m), (refunded.Status, refunded.Refundable));
        Assert.Equal(0m, (await GetFolioAsync(owner, pid, folio.Id)).Balance);

        // the rest refunds in full and the payment is REFUNDED; then nothing is left
        refunded = await RefundAsync(owner, pid, paid.Id, new RefundPaymentRequest(3500m, "Stay cancelled after all"));
        Assert.Equal(("REFUNDED", 0m), (refunded.Status, refunded.Refundable));
        Assert.Equal("PAYMENT_NOT_CAPTURED", (await ProblemAsync(await owner.PostAsync($"/api/v1/properties/{pid}/payments/{paid.Id}/refund",
            new RefundPaymentRequest(1m, "More"), Key("refund")))).Code);
    }

    // ------------------------------------------------------------------ void

    [Fact]
    public async Task A_cash_payment_entered_by_mistake_is_voided_while_its_shift_is_open_and_refunded_after()
    {
        var (owner, pid) = await OwnerAsync();
        var desk = await StaffAsync("RECEPTIONIST", pid);
        var (folio, _) = await InHouseFolioAsync(owner, pid);
        await FreshShiftAsync(owner, pid, 1000m);
        var wrong = await PayAsync(desk, pid, new CreatePaymentRequest("CASH", 2500m, folio.Id));
        var kept = await PayAsync(desk, pid, new CreatePaymentRequest("CASH", 500m, folio.Id));

        // reception can take payments but not void them
        var url = $"/api/v1/properties/{pid}/payments/{wrong.Id}/void";
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PostAsync(url, new VoidPaymentRequest("Typo"))).StatusCode);
        Assert.Equal("Say why the payment is voided (at least 3 characters), e.g. Entered twice by mistake.",
            await FieldErrorAsync(await owner.PostAsync(url, new VoidPaymentRequest("x")), "Reason"));
        var voided = await OkAsync<PaymentDto>(await owner.PostAsync(url, new VoidPaymentRequest("Typed 2,500 for 250")));
        Assert.Equal(("CANCELLED", "Typed 2,500 for 250", false), (voided.Status, voided.VoidReason, voided.CanVoid));
        Assert.Equal("ALREADY_VOIDED", (await ProblemAsync(await owner.PostAsync(url, new VoidPaymentRequest("Again")))).Code);

        // it counts for nothing on the bill, and its cash went back out of the drawer's ledger
        folio = await GetFolioAsync(owner, pid, folio.Id);
        Assert.Equal(500m, folio.Payments);
        Assert.True(folio.PaymentLines.Single(l => l.PaymentId == wrong.Id).Voided);
        var shift = (await owner.GetJsonAsync<CashDrawerDto>($"/api/v1/properties/{pid}/cash-drawer")).Current!;
        Assert.Equal((1500m, 2500m + 500m, 2500m), (shift.Expected, shift.CashIn, shift.CashOut));

        // a cash refund comes out of the drawer, at most what it holds
        var upi = await PayAsync(owner, pid, new CreatePaymentRequest("UPI", 2000m, folio.Id));
        Assert.Equal("The drawer should hold ₹1,500, less than ₹2,000. Refund by another method, or add cash to the drawer first.",
            await FieldErrorAsync(await owner.PostAsync($"/api/v1/properties/{pid}/payments/{upi.Id}/refund",
                new RefundPaymentRequest(2000m, "Wrong guest", "CASH"), Key("refund")), "Method"));
        await RefundAsync(owner, pid, upi.Id, new RefundPaymentRequest(1000m, "Wrong guest", "CASH"));
        Assert.Equal(500m, (await owner.GetJsonAsync<CashDrawerDto>($"/api/v1/properties/{pid}/cash-drawer")).Current!.Expected);

        // once the shift is closed its cash has been counted: a void is refused, a refund is the way
        await CloseAnyShiftAsync(owner, pid);
        var after = await GetPaymentAsync(owner, pid, kept.Id);
        Assert.False(after.CanVoid);
        Assert.StartsWith($"{kept.Number} was cash in the drawer shift closed at ", after.CantVoidReason);
        Assert.Equal("NOT_VOIDABLE", (await ProblemAsync(await owner.PostAsync($"/api/v1/properties/{pid}/payments/{kept.Id}/void",
            new VoidPaymentRequest("Too late")))).Code);
    }

    // ------------------------------------------------------------------ list, permissions and tenancy

    [Fact]
    public async Task The_payments_list_adds_up_by_method_and_housekeepers_and_other_organisations_cant_see_it()
    {
        var (owner, pid) = await OwnerAsync();
        var (folio, booking) = await InHouseFolioAsync(owner, pid);
        var upi = await PayAsync(owner, pid, new CreatePaymentRequest("UPI", 1200m, folio.Id));
        await PayAsync(owner, pid, new CreatePaymentRequest("CARD", 800m, folio.Id, PaymentReference: "AUTH 778899"));
        await RefundAsync(owner, pid, upi.Id, new RefundPaymentRequest(200m, "Rounded down"));

        var list = await owner.GetJsonAsync<PaymentListDto>($"/api/v1/properties/{pid}/payments?q={booking.Reference}");
        Assert.Equal(2, list.Total);
        Assert.Equal(new[] { ("UPI", 1200m, 200m, 1000m), ("CARD", 800m, 0m, 800m) },
            list.ByMethod.Select(m => (m.Method, m.Received, m.Refunded, m.Net)));
        Assert.Equal((2000m, 200m, 1800m), (list.Received, list.RefundedTotal, list.Net));
        Assert.All(list.Items, i => Assert.Equal(folio.Number, i.Folios));
        Assert.Single((await owner.GetJsonAsync<PaymentListDto>($"/api/v1/properties/{pid}/payments?q={booking.Reference}&method=CARD")).Items);
        Assert.Equal("'cheque' isn't a payment method. Use CASH, UPI, CARD, BANK_TRANSFER, ONLINE.",
            await FieldErrorAsync(await owner.GetAsync($"/api/v1/properties/{pid}/payments?method=cheque"), "Method"));

        var hk = await StaffAsync("HOUSEKEEPER", pid);
        Assert.Equal(HttpStatusCode.Forbidden, (await hk.GetAsync($"/api/v1/properties/{pid}/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await hk.GetAsync($"/api/v1/properties/{pid}/cash-drawer")).StatusCode);
        var desk = await StaffAsync("RECEPTIONIST", pid);
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PostAsync($"/api/v1/properties/{pid}/payments/{upi.Id}/refund",
            new RefundPaymentRequest(1m, "No"), Key("refund"))).StatusCode);

        var other = await app.CreateSecondTenantAsync();
        var stranger = await app.SignInAsync(other.OwnerEmail, other.Password);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/properties/{other.PropertyId}/payments/{upi.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/properties/{pid}/payments/{upi.Id}")).StatusCode);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(TestClient Owner, Guid PropertyId)> OwnerAsync() => (await app.SignInOwnerAsync(), await app.MainPropertyIdAsync());

    private async Task<TestClient> StaffAsync(string role, Guid pid)
    {
        var (email, pw) = await app.CreateUserAsync(role, pid);
        return await app.SignInAsync(email, pw);
    }

    private static Dictionary<string, string> Key(string what) => new() { ["Idempotency-Key"] = $"{what}-{Guid.NewGuid()}" };

    private static string Rupees(decimal v) => "₹" + v.ToString("#,##0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A guest checked in today for two nights at ₹3,500, and their main folio (₹7,000 of room nights).</summary>
    private static async Task<(FolioDto Folio, ReservationDto Booking)> InHouseFolioAsync(TestClient owner, Guid pid)
    {
        var stay = await SetupAsync(owner, pid, rooms: 1);
        var guest = await CreateGuestAsync(owner);
        var booked = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, await WalkInAsync(owner, pid), Today, Today.AddDays(2))));
        await FrontDeskTests.OkAsync(await owner.PostAsync(FrontDeskTests.CheckInUrl(pid, booked), new CheckInRequest(null)));
        return (await MainFolioAsync(owner, pid, booked.Id), booked);
    }

    private static async Task<FolioDto> MainFolioAsync(TestClient c, Guid pid, Guid reservationId)
    {
        var list = await c.GetJsonAsync<List<FolioListItemDto>>($"/api/v1/properties/{pid}/reservations/{reservationId}/folios");
        return await GetFolioAsync(c, pid, list.Single(f => f.IsPrimary).Id);
    }

    private static Task<FolioDto> GetFolioAsync(TestClient c, Guid pid, Guid id) => c.GetJsonAsync<FolioDto>($"/api/v1/properties/{pid}/folios/{id}");

    private static Task<PaymentDto> GetPaymentAsync(TestClient c, Guid pid, Guid id) => c.GetJsonAsync<PaymentDto>($"/api/v1/properties/{pid}/payments/{id}");

    private static async Task<PaymentDto> PayAsync(TestClient c, Guid pid, CreatePaymentRequest body)
    {
        var res = await c.PostAsync($"/api/v1/properties/{pid}/payments", body, Key("pay"));
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<PaymentDto>())!;
    }

    private static async Task<PaymentDto> RefundAsync(TestClient c, Guid pid, Guid paymentId, RefundPaymentRequest body) =>
        await OkAsync<PaymentDto>(await c.PostAsync($"/api/v1/properties/{pid}/payments/{paymentId}/refund", body, Key("refund")));

    /// <summary>Closes the drawer's open shift, if any, counting exactly what it should hold (so nothing waits for approval).</summary>
    private static async Task CloseAnyShiftAsync(TestClient c, Guid pid)
    {
        var drawer = await c.GetJsonAsync<CashDrawerDto>($"/api/v1/properties/{pid}/cash-drawer");
        if (drawer.Current is { } open)
            await OkAsync<CashDrawerSessionDto>(await c.PostAsync($"/api/v1/properties/{pid}/cash-drawer/close", new CloseCashDrawerRequest(open.Expected)));
    }

    /// <summary>A new shift of the property's drawer with <paramref name="openingFloat"/> (the open one is closed first).</summary>
    private static async Task<CashDrawerSessionDto> FreshShiftAsync(TestClient c, Guid pid, decimal openingFloat)
    {
        await CloseAnyShiftAsync(c, pid);
        var res = await c.PostAsync($"/api/v1/properties/{pid}/cash-drawer/open", new OpenCashDrawerRequest(openingFloat));
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<CashDrawerSessionDto>())!;
    }

    private static async Task<T> OkAsync<T>(HttpResponseMessage res)
    {
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<(string? Code, string? Message)> ProblemAsync(HttpResponseMessage res) => await FrontDeskTests.ProblemAsync(res);

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
