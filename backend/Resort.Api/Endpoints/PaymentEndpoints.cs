using Microsoft.AspNetCore.Mvc;
using Resort.Api.Infrastructure;
using Resort.Application.Common;
using Resort.Application.Payments;
using Resort.Contracts;

namespace Resort.Api.Endpoints;

/// <summary>Sprint 10 — payments, refunds and the cash drawer. Take a payment (Idempotency-Key), put money on account onto
/// folios, refund (payment.refund, a reason and an Idempotency-Key), void (payment.void and a reason); open and close the
/// cash drawer, and approve a short or over drawer (cash_drawer.approve_variance). Nothing is edited or deleted.</summary>
public static class PaymentEndpoints
{
    public static void MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        var p = app.MapGroup("/api/v1/properties/{propertyId:guid}").WithTags("Payments");

        p.MapGet("/payments", (Guid propertyId, string? q, string? method, string? status, DateOnly? from, DateOnly? to, Guid? reservationId,
                int? page, int? pageSize, PaymentService s, CancellationToken ct) =>
                s.ListAsync(propertyId, new PaymentService.ListQuery(q, method, status, from, to, reservationId, page ?? 1, pageSize ?? 50), ct))
            .RequirePermission(Perm.PaymentRead);
        p.MapGet("/payments/{id:guid}", (Guid propertyId, Guid id, PaymentService s, CancellationToken ct) => s.GetAsync(propertyId, id, ct))
            .RequirePermission(Perm.PaymentRead);
        p.MapPost("/payments", async (Guid propertyId, [FromHeader(Name = Idempotency.Header)] string? idempotencyKey,
                CreatePaymentRequest body, PaymentService s, HttpContext http, CancellationToken ct) =>
            {
                var (payment, replayed) = await s.CreateAsync(propertyId, idempotencyKey, body, ct);
                if (replayed) http.Response.Headers[Idempotency.ReplayedHeader] = "true";
                return Results.Created($"/api/v1/properties/{propertyId}/payments/{payment.Id}", payment);
            })
            .Validate<CreatePaymentRequest>().RequirePermission(Perm.PaymentCreate);
        p.MapPost("/payments/{id:guid}/allocate", (Guid propertyId, Guid id, AllocatePaymentRequest body, PaymentService s, CancellationToken ct) =>
                s.AllocateAsync(propertyId, id, body, ct))
            .Validate<AllocatePaymentRequest>().RequirePermission(Perm.PaymentCreate);
        p.MapPost("/payments/{id:guid}/refund", async (Guid propertyId, Guid id, [FromHeader(Name = Idempotency.Header)] string? idempotencyKey,
                RefundPaymentRequest body, PaymentService s, HttpContext http, CancellationToken ct) =>
            {
                var (payment, replayed) = await s.RefundAsync(propertyId, id, idempotencyKey, body, ct);
                if (replayed) http.Response.Headers[Idempotency.ReplayedHeader] = "true";
                return Results.Ok(payment);
            })
            .Validate<RefundPaymentRequest>().RequirePermission(Perm.PaymentRefund);
        p.MapPost("/payments/{id:guid}/void", (Guid propertyId, Guid id, VoidPaymentRequest body, PaymentService s, CancellationToken ct) =>
                s.VoidAsync(propertyId, id, body, ct))
            .Validate<VoidPaymentRequest>().RequirePermission(Perm.PaymentVoid);

        var d = p.MapGroup("/cash-drawer").WithTags("Cash drawer");
        d.MapGet("", (Guid propertyId, Guid? drawerId, CashDrawerService s, CancellationToken ct) => s.GetAsync(propertyId, drawerId, ct))
            .RequirePermission(Perm.PaymentRead);
        d.MapGet("/sessions/{id:guid}", (Guid propertyId, Guid id, CashDrawerService s, CancellationToken ct) => s.GetSessionAsync(propertyId, id, ct))
            .RequirePermission(Perm.PaymentRead);
        d.MapPost("/open", async (Guid propertyId, OpenCashDrawerRequest body, CashDrawerService s, CancellationToken ct) =>
            {
                var session = await s.OpenAsync(propertyId, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/cash-drawer/sessions/{session.Id}", session);
            })
            .Validate<OpenCashDrawerRequest>().RequirePermission(Perm.CashDrawerOperate);
        d.MapPost("/close", (Guid propertyId, CloseCashDrawerRequest body, CashDrawerService s, CancellationToken ct) => s.CloseAsync(propertyId, body, ct))
            .Validate<CloseCashDrawerRequest>().RequirePermission(Perm.CashDrawerOperate);
        d.MapPost("/sessions/{id:guid}/approve-variance", (Guid propertyId, Guid id, ApproveVarianceRequest body, CashDrawerService s,
                CancellationToken ct) => s.ApproveVarianceAsync(propertyId, id, body, ct))
            .Validate<ApproveVarianceRequest>().RequirePermission(Perm.CashDrawerApproveVariance);
    }
}
