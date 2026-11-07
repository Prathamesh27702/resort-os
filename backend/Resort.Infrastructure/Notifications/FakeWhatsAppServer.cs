using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Resort.Infrastructure.Notifications;

/// <summary>A message the fake WhatsApp accepted.</summary>
public sealed record FakeWhatsAppMessage(string Wamid, string To, string Template, string Language, IReadOnlyDictionary<string, string> Parameters,
    DateTimeOffset At);

/// <summary>A status webhook the fake would deliver: the exact body and its X-Hub-Signature-256.</summary>
public sealed record FakeWhatsAppWebhook(byte[] Body, string Signature)
{
    public string Text => Encoding.UTF8.GetString(Body);
}

/// <summary>
/// The fake WhatsApp (Sprint 24): an in-memory stand-in for the Cloud API's messages endpoint, answering the same HTTP call
/// with the same JSON, so <see cref="WhatsAppCloudClient"/> runs unchanged against it. It also makes the signed status
/// webhooks WhatsApp would send (delivered, read, failed). Used when no WhatsApp keys are set outside production: by the
/// integration tests, the browser tests and in development, where a message's page has "Mark delivered (test)" buttons.
/// Tests can make the next sends fail (<see cref="FailNext"/>). Everything is lost when the API stops.
/// </summary>
public sealed class FakeWhatsAppServer(IOptions<WhatsAppOptions> options)
{
    private readonly ConcurrentQueue<FakeWhatsAppMessage> sent = new();
    private readonly ConcurrentQueue<(HttpStatusCode Status, int Code, string Message)> failures = new();
    private int calls;

    /// <summary>Every message accepted, oldest first.</summary>
    public IReadOnlyList<FakeWhatsAppMessage> Sent => sent.ToArray();

    /// <summary>HTTP calls answered so far (including failed ones).</summary>
    public int Calls => calls;

    /// <summary>The next sends answer with these errors, as WhatsApp would: e.g. (503, 131000, "Something went wrong") may
    /// pass; (400, 131026, "Message undeliverable") won't.</summary>
    public void FailNext(params (HttpStatusCode Status, int Code, string Message)[] errors)
    {
        foreach (var e in errors) failures.Enqueue(e);
    }

    public FakeWhatsAppWebhook StatusWebhook(string wamid, string status, string? recipient = null, (string Code, string Title)? error = null,
        DateTimeOffset? at = null)
    {
        var st = new JsonObject
        {
            ["id"] = wamid,
            ["status"] = status,
            ["timestamp"] = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["recipient_id"] = recipient ?? "919800000000",
            ["pricing"] = new JsonObject { ["billable"] = true, ["pricing_model"] = "PMP", ["category"] = "utility" },
        };
        if (error is { } e)
            st["errors"] = new JsonArray(new JsonObject
            {
                ["code"] = int.TryParse(e.Code, out var n) ? n : 0,
                ["title"] = e.Title,
                ["message"] = e.Title,
                ["error_data"] = new JsonObject { ["details"] = "Test failure from the fake WhatsApp." },
            });
        return Webhook(new JsonObject { ["statuses"] = new JsonArray(st) });
    }

    /// <summary>A webhook with a guest's reply, which Resort OS acknowledges and doesn't store.</summary>
    public FakeWhatsAppWebhook ReplyWebhook(string from, string text) => Webhook(new JsonObject
    {
        ["contacts"] = new JsonArray(new JsonObject { ["wa_id"] = from, ["profile"] = new JsonObject { ["name"] = "Guest" } }),
        ["messages"] = new JsonArray(new JsonObject
        {
            ["from"] = from,
            ["id"] = "wamid.in" + Guid.NewGuid().ToString("N"),
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["type"] = "text",
            ["text"] = new JsonObject { ["body"] = text },
        }),
    });

    private FakeWhatsAppWebhook Webhook(JsonObject value)
    {
        value["messaging_product"] = "whatsapp";
        value["metadata"] = new JsonObject { ["display_phone_number"] = "15550000000", ["phone_number_id"] = options.Value.PhoneNumberId };
        var root = new JsonObject
        {
            ["object"] = "whatsapp_business_account",
            ["entry"] = new JsonArray(new JsonObject
            {
                ["id"] = "WABA_ID",
                ["changes"] = new JsonArray(new JsonObject { ["field"] = "messages", ["value"] = value }),
            }),
        };
        var body = Encoding.UTF8.GetBytes(root.ToJsonString());
        return new FakeWhatsAppWebhook(body, WhatsAppOptions.Sign(body, options.Value.AppSecret ?? WhatsAppOptions.FakeAppSecret));
    }

    internal async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref calls);
        if (request.Headers.Authorization?.Parameter != options.Value.AccessToken)
            return Json(HttpStatusCode.Unauthorized, Err(190, "Invalid OAuth access token - Cannot parse access token"));
        if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath.EndsWith($"/{options.Value.PhoneNumberId}/messages", StringComparison.Ordinal) != true)
            return Json(HttpStatusCode.NotFound, Err(100, "Unsupported request"));
        if (failures.TryDequeue(out var f)) return Json(f.Status, Err(f.Code, f.Message));

        var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
        var to = json["to"]?.GetValue<string>() ?? "";
        if (json["type"]?.GetValue<string>() != "template" || json["template"] is not JsonObject t)
            return Json(HttpStatusCode.BadRequest, Err(131009, "Parameter value is not valid: business-initiated messages must be templates"));
        if (!to.All(char.IsAsciiDigit) || to.Length is < 8 or > 15)
            return Json(HttpStatusCode.BadRequest, Err(131030, "Recipient phone number not valid"));
        var parameters = new Dictionary<string, string>();
        if (t["components"] is JsonArray comps)
            foreach (var c in comps.OfType<JsonObject>())
                if (c["parameters"] is JsonArray ps)
                    foreach (var p in ps.OfType<JsonObject>())
                        parameters[p["parameter_name"]?.GetValue<string>() ?? parameters.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                            p["text"]?.GetValue<string>() ?? "";
        var wamid = "wamid.FAKE" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        sent.Enqueue(new FakeWhatsAppMessage(wamid, "+" + to, t["name"]?.GetValue<string>() ?? "", t["language"]?["code"]?.GetValue<string>() ?? "",
            parameters, DateTimeOffset.UtcNow));
        return Json(HttpStatusCode.OK, new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["contacts"] = new JsonArray(new JsonObject { ["input"] = to, ["wa_id"] = to }),
            ["messages"] = new JsonArray(new JsonObject { ["id"] = wamid, ["message_status"] = "accepted" }),
        });
    }

    private static JsonObject Err(int code, string message) => new()
    {
        ["error"] = new JsonObject { ["message"] = message, ["type"] = "OAuthException", ["code"] = code, ["fbtrace_id"] = "FAKE" },
    };

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
        new(status) { Content = new StringContent(body.ToJsonString(new JsonSerializerOptions()), Encoding.UTF8, "application/json") };
}

public sealed class FakeWhatsAppHandler(FakeWhatsAppServer server) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        server.HandleAsync(request, cancellationToken);
}
