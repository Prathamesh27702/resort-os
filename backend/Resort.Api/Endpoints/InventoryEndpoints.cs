using Resort.Api.Infrastructure;
using Resort.Application.Common;
using Resort.Application.Inventory;
using Resort.Contracts;

namespace Resort.Api.Endpoints;

/// <summary>Sprint 15 — inventory and recipe costing: stocked items and their stores, the stock ledger, adjustments with a
/// reason, physical counts, recipes and the food-cost report. Served dishes use their recipes' stock automatically (see
/// the restaurant's serve endpoint).</summary>
public static class InventoryEndpoints
{
    public static void MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        var p = app.MapGroup("/api/v1/properties/{propertyId:guid}/inventory").WithTags("Inventory");

        p.MapGet("", (Guid propertyId, InventoryService s, CancellationToken ct) => s.GetAsync(propertyId, ct))
            .RequirePermission(Perm.InventoryRead);
        p.MapGet("/movements", (Guid propertyId, Guid? itemId, string? type, DateOnly? from, DateOnly? to, int? take, InventoryService s, CancellationToken ct) =>
                s.MovementsAsync(propertyId, itemId, type, from, to, take, ct))
            .RequirePermission(Perm.InventoryRead);

        p.MapPost("/categories", async (Guid propertyId, SaveInventoryCategoryRequest body, InventoryService s, CancellationToken ct) =>
            {
                var c = await s.SaveCategoryAsync(propertyId, null, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/inventory/categories/{c.Id}", c);
            })
            .Validate<SaveInventoryCategoryRequest>().RequirePermission(Perm.InventoryManage);
        p.MapPut("/categories/{id:guid}", (Guid propertyId, Guid id, SaveInventoryCategoryRequest body, InventoryService s, CancellationToken ct) =>
                s.SaveCategoryAsync(propertyId, id, body, ct))
            .Validate<SaveInventoryCategoryRequest>().RequirePermission(Perm.InventoryManage);
        p.MapPost("/locations", async (Guid propertyId, SaveStockLocationRequest body, InventoryService s, CancellationToken ct) =>
            {
                var l = await s.SaveLocationAsync(propertyId, null, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/inventory/locations/{l.Id}", l);
            })
            .Validate<SaveStockLocationRequest>().RequirePermission(Perm.InventoryManage);
        p.MapPut("/locations/{id:guid}", (Guid propertyId, Guid id, SaveStockLocationRequest body, InventoryService s, CancellationToken ct) =>
                s.SaveLocationAsync(propertyId, id, body, ct))
            .Validate<SaveStockLocationRequest>().RequirePermission(Perm.InventoryManage);
        p.MapPost("/items", async (Guid propertyId, SaveInventoryItemRequest body, InventoryService s, CancellationToken ct) =>
            {
                var i = await s.SaveItemAsync(propertyId, null, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/inventory/items/{i.Id}", i);
            })
            .Validate<SaveInventoryItemRequest>().RequirePermission(Perm.InventoryManage);
        p.MapPut("/items/{id:guid}", (Guid propertyId, Guid id, SaveInventoryItemRequest body, InventoryService s, CancellationToken ct) =>
                s.SaveItemAsync(propertyId, id, body, ct))
            .Validate<SaveInventoryItemRequest>().RequirePermission(Perm.InventoryManage);

        p.MapPost("/adjustments", async (Guid propertyId, CreateStockAdjustmentRequest body, InventoryService s, CancellationToken ct) =>
            {
                var a = await s.AdjustAsync(propertyId, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/inventory/adjustments/{a.Id}", a);
            })
            .Validate<CreateStockAdjustmentRequest>().RequirePermission(Perm.InventoryAdjust);
        p.MapPost("/counts", async (Guid propertyId, CreateStockCountRequest body, InventoryService s, CancellationToken ct) =>
                Results.Created($"/api/v1/properties/{propertyId}/inventory/movements?type=COUNT", await s.CountAsync(propertyId, body, ct)))
            .Validate<CreateStockCountRequest>().RequirePermission(Perm.InventoryAdjust);

        p.MapGet("/recipes", (Guid propertyId, RecipeService s, CancellationToken ct) => s.ListAsync(propertyId, ct))
            .RequirePermission(Perm.RecipeRead);
        p.MapPut("/recipes", (Guid propertyId, SaveRecipeRequest body, RecipeService s, CancellationToken ct) => s.SaveAsync(propertyId, body, ct))
            .Validate<SaveRecipeRequest>().RequirePermission(Perm.RecipeManage);
        p.MapGet("/food-cost", (Guid propertyId, DateOnly? from, DateOnly? to, RecipeService s, CancellationToken ct) => s.FoodCostAsync(propertyId, from, to, ct))
            .RequirePermission(Perm.RecipeRead);
    }
}
