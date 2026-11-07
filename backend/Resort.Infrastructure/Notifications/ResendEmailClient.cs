using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Resort.Application.Notifications;

namespace Resort.Infrastructure.Notifications;

/// <summary>
/// Transactional email (Sprint 24). Resend by default (free 3,000 emails a month, docs/COSTS.md): Email__ApiKey and
/// Email__FromAddress ("Sea Breeze Resort &lt;bookings@seabreeze.in&gt;", on a domain verified in Resend). Mode: Live, Fake,
/// or empty: Fake when no ApiKey is set outside production; OFF in production without a key.
/// </summary>
public sealed class EmailOptions
{
    public const string Section = "Email";
    public const string FakeApiKey = "re_fake_key";
    public const string FakeFrom = "Resort OS <no-reply@resort-os.local>";

    public string Provider { get; set; } = "RESEND";
    public string? ApiKey { get; set; }
    public string? FromAddress { get; set; }
    public string? Mode { get; set; }
    public string BaseUrl { get; set; } = "https://api.resend.com/";
    public int TimeoutSeconds { get; set; } = 10;

    public string Resolve(bool production)
    {
        if (string.Equals(Mode, "Fake", StringComparison.OrdinalIgnoreCase))
        {
            if (production) throw new InvalidOperationException("Email:Mode=Fake is for development and tests only; set a real key in production.");
            return ProviderModes.Fake;
        }
        var configured = !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(FromAddress);
        if (string.Equals(Mode, "Live", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(ApiKey))
            return configured ? ProviderModes.Live : ProviderModes.Off;
        return production ? ProviderModes.Off : ProviderModes.Fake;
    }

    public EmailOptions WithFakeDefaults()
    {
        ApiKey = string.IsNullOrWhiteSpace(ApiKey) ? FakeApiKey : ApiKey;
        FromAddress = string.IsNullOrWhiteSpace(FromAddress) ? FakeFrom : FromAddress;
        return this;
    }
}

/// <summary>Resend's POST /emails with a bearer key and an Idempotency-Key (the outbox message's id), so a retried request
/// is one email. One try per send; 429, 5xx, timeouts and network errors may pass (tried again later), other 4xx don't.</summary>
public sealed class ResendEmailClient(HttpClient http, IOptions<EmailOptions> options, EmailMode mode) : IEmailClient
{
    private readonly EmailOptions o = options.Value;

    public string Mode => mode.Value;

    public string Provider => mode.Value == ProviderModes.Fake ? "FAKE" : "RESEND";

    public async Task<string> SendAsync(EmailSend m, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["from"] = o.FromAddress,
            ["to"] = new JsonArray(m.To),
            ["subject"] = m.Subject,
            ["text"] = m.Text,
            ["html"] = m.Html,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "emails") { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey);
        req.Headers.Add("Idempotency-Key", m.IdempotencyKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, o.TimeoutSeconds)));
        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(req, timeout.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new MessageSendException($"the email provider didn't answer within {o.TimeoutSeconds} seconds", transient: true, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new MessageSendException($"couldn't reach the email provider ({ex.Message})", transient: true, ex);
        }
        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            string? id = null, message = null;
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("id", out var i)) id = i.GetString();
                if (doc.RootElement.TryGetProperty("message", out var msg)) message = msg.GetString();
            }
            catch (JsonException)
            {
                // not JSON: the status code says enough
            }
            if (res.IsSuccessStatusCode && !string.IsNullOrEmpty(id)) return id;
            var transient = (int)res.StatusCode >= 500 || res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Conflict || res.IsSuccessStatusCode;
            throw new MessageSendException($"the email provider refused it: {message ?? res.ReasonPhrase ?? "no reason given"} ({(int)res.StatusCode})", transient);
        }
    }
}

public sealed record EmailMode(string Value);

/// <summary>An email the fake provider accepted.</summary>
public sealed record FakeEmail(string Id, string From, string To, string Subject, string Text, string Html, string IdempotencyKey, DateTimeOffset At);

/// <summary>
/// The fake email provider (Sprint 24): answers Resend's POST /emails in memory with the same JSON, keeps what it was sent,
/// and honours the Idempotency-Key (the same key answers with the first email's id and sends nothing new). Used when no
/// email key is set outside production. Tests can make the next sends fail (<see cref="FailNext"/>).
/// </summary>
public sealed class FakeEmailServer(IOptions<EmailOptions> options)
{
    private readonly ConcurrentQueue<FakeEmail> sent = new();
    private readonly ConcurrentDictionary<string, string> byKey = new();
    private readonly ConcurrentQueue<(HttpStatusCode Status, string Message)> failures = new();

    public IReadOnlyList<FakeEmail> Sent => sent.ToArray();

    public void FailNext(params (HttpStatusCode Status, string Message)[] errors)
    {
        foreach (var e in errors) failures.Enqueue(e);
    }

    internal async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Headers.Authorization?.Parameter != options.Value.ApiKey)
            return Json(HttpStatusCode.Unauthorized, new JsonObject { ["statusCode"] = 401, ["name"] = "validation_error", ["message"] = "API key is invalid" });
        if (failures.TryDequeue(out var f))
            return Json(f.Status, new JsonObject { ["statusCode"] = (int)f.Status, ["name"] = "application_error", ["message"] = f.Message });
        var key = request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.First() : Guid.NewGuid().ToString("N");
        if (byKey.TryGetValue(key, out var existing)) return Json(HttpStatusCode.OK, new JsonObject { ["id"] = existing });
        var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
        var to = json["to"] is JsonArray a ? a[0]?.GetValue<string>() ?? "" : json["to"]?.GetValue<string>() ?? "";
        if (!to.Contains('@'))
            return Json(HttpStatusCode.UnprocessableEntity, new JsonObject { ["statusCode"] = 422, ["name"] = "validation_error", ["message"] = "Invalid `to` field." });
        var id = Guid.NewGuid().ToString();
        byKey[key] = id;
        sent.Enqueue(new FakeEmail(id, json["from"]?.GetValue<string>() ?? "", to, json["subject"]?.GetValue<string>() ?? "",
            json["text"]?.GetValue<string>() ?? "", json["html"]?.GetValue<string>() ?? "", key, DateTimeOffset.UtcNow));
        return Json(HttpStatusCode.OK, new JsonObject { ["id"] = id });
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}

public sealed class FakeEmailHandler(FakeEmailServer server) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        server.HandleAsync(request, cancellationToken);
}
