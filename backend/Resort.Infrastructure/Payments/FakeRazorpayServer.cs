using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Resort.Infrastructure.Payments;

/// <summary>A webhook the fake Razorpay would deliver: Razorpay's event id header, the event name, the exact body and its
/// X-Razorpay-Signature.</summary>
public sealed record FakeWebhook(string EventId, string EventType, byte[] Body, string Signature)
{
    public string Text => Encoding.UTF8.GetString(Body);
}

/// <summary>
/// The fake Razorpay (Sprint 11): an in-memory stand-in for Razorpay's Orders, Payments and Refunds APIs, answering the
/// same HTTP calls with the same JSON, so <see cref="RazorpayPaymentGateway"/> runs unchanged against it. It also makes
/// the signed webhooks Razorpay would send when a guest pays (<see cref="Pay"/>) or a refund goes through.
///
/// Used when no Razorpay keys are set outside production: by the integration tests (which deliver its webhooks
/// duplicated, out of order or with a bad signature), by the browser tests and in local development, where the pay page
/// shows "Pay (test)" buttons instead of Razorpay Checkout. Tests can make the next calls fail or hang
/// (<see cref="FailNext"/>, <see cref="HangNext"/>) to check retries and timeouts. Everything is lost when the API stops.
/// </summary>
public sealed class FakeRazorpayServer(IOptions<RazorpayOptions> options)
{
    private readonly RazorpayOptions o = options.Value;
    private readonly ConcurrentDictionary<string, JsonObject> orders = new();
    private readonly ConcurrentDictionary<string, JsonObject> payments = new();
    private readonly ConcurrentDictionary<string, JsonObject> refunds = new();
    private readonly ConcurrentQueue<HttpStatusCode> failures = new();
    private int hangs;
    private int calls;

    /// <summary>HTTP calls answered so far (including failed ones).</summary>
    public int Calls => calls;

    /// <summary>The next calls answer with these statuses (e.g. 503, 503) before the fake answers normally again.</summary>
    public void FailNext(params HttpStatusCode[] statuses) { foreach (var s in statuses) failures.Enqueue(s); }

    /// <summary>The next <paramref name="count"/> calls never answer (until the caller's timeout cancels them).</summary>
    public void HangNext(int count) => Interlocked.Add(ref hangs, count);

    /// <summary>What refunds answer: "processed" (default, as test mode usually does), "pending" or "failed".</summary>
    public string RefundStatus { get; set; } = "processed";

    public JsonObject? Order(string id) => orders.GetValueOrDefault(id);
    public JsonObject? Payment(string id) => payments.GetValueOrDefault(id);

    // ------------------------------------------------------------------ what a guest does at Checkout

    /// <summary>
    /// The guest pays order <paramref name="orderId"/>. Succeeds: the payment is authorised and captured, and the webhooks
    /// are payment.authorized, payment.captured and order.paid, in that order (each body as it was at that moment).
    /// Fails: the payment is failed with <paramref name="failure"/> and the webhook is payment.failed.
    /// </summary>
    public (string PaymentId, IReadOnlyList<FakeWebhook> Webhooks) Pay(string orderId, string method = "upi", bool succeed = true,
        string failure = "Payment was declined by the bank.")
    {
        var order = orders.GetValueOrDefault(orderId) ?? throw new InvalidOperationException($"The fake Razorpay has no order {orderId}.");
        var id = NewId("pay_");
        var payment = new JsonObject
        {
            ["id"] = id,
            ["entity"] = "payment",
            ["amount"] = order["amount"]!.GetValue<long>(),
            ["currency"] = order["currency"]!.GetValue<string>(),
            ["status"] = "created",
            ["order_id"] = orderId,
            ["method"] = method,
            ["captured"] = false,
            ["amount_refunded"] = 0L,
            ["error_code"] = null,
            ["error_description"] = null,
            ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            // what a real payment carries and must never be stored by us
            ["email"] = "guest@example.com",
            ["contact"] = "+919800000000",
            ["card"] = method == "card" ? new JsonObject { ["last4"] = "1111", ["network"] = "Visa", ["name"] = "Test Guest" } : null,
            ["vpa"] = method == "upi" ? "guest@okbank" : null,
        };
        payments[id] = payment;
        var hooks = new List<FakeWebhook>();
        if (succeed)
        {
            payment["status"] = "authorized";
            hooks.Add(Webhook("payment.authorized", payment, order));
            payment["status"] = "captured";
            payment["captured"] = true;
            order["status"] = "paid";
            order["amount_paid"] = order["amount"]!.GetValue<long>();
            hooks.Add(Webhook("payment.captured", payment, order));
            hooks.Add(Webhook("order.paid", payment, order));
        }
        else
        {
            payment["status"] = "failed";
            payment["error_code"] = "BAD_REQUEST_ERROR";
            payment["error_description"] = failure;
            order["status"] = "attempted";
            hooks.Add(Webhook("payment.failed", payment, order));
        }
        return (id, hooks);
    }

    /// <summary>A webhook for a payment as it is now (e.g. a second "captured" for a test).</summary>
    public FakeWebhook PaymentWebhook(string eventType, string paymentId)
    {
        var p = payments[paymentId];
        return Webhook(eventType, p, orders[p["order_id"]!.GetValue<string>()]);
    }

    /// <summary>refund.processed or refund.failed for a refund made through the fake; marks it so.</summary>
    public FakeWebhook RefundWebhook(string refundId, bool processed = true)
    {
        var r = refunds[refundId];
        r["status"] = processed ? "processed" : "failed";
        var p = payments[r["payment_id"]!.GetValue<string>()];
        if (!processed) p["amount_refunded"] = p["amount_refunded"]!.GetValue<long>() - r["amount"]!.GetValue<long>();
        var body = new JsonObject
        {
            ["entity"] = "event",
            ["account_id"] = "acc_fake",
            ["event"] = processed ? "refund.processed" : "refund.failed",
            ["contains"] = new JsonArray("refund", "payment"),
            ["payload"] = new JsonObject { ["refund"] = new JsonObject { ["entity"] = r.DeepClone() }, ["payment"] = new JsonObject { ["entity"] = p.DeepClone() } },
            ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        return Signed(processed ? "refund.processed" : "refund.failed", body);
    }

    /// <summary>The signature Razorpay would put on <paramref name="body"/>.</summary>
    public string Sign(byte[] body) => RazorpayPaymentGateway.Hmac(o.WebhookSecret ?? RazorpayOptions.FakeWebhookSecret, body);

    private FakeWebhook Webhook(string eventType, JsonObject payment, JsonObject order)
    {
        var payload = new JsonObject { ["payment"] = new JsonObject { ["entity"] = payment.DeepClone() } };
        if (eventType == "order.paid") payload["order"] = new JsonObject { ["entity"] = order.DeepClone() };
        var body = new JsonObject
        {
            ["entity"] = "event",
            ["account_id"] = "acc_fake",
            ["event"] = eventType,
            ["contains"] = eventType == "order.paid" ? new JsonArray("payment", "order") : new JsonArray("payment"),
            ["payload"] = payload,
            ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        return Signed(eventType, body);
    }

    private FakeWebhook Signed(string eventType, JsonObject body)
    {
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        return new FakeWebhook(NewId("evt_"), eventType, bytes, Sign(bytes));
    }

    // ------------------------------------------------------------------ the HTTP API

    internal async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage req, CancellationToken ct)
    {
        Interlocked.Increment(ref calls);
        if (Interlocked.Decrement(ref hangs) >= 0)
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        else Interlocked.Exchange(ref hangs, 0);
        if (failures.TryDequeue(out var fail))
            return Error(fail, "SERVER_ERROR", "The fake Razorpay was told to fail this call.");

        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{o.KeyId}:{o.KeySecret}"));
        if (req.Headers.Authorization is not { Scheme: "Basic" } auth || auth.Parameter != expected)
            return Error(HttpStatusCode.Unauthorized, "BAD_REQUEST_ERROR", "Authentication failed");

        var path = req.RequestUri!.AbsolutePath.TrimEnd('/');
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);   // v1, orders | v1, payments, {id}[, refund]
        var body = req.Content is null ? null : await req.Content.ReadFromJsonAsync<JsonObject>(ct);

        if (req.Method == HttpMethod.Post && parts is [_, "orders"])
        {
            var amount = body?["amount"]?.GetValue<long>() ?? 0;
            if (amount < 100) return Error(HttpStatusCode.BadRequest, "BAD_REQUEST_ERROR", "Order amount less than minimum amount allowed");
            var order = new JsonObject
            {
                ["id"] = NewId("order_"),
                ["entity"] = "order",
                ["amount"] = amount,
                ["amount_paid"] = 0L,
                ["amount_due"] = amount,
                ["currency"] = body!["currency"]?.GetValue<string>() ?? "INR",
                ["receipt"] = body["receipt"]?.GetValue<string>(),
                ["status"] = "created",
                ["attempts"] = 0,
                ["notes"] = body["notes"]?.DeepClone(),
                ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            orders[order["id"]!.GetValue<string>()] = order;
            return Json(order);
        }
        if (req.Method == HttpMethod.Get && parts is [_, "payments", var pid])
            return payments.TryGetValue(pid, out var p) ? Json(p) : Error(HttpStatusCode.BadRequest, "BAD_REQUEST_ERROR", "The id provided does not exist");
        if (req.Method == HttpMethod.Post && parts is [_, "payments", var rpid, "refund"])
        {
            if (!payments.TryGetValue(rpid, out var p)) return Error(HttpStatusCode.BadRequest, "BAD_REQUEST_ERROR", "The id provided does not exist");
            var amount = body?["amount"]?.GetValue<long>() ?? p["amount"]!.GetValue<long>();
            var refunded = p["amount_refunded"]!.GetValue<long>();
            if (p["status"]!.GetValue<string>() is not ("captured" or "refunded"))
                return Error(HttpStatusCode.BadRequest, "BAD_REQUEST_ERROR", "The payment has not been captured");
            if (refunded + amount > p["amount"]!.GetValue<long>())
                return Error(HttpStatusCode.BadRequest, "BAD_REQUEST_ERROR", "The refund amount provided is greater than amount captured");
            p["amount_refunded"] = refunded + amount;
            var refund = new JsonObject
            {
                ["id"] = NewId("rfnd_"),
                ["entity"] = "refund",
                ["amount"] = amount,
                ["currency"] = p["currency"]!.GetValue<string>(),
                ["payment_id"] = rpid,
                ["receipt"] = body?["receipt"]?.GetValue<string>(),
                ["status"] = RefundStatus,
                ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            refunds[refund["id"]!.GetValue<string>()] = refund;
            return Json(refund);
        }
        return Error(HttpStatusCode.NotFound, "BAD_REQUEST_ERROR", $"The fake Razorpay doesn't know {req.Method} {path}");
    }

    private static HttpResponseMessage Json(JsonNode body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode status, string code, string description) =>
        new(status)
        {
            Content = new StringContent(new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["description"] = description } }.ToJsonString(),
            Encoding.UTF8, "application/json")
        };

    private static string NewId(string prefix)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return prefix + string.Create(14, 0, (span, _) => { for (var i = 0; i < span.Length; i++) span[i] = chars[Random.Shared.Next(chars.Length)]; });
    }
}

/// <summary>Sends <see cref="RazorpayPaymentGateway"/>'s HTTP calls to the fake instead of api.razorpay.com.</summary>
public sealed class FakeRazorpayHandler(FakeRazorpayServer server) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        server.HandleAsync(request, cancellationToken);
}
