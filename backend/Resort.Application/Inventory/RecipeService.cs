using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Reservations;
using Resort.Contracts;
using Resort.Domain.Inventory;
using Resort.Domain.Restaurant;

namespace Resort.Application.Inventory;

/// <summary>
/// Recipes and food cost (Sprint 15, brief §37). A recipe says what one portion of a dish uses from stock; its cost is the
/// ingredients at the items' unit cost now. Food cost % = cost / price, contribution = price − cost, margin % =
/// contribution / price (menu prices are before GST). When a dish is served, its recipe's ingredients leave the kitchen
/// store (<see cref="StockLedger.ConsumeAsync"/>), valued at the cost of the time, which the food-cost report adds up.
/// </summary>
public sealed class RecipeService(IAppDbContext db, ICurrentUser caller, AuditContext audit, TimeProvider clock, InventoryService inventory)
{
    /// <summary>Every dish on the menu (each portion of a dish that has portions) with the recipe it is made by and its cost.</summary>
    public async Task<RecipeListDto> ListAsync(Guid propertyId, CancellationToken ct)
    {
        await inventory.RequirePropertyAsync(propertyId, ct);
        var costing = await CostingAsync(propertyId, ct);
        return new RecipeListDto(costing.Dishes, await inventory.UnitsAsync(ct));
    }

    /// <summary>
    /// Saves the recipe of a dish or of one of its portions (recipe.manage; audited) and returns the dish's costed rows. Each ingredient is an active stock item
    /// of this property, once, in a unit of the item's kind (g or kg for an item kept in kg). No ingredients takes the
    /// recipe off: the dish then uses nothing from stock (or its dish's recipe, for a portion).
    /// </summary>
    public async Task<IReadOnlyList<CostedDishDto>> SaveAsync(Guid propertyId, SaveRecipeRequest r, CancellationToken ct)
    {
        await inventory.RequirePropertyAsync(propertyId, ct);
        var dish = await db.MenuItems.AsNoTracking().Include(i => i.Variants).SingleOrDefaultAsync(i => i.Id == r.MenuItemId && i.PropertyId == propertyId, ct)
                   ?? throw ReservationService.Field(nameof(r.MenuItemId), "That dish isn't on this property's menu. Reload and choose again.");
        if (r.VariantId is { } vid && !dish.Variants.Any(v => v.Id == vid && v.IsActive))
            throw ReservationService.Field(nameof(r.VariantId), $"That portion isn't on {dish.Name} any more. Reload and choose again.");
        var label = RestaurantMath.Label(dish.Name, dish.Variants.FirstOrDefault(v => v.Id == r.VariantId)?.Name);

        var itemIds = r.Ingredients.Select(i => i.ItemId).Distinct().ToList();
        var items = await db.InventoryItems.AsNoTracking().Where(i => itemIds.Contains(i.Id) && i.PropertyId == propertyId).ToDictionaryAsync(i => i.Id, ct);
        var units = await db.InventoryUnits.AsNoTracking().ToDictionaryAsync(u => u.Id, ct);
        for (var n = 0; n < r.Ingredients.Count; n++)
        {
            var x = r.Ingredients[n];
            if (!items.TryGetValue(x.ItemId, out var item))
                throw ReservationService.Field($"Ingredients[{n}].ItemId", "That stock item doesn't exist at this property. Reload and choose again.");
            if (!item.IsActive) throw ReservationService.Field($"Ingredients[{n}].ItemId", $"{item.Name} is switched off. Switch it on, or choose another item.");
            if (r.Ingredients.Take(n).Any(y => y.ItemId == x.ItemId))
                throw ReservationService.Field($"Ingredients[{n}].ItemId", $"{item.Name} is in the recipe twice. Put the whole quantity on one line.");
            if (!units.TryGetValue(x.UnitId, out var unit))
                throw ReservationService.Field($"Ingredients[{n}].UnitId", "Choose the unit of the quantity.");
            var own = units[item.UnitId];
            if (unit.Dimension != own.Dimension)
                throw ReservationService.Field($"Ingredients[{n}].UnitId",
                    $"{item.Name} is kept in {own.Code}, so its quantity must be in {string.Join(" or ", units.Values.Where(u => u.Dimension == own.Dimension).OrderBy(u => u.SortOrder).Select(u => u.Code))}.");
            if (InventoryMath.Convert(x.Quantity, unit.Factor, own.Factor) == 0)
                throw ReservationService.Field($"Ingredients[{n}].Quantity",
                    $"{InventoryMath.Quantity(x.Quantity, unit.Code)} is less than a thousandth of a {own.Code} of {item.Name}. Enter a larger quantity, or leave it out.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var recipe = await db.Recipes.Include(x => x.Ingredients)
            .SingleOrDefaultAsync(x => x.PropertyId == propertyId && x.MenuItemId == dish.Id && x.VariantId == r.VariantId, ct);
        if (recipe is not null && r.Version is { } v && recipe.Version != v)
            throw new ConflictAppException("CONCURRENCY_CONFLICT", $"Someone else changed the recipe of {label} a moment ago. Reload to see it, then make your change again.");
        if (recipe is not null && r.Version is { } ok) ConcurrencyGuard.ExpectVersion(db, recipe, ok);
        if (recipe is null)
        {
            if (r.Ingredients.Count == 0) throw ReservationService.Field(nameof(r.Ingredients), $"Add at least one ingredient to the recipe of {label}.");
            recipe = new Recipe { OrganizationId = caller.RequireOrganizationId(), PropertyId = propertyId, MenuItemId = dish.Id, VariantId = r.VariantId };
            db.Recipes.Add(recipe);
        }
        foreach (var old in recipe.Ingredients) db.RecipeIngredients.Remove(old);
        recipe.Ingredients.Clear();
        var now = clock.GetUtcNow();
        for (var n = 0; n < r.Ingredients.Count; n++)
        {
            var x = r.Ingredients[n];
            var ing = new RecipeIngredient
            {
                OrganizationId = recipe.OrganizationId,
                RecipeId = recipe.Id,
                ItemId = x.ItemId,
                Quantity = x.Quantity,
                UnitId = x.UnitId,
                SortOrder = n,
                CreatedAt = now,
            };
            recipe.Ingredients.Add(ing);
            db.RecipeIngredients.Add(ing);
        }
        recipe.Note = ReservationService.Clean(r.Note);
        recipe.IsActive = r.Ingredients.Count > 0;
        recipe.UpdatedAt = now;   // moves the version on even when only the ingredients changed
        audit.Reason = recipe.IsActive
            ? $"Recipe of {label}: {string.Join(", ", r.Ingredients.Select(i => $"{InventoryMath.Quantity(i.Quantity, units[i.UnitId].Code)} {items[i.ItemId].Name}"))}"
            : $"Recipe of {label} taken off";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return (await CostingAsync(propertyId, ct)).Dishes.Where(d => d.MenuItemId == dish.Id).ToList();
    }

    /// <summary>
    /// The food-cost report for <paramref name="from"/>–<paramref name="to"/> (property dates; default this month to today):
    /// every costed dish with its food cost %, contribution and margin, what was served in the period, and the highest and
    /// lowest margins.
    /// </summary>
    public async Task<FoodCostReportDto> FoodCostAsync(Guid propertyId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var property = await inventory.RequirePropertyAsync(propertyId, ct);
        var today = property.LocalDate(clock.GetUtcNow());
        var end = to ?? today;
        var start = from ?? new DateOnly(end.Year, end.Month, 1);
        if (start > end) throw ReservationService.Field("from", "The start date must be on or before the end date.");
        if (end.DayNumber - start.DayNumber > 366) throw ReservationService.Field("to", "Choose a period of at most a year.");

        var costing = await CostingAsync(propertyId, ct);
        // Served lines: sent to the kitchen, not voided, on a ticket the waiter served, of an order of the period.
        var served = await (from i in db.OrderItems.AsNoTracking()
                            join o in db.Orders.AsNoTracking() on i.OrderId equals o.Id
                            join k in db.Kots.AsNoTracking() on i.KotId equals k.Id
                            where o.PropertyId == propertyId && o.BusinessDate >= start && o.BusinessDate <= end
                                  && i.Status == OrderItemStatuses.Sent && k.Status == KotStatuses.Served
                            select new { i.Id, i.MenuItemId, i.VariantId, i.Name, i.VariantName, i.Quantity, i.Amount }).ToListAsync(ct);
        var lineIds = served.Select(s => s.Id).ToList();
        var used = await db.StockMovements.AsNoTracking()
            .Where(m => m.MovementType == MovementTypes.Consumption && m.OrderItemId != null && lineIds.Contains(m.OrderItemId.Value))
            .GroupBy(m => m.OrderItemId!.Value).Select(g => new { Id = g.Key, Value = g.Sum(m => m.Value) }).ToDictionaryAsync(x => x.Id, x => -x.Value, ct);
        var (since, until) = (property.StartOfDay(start), property.StartOfDay(end.AddDays(1)));
        var wastage = await db.StockMovements.AsNoTracking()
            .Where(m => m.PropertyId == propertyId && m.MovementType == MovementTypes.Wastage && m.CreatedAt >= since && m.CreatedAt < until)
            .SumAsync(m => (decimal?)m.Value, ct) ?? 0m;

        var lines = costing.Dishes.Where(d => d.Recipe is not null).Select(d =>
        {
            var mine = served.Where(s => s.MenuItemId == d.MenuItemId && s.VariantId == d.VariantId).ToList();
            var revenue = mine.Sum(s => s.Amount);
            var cost = mine.Sum(s => used.GetValueOrDefault(s.Id));
            return new FoodCostLineDto(d.MenuItemId, d.VariantId, d.Name, d.Category, d.Price, d.Cost, d.FoodCostPercent, d.Contribution, d.MarginPercent,
                mine.Sum(s => s.Quantity), revenue, cost, InventoryMath.FoodCostPercent(revenue, cost));
        }).OrderByDescending(l => l.MarginPercent ?? decimal.MinValue).ThenBy(l => l.Name).ToList();
        var ranked = lines.Where(l => l.MarginPercent is not null).ToList();
        var revenueAll = lines.Sum(l => l.Revenue);
        var costAll = lines.Sum(l => l.CostOfSales);
        return new FoodCostReportDto(start, end, lines, ranked.Take(3).ToList(), ranked.AsEnumerable().Reverse().Take(3).ToList(),
            costing.Dishes.Where(d => d.Recipe is null).Select(d => d.Name).ToList(), lines.Sum(l => l.Sold), revenueAll, costAll,
            InventoryMath.FoodCostPercent(revenueAll, costAll), -wastage);
    }

    // ------------------------------------------------------------------ costing

    private sealed record Costing(List<CostedDishDto> Dishes);

    /// <summary>Each active dish (or each active portion of a dish with portions), its recipe and cost at today's unit costs.</summary>
    private async Task<Costing> CostingAsync(Guid propertyId, CancellationToken ct)
    {
        var categories = await db.MenuCategories.AsNoTracking().Where(c => c.PropertyId == propertyId).ToListAsync(ct);
        var dishes = await db.MenuItems.AsNoTracking().Include(i => i.Variants).Where(i => i.PropertyId == propertyId && i.IsActive).ToListAsync(ct);
        var recipes = await db.Recipes.AsNoTracking().Include(r => r.Ingredients).Where(r => r.PropertyId == propertyId && r.IsActive).ToListAsync(ct);
        var itemIds = recipes.SelectMany(r => r.Ingredients.Select(i => i.ItemId)).Distinct().ToList();
        var items = await db.InventoryItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var units = await db.InventoryUnits.AsNoTracking().ToDictionaryAsync(u => u.Id, ct);

        RecipeDto ToDto(Recipe r)
        {
            var ings = r.Ingredients.OrderBy(i => i.SortOrder).Select(i =>
            {
                var item = items[i.ItemId];
                var own = units[item.UnitId];
                var inOwn = InventoryMath.Convert(i.Quantity, units[i.UnitId].Factor, own.Factor);
                return new RecipeIngredientDto(i.Id, item.Id, item.Code, item.Name, i.Quantity, i.UnitId, units[i.UnitId].Code, inOwn, own.Code,
                    InventoryMath.Value(inOwn, item.UnitCost));
            }).ToList();
            return new RecipeDto(r.Id, r.MenuItemId, r.VariantId, r.Note, ings, ings.Sum(i => i.Cost), r.Version);
        }

        CostedDishDto Dish(MenuItem d, MenuItemVariant? v)
        {
            var recipe = StockLedger.RecipeFor(recipes, d.Id, v?.Id);
            var dto = recipe is null ? null : ToDto(recipe);
            var price = v?.Price ?? d.Price;
            var cost = dto?.Cost;
            return new CostedDishDto(d.Id, v?.Id, RestaurantMath.Label(d.Name, v?.Name), categories.FirstOrDefault(c => c.Id == d.CategoryId)?.Name ?? "",
                d.FoodType, price, recipe is not null && recipe.VariantId == v?.Id, dto, cost,
                cost is { } c1 ? InventoryMath.FoodCostPercent(price, c1) : null,
                cost is { } c2 ? InventoryMath.Contribution(price, c2) : null,
                cost is { } c3 ? InventoryMath.MarginPercent(price, c3) : null);
        }

        var list = dishes
            .OrderBy(d => categories.FirstOrDefault(c => c.Id == d.CategoryId)?.SortOrder ?? 0).ThenBy(d => d.SortOrder).ThenBy(d => d.Name)
            .SelectMany(d => d.Variants.Any(v => v.IsActive)
                ? d.Variants.Where(v => v.IsActive).OrderBy(v => v.SortOrder).Select(v => Dish(d, v))
                : [Dish(d, null)])
            .ToList();
        return new Costing(list);
    }
}
