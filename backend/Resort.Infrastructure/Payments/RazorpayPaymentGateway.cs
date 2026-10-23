using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Resort.Application.Payments;

namespace Resort.Infrastructure.Payments;

/// <summary>
/// Razorpay keys and how the adapter talks to it (section Razorpay; env Razorpay__KeyId etc.). Test-mode keys outside
/// production, issued privately by the owner; never committed. With no KeyId outside production the built-in fake
/// Razorpay is used (Mode = Fake), so the app, the browser tests and the integration tests run without an account.
/// </summary>
public sealed class RazorpayOptions
{
    public const string Section = "Razorpay";
    public const string FakeKeyId = "rzp_test_fake";
    public const string FakeKeySecret = "fake-key-secret";
    public const string FakeWebhookSecret = "fake-webhook-secret";

    public string? KeyId { get; set; }
    public string? KeySecret { get; set; }
    public string? WebhookSecret { get; set; }
    /// <summary>Live (call Razorpay; test or live keys decide which account), Fake (the built-in fake), or empty: Fake
    /// when no KeyId is set outside production.</summary>
    public string? Mode { get; set; }
    public string BaseUrl { get; set; } = "https://api.razorpay.com/v1/";
    /// <summary>Each try gives up after this long.</summary>
    public int TimeoutSeconds { get; set; } = 10;
    /// <summary>Tries for a call that is safe to repeat (create order, fetch payment). A refund is tried once more only
    /// when Razorpay says "too many requests" (it wasn't processed).</summary>
    public int MaxAttempts { get; set; } = 3;
    /// <summary>The wait before the second try; it doubles each time, with ±50% jitter.</summary>
    public int RetryBaseDelayMs { get; set; } = 300;

    public bool UseFake(bool production)
    {
        if (string.Equals(Mode, "Fake", StringComparison.OrdinalIgnoreCase))
        {
            if (production) throw new InvalidOperationException("Razorpay:Mode=Fake is for development and tests only; set real keys in production.");
            return true;
        }
        if (string.Equals(Mode, "Live", StringComparison.OrdinalIgnoreCase)) return false;
        return !production && string.IsNullOrWhiteSpace(KeyId);
    }

    /// <summary>The keys the fake uses unless others are set (so a test can sign with a known secret).</summary>
    public RazorpayOptions WithFakeDefaults()
    {
        KeyId = string.IsNullOrWhiteSpace(KeyId) ? FakeKeyId : KeyId;
        KeySecret = string.IsNullOrWhiteSpace(KeySecret) ? FakeKeySecret : KeySecret;
        WebhookSecret = string.IsNullOrWhiteSpace(WebhookSecret) ? FakeWebhookSecret : WebhookSecret;
        return this;
    }
}

/// <summary>
/// Razorpay's Orders, Payments and Refunds APIs over HttpClient (basic auth with the key id and secret), and the two
/// signature checks. Every try has its own timeout; a try that times out, can't connect, or gets 429/5xx is retried
/// with exponential backoff and jitter, except a refund, which is retried only after a 429 (an unclear failure might
/// have refunded already). The fake Razorpay (<see cref="FakeRazorpayServer"/>) answers the same HTTP calls.
/// </summary>
public sealed class RazorpayPaymentGateway(HttpClient http, IOptions<RazorpayOptions> options, ILogger<RazorpayPaymentGateway> log,
    FakeRazorpayServer? fake = null) : IPaymentGateway
{
    private readonly RazorpayOptions o = options.Value;

    public string Name => "RAZORPAY";
    public string KeyId => o.KeyId ?? "";
    public bool IsFake => fake is not null;

    /// <summary>True when keys are set (or the fake is used): links can be created.</summary>
    public bool Configured => IsFake || (!string.IsNullOrWhiteSpace(o.KeyId) && !string.IsNullOrWhiteSpace(o.KeySecret));

    public async Task<GatewayOrder> CreateOrderAsync(long amountPaise, string currency, string receipt, IReadOnlyDictionary<string, string> notes,
        CancellationToken ct)
    {
        RequireConfigured();
        var body = new Dictionary<string, object> { ["amount"] = amountPaise, ["currency"] = currency, ["receipt"] = receipt, ["notes"] = notes };
        var json = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "orders") { Content = JsonContent.Create(body) },
            "create the payment order", retryUnclear: true, ct);
        return new GatewayOrder(Str(json, "id")!, json.GetProperty("amount").GetInt64(), Str(json, "currency")!, Str(json, "status")!);
    }

    public async Task<GatewayPayment> FetchPaymentAsync(string paymentId, CancellationToken ct)
    {
        RequireConfigured();
        var json = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"payments/{Uri.EscapeDataString(paymentId)}"),
            $"look up payment {paymentId}", retryUnclear: true, ct);
        return ParsePayment(json);
    }

    public async Task<GatewayRefund> RefundAsync(string paymentId, long amountPaise, string receipt, IReadOnlyDictionary<string, string> notes,
        CancellationToken ct)
    {
        RequireConfigured();
        var body = new Dictionary<string, object> { ["amount"] = amountPaise, ["receipt"] = receipt, ["notes"] = notes };
        var json = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"payments/{Uri.EscapeDataString(paymentId)}/refund")
        { Content = JsonContent.Create(body) }, "send the refund", retryUnclear: false, ct);
        return ParseRefund(json);
    }

    public bool VerifyWebhook(ReadOnlySpan<byte> rawBody, string? signature) =>
        !string.IsNullOrWhiteSpace(o.WebhookSecret) && SignatureMatches(Hmac(o.WebhookSecret, rawBody), signature);

    public bool VerifyCheckout(string orderId, string paymentId, string? signature) =>
        !string.IsNullOrWhiteSpace(o.KeySecret) && SignatureMatches(Hmac(o.KeySecret, Encoding.UTF8.GetBytes($"{orderId}|{paymentId}")), signature);

    /// <summary>Lower-case hex HMAC-SHA256, as Razorpay sends it.</summary>
    public static string Hmac(string secret, ReadOnlySpan<byte> data) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), data));

    private static bool SignatureMatches(string expected, string? given)
    {
        if (string.IsNullOrWhiteSpace(given)) return false;
        var a = Encoding.ASCII.GetBytes(expected);
        var b = Encoding.ASCII.GetBytes(given.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    internal static GatewayPayment ParsePayment(JsonElement p) => new(
        Str(p, "id")!, Str(p, "order_id"), p.GetProperty("amount").GetInt64(), Str(p, "currency") ?? "INR", Str(p, "status") ?? "created",
        Str(p, "method"), Str(p, "error_code"), Str(p, "error_description"),
        p.TryGetProperty("created_at", out var c) && c.ValueKind == JsonValueKind.Number ? DateTimeOffset.FromUnixTimeSeconds(c.GetInt64()) : DateTimeOffset.UtcNow);

    internal static GatewayRefund ParseRefund(JsonElement r) =>
        new(Str(r, "id")!, Str(r, "payment_id")!, r.GetProperty("amount").GetInt64(), Str(r, "status") ?? "pending");

    internal static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private void RequireConfigured()
    {
        if (!Configured)
            throw new PaymentGatewayException(
                "Online payments aren't set up: the Razorpay keys (Razorpay__KeyId and Razorpay__KeySecret) are missing on the server. Ask the owner to add them.");
    }

    private async Task<JsonElement> SendAsync(Func<HttpRequestMessage> request, string what, bool retryUnclear, CancellationToken ct)
    {
        var attempts = Math.Max(1, o.MaxAttempts);
        for (var attempt = 1; ; attempt++)
        {
            string failure;
            bool retry;
            int? status = null;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, o.TimeoutSeconds)));
                try
                {
                    using var req = request();
                    req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{o.KeyId}:{o.KeySecret}")));
                    using var res = await http.SendAsync(req, timeout.Token);
                    var text = await res.Content.ReadAsStringAsync(timeout.Token);
                    if (res.IsSuccessStatusCode)
                        return JsonDocument.Parse(text).RootElement.Clone();
                    status = (int)res.StatusCode;
                    var reason = (ErrorDescription(text) ?? res.ReasonPhrase ?? "no reason given").TrimEnd('.');
                    if (res.StatusCode == HttpStatusCode.TooManyRequests)
                        (failure, retry) = ($"Razorpay is busy (too many requests) and couldn't {what}", true);
                    else if (status >= 500)
                        (failure, retry) = ($"Razorpay had a problem ({status}) and couldn't {what}: {reason}", retryUnclear);
                    else if (status is 401)
                        throw new PaymentGatewayException($"Razorpay refused the keys, so it couldn't {what}. Check Razorpay__KeyId and Razorpay__KeySecret on the server.", status);
                    else
                        throw new PaymentGatewayException($"Razorpay couldn't {what}: {reason}.", status);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    (failure, retry) = ($"Razorpay didn't answer within {o.TimeoutSeconds} seconds, so it couldn't {what}", retryUnclear);
                }
                catch (HttpRequestException ex)
                {
                    (failure, retry) = ($"Razorpay couldn't be reached ({ex.Message.TrimEnd('.')}), so it couldn't {what}", retryUnclear);
                }
            }
            if (!retry || attempt >= attempts)
            {
                log.LogWarning("Razorpay call failed after {Attempts} tries: {Failure}", attempt, failure);
                throw new PaymentGatewayException(failure + (attempt > 1 ? $" (tried {attempt} times)." : "."), status);
            }
            var wait = o.RetryBaseDelayMs * Math.Pow(2, attempt - 1) * (0.5 + Random.Shared.NextDouble());
            log.LogInformation("Razorpay call failed ({Failure}); try {Next} of {Attempts} in {Wait} ms", failure, attempt + 1, attempts,
                ((int)wait).ToString(CultureInfo.InvariantCulture));
            await Task.Delay(TimeSpan.FromMilliseconds(wait), ct);
        }
    }

    private static string? ErrorDescription(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var e) ? Str(e, "description") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
