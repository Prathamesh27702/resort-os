using Microsoft.AspNetCore.Mvc;
using Resort.Api.Infrastructure;
using Resort.Application.Common;
using Resort.Application.Purchasing;
using Resort.Contracts;

namespace Resort.Api.Endpoints;

/// <summary>Sprint 16 — suppliers and purchasing: suppliers, purchase requests, purchase orders and their approval, goods
/// received into stock, supplier invoices with their input GST, and payments to suppliers.</summary>
public static class PurchasingEndpoints
{
    public static void MapPurchasingEndpoints(this IEndpointRouteBuilder app)
    {
        var p = app.MapGroup("/api/v1/properties/{propertyId:guid}/purchasing").WithTags("Purchasing");

        p.MapGet("/suppliers", (Guid propertyId, SupplierService s, CancellationToken ct) => s.ListAsync(propertyId, ct))
            .RequirePermission(Perm.SupplierRead);
        p.MapPost("/suppliers", async (Guid propertyId, SaveSupplierRequest body, SupplierService s, CancellationToken ct) =>
            {
                var x = await s.SaveAsync(propertyId, null, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/purchasing/suppliers/{x.Id}", x);
            })
            .Validate<SaveSupplierRequest>().RequirePermission(Perm.SupplierManage);
        p.MapPut("/suppliers/{id:guid}", (Guid propertyId, Guid id, SaveSupplierRequest body, SupplierService s, CancellationToken ct) =>
                s.SaveAsync(propertyId, id, body, ct))
            .Validate<SaveSupplierRequest>().RequirePermission(Perm.SupplierManage);

        p.MapGet("/requests", (Guid propertyId, string? status, PurchasingService s, CancellationToken ct) => s.ListRequestsAsync(propertyId, status, ct))
            .RequirePermission(Perm.PurchaseRead);
        p.MapPost("/requests", async (Guid propertyId, CreatePurchaseRequestRequest body, PurchasingService s, CancellationToken ct) =>
            {
                var x = await s.CreateRequestAsync(propertyId, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/purchasing/requests/{x.Id}", x);
            })
            .Validate<CreatePurchaseRequestRequest>().RequirePermission(Perm.PurchaseCreate);
        p.MapPost("/requests/{id:guid}/cancel", (Guid propertyId, Guid id, PurchaseReasonRequest body, PurchasingService s, CancellationToken ct) =>
                s.CancelRequestAsync(propertyId, id, body, ct))
            .Validate<PurchaseReasonRequest>().RequirePermission(Perm.PurchaseCreate);

        var o = p.MapGroup("/orders");
        o.MapGet("", (Guid propertyId, string? status, Guid? supplierId, PurchasingService s, CancellationToken ct) => s.ListOrdersAsync(propertyId, status, supplierId, ct))
            .RequirePermission(Perm.PurchaseRead);
        o.MapGet("/{id:guid}", (Guid propertyId, Guid id, PurchasingService s, CancellationToken ct) => s.GetOrderAsync(propertyId, id, ct))
            .RequirePermission(Perm.PurchaseRead);
        o.MapPost("", async (Guid propertyId, SavePurchaseOrderRequest body, PurchasingService s, CancellationToken ct) =>
            {
                var x = await s.SaveOrderAsync(propertyId, null, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/purchasing/orders/{x.Id}", x);
            })
            .Validate<SavePurchaseOrderRequest>().RequirePermission(Perm.PurchaseCreate);
        o.MapPut("/{id:guid}", (Guid propertyId, Guid id, SavePurchaseOrderRequest body, PurchasingService s, CancellationToken ct) =>
                s.SaveOrderAsync(propertyId, id, body, ct))
            .Validate<SavePurchaseOrderRequest>().RequirePermission(Perm.PurchaseCreate);
        o.MapPost("/{id:guid}/submit", (Guid propertyId, Guid id, PurchaseOrderStepRequest body, PurchasingService s, CancellationToken ct) =>
                s.SubmitAsync(propertyId, id, body, ct))
            .Validate<PurchaseOrderStepRequest>().RequirePermission(Perm.PurchaseCreate);
        o.MapPost("/{id:guid}/approve", (Guid propertyId, Guid id, PurchaseOrderStepRequest body, PurchasingService s, CancellationToken ct) =>
                s.ApproveAsync(propertyId, id, body, ct))
            .Validate<PurchaseOrderStepRequest>().RequirePermission(Perm.PurchaseApprove);
        o.MapPost("/{id:guid}/reject", (Guid propertyId, Guid id, PurchaseReasonRequest body, PurchasingService s, CancellationToken ct) =>
                s.RejectAsync(propertyId, id, body, ct))
            .Validate<PurchaseReasonRequest>().RequirePermission(Perm.PurchaseApprove);
        o.MapPost("/{id:guid}/cancel", (Guid propertyId, Guid id, PurchaseReasonRequest body, PurchasingService s, CancellationToken ct) =>
                s.CancelAsync(propertyId, id, body, ct))
            .Validate<PurchaseReasonRequest>().RequirePermission(Perm.PurchaseCreate);
        o.MapPost("/{id:guid}/close", (Guid propertyId, Guid id, PurchaseReasonRequest body, PurchasingService s, CancellationToken ct) =>
                s.CloseAsync(propertyId, id, body, ct))
            .Validate<PurchaseReasonRequest>().RequirePermission(Perm.PurchaseCreate);
        o.MapPost("/{id:guid}/receipts", async (Guid propertyId, Guid id, ReceiveGoodsRequest body, PurchasingService s, CancellationToken ct) =>
                Results.Created($"/api/v1/properties/{propertyId}/purchasing/orders/{id}", await s.ReceiveAsync(propertyId, id, body, ct)))
            .Validate<ReceiveGoodsRequest>().RequirePermission(Perm.GrnCreate);
        o.MapPost("/{id:guid}/invoices", async (Guid propertyId, Guid id, RecordSupplierInvoiceRequest body, SupplierInvoiceService s, CancellationToken ct) =>
            {
                var x = await s.RecordAsync(propertyId, id, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/purchasing/invoices?supplierId={x.SupplierId}", x);
            })
            .Validate<RecordSupplierInvoiceRequest>().RequirePermission(Perm.SupplierInvoiceManage);

        p.MapGet("/invoices", (Guid propertyId, string? status, Guid? supplierId, DateOnly? from, DateOnly? to, SupplierInvoiceService s, CancellationToken ct) =>
                s.ListAsync(propertyId, status, supplierId, from, to, ct))
            .RequirePermission(Perm.PurchaseRead);
        p.MapPost("/invoices/{id:guid}/payments", async (Guid propertyId, Guid id, [FromHeader(Name = Idempotency.Header)] string? idempotencyKey,
                PaySupplierRequest body, SupplierInvoiceService s, HttpContext http, CancellationToken ct) =>
            {
                var (invoice, replayed) = await s.PayAsync(propertyId, id, idempotencyKey, body, ct);
                if (replayed) http.Response.Headers[Idempotency.ReplayedHeader] = "true";
                return Results.Created($"/api/v1/properties/{propertyId}/purchasing/invoices?supplierId={invoice.SupplierId}", invoice);
            })
            .Validate<PaySupplierRequest>().RequirePermission(Perm.SupplierInvoiceManage);
    }
}
