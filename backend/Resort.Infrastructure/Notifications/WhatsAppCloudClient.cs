using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Resort.Application.Notifications;

namespace Resort.Infrastructure.Notifications;

/// <summary>
/// WhatsApp Business Platform, Cloud API (Sprint 24; docs/WHATSAPP.md). Keys: WhatsApp__PhoneNumberId and
/// WhatsApp__AccessToken (a system user's permanent token) to send, WhatsApp__AppSecret to check the webhook's signature,
/// WhatsApp__WebhookVerifyToken for Meta's check of the webhook URL. Mode: Live, Fake, or empty: Fake when no
/// PhoneNumberId is set outside production; in production without keys WhatsApp is OFF and messages fail with the reason.
/// </summary>
public sealed class WhatsAppOptions
{
    public const string Section = "WhatsApp";
    public const string FakePhoneNumberId = "100000000000001";
    public const string FakeAccessToken = "fake-whatsapp-token";
    public const string FakeAppSecret = "fake-whatsapp-app-secret";
    public const string FakeVerifyToken = "fake-verify-token";

    public string? PhoneNumberId { get; set; }
    public string? AccessToken { get; set; }
    public string? AppSecret { get; set; }
    public string? WebhookVerifyToken { get; set; }
    public string? Mode { get; set; }
    public string BaseUrl { get; set; } = "https://graph.facebook.com/";
    public string ApiVersion { get; set; } = "v21.0";
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>LIVE, FAKE or OFF for this server.</summary>
    public string Resolve(bool production)
    {
        if (string.Equals(Mode, "Fake", StringComparison.OrdinalIgnoreCase))
        {
            if (production) throw new InvalidOperationException("WhatsApp:Mode=Fake is for development and tests only; set real keys in production.");
            return ProviderModes.Fake;
        }
        var configured = !string.IsNullOrWhiteSpace(PhoneNumberId) && !string.IsNullOrWhiteSpace(AccessToken);
        if (string.Equals(Mode, "Live", StringComparison.OrdinalIgnoreCase) || configured) return configured ? ProviderModes.Live : ProviderModes.Off;
        return production ? ProviderModes.Off : ProviderModes.Fake;
    }

    public WhatsAppOptions WithFakeDefaults()
    {
        PhoneNumberId = string.IsNullOrWhiteSpace(PhoneNumberId) ? FakePhoneNumberId : PhoneNumberId;
        AccessToken = string.IsNullOrWhiteSpace(AccessToken) ? FakeAccessToken : AccessToken;
        AppSecret = string.IsNullOrWhiteSpace(AppSecret) ? FakeAppSecret : AppSecret;
        WebhookVerifyToken = string.IsNullOrWhiteSpace(WebhookVerifyToken) ? FakeVerifyToken : WebhookVerifyToken;
        return this;
    }

    /// <summary>"sha256=" + the lower-case hex HMAC-SHA256 of <paramref name="body"/> with <paramref name="appSecret"/>,
    /// as Meta puts in X-Hub-Signature-256.</summary>
    public static string Sign(byte[] body, string appSecret) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body));
}

/// <summary>Sends template messages through the Cloud API (POST /{version}/{phone-number-id}/messages, bearer token) and
/// checks webhook signatures. One try per send, with its own timeout: the dispatcher decides when to try again, from
/// whether the failure may pass (5xx, 429, timeouts, WhatsApp's "try later" codes) or not.</summary>
public sealed class WhatsAppCloudClient(HttpClient http, IOptions<WhatsAppOptions> options, WhatsAppMode mode) : IWhatsAppClient
{
    private readonly WhatsAppOptions o = options.Value;

    /// <summary>WhatsApp error codes that mean "try again later" (rate limits, a problem at WhatsApp).</summary>
    private static readonly HashSet<string> TransientCodes = ["4", "80007", "130429", "131000", "131016", "131048", "131056", "133004"];

    public string Mode => mode.Value;

    public string? WebhookVerifyToken => string.IsNullOrWhiteSpace(o.WebhookVerifyToken) ? null : o.WebhookVerifyToken;

    public async Task<string> SendTemplateAsync(WhatsAppTemplateSend m, CancellationToken ct)
    {
        var template = new JsonObject
        {
            ["name"] = m.Template,
            ["language"] = new JsonObject { ["code"] = m.Language },
        };
        if (m.Parameters.Count > 0)
            template["components"] = new JsonArray(new JsonObject
            {
                ["type"] = "body",
                ["parameters"] = new JsonArray(m.Parameters.Select(p => (JsonNode)new JsonObject
                {
                    ["type"] = "text",
                    ["parameter_name"] = p.Key,
                    ["text"] = p.Value.Length == 0 ? "-" : p.Value,
                }).ToArray()),
            });
        var body = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = m.To.TrimStart('+'),
            ["type"] = "template",
            ["template"] = template,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{o.ApiVersion}/{o.PhoneNumberId}/messages") { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.AccessToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, o.TimeoutSeconds)));
        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(req, timeout.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new MessageSendException($"WhatsApp didn't answer within {o.TimeoutSeconds} seconds", transient: true, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new MessageSendException($"couldn't reach WhatsApp ({ex.Message})", transient: true, ex);
        }
        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            if (res.IsSuccessStatusCode && TryWamid(text, out var wamid)) return wamid;
            var (code, message) = Error(text);
            var transient = (int)res.StatusCode >= 500 || res.StatusCode == HttpStatusCode.TooManyRequests || (code is not null && TransientCodes.Contains(code));
            throw new MessageSendException(res.IsSuccessStatusCode
                ? "WhatsApp's answer had no message id"
                : $"WhatsApp refused it: {message ?? res.ReasonPhrase ?? "no reason given"}{(code is null ? "" : $" (error {code})")}", transient || res.IsSuccessStatusCode);
        }
    }

    public bool VerifySignature(byte[] body, string? signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(o.AppSecret) || string.IsNullOrWhiteSpace(signatureHeader)) return false;
        var expected = Encoding.ASCII.GetBytes(WhatsAppOptions.Sign(body, o.AppSecret));
        var given = Encoding.ASCII.GetBytes(signatureHeader.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    private static bool TryWamid(string json, [NotNullWhen(true)] out string? wamid)
    {
        wamid = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array && msgs.GetArrayLength() > 0
                && msgs[0].TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } s)
                wamid = s;
        }
        catch (JsonException)
        {
            return false;
        }
        return wamid is not null;
    }

    private static (string? Code, string? Message) Error(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("error", out var e)) return (null, null);
            var code = e.TryGetProperty("code", out var c) ? c.GetRawText().Trim('"') : null;
            var message = e.TryGetProperty("message", out var m) ? m.GetString() : null;
            if (e.TryGetProperty("error_data", out var d) && d.TryGetProperty("details", out var det) && det.GetString() is { Length: > 0 } details)
                message = $"{message} {details}".Trim();
            return (code, message);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}

/// <summary>The resolved mode, registered once at start-up.</summary>
public sealed record WhatsAppMode(string Value);
