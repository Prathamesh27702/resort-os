using Resort.Api.Infrastructure;
using Resort.Application.Common;
using Resort.Application.Notifications;
using Resort.Contracts;
using Resort.Infrastructure.Notifications;

namespace Resort.Api.Endpoints;

/// <summary>Sprint 24 — notifications: the messages queued at a property and their delivery (sending a failed one again,
/// sending what is queued now), the templates' wording, the scheduled jobs (on/off, time, Run now) and what they did, each
/// user's bell, and WhatsApp's webhook (anonymous, signed with the app secret). With the fake WhatsApp (no keys, never in
/// production) a message can be marked delivered, read or failed through the real webhook.</summary>
public static class NotificationsEndpoints
{
    /// <summary>A webhook body bigger than this is refused (WhatsApp's are a few kB).</summary>
    private const int MaxWebhookBytes = 256 * 1024;

    public static void MapNotificationsEndpoints(this IEndpointRouteBuilder app)
    {
        var m = app.MapGroup("/api/v1/properties/{propertyId:guid}/messages").WithTags("Messages");
        m.MapGet("/setup", (Guid propertyId, MessageService s, CancellationToken ct) => s.SetupAsync(propertyId, ct))
            .RequirePermission(Perm.MessageRead);
        m.MapGet("", (Guid propertyId, string? status, string? channel, Guid? reservationId, string? q, int? page, int? pageSize, MessageService s,
                CancellationToken ct) => s.ListAsync(propertyId, status, channel, reservationId, q, page ?? 1, pageSize ?? 25, ct))
            .RequirePermission(Perm.MessageRead);
        m.MapGet("/{id:guid}", (Guid propertyId, Guid id, MessageService s, CancellationToken ct) => s.GetAsync(propertyId, id, ct))
            .RequirePermission(Perm.MessageRead);
        m.MapPost("/{id:guid}/retry", (Guid propertyId, Guid id, MessageService s, CancellationToken ct) => s.RetryAsync(propertyId, id, ct))
            .RequirePermission(Perm.MessageSend);
        m.MapPost("/dispatch", async (Guid propertyId, MessageService s, CancellationToken ct) =>
                Results.Ok(new { handled = await s.DispatchNowAsync(propertyId, ct) }))
            .RequirePermission(Perm.MessageSend);
        m.MapPost("/{id:guid}/fake-status", async (Guid propertyId, Guid id, FakeWhatsAppStatusRequest body, MessageService s,
                WhatsAppWebhookService webhooks, IServiceProvider sp, CancellationToken ct) =>
            {
                var fake = sp.GetService<FakeWhatsAppServer>()
                           ?? throw new NotFoundAppException("Test statuses only work while the server uses the fake WhatsApp (no WhatsApp keys set).");
                var msg = await s.GetAsync(propertyId, id, ct);
                if (msg.Summary.Channel != "WHATSAPP" || msg.ProviderMessageId is null)
                    throw new BusinessRuleAppException("NOT_SENT_ON_WHATSAPP", "Only a message WhatsApp accepted can be marked delivered, read or failed.");
                var status = (body.Status ?? "").Trim().ToLowerInvariant();
                if (status is not ("delivered" or "read" or "failed"))
                    throw new FluentValidation.ValidationException([new FluentValidation.Results.ValidationFailure(nameof(body.Status), "The test status is DELIVERED, READ or FAILED.")]);
                var hook = fake.StatusWebhook(msg.ProviderMessageId, status, error: status == "failed" ? ("131026", "Message undeliverable") : null);
                var result = await webhooks.HandleAsync(hook.Body, hook.Signature, ct);
                if (result.StatusCode != 200) throw new BusinessRuleAppException("WEBHOOK_FAILED", result.Ack.Message);
                return await s.GetAsync(propertyId, id, ct);
            })
            .Validate<FakeWhatsAppStatusRequest>().RequirePermission(Perm.MessageSend);

        var t = app.MapGroup("/api/v1/properties/{propertyId:guid}/message-templates").WithTags("Messages");
        t.MapGet("", (Guid propertyId, MessageService s, CancellationToken ct) => s.TemplatesAsync(propertyId, ct))
            .RequirePermission(Perm.MessageRead);
        t.MapPut("/{id:guid}", (Guid propertyId, Guid id, SaveMessageTemplateRequest body, MessageService s, CancellationToken ct) =>
                s.SaveTemplateAsync(propertyId, id, body, ct))
            .Validate<SaveMessageTemplateRequest>().RequirePermission(Perm.IntegrationManage);

        var j = app.MapGroup("/api/v1/properties/{propertyId:guid}/jobs").WithTags("Messages");
        j.MapGet("", (Guid propertyId, MessageService s, CancellationToken ct) => s.JobsAsync(propertyId, ct))
            .RequirePermission(Perm.MessageRead);
        j.MapGet("/runs", (Guid propertyId, string? job, int? limit, MessageService s, CancellationToken ct) => s.RunsAsync(propertyId, job, limit ?? 50, ct))
            .RequirePermission(Perm.MessageRead);
        j.MapPut("/{job}", (Guid propertyId, string job, SaveScheduledJobRequest body, MessageService s, CancellationToken ct) =>
                s.SaveJobAsync(propertyId, job, body, ct))
            .RequirePermission(Perm.IntegrationManage);
        j.MapPost("/{job}/run", (Guid propertyId, string job, MessageService s, CancellationToken ct) => s.RunJobAsync(propertyId, job, ct))
            .RequirePermission(Perm.MessageSend);

        // The bell: each signed-in user's own notifications.
        var n = app.MapGroup("/api/v1/notifications").WithTags("Messages").RequireAuthorization();
        n.MapGet("", (bool? unread, MessageService s, CancellationToken ct) => s.MineAsync(unread ?? false, ct));
        n.MapPost("/{id:guid}/read", (Guid id, MessageService s, CancellationToken ct) => s.ReadAsync(id, ct));
        n.MapPost("/read-all", (MessageService s, CancellationToken ct) => s.ReadAsync(null, ct));

        // WhatsApp → us. No sign-in: Meta's check of the URL (verify token), then statuses signed with the app secret.
        var w = app.MapGroup("/api/v1/webhooks/whatsapp").WithTags("WhatsApp").AllowAnonymous();
        w.MapGet("", (HttpContext http, WhatsAppWebhookService s) =>
            Results.Text(s.Verify(http.Request.Query["hub.mode"], http.Request.Query["hub.verify_token"], http.Request.Query["hub.challenge"]), "text/plain"));
        w.MapPost("", async (HttpContext http, WhatsAppWebhookService s, CancellationToken ct) =>
        {
            if (http.Request.ContentLength > MaxWebhookBytes)
                return Problems.Result(http, 413, "TOO_LARGE", $"A WhatsApp webhook is at most {MaxWebhookBytes / 1024} kB.");
            using var body = new MemoryStream();
            await http.Request.Body.CopyToAsync(body, ct);
            if (body.Length > MaxWebhookBytes)
                return Problems.Result(http, 413, "TOO_LARGE", $"A WhatsApp webhook is at most {MaxWebhookBytes / 1024} kB.");
            var result = await s.HandleAsync(body.ToArray(), http.Request.Headers[WhatsAppWebhookService.SignatureHeader], ct);
            return result.StatusCode == 200 ? Results.Ok(result.Ack) : Problems.Result(http, result.StatusCode, "WEBHOOK_FAILED", result.Ack.Message);
        });
    }
}
