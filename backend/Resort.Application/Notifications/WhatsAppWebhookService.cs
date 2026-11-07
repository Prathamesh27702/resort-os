using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Resort.Application.Common;
using Resort.Contracts;
using Resort.Domain.Notifications;

namespace Resort.Application.Notifications;

/// <summary>
/// WhatsApp's webhook (Sprint 24; docs/WHATSAPP.md). Meta first checks the URL (GET with hub.mode=subscribe, our verify token
/// and a challenge to echo back), then POSTs a status for every message we sent: sent, delivered, read or failed. In order:
///
///  1. Verify X-Hub-Signature-256 ("sha256=" + the HMAC-SHA256 of the raw body with the app secret). Wrong or missing: 401,
///     nothing stored.
///  2. Each status in the body is its own event, <c>&lt;wamid&gt;:&lt;status&gt;</c>, claimed in webhook_events (UNIQUE
///     provider + event id) in its own transaction: Meta delivers at least once, so a second delivery changes nothing.
///  3. Match it to one of our messages by its wamid (not ours: stored as IGNORED) and move it forward only: SENT →
///     DELIVERED → READ, or SENT → FAILED with WhatsApp's error. A "delivered" arriving after "read" changes nothing.
///  4. Our own error: rolled back, stored as FAILED with the reason, and 500 so Meta delivers it again.
///
/// Guests' replies (messages, not statuses) aren't read yet: they are acknowledged and not stored, since they hold the
/// guest's number and words. webhook_events keeps the wamid, status, time and error only, never the phone number.
/// </summary>
public sealed class WhatsAppWebhookService(IAppDbContext db, IWhatsAppClient client, TimeProvider clock, ILogger<WhatsAppWebhookService> log)
{
    public const string SignatureHeader = "X-Hub-Signature-256";
    private const string Provider = "WHATSAPP";

    public sealed record Result(int StatusCode, WebhookAckDto Ack);

    private sealed record Status(string Wamid, string Value, DateTimeOffset? At, string? ErrorCode, string? ErrorTitle, string? Pricing);

    /// <summary>Meta's check of the webhook URL: the challenge when the mode and token are right.</summary>
    public string Verify(string? mode, string? token, string? challenge)
    {
        var expected = client.WebhookVerifyToken;
        if (mode != "subscribe" || string.IsNullOrEmpty(expected) || token is null
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected)))
            throw new ForbiddenAppException("The verify token doesn't match WhatsApp__WebhookVerifyToken, so the webhook wasn't verified. " +
                                            "Use the same token in Meta's webhook settings and on this server.");
        return challenge ?? "";
    }

    public async Task<Result> HandleAsync(byte[] rawBody, string? signature, CancellationToken ct)
    {
        if (!client.VerifySignature(rawBody, signature))
            throw new UnauthorizedAppException("BAD_SIGNATURE",
                $"The {SignatureHeader} header doesn't match this body for the app secret, so the webhook was refused and nothing was stored. " +
                "Check that WhatsApp__AppSecret is the app secret of the Meta app that sends it.");
        var (statuses, replies) = Parse(rawBody);
        int applied = 0, duplicate = 0, ignored = 0, failed = 0;
        string? firstError = null;
        foreach (var s in statuses)
        {
            var outcome = await ApplyAsync(s, ct);
            switch (outcome.Status)
            {
                case "processed": applied++; break;
                case "duplicate": duplicate++; break;
                case "failed": failed++; firstError ??= outcome.Text; break;
                default: ignored++; break;
            }
        }
        var words = $"{statuses.Count} status{(statuses.Count == 1 ? "" : "es")}: {applied} applied, {duplicate} already seen, {ignored} changed nothing" +
                    (replies > 0 ? $"; {replies} guest repl{(replies == 1 ? "y" : "ies")} not read (Resort OS doesn't read replies yet)" : "") + ".";
        if (failed > 0)
            return new Result(500, new WebhookAckDto("failed", $"{failed} status{(failed == 1 ? "" : "es")} couldn't be processed ({firstError}); " +
                                                               "WhatsApp delivers them again."));
        return new Result(200, new WebhookAckDto(applied > 0 ? "processed" : duplicate == statuses.Count && statuses.Count > 0 ? "duplicate" : "ignored", words));
    }

    private sealed record Outcome(string Status, string Text);

    private async Task<Outcome> ApplyAsync(Status s, CancellationToken ct)
    {
        var eventId = $"{s.Wamid}:{s.Value}";
        if (eventId.Length > 100) eventId = eventId[^100..];
        var payload = JsonSerializer.Serialize(new { wamid = s.Wamid, status = s.Value, timestamp = s.At, error_code = s.ErrorCode, error_title = s.ErrorTitle, pricing = s.Pricing });
        var type = "status." + s.Value;
        var now = clock.GetUtcNow();
        WhatsAppMessage? seen = null;
        try
        {
            seen = await db.WhatsAppMessages.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(w => w.Wamid == s.Wamid, ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var claimed = await db.Database.SqlQuery<Guid>(
                $"""
                 INSERT INTO webhook_events (id, provider, event_id, event_type, status, payload, received_at)
                 VALUES ({Guid.CreateVersion7()}, {Provider}, {eventId}, {type}, 'FAILED', {payload}::jsonb, {now})
                 ON CONFLICT (provider, event_id) DO UPDATE SET attempts = webhook_events.attempts + 1
                  WHERE webhook_events.status = 'FAILED'
                 RETURNING id AS "Value"
                 """).ToListAsync(ct);
            if (claimed.Count == 0) return new Outcome("duplicate", "");

            string status, text;
            if (seen is null)
                (status, text) = ("IGNORED", $"WhatsApp message {s.Wamid} wasn't sent by Resort OS, so nothing changed.");
            else
            {
                await db.Database.ExecuteSqlAsync($"SELECT 1 FROM outbox_messages WHERE id = {seen.OutboxMessageId} FOR UPDATE", ct);
                var wa = await db.WhatsAppMessages.IgnoreQueryFilters().SingleAsync(w => w.Id == seen.Id, ct);
                var m = await db.OutboxMessages.IgnoreQueryFilters().SingleAsync(x => x.Id == wa.OutboxMessageId, ct);
                (status, text) = Move(s, wa, m, now);
                await db.SaveChangesAsync(ct);
            }
            await db.Database.ExecuteSqlAsync(
                $"""
                 UPDATE webhook_events SET status = {status}, outcome = {text}, processed_at = {now},
                        organization_id = {seen?.OrganizationId}, property_id = {seen?.PropertyId}
                  WHERE provider = {Provider} AND event_id = {eventId}
                 """, ct);
            await tx.CommitAsync(ct);
            return new Outcome(status == "PROCESSED" ? "processed" : "ignored", text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            (db as DbContext)?.ChangeTracker.Clear();
            var reason = ex is DbUpdateException { InnerException: { } inner } ? $"the database refused it: {inner.Message}" : $"{ex.GetType().Name}: {ex.Message}";
            log.LogError(ex, "WhatsApp status {Event} failed; WhatsApp will deliver it again", eventId);
            var outcome = "Failed: " + reason;
            if (outcome.Length > 1000) outcome = outcome[..1000];
            await db.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO webhook_events (id, provider, event_id, event_type, organization_id, property_id, status, outcome, payload, received_at)
                 VALUES ({Guid.CreateVersion7()}, {Provider}, {eventId}, {type}, {seen?.OrganizationId}, {seen?.PropertyId}, 'FAILED', {outcome},
                         {payload}::jsonb, {now})
                 ON CONFLICT (provider, event_id) DO UPDATE SET outcome = EXCLUDED.outcome WHERE webhook_events.status = 'FAILED'
                 """, CancellationToken.None);
            return new Outcome("failed", reason);
        }
    }

    /// <summary>Moves the WhatsApp record and its outbox message forward to the status, never back.</summary>
    private static (string Status, string Text) Move(Status s, WhatsAppMessage wa, OutboxMessage m, DateTimeOffset now)
    {
        var target = s.Value switch
        {
            "sent" => MessageStatuses.Sent,
            "delivered" => MessageStatuses.Delivered,
            "read" => MessageStatuses.Read,
            "failed" => MessageStatuses.Failed,
            _ => null,
        };
        if (target is null)
            return ("IGNORED", $"Resort OS doesn't use the WhatsApp status \"{s.Value}\"; nothing changed.");
        if (MessageStatuses.Rank(target) <= MessageStatuses.Rank(wa.Status))
            return ("IGNORED", $"The message to {m.RecipientName} is already {MessageStatuses.Words(wa.Status).ToLowerInvariant()}, so \"{s.Value}\" changes nothing.");
        // WhatsApp's timestamps are whole seconds from its clock: never before we sent it.
        var at = s.At is { } t0 && t0 > wa.SentAt ? t0 : s.At is null ? now : wa.SentAt;
        wa.Status = target;
        wa.UpdatedAt = now;
        if (s.Pricing is { } p) wa.PricingCategory = p.Length <= 40 ? p : p[..40];
        m.Status = target;
        switch (target)
        {
            case MessageStatuses.Delivered:
                wa.DeliveredAt = m.DeliveredAt = at;
                break;
            case MessageStatuses.Read:
                wa.ReadAt = m.ReadAt = at;
                wa.DeliveredAt ??= at;
                m.DeliveredAt ??= at;
                break;
            case MessageStatuses.Failed:
                wa.FailedAt = m.FailedAt = at;
                wa.ErrorCode = s.ErrorCode;
                wa.ErrorTitle = s.ErrorTitle is { Length: > 500 } t ? t[..500] : s.ErrorTitle;
                m.LastError = $"WhatsApp couldn't deliver it: {s.ErrorTitle ?? "no reason given"}{(s.ErrorCode is null ? "" : $" (error {s.ErrorCode})")}.";
                break;
        }
        return ("PROCESSED", $"The {m.TemplateCode} message to {m.RecipientName} is {MessageStatuses.Words(target).ToLowerInvariant()}.");
    }

    private static (List<Status> Statuses, int Replies) Parse(byte[] raw)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(raw).RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new BusinessRuleAppException("BAD_WEBHOOK", "The body isn't JSON, so it isn't a WhatsApp webhook.");
        }
        var statuses = new List<Status>();
        var replies = 0;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return (statuses, replies);
        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array) continue;
            foreach (var change in changes.EnumerateArray())
            {
                if (!change.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object) continue;
                if (value.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array) replies += msgs.GetArrayLength();
                if (!value.TryGetProperty("statuses", out var list) || list.ValueKind != JsonValueKind.Array) continue;
                foreach (var st in list.EnumerateArray())
                {
                    var id = Str(st, "id");
                    var status = Str(st, "status");
                    if (id is null || status is null) continue;
                    DateTimeOffset? at = long.TryParse(Str(st, "timestamp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)
                        ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;
                    string? code = null, title = null;
                    if (st.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                    {
                        var e = errors[0];
                        code = e.TryGetProperty("code", out var c) ? c.ValueKind == JsonValueKind.Number ? c.GetRawText() : c.GetString() : null;
                        title = Str(e, "title") ?? Str(e, "message");
                        if (e.TryGetProperty("error_data", out var data) && Str(data, "details") is { } details) title = $"{title}: {details}";
                    }
                    var pricing = st.TryGetProperty("pricing", out var pr) ? Str(pr, "category") : null;
                    statuses.Add(new Status(id, status.ToLowerInvariant(), at, code, title, pricing));
                }
            }
        }
        return (statuses, replies);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
