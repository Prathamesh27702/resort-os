using Microsoft.AspNetCore.Mvc;
using Resort.Api.Infrastructure;
using Resort.Application.Common;
using Resort.Application.Payments;
using Resort.Contracts;
using Resort.Infrastructure.Payments;

namespace Resort.Api.Endpoints;

/// <summary>Sprint 11 — Razorpay. Staff create payment links for a booking (payment.create, Idempotency-Key), list and
/// cancel them; the guest's page (anonymous, by the link's token) shows the status and hands Checkout what it needs; and
/// Razorpay's webhook (anonymous, HMAC-signed) is the only thing that records a payment. With the fake Razorpay (no keys,
/// never in production) the guest's page can pay or fail a link with test buttons.</summary>
public static class RazorpayEndpoints
{
    /// <summary>A webhook body bigger than this is refused (Razorpay's are a few kB).</summary>
    private const int MaxWebhookBytes = 256 * 1024;

    public static void MapRazorpayEndpoints(this IEndpointRouteBuilder app)
    {
        var p = app.MapGroup("/api/v1/properties/{propertyId:guid}/payments/razorpay/orders").WithTags("Razorpay");
        p.MapPost("", async (Guid propertyId, [FromHeader(Name = Idempotency.Header)] string? idempotencyKey, CreatePaymentLinkRequest body,
                PaymentLinkService s, HttpContext http, CancellationToken ct) =>
            {
                var (link, replayed) = await s.CreateAsync(propertyId, idempotencyKey, body, ct);
                if (replayed) http.Response.Headers[Idempotency.ReplayedHeader] = "true";
                return Results.Created($"/api/v1/properties/{propertyId}/payments/razorpay/orders/{link.Id}", link);
            })
            .Validate<CreatePaymentLinkRequest>().RequirePermission(Perm.PaymentCreate);
        p.MapGet("", (Guid propertyId, Guid? reservationId, PaymentLinkService s, CancellationToken ct) => s.ListAsync(propertyId, reservationId, ct))
            .RequirePermission(Perm.PaymentRead);
        p.MapGet("/{id:guid}", (Guid propertyId, Guid id, PaymentLinkService s, CancellationToken ct) => s.GetAsync(propertyId, id, ct))
            .RequirePermission(Perm.PaymentRead);
        p.MapPost("/{id:guid}/cancel", (Guid propertyId, Guid id, CancelPaymentLinkRequest body, PaymentLinkService s, CancellationToken ct) =>
                s.CancelAsync(propertyId, id, body, ct))
            .Validate<CancelPaymentLinkRequest>().RequirePermission(Perm.PaymentCreate);

        // Razorpay → us. No sign-in: the HMAC signature over the raw body is the proof.
        app.MapPost("/api/v1/webhooks/razorpay", async (HttpContext http, RazorpayWebhookService s, CancellationToken ct) =>
            {
                if (http.Request.ContentLength > MaxWebhookBytes)
                    return Problems.Result(http, 413, "TOO_LARGE", $"A Razorpay webhook is at most {MaxWebhookBytes / 1024} kB.");
                using var body = new MemoryStream();
                await http.Request.Body.CopyToAsync(body, ct);
                if (body.Length > MaxWebhookBytes)
                    return Problems.Result(http, 413, "TOO_LARGE", $"A Razorpay webhook is at most {MaxWebhookBytes / 1024} kB.");
                var result = await s.HandleAsync(body.ToArray(), http.Request.Headers[RazorpayWebhookService.SignatureHeader],
                    http.Request.Headers[RazorpayWebhookService.EventIdHeader], ct);
                return result.StatusCode == 200 ? Results.Ok(result.Ack) : Problems.Result(http, result.StatusCode, "WEBHOOK_FAILED", result.Ack.Message);
            })
            .WithTags("Razorpay").AllowAnonymous();

        // The guest's page. No sign-in: the link's token (32 random bytes) is the proof.
        var pay = app.MapGroup("/api/v1/pay/{token}").WithTags("Pay page").AllowAnonymous();
        pay.MapGet("", (string token, PaymentLinkService s, CancellationToken ct) => s.PageAsync(token, ct));
        pay.MapPost("/callback", (string token, PayCallbackRequest body, PaymentLinkService s, CancellationToken ct) => s.CallbackAsync(token, body, ct));
        pay.MapPost("/fake-checkout", async (string token, FakeCheckoutRequest body, PaymentLinkService links, RazorpayWebhookService webhooks,
                IServiceProvider sp, CancellationToken ct) =>
            {
                var fake = sp.GetService<FakeRazorpayServer>()
                           ?? throw new NotFoundAppException("Test payments only work while the server uses the fake Razorpay (no Razorpay keys set). Use Razorpay Checkout.");
                var link = await links.FindAsync(token, ct);
                var page = await links.PageAsync(token, ct);
                if (page.Status != "AWAITING_PAYMENT")
                    throw new BusinessRuleAppException("NOT_PAYABLE", page.Message);
                var method = string.IsNullOrWhiteSpace(body.Method) ? "upi" : body.Method.Trim().ToLowerInvariant();
                var (_, hooks) = fake.Pay(link.GatewayOrderId, method, body.Succeed,
                    "Payment was declined by the bank (test). Try another card or UPI app.");
                foreach (var hook in hooks)
                {
                    var result = await webhooks.HandleAsync(hook.Body, hook.Signature, hook.EventId, ct);
                    if (result.StatusCode != 200) throw new BusinessRuleAppException("WEBHOOK_FAILED", result.Ack.Message);
                }
                return await links.PageAsync(token, ct);
            });
    }
}
