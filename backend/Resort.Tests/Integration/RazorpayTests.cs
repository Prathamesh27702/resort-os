using System.Net;
using System.Net.Http.Json;
using System.Text;
using Npgsql;
using Resort.Contracts;
using Resort.Infrastructure.Payments;
using Resort.Tests.Support;
using static Resort.Tests.Support.BookingKit;

namespace Resort.Tests.Integration;

/// <summary>Sprint 11 — Razorpay. Staff send a payment link; the guest pays at (the fake) Razorpay; only the signed webhook
/// records the payment, marks the link paid and confirms a pending booking. Duplicate deliveries change nothing, a bad
/// signature stores nothing, and events that arrive out of order can't move a payment backwards. Real API, real
/// PostgreSQL, the real Razorpay adapter talking HTTP to the fake Razorpay.</summary>
public class RazorpayTests(RazorpayFactory app) : IClassFixture<RazorpayFactory>
{
    // ------------------------------------------------------------------ the sprint review demo (acceptance)

    [Fact]
    public async Task The_demo_a_pending_booking_paid_by_link_is_confirmed_by_the_webhook_and_the_browser_only_shows_status()
    {
        var (owner, pid) = await OwnerAsync();
        var desk = await StaffAsync("RECEPTIONIST", pid);
        var booking = await PendingBookingAsync(owner, pid);   // 2 nights at ₹3,500
        Assert.Equal("PENDING", booking.Status);

        var links = await desk.GetJsonAsync<BookingPaymentLinksDto>(LinksUrl(pid, booking.Id));
        Assert.Equal((7000m, 0m, 7000m, 0m, true, true), (links.Due, links.Paid, links.Owed, links.Open, links.CanCreate, links.Fake));

        var link = await CreateLinkAsync(desk, pid, new CreatePaymentLinkRequest(booking.Id, 7000m, "Full stay"));
        Assert.Equal(("CREATED", 7000m, "INR", booking.Reference, "RECEPTIONIST user"), (link.Status, link.Amount, link.Currency, link.Reference, link.CreatedByName));
        Assert.StartsWith("order_", link.GatewayOrderId);
        Assert.Matches("^/pay/[A-Za-z0-9_-]{43}$", link.PayPath);
        Assert.Equal(700000L, app.Fake.Order(link.GatewayOrderId)!["amount"]!.GetValue<long>());   // paise at the boundary

        // the guest opens the link: no sign-in, minimal details, and what Checkout needs
        var guest = app.NewClient();
        var page = await guest.GetJsonAsync<PayPageDto>("/api/v1" + link.PayPath);
        Assert.Equal(("AWAITING_PAYMENT", 7000m, booking.Reference, "Asha", RazorpayOptions.FakeKeyId, link.GatewayOrderId, true),
            (page.Status, page.Amount, page.BookingReference, page.GuestName, page.KeyId, page.GatewayOrderId, page.Fake));

        // Checkout succeeds and the browser comes back with a valid signature: the page says "confirming", nothing is paid
        var (paymentId, hooks) = app.Fake.Pay(link.GatewayOrderId, "card");
        var signature = RazorpayPaymentGateway.Hmac(RazorpayOptions.FakeKeySecret, Encoding.UTF8.GetBytes($"{link.GatewayOrderId}|{paymentId}"));
        page = await OkAsync<PayPageDto>(await guest.PostAsync("/api/v1" + link.PayPath + "/callback", new PayCallbackRequest(paymentId, link.GatewayOrderId, signature)));
        Assert.Equal("CONFIRMING", page.Status);
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM payments WHERE reservation_id = '{booking.Id}'"));
        Assert.Equal("PENDING", (await GetBookingAsync(owner, pid, booking.Id)).Status);
        Assert.Equal("Razorpay's confirmation didn't check out, so this page can't tell whether you paid. If money left your account, the resort will see it within a few minutes; otherwise try again.",
            await FieldErrorAsync(await guest.PostAsync("/api/v1" + link.PayPath + "/callback", new PayCallbackRequest(paymentId, link.GatewayOrderId, "bad")),
                "RazorpaySignature"));

        // Razorpay's webhooks arrive: authorised, captured, order paid. By the time the first is checked, Razorpay already
        // has the payment captured, and its word decides: the first event records it captured, the others change nothing
        var acks = new List<WebhookAckDto>();
        foreach (var hook in hooks) acks.Add(await OkAsync<WebhookAckDto>(await app.DeliverAsync(hook)));
        Assert.Equal(new[] { "processed", "ignored", "ignored" }, acks.Select(a => a.Status));
        Assert.Matches($"^P[0-9]{{5}} \\({paymentId}, ₹7,000 by card\\) captured; {booking.Reference} confirmed; on account for {booking.Reference}\\.$", acks[0].Message);

        booking = await GetBookingAsync(owner, pid, booking.Id);
        Assert.Equal("CONFIRMED", booking.Status);
        Assert.Contains(booking.History, h => h.ToStatus == "CONFIRMED" && h.Reason!.StartsWith("Paid online: P", StringComparison.Ordinal));
        link = await owner.GetJsonAsync<PaymentLinkDto>($"{OrdersUrl(pid)}/{link.Id}");
        Assert.Equal(("PAID", "CAPTURED"), (link.Status, link.PaymentStatus));
        Assert.Equal(new[] { ("payment.authorized", "PROCESSED"), ("payment.captured", "IGNORED"), ("order.paid", "IGNORED") },
            link.Events!.Select(e => (e.EventType, e.Status)));
        var payment = await owner.GetJsonAsync<PaymentDto>($"/api/v1/properties/{pid}/payments/{link.PaymentId}");
        Assert.Equal(("ONLINE", "CAPTURED", 7000m, paymentId, "card", link.Id, 7000m), (payment.Method, payment.Status, payment.Amount, payment.PaymentReference,
            payment.GatewayMethod, payment.PaymentLinkId, payment.OnAccount));
        Assert.False(payment.CanVoid);
        Assert.Equal($"{payment.Number} was paid online through Razorpay, so the money really came in and it can't be voided. Refund it instead.", payment.CantVoidReason);

        // the guest's page now says paid; the stored events keep nothing of the card or the guest's contact
        page = await guest.GetJsonAsync<PayPageDto>("/api/v1" + link.PayPath);
        Assert.Equal(("PAID", payment.Number, null), (page.Status, page.PaymentNumber, page.KeyId));
        Assert.StartsWith("Paid. Thank you, Asha: ", page.Message);
        Assert.Equal(0L, await ScalarAsync(
            $"""
             SELECT count(*) FROM webhook_events WHERE gateway_order_id = '{link.Id}'
                AND (payload::text LIKE '%last4%' OR payload::text LIKE '%guest@example.com%' OR payload::text LIKE '%9800000000%' OR payload::text LIKE '%okbank%')
             """));
        Assert.Equal(3L, await ScalarAsync($"SELECT count(*) FROM webhook_events WHERE gateway_order_id = '{link.Id}' AND payload->'payment'->>'id' = '{paymentId}'"));

        // the booking is paid in full, so no further link
        links = await desk.GetJsonAsync<BookingPaymentLinksDto>(LinksUrl(pid, booking.Id));
        Assert.Equal((7000m, 0m, false), (links.Paid, links.Owed, links.CanCreate));
        Assert.Equal($"{booking.Reference} is paid in full ({Rupees(7000m)} of {Rupees(7000m)}), so there is nothing to ask for.",
            await FieldErrorAsync(await desk.PostAsync(OrdersUrl(pid), new CreatePaymentLinkRequest(booking.Id, 100m), Key("link")), "Amount"));
    }

    // ------------------------------------------------------------------ required tests

    [Fact]
    public async Task A_duplicate_delivery_is_processed_once_even_when_both_arrive_at_the_same_moment()
    {
        var (owner, pid) = await OwnerAsync();
        var booking = await PendingBookingAsync(owner, pid);
        var link = await CreateLinkAsync(owner, pid, new CreatePaymentLinkRequest(booking.Id, 2000m));
        var (_, hooks) = app.Fake.Pay(link.GatewayOrderId);
        var captured = hooks.Single(h => h.EventType == "payment.captured");

        // the same delivery twice at once
        using var barrier = new Barrier(2);
        Task<HttpResponseMessage> Deliver() => Task.Run(() => { barrier.SignalAndWait(); return app.DeliverAsync(captured); });
        var both = await Task.WhenAll(Deliver(), Deliver());
        var acks = new List<WebhookAckDto>();
        foreach (var r in both) acks.Add(await OkAsync<WebhookAckDto>(r));
        Assert.Equal(new[] { "duplicate", "processed" }, acks.Select(a => a.Status).Order());

        // and again later, and the other events
        var again = await OkAsync<WebhookAckDto>(await app.DeliverAsync(captured));
        Assert.Equal(("duplicate", $"Event {captured.EventId} (payment.captured) was already processed; nothing changed."), (again.Status, again.Message));
        foreach (var hook in hooks) await OkAsync<WebhookAckDto>(await app.DeliverAsync(hook));

        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM payments WHERE gateway_order_id = '{link.Id}'"));
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM webhook_events WHERE event_id = '{captured.EventId}'"));
        Assert.Equal(1, (int)(await ScalarAsync($"SELECT attempts FROM webhook_events WHERE event_id = '{captured.EventId}'"))!);
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM reservation_status_history WHERE reservation_id = '{booking.Id}' AND to_status = 'CONFIRMED'"));
        Assert.Equal(2000m, (await owner.GetJsonAsync<BookingPaymentLinksDto>(LinksUrl(pid, booking.Id))).Paid);
    }

    [Fact]
    public async Task A_bad_signature_is_refused_with_401_and_nothing_is_stored()
    {
        var (owner, pid) = await OwnerAsync();
        var booking = await PendingBookingAsync(owner, pid);
        var link = await CreateLinkAsync(owner, pid, new CreatePaymentLinkRequest(booking.Id, 1500m));
        var (_, hooks) = app.Fake.Pay(link.GatewayOrderId);
        var captured = hooks.Single(h => h.EventType == "payment.captured");
        var before = await ScalarAsync("SELECT count(*) FROM webhook_events");

        // a wrong signature, none at all, a body changed after signing (₹1,500 → ₹15,000), and one signed with another secret
        var tampered = Encoding.UTF8.GetBytes(captured.Text.Replace("\"amount\":150000", "\"amount\":1500000", StringComparison.Ordinal));
        Assert.NotEqual(captured.Body, tampered);
        foreach (var res in new[]
                 {
                     await app.DeliverAsync(captured, signature: new string('0', 64)),
                     await app.DeliverAsync(captured, signature: ""),
                     await app.DeliverAsync(captured, body: tampered),
                     await app.DeliverAsync(captured, signature: RazorpayPaymentGateway.Hmac("someone-elses-secret", captured.Body)),
                 })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
            var (code, message) = await ProblemAsync(res);
            Assert.Equal("BAD_SIGNATURE", code);
            Assert.StartsWith("The X-Razorpay-Signature header doesn't match this body for the webhook secret, so the event was refused and nothing was stored.", message);
        }

        Assert.Equal(before, await ScalarAsync("SELECT count(*) FROM webhook_events"));
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM payments WHERE reservation_id = '{booking.Id}'"));
        Assert.Equal(("CREATED", "PENDING"), ((await owner.GetJsonAsync<PaymentLinkDto>($"{OrdersUrl(pid)}/{link.Id}")).Status,
            (await GetBookingAsync(owner, pid, booking.Id)).Status));

        // the genuine delivery still works afterwards
        Assert.Equal("processed", (await OkAsync<WebhookAckDto>(await app.DeliverAsync(captured))).Status);
    }

    [Fact]
    public async Task Out_of_order_events_captured_before_authorized_and_failed_after_captured_never_move_the_payment_back()
    {
        var (owner, pid) = await OwnerAsync();
        var booking = await PendingBookingAsync(owner, pid);
        var link = await CreateLinkAsync(owner, pid, new CreatePaymentLinkRequest(booking.Id, 3000m));
        var (paymentId, hooks) = app.Fake.Pay(link.GatewayOrderId);
        var authorized = hooks.Single(h => h.EventType == "payment.authorized");
        var captured = hooks.Single(h => h.EventType == "payment.captured");

        // captured arrives first: recorded as captured at once
        var first = await OkAsync<WebhookAckDto>(await app.DeliverAsync(captured));
        Assert.Equal("processed", first.Status);
        var payment = await PaymentOfLinkAsync(owner, pid, link.Id);
        Assert.Equal("CAPTURED", payment.Status);

        // then the late "authorized": Razorpay has it captured, so it changes nothing
        var late = await OkAsync<WebhookAckDto>(await app.DeliverAsync(authorized));
        Assert.Equal(("ignored", $"{payment.Number} is already captured; Razorpay has it as captured, so this payment.authorized changes nothing."),
            (late.Status, late.Message));

        // and a "failed" for the same payment after it was captured changes nothing either
        var failed = app.Fake.PaymentWebhook("payment.failed", paymentId);
        Assert.Equal("ignored", (await OkAsync<WebhookAckDto>(await app.DeliverAsync(failed))).Status);

        payment = await PaymentOfLinkAsync(owner, pid, link.Id);
        Assert.Equal(("CAPTURED", 3000m), (payment.Status, payment.Amount));
        Assert.Equal("CONFIRMED", (await GetBookingAsync(owner, pid, booking.Id)).Status);
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM payments WHERE gateway_order_id = '{link.Id}'"));

        // authorised alone counts for nothing, and becomes captured when that arrives
        var second = await PendingBookingAsync(owner, pid);
        var link2 = await CreateLinkAsync(owner, pid, new CreatePaymentLinkRequest(second.Id, 1000m));
        var (payId2, hooks2) = app.Fake.Pay(link2.GatewayOrderId);
        app.Fake.Payment(payId2)!["status"] = "authorized";   // Razorpay hasn't captured it yet
        await OkAsync<WebhookAckDto>(await app.DeliverAsync(hooks2[0]));
        var authorisedOnly = await PaymentOfLinkAsync(owner, pid, link2.Id);
        Assert.Equal(("AUTHORIZED", 0m, 0m), (authorisedOnly.Status, authorisedOnly.OnAccount, authorisedOnly.Refundable));
        Assert.Equal(0m, (await owner.GetJsonAsync<BookingPaymentLinksDto>(LinksUrl(pid, second.Id))).Paid);
        Assert.Equal("CONFIRMING", (await app.NewClient().GetJsonAsync<PayPageDto>("/api/v1" + link2.PayPath)).Status);
        app.Fake.Payment(payId2)!["status"] = "captured";
        await OkAsync<WebhookAckDto>(await app.DeliverAsync(hooks2[1]));
        Assert.Equal("CAPTURED", (await PaymentOfLinkAsync(owner, pid, link2.Id)).Status);
        Assert.Equal("CONFIRMED", (await GetBookingAsync(owner, pid, second.Id)).Status);
    }

    // ------------------------------------------------------------------ failures and cancelled links

    [Fact]
    public async Task A_failed_try_leaves_the_link_open_with_the_reason_and_a_cancelled_link_still_records_money_that_arrives()
    {
        var (owner, pid) = await OwnerAsync();
        var booking = await PendingBookingAsync(owner, pid);
        var link = await CreateLinkAsync(owner, pid, new CreatePaymentLinkRequest(booking.Id, 2500m));

        var (_, failedHooks) = app.Fake.Pay(link.GatewayOrderId, "card", succeed: false, failure: "Card declined by the issuing bank.");
        var ack = await OkAsync<WebhookAckDto>(await app.DeliverAsync(failedHooks.Single()));
        Assert.EndsWith("failed: Card declined by the issuing bank. The link stays open to try again.", ack.Message);
        link = await owner.GetJsonAsync<PaymentLinkDto>($"{OrdersUrl(pid)}/{link.Id}");
        Assert.Equal(("CREATED", 1, "Card declined by the issuing bank.", (Guid?)null), (link.Status, link.Attempts, link.LastError, link.PaymentId));
        var page = await app.NewClient().GetJsonAsync<PayPageDto>("/api/v1" + link.PayPath);
        Assert.Equal(("AWAITING_PAYMENT", "Card declined by the issuing bank."), (page.Status, page.LastError));
        Assert.EndsWith("Your last try didn't go through: Card declined by the issuing bank.", page.Message);

        // staff cancel it (a reason is needed); the guest's page says so and test checkout refuses it
        var cancelUrl = $"{OrdersUrl(pid)}/{link.Id}/cancel";
        Assert.Equal("Say why the link is cancelled (at least 3 characters), e.g. Guest will pay at the desk.",
            await FieldErrorAsync(await owner.PostAsync(cancelUrl, new CancelPaymentLinkRequest(" ")), "Reason"));
        link = await OkAsync<PaymentLinkDto>(await owner.PostAsync(cancelUrl, new CancelPaymentLinkRequest("Guest will pay at the desk")));
        Assert.Equal(("CANCELLED", "Guest will pay at the desk"), (link.Status, link.CancelReason));
        var twice = await owner.PostAsync(cancelUrl, new CancelPaymentLinkRequest("Again"));
        Assert.Equal((HttpStatusCode.Conflict, "LINK_ALREADY_CANCELLED"), (twice.StatusCode, (await ProblemAsync(twice)).Code));
        var guest = app.NewClient();
        page = await guest.GetJsonAsync<PayPageDto>("/api/v1" + link.PayPath);
        Assert.Equal(("CANCELLED", null, null), (page.Status, page.KeyId, page.GatewayOrderId));
        var refused = await guest.PostAsync("/api/v1" + link.PayPath + "/fake-checkout", new FakeCheckoutRequest());
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "NOT_PAYABLE"), (refused.StatusCode, (await ProblemAsync(refused)).Code));

        // the guest had Checkout open and paid anyway: the money is real, so it is recorded and the link becomes paid
        var (_, hooks) = app.Fake.Pay(link.GatewayOrderId);
        ack = await OkAsync<WebhookAckDto>(await app.DeliverAsync(hooks.Single(h => h.EventType == "payment.captured")));
        Assert.Contains("the link had been cancelled, but the money came, so it is recorded", ack.Message);
        link = await owner.GetJsonAsync<PaymentLinkDto>($"{OrdersUrl(pid)}/{link.Id}");
        Assert.Equal(("PAID", "CAPTURED"), (link.Status, link.PaymentStatus));
        var paid = await owner.PostAsync(cancelUrl, new CancelPaymentLinkRequest("Too late"));
        Assert.Equal((HttpStatusCode.Conflict, "LINK_ALREADY_PAID"), (paid.StatusCode, (await ProblemAsync(paid)).Code));
    }

    [Fact]
    public async Task The_fake_checkout_pays_a_link_end_to_end_through_the_signed_webhook()
    {
        var (owner, pid) = await OwnerAsync();
        var booking = await PendingBookingAsync(owner, pid);
        var link = await CreateLinkAsync(owner, pid, new CreatePaymentLinkRequest(booking.Id, 7000m));
        var guest = app.NewClient();

        var failed = await OkAsync<PayPageDto>(await guest.PostAsync("/api/v1" + link.PayPath + "/fake-checkout", new FakeCheckoutRequest(false, "card")));
        Assert.Equal(("AWAITING_PAYMENT", "Payment was declined by the bank (test). Try another card or UPI app."), (failed.Status, failed.LastError));
        var paid = await OkAsync<PayPageDto>(await guest.PostAsync("/api/v1" + link.PayPath + "/fake-checkout", new FakeCheckoutRequest(true, "upi")));
        Assert.Equal("PAID", paid.Status);
        Assert.Equal("CONFIRMED", (await GetBookingAsync(owner, pid, booking.Id)).Status);
        Assert.Equal("upi", (await PaymentOfLinkAsync(owner, pid, link.Id)).GatewayMethod);

        var unknown = await guest.GetAsync("/api/v1/pay/" + new string('x', 43));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("This payment link isn't valid. Check you opened the whole link, or ask the resort for a new one.", (await ProblemAsync(unknown)).Message);
    }

    // ------------------------------------------------------------------ creating links

    [Fact]
    public async Task A_link_needs_an_idempotency_key_a_retry_makes_one_order_and_every_mistake_is_named_under_its_field()
    {
        var (owner, pid) = await OwnerAsync();
        var booking = await PendingBookingAsync(owner, pid);
        var body = new CreatePaymentLinkRequest(booking.Id, 2000m, "Advance");

        Assert.StartsWith("Send an Idempotency-Key header", await FieldErrorAsync(await owner.PostAsync(OrdersUrl(pid), body), "Idempotency-Key"));
        var key = Key("link");
        var calls = app.Fake.Calls;
        var first = await owner.PostAsync(OrdersUrl(pid), body, key);
        var second = await owner.PostAsync(OrdersUrl(pid), body, key);
        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Created), (first.StatusCode, second.StatusCode));
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal((await first.Content.ReadFromJsonAsync<PaymentLinkDto>())!.Id, (await second.Content.ReadFromJsonAsync<PaymentLinkDto>())!.Id);
        Assert.Equal(calls + 1, app.Fake.Calls);   // one order at Razorpay

        Assert.Equal("The amount must be more than ₹0.", await FieldErrorAsync(await owner.PostAsync(OrdersUrl(pid), body with { Amount = 0 }, Key("link")), "Amount"));
        Assert.Equal("The amount must be at least ₹1, Razorpay's smallest payment.",
            await FieldErrorAsync(await owner.PostAsync(OrdersUrl(pid), body with { Amount = 0.5m }, Key("link")), "Amount"));
        Assert.Equal("The amount can have at most 2 decimal places (paise).",
            await FieldErrorAsync(await owner.PostAsync(OrdersUrl(pid), body with { Amount = 10.005m }, Key("link")), "Amount"));
        Assert.Equal("Choose the booking the guest is paying for.",
            await FieldErrorAsync(await owner.PostAsync(OrdersUrl(pid), body with { ReservationId = Guid.Empty }, Key("link")), "ReservationId"));
        Assert.Equal("That booking isn't at this property. Search for it again.",
            await FieldErrorAsync(await owner.PostAsync(OrdersUrl(pid), body with { ReservationId = Guid.NewGuid() }, Key("link")), "ReservationId"));

        // ₹7,000 owed, ₹2,000 already asked for: a new link can ask for ₹5,000 at most
        Assert.Equal($"{booking.Reference} still owes {Rupees(7000m)} and open links already ask for {Rupees(2000m)}, so a new link can be for at most {Rupees(5000m)}. Cancel an open link to ask for more.",
            await FieldErrorAsync(await owner.PostAsync(OrdersUrl(pid), body with { Amount = 5000.01m }, Key("link")), "Amount"));
        await CreateLinkAsync(owner, pid, body with { Amount = 5000m });
        var full = await owner.GetJsonAsync<BookingPaymentLinksDto>(LinksUrl(pid, booking.Id));
        Assert.Equal((7000m, false, $"Open links already ask for all {Rupees(7000m)} {booking.Reference} owes. Cancel one to send a new link."),
            (full.Open, full.CanCreate, full.CantCreateReason));
        Assert.Equal(2, full.Links.Count);

        // a cancelled booking takes no link
        var cancelled = await PendingBookingAsync(owner, pid);
        await PostAsync(owner, $"/api/v1/properties/{pid}/reservations/{cancelled.Id}/cancel", new ReservationStatusRequest("Guest changed plans"));
        var closed = await owner.PostAsync(OrdersUrl(pid), new CreatePaymentLinkRequest(cancelled.Id, 100m), Key("link"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, closed.StatusCode);
        Assert.Equal(("BOOKING_CLOSED", $"{cancelled.Reference} is cancelled, so no payment link can be sent for it."), await ProblemAsync(closed));

        // listing needs the booking
        Assert.Equal("Choose the booking whose payment links to show (reservationId).",
            await FieldErrorAsync(await owner.GetAsync(OrdersUrl(pid)), "ReservationId"));
    }

    [Fact]
    public async Task Links_need_payment_permissions_and_stay_inside_their_organisation()
    {
        var (owner, pid) = await OwnerAsync();
        var booking = await PendingBookingAsync(owner, pid);
        var link = await CreateLinkAsync(owner, pid, new CreatePaymentLinkRequest(booking.Id, 1000m));

        var housekeeper = await StaffAsync("HOUSEKEEPER", pid);
        Assert.Equal(HttpStatusCode.Forbidden, (await housekeeper.PostAsync(OrdersUrl(pid), new CreatePaymentLinkRequest(booking.Id, 10m), Key("link"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await housekeeper.GetAsync(LinksUrl(pid, booking.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await housekeeper.PostAsync($"{OrdersUrl(pid)}/{link.Id}/cancel", new CancelPaymentLinkRequest("No"))).StatusCode);

        var (_, otherPid, otherEmail, otherPw) = await app.CreateSecondTenantAsync();
        var other = await app.SignInAsync(otherEmail, otherPw);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{OrdersUrl(otherPid)}/{link.Id}")).StatusCode);
        Assert.Equal("That booking isn't at this property. Search for it again.",
            await FieldErrorAsync(await other.GetAsync(LinksUrl(otherPid, booking.Id)), "ReservationId"));

        // a webhook for an order that isn't ours is stored as ignored
        var stranger = new FakeWebhook("evt_stranger" + Suffix(), "payment.captured",
            Encoding.UTF8.GetBytes("""{"event":"payment.captured","payload":{"payment":{"entity":{"id":"pay_X1","order_id":"order_notours","amount":100,"currency":"INR","status":"captured"}}}}"""), "");
        stranger = stranger with { Signature = app.Fake.Sign(stranger.Body) };
        var ack = await OkAsync<WebhookAckDto>(await app.DeliverAsync(stranger));
        Assert.Equal(("ignored", "Order order_notours isn't a Resort OS payment link, so nothing changed here."), (ack.Status, ack.Message));
        Assert.Equal("IGNORED", await ScalarAsync($"SELECT status FROM webhook_events WHERE event_id = '{stranger.EventId}'"));
    }

    // ------------------------------------------------------------------ the gateway: retries, timeouts, refusals

    [Fact]
    public async Task Razorpay_calls_are_retried_with_backoff_on_503_and_timeouts_and_a_clear_refusal_is_reported_once()
    {
        var (owner, pid) = await OwnerAsync();
        var booking = await PendingBookingAsync(owner, pid);

        // two 503s, then it answers: one link
        var calls = app.Fake.Calls;
        app.Fake.FailNext(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable);
        await CreateLinkAsync(owner, pid, new CreatePaymentLinkRequest(booking.Id, 100m));
        Assert.Equal(calls + 3, app.Fake.Calls);

        // a try that hangs gives up after its timeout and the next one answers
        app.Fake.HangNext(1);
        await CreateLinkAsync(owner, pid, new CreatePaymentLinkRequest(booking.Id, 100m));

        // three 503s: no link, and the message says what happened
        app.Fake.FailNext(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable);
        var down = await owner.PostAsync(OrdersUrl(pid), new CreatePaymentLinkRequest(booking.Id, 100m), Key("link"));
        var (code, message) = await ProblemAsync(down);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "GATEWAY_UNAVAILABLE"), (down.StatusCode, code));
        Assert.Equal("Razorpay had a problem (503) and couldn't create the payment order: The fake Razorpay was told to fail this call (tried 3 times). No link was created; try again in a minute.",
            message);

        // a 400 is not retried
        calls = app.Fake.Calls;
        app.Fake.FailNext(HttpStatusCode.BadRequest);
        var bad = await owner.PostAsync(OrdersUrl(pid), new CreatePaymentLinkRequest(booking.Id, 100m), Key("link"));
        Assert.Equal("Razorpay couldn't create the payment order: The fake Razorpay was told to fail this call. No link was created; try again in a minute.",
            (await ProblemAsync(bad)).Message);
        Assert.Equal(calls + 1, app.Fake.Calls);
        Assert.Equal(2, (await owner.GetJsonAsync<BookingPaymentLinksDto>(LinksUrl(pid, booking.Id))).Links.Count);
    }

    // ------------------------------------------------------------------ refunds back through Razorpay

    [Fact]
    public async Task An_online_payment_is_refunded_through_Razorpay_and_a_failed_refund_gives_the_money_back_to_the_payment()
    {
        var (owner, pid) = await OwnerAsync();
        var payment = await PaidOnlineAsync(owner, pid, 4000m);

        // processed at once (as test mode usually is)
        var refunded = await RefundAsync(owner, pid, payment.Id, new RefundPaymentRequest(1000m, "Airport transfer not used"));
        var first = Assert.Single(refunded.Refunds);
        Assert.Equal(("ONLINE", "PROCESSED", "PARTIALLY_REFUNDED", 3000m), (first.Method, first.Status, refunded.Status, refunded.Refundable));
        Assert.StartsWith("rfnd_", (string)(await ScalarAsync($"SELECT gateway_refund_id FROM refunds WHERE id = '{first.Id}'"))!);

        // pending, then Razorpay says it failed: the money counts on the payment again
        app.Fake.RefundStatus = "pending";
        try
        {
            refunded = await RefundAsync(owner, pid, payment.Id, new RefundPaymentRequest(3000m, "Stay cancelled"));
            var pending = refunded.Refunds.Single(r => r.Id != first.Id);
            Assert.Equal(("PENDING", "REFUNDED", 0m), (pending.Status, refunded.Status, refunded.Refundable));
            var gatewayId = (string)(await ScalarAsync($"SELECT gateway_refund_id FROM refunds WHERE id = '{pending.Id}'"))!;
            var failed = app.Fake.RefundWebhook(gatewayId, processed: false);
            var ack = await OkAsync<WebhookAckDto>(await app.DeliverAsync(failed));
            Assert.Equal($"The refund of {Rupees(3000m)} from {payment.Number} ({gatewayId}) failed at Razorpay, so the money counts on the payment again (partly refunded). Refund it again, or by another method.",
                ack.Message);
            var after = await owner.GetJsonAsync<PaymentDto>($"/api/v1/properties/{pid}/payments/{payment.Id}");
            Assert.Equal(("PARTIALLY_REFUNDED", 3000m, 1000m), (after.Status, after.Refundable, after.Refunded));
            Assert.Equal("ignored", (await OkAsync<WebhookAckDto>(await app.DeliverAsync(app.Fake.RefundWebhook(gatewayId, processed: false) with { EventId = "evt_" + Suffix() }))).Status);

            // pending, then processed
            refunded = await RefundAsync(owner, pid, payment.Id, new RefundPaymentRequest(500m, "Dinner credit not used"));
            var third = refunded.Refunds.Single(r => r.Status == "PENDING");
            var id3 = (string)(await ScalarAsync($"SELECT gateway_refund_id FROM refunds WHERE id = '{third.Id}'"))!;
            Assert.Equal("processed", (await OkAsync<WebhookAckDto>(await app.DeliverAsync(app.Fake.RefundWebhook(id3)))).Status);
            Assert.Equal("PROCESSED", (await owner.GetJsonAsync<PaymentDto>($"/api/v1/properties/{pid}/payments/{payment.Id}")).Refunds.Single(r => r.Id == third.Id).Status);
        }
        finally { app.Fake.RefundStatus = "processed"; }

        // a desk payment can't go back online, an online one can't be taken at the desk or voided
        var desk = await StaffAsync("RECEPTIONIST", pid);
        Assert.Equal("Online payments are recorded by Razorpay when the guest pays. Send the guest a payment link from the booking instead.",
            await FieldErrorAsync(await desk.PostAsync($"/api/v1/properties/{pid}/payments",
                new CreatePaymentRequest("ONLINE", 100m, ReservationId: payment.ReservationId), Key("pay")), "Method"));
        var upi = await OkCreatedAsync<PaymentDto>(await owner.PostAsync($"/api/v1/properties/{pid}/payments",
            new CreatePaymentRequest("UPI", 100m, ReservationId: payment.ReservationId), Key("pay")));
        Assert.Equal($"{upi.Number} was paid upi, not online, so it can't go back through Razorpay. Choose Cash, UPI, Card, Bank transfer.",
            await FieldErrorAsync(await owner.PostAsync($"/api/v1/properties/{pid}/payments/{upi.Id}/refund",
                new RefundPaymentRequest(50m, "Wrong", "ONLINE"), Key("refund")), "Method"));
        var voided = await owner.PostAsync($"/api/v1/properties/{pid}/payments/{payment.Id}/void", new VoidPaymentRequest("Mistake"));
        Assert.Equal("NOT_VOIDABLE", (await ProblemAsync(voided)).Code);

        // Razorpay refusing a refund outright: nothing recorded
        var other = await PaidOnlineAsync(owner, pid, 1000m);
        app.Fake.FailNext(HttpStatusCode.BadRequest);
        var refusedRes = await owner.PostAsync($"/api/v1/properties/{pid}/payments/{other.Id}/refund", new RefundPaymentRequest(100m, "Guest asked"), Key("refund"));
        Assert.Equal(("GATEWAY_REFUND_FAILED", "Razorpay couldn't send the refund: The fake Razorpay was told to fail this call. Nothing was refunded."),
            await ProblemAsync(refusedRes));
        // and an unclear failure (no answer) isn't retried, and says to check the dashboard
        app.Fake.FailNext(HttpStatusCode.ServiceUnavailable);
        var calls = app.Fake.Calls;
        var unclear = await owner.PostAsync($"/api/v1/properties/{pid}/payments/{other.Id}/refund", new RefundPaymentRequest(100m, "Guest asked"), Key("refund"));
        Assert.Equal("GATEWAY_REFUND_UNCLEAR", (await ProblemAsync(unclear)).Code);
        Assert.Equal(calls + 1, app.Fake.Calls);
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM refunds WHERE payment_id = '{other.Id}'"));
    }

    // ------------------------------------------------------------------ the database

    [Fact]
    public async Task The_database_keeps_links_and_events_and_allows_one_payment_row_per_Razorpay_payment()
    {
        var (owner, pid) = await OwnerAsync();
        var payment = await PaidOnlineAsync(owner, pid, 1200m);
        var linkId = payment.PaymentLinkId!.Value;

        foreach (var sql in new[]
                 {
                     $"DELETE FROM payment_gateway_orders WHERE id = '{linkId}'",
                     $"UPDATE payment_gateway_orders SET amount = 1 WHERE id = '{linkId}'",
                     $"DELETE FROM webhook_events WHERE gateway_order_id = '{linkId}'",
                     $"UPDATE webhook_events SET outcome = 'edited' WHERE gateway_order_id = '{linkId}'",
                     $"UPDATE payments SET gateway_method = 'card' WHERE id = '{payment.Id}'",
                 })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
        var back = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"UPDATE payment_gateway_orders SET status = 'CREATED', paid_at = NULL, payment_id = NULL WHERE id = '{linkId}'"));
        Assert.Equal("ck_payment_gateway_orders_transition", back.ConstraintName);
        var twice = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"""
             INSERT INTO payments (organization_id, property_id, number, reservation_id, method, status, amount, currency, reference, gateway_order_id, received_at)
             SELECT organization_id, property_id, 'P99997', reservation_id, 'ONLINE', 'CAPTURED', amount, currency, reference, gateway_order_id, now()
               FROM payments WHERE id = '{payment.Id}'
             """));
        Assert.Equal("ux_payments_gateway_payment", twice.ConstraintName);
        // a desk payment can't be created "authorised" or move back from refunded: only the gateway does that
        var desk = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"""
             INSERT INTO payments (organization_id, property_id, number, reservation_id, method, status, amount, currency, received_at)
             SELECT organization_id, property_id, 'P99996', reservation_id, 'UPI', 'AUTHORIZED', 10, currency, now() FROM payments WHERE id = '{payment.Id}'
             """));
        Assert.Equal("ck_payments_status_transition", desk.ConstraintName);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(TestClient Owner, Guid PropertyId)> OwnerAsync() => (await app.SignInOwnerAsync(), await app.MainPropertyIdAsync());

    private async Task<TestClient> StaffAsync(string role, Guid pid)
    {
        var (email, pw) = await app.CreateUserAsync(role, pid);
        return await app.SignInAsync(email, pw);
    }

    private static string OrdersUrl(Guid pid) => $"/api/v1/properties/{pid}/payments/razorpay/orders";
    private static string LinksUrl(Guid pid, Guid reservationId) => $"{OrdersUrl(pid)}?reservationId={reservationId}";
    private static Dictionary<string, string> Key(string what) => new() { ["Idempotency-Key"] = $"{what}-{Guid.NewGuid()}" };
    private static string Rupees(decimal v) => "₹" + v.ToString("#,##0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A pending booking for Asha (2 nights from a week ahead, ₹3,500 a night) on its own room type.</summary>
    private static async Task<ReservationDto> PendingBookingAsync(TestClient owner, Guid pid)
    {
        var stay = await SetupAsync(owner, pid, rooms: 1);
        var guest = await CreateGuestAsync(owner, "Asha", "Rao");
        var from = Today.AddDays(7);
        return await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, await WalkInAsync(owner, pid), from, from.AddDays(2)) with { Status = "PENDING" }));
    }

    private static Task<ReservationDto> GetBookingAsync(TestClient c, Guid pid, Guid id) => c.GetJsonAsync<ReservationDto>($"/api/v1/properties/{pid}/reservations/{id}");

    private static async Task<PaymentLinkDto> CreateLinkAsync(TestClient c, Guid pid, CreatePaymentLinkRequest body) =>
        await OkCreatedAsync<PaymentLinkDto>(await c.PostAsync(OrdersUrl(pid), body, Key("link")));

    private static async Task<PaymentDto> PaymentOfLinkAsync(TestClient c, Guid pid, Guid linkId)
    {
        var link = await c.GetJsonAsync<PaymentLinkDto>($"{OrdersUrl(pid)}/{linkId}");
        return await c.GetJsonAsync<PaymentDto>($"/api/v1/properties/{pid}/payments/{link.PaymentId}");
    }

    /// <summary>A pending booking paid online in full by a link of <paramref name="amount"/>, and its payment.</summary>
    private async Task<PaymentDto> PaidOnlineAsync(TestClient owner, Guid pid, decimal amount)
    {
        var booking = await PendingBookingAsync(owner, pid);
        var link = await CreateLinkAsync(owner, pid, new CreatePaymentLinkRequest(booking.Id, amount));
        var (_, hooks) = app.Fake.Pay(link.GatewayOrderId);
        foreach (var hook in hooks) await OkAsync<WebhookAckDto>(await app.DeliverAsync(hook));
        return await PaymentOfLinkAsync(owner, pid, link.Id);
    }

    private static async Task<PaymentDto> RefundAsync(TestClient c, Guid pid, Guid paymentId, RefundPaymentRequest body) =>
        await OkAsync<PaymentDto>(await c.PostAsync($"/api/v1/properties/{pid}/payments/{paymentId}/refund", body, Key("refund")));

    private static async Task<T> OkAsync<T>(HttpResponseMessage res)
    {
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<T> OkCreatedAsync<T>(HttpResponseMessage res)
    {
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
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
