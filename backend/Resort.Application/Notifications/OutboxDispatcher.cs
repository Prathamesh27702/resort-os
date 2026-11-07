using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Resort.Application.Common;
using Resort.Domain.Notifications;

namespace Resort.Application.Notifications;

/// <summary>
/// Sends what the outbox holds (Sprint 24). One message at a time, each in its own transaction:
///
///  1. Claim the oldest due QUEUED row with SELECT … FOR UPDATE SKIP LOCKED: another dispatcher (a second API instance, or
///     Send now at the same moment) skips the row this one holds and takes the next, so no message is sent by two.
///  2. Send it: WhatsApp's Cloud API (a template message), the email provider (with the message's id as the idempotency
///     key, so a retried request is one email), or the staff bell (a notifications row).
///  3. Record what happened in the same transaction: SENT with the provider's id; or, when it failed for a reason that may
///     pass (provider down, timeout, too many requests), still QUEUED with the next try 1, 5, 15 and 60 minutes later, then
///     FAILED after <see cref="NotificationOptions.MaxAttempts"/> tries; or FAILED at once when trying again can't help (the
///     number isn't on WhatsApp, the template isn't approved, WhatsApp isn't connected), with the reason staff see.
///
/// If the API stops between the provider accepting a WhatsApp message and the commit, the row is still QUEUED and is sent
/// again: WhatsApp has no idempotency key, so at worst a guest gets one message twice; never none.
/// </summary>
public sealed class OutboxDispatcher(IAppDbContext db, IWhatsAppClient whatsapp, IEmailClient email, TimeProvider clock,
    IOptions<NotificationOptions> options, ILogger<OutboxDispatcher> log)
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60)];

    /// <summary>Sends up to <paramref name="max"/> due messages; returns how many it handled.</summary>
    public async Task<int> DispatchAsync(int max, CancellationToken ct)
    {
        var done = 0;
        while (done < max && await DispatchOneAsync(null, ct)) done++;
        return done;
    }

    /// <summary>Sends one due message (or the given one, if it is due). False when there was nothing to send.</summary>
    public async Task<bool> DispatchOneAsync(Guid? only, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        var ids = only is { } one
            ? await db.Database.SqlQuery<Guid>(
                $"""SELECT id AS "Value" FROM outbox_messages WHERE id = {one} AND status = 'QUEUED' AND next_attempt_at <= {now} FOR UPDATE SKIP LOCKED""")
                .ToListAsync(ct)
            : await db.Database.SqlQuery<Guid>(
                $"""
                 SELECT id AS "Value" FROM outbox_messages WHERE status = 'QUEUED' AND next_attempt_at <= {now}
                  ORDER BY next_attempt_at, id LIMIT 1 FOR UPDATE SKIP LOCKED
                 """).ToListAsync(ct);
        if (ids.Count == 0) return false;
        var m = await db.OutboxMessages.IgnoreQueryFilters().SingleAsync(x => x.Id == ids[0], ct);
        m.Attempts++;
        try
        {
            switch (m.Channel)
            {
                case MessageChannels.WhatsApp:
                    await SendWhatsAppAsync(m, now, ct);
                    break;
                case MessageChannels.Email:
                    await SendEmailAsync(m, now, ct);
                    break;
                default:
                    db.Notifications.Add(new Notification
                    {
                        OrganizationId = m.OrganizationId,
                        PropertyId = m.PropertyId,
                        UserId = m.UserId!.Value,
                        OutboxMessageId = m.Id,
                        Title = m.Subject ?? m.TemplateCode,
                        Body = m.Body,
                        Link = m.Link,
                        CreatedAt = now,
                    });
                    m.ProviderMessageId = null;
                    break;
            }
            m.Status = MessageStatuses.Sent;
            m.SentAt = now;
            m.LastError = null;
            m.FailedAt = null;
        }
        catch (Exception ex) when (ex is MessageSendException || ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var transient = ex is not MessageSendException se || se.Transient;
            var reason = ex is MessageSendException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            var max = Math.Max(1, options.Value.MaxAttempts);
            if (transient && m.Attempts < max)
            {
                m.NextAttemptAt = now + Backoff[Math.Min(m.Attempts, Backoff.Length) - 1];
                m.LastError = Cut($"Try {m.Attempts} of {max} didn't work ({reason}); it is tried again automatically.");
            }
            else
            {
                m.Status = MessageStatuses.Failed;
                m.FailedAt = now;
                m.LastError = Cut(transient ? $"Tried {m.Attempts} times without success. The last answer: {reason}" : reason);
            }
            log.LogWarning("Message {Id} ({Code}, {Channel}) try {Attempt}: {Reason}", m.Id, m.TemplateCode, m.Channel, m.Attempts, reason);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private async Task SendWhatsAppAsync(OutboxMessage m, DateTimeOffset now, CancellationToken ct)
    {
        if (whatsapp.Mode == ProviderModes.Off)
            throw new MessageSendException("WhatsApp isn't connected on this server. Set WhatsApp__PhoneNumberId, WhatsApp__AccessToken and " +
                                           "WhatsApp__AppSecret (docs/WHATSAPP.md), then send it again.", transient: false);
        var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(m.Params) ?? [];
        var wamid = await whatsapp.SendTemplateAsync(new WhatsAppTemplateSend(m.ToAddress!, m.ProviderTemplate!, m.Language ?? "en", parameters), ct);
        m.ProviderMessageId = wamid;
        // Sent again after a failure: the same row follows the new WhatsApp message.
        var existing = await db.WhatsAppMessages.IgnoreQueryFilters().SingleOrDefaultAsync(w => w.OutboxMessageId == m.Id, ct);
        if (existing is null)
            db.WhatsAppMessages.Add(new WhatsAppMessage
            {
                OrganizationId = m.OrganizationId,
                PropertyId = m.PropertyId,
                OutboxMessageId = m.Id,
                Wamid = wamid,
                TemplateName = m.ProviderTemplate!,
                Language = m.Language ?? "en",
                SentAt = now,
                UpdatedAt = now,
            });
        else
        {
            existing.Wamid = wamid;
            existing.Status = MessageStatuses.Sent;
            existing.SentAt = now;
            existing.DeliveredAt = existing.ReadAt = existing.FailedAt = null;
            existing.ErrorCode = existing.ErrorTitle = null;
            existing.UpdatedAt = now;
        }
        m.DeliveredAt = m.ReadAt = null;
    }

    private async Task SendEmailAsync(OutboxMessage m, DateTimeOffset now, CancellationToken ct)
    {
        if (email.Mode == ProviderModes.Off)
            throw new MessageSendException("Email isn't connected on this server. Set Email__ApiKey and Email__FromAddress (docs/WHATSAPP.md), " +
                                           "then send it again.", transient: false);
        var id = await email.SendAsync(new EmailSend(m.ToAddress!, m.RecipientName, m.Subject ?? m.TemplateCode, m.Body, MessageText.Html(m.Body),
            m.Id.ToString("N", CultureInfo.InvariantCulture)), ct);
        m.ProviderMessageId = id;
        if (!await db.EmailMessages.IgnoreQueryFilters().AnyAsync(e => e.OutboxMessageId == m.Id, ct))
            db.EmailMessages.Add(new EmailMessage
            {
                OrganizationId = m.OrganizationId,
                PropertyId = m.PropertyId,
                OutboxMessageId = m.Id,
                Provider = email.Provider,
                ProviderMessageId = id,
                SentAt = now,
            });
    }

    private static string Cut(string s) => s.Length <= 1000 ? s : s[..999] + "…";
}
