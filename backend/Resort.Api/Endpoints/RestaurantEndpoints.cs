using Microsoft.AspNetCore.Mvc;
using Resort.Api.Infrastructure;
using Resort.Application.Common;
using Resort.Application.Restaurant;
using Resort.Contracts;

namespace Resort.Api.Endpoints;

/// <summary>Sprint 14 — the restaurant: the menu and tables, orders (taken on a phone, sent to the kitchen, settled by
/// payment or a room charge) and the kitchen display. Every change to an order carries the order's version (409 when
/// another waiter saved first). Opening and settling an order may carry an Idempotency-Key.</summary>
public static class RestaurantEndpoints
{
    public static void MapRestaurantEndpoints(this IEndpointRouteBuilder app)
    {
        var p = app.MapGroup("/api/v1/properties/{propertyId:guid}");

        // ---------------------------------------------------------------- menu and tables
        var menu = p.MapGroup("/menu").WithTags("Restaurant menu");
        menu.MapGet("", (Guid propertyId, bool? all, MenuService s, CancellationToken ct) => s.GetAsync(propertyId, all ?? false, ct))
            .RequirePermission(Perm.MenuRead);
        menu.MapPost("/categories", async (Guid propertyId, SaveMenuCategoryRequest body, MenuService s, CancellationToken ct) =>
            {
                var c = await s.SaveCategoryAsync(propertyId, null, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/menu/categories/{c.Id}", c);
            })
            .Validate<SaveMenuCategoryRequest>().RequirePermission(Perm.MenuCreate);
        menu.MapPut("/categories/{id:guid}", (Guid propertyId, Guid id, SaveMenuCategoryRequest body, MenuService s, CancellationToken ct) =>
                s.SaveCategoryAsync(propertyId, id, body, ct))
            .Validate<SaveMenuCategoryRequest>().RequirePermission(Perm.MenuUpdate);
        menu.MapPost("/items", async (Guid propertyId, SaveMenuItemRequest body, MenuService s, CancellationToken ct) =>
            {
                var i = await s.SaveItemAsync(propertyId, null, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/menu/items/{i.Id}", i);
            })
            .Validate<SaveMenuItemRequest>().RequirePermission(Perm.MenuCreate);
        menu.MapPut("/items/{id:guid}", (Guid propertyId, Guid id, SaveMenuItemRequest body, MenuService s, CancellationToken ct) =>
                s.SaveItemAsync(propertyId, id, body, ct))
            .Validate<SaveMenuItemRequest>().RequirePermission(Perm.MenuUpdate);
        menu.MapPost("/items/{id:guid}/availability", (Guid propertyId, Guid id, SetAvailabilityRequest body, MenuService s, CancellationToken ct) =>
                s.SetAvailabilityAsync(propertyId, id, body, ct))
            .RequirePermission(Perm.MenuUpdate);
        menu.MapGet("/tables", (Guid propertyId, MenuService s, CancellationToken ct) => s.TablesAsync(propertyId, ct))
            .RequirePermission(Perm.MenuRead);
        menu.MapPost("/tables", async (Guid propertyId, SaveRestaurantTableRequest body, MenuService s, CancellationToken ct) =>
            {
                var t = await s.SaveTableAsync(propertyId, null, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/menu/tables/{t.Id}", t);
            })
            .Validate<SaveRestaurantTableRequest>().RequirePermission(Perm.MenuCreate);
        menu.MapPut("/tables/{id:guid}", (Guid propertyId, Guid id, SaveRestaurantTableRequest body, MenuService s, CancellationToken ct) =>
                s.SaveTableAsync(propertyId, id, body, ct))
            .Validate<SaveRestaurantTableRequest>().RequirePermission(Perm.MenuUpdate);

        // ---------------------------------------------------------------- orders
        var o = p.MapGroup("/orders").WithTags("Restaurant orders");
        o.MapGet("", (Guid propertyId, DateOnly? date, string? status, OrderService s, CancellationToken ct) => s.ListAsync(propertyId, date, status, ct))
            .RequirePermission(Perm.PosOrderRead);
        o.MapGet("/floor", (Guid propertyId, OrderService s, CancellationToken ct) => s.FloorAsync(propertyId, ct))
            .RequirePermission(Perm.PosOrderRead);
        o.MapGet("/rooms", (Guid propertyId, OrderService s, CancellationToken ct) => s.InHouseRoomsAsync(propertyId, ct))
            .RequirePermission(Perm.PosOrderRead);
        o.MapPost("", async (Guid propertyId, [FromHeader(Name = Idempotency.Header)] string? idempotencyKey, CreateOrderRequest body,
                OrderService s, HttpContext http, CancellationToken ct) =>
            {
                var (order, replayed) = await s.CreateAsync(propertyId, idempotencyKey, body, ct);
                if (replayed) http.Response.Headers[Idempotency.ReplayedHeader] = "true";
                return Results.Created($"/api/v1/properties/{propertyId}/orders/{order.Id}", order);
            })
            .Validate<CreateOrderRequest>().RequirePermission(Perm.PosOrderCreate);
        o.MapGet("/{id:guid}", (Guid propertyId, Guid id, OrderService s, CancellationToken ct) => s.GetAsync(propertyId, id, ct))
            .RequirePermission(Perm.PosOrderRead);
        o.MapPut("/{id:guid}", (Guid propertyId, Guid id, UpdateOrderRequest body, OrderService s, CancellationToken ct) =>
                s.UpdateAsync(propertyId, id, body, ct))
            .Validate<UpdateOrderRequest>().RequirePermission(Perm.PosOrderUpdate);
        o.MapPost("/{id:guid}/items", (Guid propertyId, Guid id, AddOrderItemsRequest body, OrderService s, CancellationToken ct) =>
                s.AddItemsAsync(propertyId, id, body, ct))
            .Validate<AddOrderItemsRequest>().RequirePermission(Perm.PosOrderUpdate);
        o.MapPut("/{id:guid}/items/{itemId:guid}", (Guid propertyId, Guid id, Guid itemId, UpdateOrderItemRequest body, OrderService s, CancellationToken ct) =>
                s.UpdateItemAsync(propertyId, id, itemId, body, ct))
            .Validate<UpdateOrderItemRequest>().RequirePermission(Perm.PosOrderUpdate);
        o.MapDelete("/{id:guid}/items/{itemId:guid}", (Guid propertyId, Guid id, Guid itemId, uint? version, OrderService s, CancellationToken ct) =>
                s.RemoveItemAsync(propertyId, id, itemId, version, ct))
            .RequirePermission(Perm.PosOrderUpdate);
        o.MapPost("/{id:guid}/items/{itemId:guid}/void", (Guid propertyId, Guid id, Guid itemId, VoidRequest body, OrderService s, CancellationToken ct) =>
                s.VoidItemAsync(propertyId, id, itemId, body, ct))
            .Validate<VoidRequest>().RequirePermission(Perm.PosOrderVoid);
        o.MapPost("/{id:guid}/send", (Guid propertyId, Guid id, SendToKitchenRequest? body, OrderService s, CancellationToken ct) =>
                s.SendAsync(propertyId, id, body ?? new SendToKitchenRequest(null, null), ct))
            .Validate<SendToKitchenRequest>().RequirePermission(Perm.PosOrderUpdate);
        o.MapPost("/{id:guid}/kots/{kotId:guid}/serve", (Guid propertyId, Guid id, Guid kotId, OrderService s, CancellationToken ct) =>
                s.ServeAsync(propertyId, id, kotId, ct))
            .RequirePermission(Perm.PosOrderUpdate);
        o.MapPost("/{id:guid}/discount", (Guid propertyId, Guid id, DiscountRequest body, OrderService s, CancellationToken ct) =>
                s.DiscountAsync(propertyId, id, body, ct))
            .Validate<DiscountRequest>().RequirePermission(Perm.PosDiscount);
        o.MapPost("/{id:guid}/void", (Guid propertyId, Guid id, VoidRequest body, OrderService s, CancellationToken ct) =>
                s.VoidAsync(propertyId, id, body, ct))
            .Validate<VoidRequest>().RequirePermission(Perm.PosOrderVoid);
        o.MapPost("/{id:guid}/pay", async (Guid propertyId, Guid id, [FromHeader(Name = Idempotency.Header)] string? idempotencyKey, PayOrderRequest body,
                OrderService s, HttpContext http, CancellationToken ct) =>
            {
                var (order, replayed) = await s.PayAsync(propertyId, id, idempotencyKey, body, ct);
                if (replayed) http.Response.Headers[Idempotency.ReplayedHeader] = "true";
                return order;
            })
            .Validate<PayOrderRequest>().RequirePermission(Perm.PaymentCreate);
        o.MapPost("/{id:guid}/charge-to-room", async (Guid propertyId, Guid id, [FromHeader(Name = Idempotency.Header)] string? idempotencyKey,
                ChargeToRoomRequest body, OrderService s, HttpContext http, CancellationToken ct) =>
            {
                var (order, replayed) = await s.ChargeToRoomAsync(propertyId, id, idempotencyKey, body, ct);
                if (replayed) http.Response.Headers[Idempotency.ReplayedHeader] = "true";
                return order;
            })
            .Validate<ChargeToRoomRequest>().RequirePermission(Perm.FolioPostCharge);

        // ---------------------------------------------------------------- kitchen display
        var k = p.MapGroup("/kitchen").WithTags("Kitchen");
        k.MapGet("/kots", (Guid propertyId, OrderService s, CancellationToken ct) => s.KitchenAsync(propertyId, ct))
            .RequirePermission(Perm.KotRead);
        k.MapPost("/kots/{id:guid}/start", (Guid propertyId, Guid id, KotStepRequest? body, OrderService s, CancellationToken ct) =>
                s.KotStepAsync(propertyId, id, "start", body ?? new KotStepRequest(null), ct))
            .RequirePermission(Perm.KotUpdate);
        k.MapPost("/kots/{id:guid}/ready", (Guid propertyId, Guid id, KotStepRequest? body, OrderService s, CancellationToken ct) =>
                s.KotStepAsync(propertyId, id, "ready", body ?? new KotStepRequest(null), ct))
            .RequirePermission(Perm.KotUpdate);
    }
}
