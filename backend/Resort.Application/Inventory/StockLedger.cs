using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Domain.Inventory;
using Resort.Domain.Restaurant;

namespace Resort.Application.Inventory;

/// <summary>
/// Writes the stock ledger (Sprint 15). Stock never changes except by adding a movement: the database adds it to the
/// item's level at that location in the same statement (trigger on stock_movements), so on hand is always the sum of the
/// movements.
///
/// Every change locks the rows of the items it touches (SELECT … FOR UPDATE, in id order so two changes never wait on each
/// other in a circle). Two people writing off the same item at once both count, and a check such as "is there enough to
/// write off" sees the other's change first.
/// </summary>
public sealed class StockLedger(IAppDbContext db, ICurrentUser caller, TimeProvider clock)
{
    /// <summary>Locks the items (in id order) until the transaction ends.</summary>
    public async Task LockItemsAsync(Guid propertyId, IEnumerable<Guid> itemIds, CancellationToken ct)
    {
        var ids = itemIds.Distinct().Order().ToArray();
        if (ids.Length == 0) return;
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM inventory_items WHERE id = ANY({ids}) AND property_id = {propertyId} AND organization_id = {caller.RequireOrganizationId()} ORDER BY id FOR UPDATE", ct);
    }

    /// <summary>On hand of an item at a location (0 when nothing ever moved there). Read after <see cref="LockItemsAsync"/>.</summary>
    public async Task<decimal> OnHandAsync(Guid itemId, Guid locationId, CancellationToken ct) =>
        await db.StockLevels.AsNoTracking().Where(l => l.ItemId == itemId && l.LocationId == locationId).Select(l => (decimal?)l.OnHand).SingleOrDefaultAsync(ct) ?? 0m;

    /// <summary>On hand of an item across all its locations.</summary>
    public async Task<decimal> TotalOnHandAsync(Guid itemId, CancellationToken ct) =>
        await db.StockLevels.AsNoTracking().Where(l => l.ItemId == itemId).SumAsync(l => (decimal?)l.OnHand, ct) ?? 0m;

    /// <summary>Adds a movement (not saved). Quantity in the item's unit, signed; valued at <paramref name="unitCost"/>.</summary>
    public StockMovement Add(InventoryItem item, Guid locationId, string type, decimal quantity, decimal unitCost, string? note = null,
        Guid? adjustmentId = null, Guid? countId = null, Guid? orderItemId = null, Guid? kotId = null, Guid? grnItemId = null)
    {
        var m = new StockMovement
        {
            OrganizationId = item.OrganizationId,
            PropertyId = item.PropertyId,
            ItemId = item.Id,
            LocationId = locationId,
            MovementType = type,
            Quantity = quantity,
            UnitCost = unitCost,
            Value = InventoryMath.Value(quantity, unitCost),
            Note = note,
            AdjustmentId = adjustmentId,
            CountId = countId,
            OrderItemId = orderItemId,
            KotId = kotId,
            GrnItemId = grnItemId,
            CreatedAt = clock.GetUtcNow(),
            CreatedBy = caller.UserId,
        };
        db.StockMovements.Add(m);
        return m;
    }

    /// <summary>
    /// A kitchen ticket was served: each live line uses its recipe's ingredients from the kitchen, at the items' cost now.
    /// The recipe of the line's portion wins over the dish's; a dish without a recipe uses nothing. Stock may go below zero
    /// here (the restaurant never stops serving because a count is behind); the stock screen marks it for a count. Nothing
    /// happens when the property has no kitchen location. Called inside the serving transaction; saved with it.
    /// </summary>
    public async Task ConsumeAsync(Guid propertyId, Kot kot, string orderNumber, CancellationToken ct)
    {
        var kitchen = await db.StockLocations.AsNoTracking().SingleOrDefaultAsync(l => l.PropertyId == propertyId && l.IsKitchen, ct);
        if (kitchen is null) return;
        var lines = await db.OrderItems.AsNoTracking().Where(i => i.KotId == kot.Id && i.Status == OrderItemStatuses.Sent)
            .OrderBy(i => i.CreatedAt).ToListAsync(ct);
        if (lines.Count == 0) return;
        var dishIds = lines.Select(l => l.MenuItemId).Distinct().ToList();
        var recipes = await db.Recipes.AsNoTracking().Include(r => r.Ingredients)
            .Where(r => r.PropertyId == propertyId && r.IsActive && dishIds.Contains(r.MenuItemId)).ToListAsync(ct);
        var used = lines.Select(l => (Line: l, Recipe: RecipeFor(recipes, l.MenuItemId, l.VariantId))).Where(x => x.Recipe is not null).ToList();
        if (used.Count == 0) return;

        var itemIds = used.SelectMany(x => x.Recipe!.Ingredients.Select(i => i.ItemId)).Distinct().ToList();
        await LockItemsAsync(propertyId, itemIds, ct);
        var items = await db.InventoryItems.Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var units = await db.InventoryUnits.AsNoTracking().ToDictionaryAsync(u => u.Id, ct);
        foreach (var (line, recipe) in used)
            foreach (var ing in recipe!.Ingredients.OrderBy(i => i.SortOrder))
            {
                var item = items[ing.ItemId];
                var quantity = InventoryMath.Convert(ing.Quantity, units[ing.UnitId].Factor, units[item.UnitId].Factor) * line.Quantity;
                if (quantity == 0) continue;
                Add(item, kitchen.Id, MovementTypes.Consumption, -quantity, item.UnitCost,
                    $"{orderNumber} · {kot.Number} · {line.Quantity}× {RestaurantMath.Label(line.Name, line.VariantName)}", orderItemId: line.Id, kotId: kot.Id);
            }
    }

    /// <summary>The recipe a dish (or one of its portions) is made by: the portion's own, else the dish's.</summary>
    public static Recipe? RecipeFor(IReadOnlyList<Recipe> recipes, Guid menuItemId, Guid? variantId) =>
        (variantId is null ? null : recipes.FirstOrDefault(r => r.MenuItemId == menuItemId && r.VariantId == variantId && r.IsActive))
        ?? recipes.FirstOrDefault(r => r.MenuItemId == menuItemId && r.VariantId == null && r.IsActive);
}
