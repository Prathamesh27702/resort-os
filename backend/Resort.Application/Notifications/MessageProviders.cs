namespace Resort.Application.Notifications;

/// <summary>A WhatsApp template message: to an E.164 number (+919820012345), the approved template's name and language,
/// and its named body parameters.</summary>
public sealed record WhatsAppTemplateSend(string To, string Template, string Language, IReadOnlyDictionary<string, string> Parameters);

/// <summary>An email. IdempotencyKey: the provider sends a retried request with the same key once.</summary>
public sealed record EmailSend(string To, string ToName, string Subject, string Text, string Html, string IdempotencyKey);

/// <summary>
/// A send that didn't work. Transient: worth trying again later (the provider is down or slow, too many requests, a
/// network error). Otherwise permanent (the number isn't on WhatsApp, the template isn't approved, the key is wrong):
/// trying again won't help, so the message fails at once with the reason.
/// </summary>
public sealed class MessageSendException(string message, bool transient, Exception? inner = null) : Exception(message, inner)
{
    public bool Transient { get; } = transient;
}

/// <summary>WhatsApp Business Platform (Cloud API). The real one calls graph.facebook.com; with no keys outside production a
/// built-in fake answers instead (tests, browser tests, development).</summary>
public interface IWhatsAppClient
{
    /// <summary>LIVE (keys set), FAKE (the built-in fake) or OFF (production without keys: messages fail with the reason).</summary>
    string Mode { get; }

    /// <summary>Sends a template message; returns WhatsApp's message id (wamid).</summary>
    Task<string> SendTemplateAsync(WhatsAppTemplateSend message, CancellationToken ct);

    /// <summary>True when X-Hub-Signature-256 ("sha256=" + hex HMAC-SHA256 of the raw body with the app secret) matches.</summary>
    bool VerifySignature(byte[] body, string? signatureHeader);

    /// <summary>The token Meta sends back when it verifies the webhook URL (hub.verify_token); null when not set.</summary>
    string? WebhookVerifyToken { get; }
}

/// <summary>Transactional email (Resend by default). With no API key outside production a built-in fake answers.</summary>
public interface IEmailClient
{
    /// <summary>LIVE, FAKE or OFF, as for WhatsApp.</summary>
    string Mode { get; }

    /// <summary>The provider's name stored with each email (RESEND, FAKE).</summary>
    string Provider { get; }

    /// <summary>Sends an email; returns the provider's message id.</summary>
    Task<string> SendAsync(EmailSend message, CancellationToken ct);
}

public static class ProviderModes
{
    public const string Live = "LIVE";
    public const string Fake = "FAKE";
    public const string Off = "OFF";
}

/// <summary>Where links in messages point (the guest's pay page): Notifications:PublicUrl, the web app's address.</summary>
public sealed class NotificationOptions
{
    public const string Section = "Notifications";
    public string PublicUrl { get; set; } = "http://localhost:4300";
    /// <summary>The scheduler (default on): every minute it runs the jobs that are due.</summary>
    public bool JobsEnabled { get; set; } = true;
    /// <summary>The dispatcher (default on): every few seconds it sends what is queued.</summary>
    public bool DispatchEnabled { get; set; } = true;
    public int DispatchIntervalSeconds { get; set; } = 10;
    /// <summary>A message that keeps failing for a reason that may pass is tried this many times, then fails.</summary>
    public int MaxAttempts { get; set; } = 5;
}
