using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Resort.Contracts;
using Resort.Tests.Support;
using static Resort.Tests.Support.BookingKit;

namespace Resort.Tests.Integration;

/// <summary>Sprint 15 — inventory and recipe costing: served dishes use their recipe's stock from the kitchen, the food-cost
/// report, the append-only ledger and its materialised levels, two people changing the same item at once, adjustments
/// with a reason (audited), counts, and recipes in any unit of the item's kind. Each test uses a property of its own (one
/// kitchen per property). Real API, real PostgreSQL.</summary>
public class InventoryTests(ResortApiFactory app) : IClassFixture<ResortApiFactory>
{
    // ------------------------------------------------------------------ required demo: sell 3 Paneer Tikka → stock → report

    [Fact]
    public async Task Serving_3_paneer_tikka_uses_their_recipe_from_the_kitchen_and_the_report_shows_a_37_5_percent_food_cost()
    {
        var (owner, pid) = await PropertyAsync();
        var waiter = await StaffAsync("WAITER", pid);
        var kitchenStaff = await StaffAsync("KITCHEN", pid);
        var stock = await StockAsync(owner, pid);
        var menu = await MenuAsync(owner, pid);

        // The recipe of a full portion: 200 g paneer, 50 g curd, 20 g tikka masala, 100 g onion and capsicum, 20 ml oil = ₹105.
        var full = menu.Tikka.Variants.Single(v => v.Name == "Full");
        var rows = await OkAsync<List<CostedDishDto>>(await owner.PutAsync(RecipesUrl(pid), TikkaRecipe(menu.Tikka.Id, full.Id, stock)));
        var costed = rows.Single(r => r.VariantId == full.Id);
        Assert.Equal(("Paneer Tikka (Full)", 280m, 105m, 37.5m, 175m, 62.5m, true),
            (costed.Name, costed.Price, costed.Cost, costed.FoodCostPercent, costed.Contribution, costed.MarginPercent, costed.OwnRecipe));
        Assert.Equal(new[] { (200m, "g", 0.2m, "kg", 80m), (50m, "g", 0.05m, "kg", 5m), (20m, "g", 0.02m, "kg", 10m), (100m, "g", 0.1m, "kg", 6m), (20m, "ml", 0.02m, "l", 4m) },
            costed.Recipe!.Ingredients.Select(i => (i.Quantity, i.Unit, i.ItemQuantity, i.ItemUnit, i.Cost)));
        Assert.Null(rows.Single(r => r.Name == "Paneer Tikka (Half)").Recipe);   // the half portion has none of its own, and the dish none

        // The kitchen can read recipes but not change them.
        Assert.Equal(HttpStatusCode.Forbidden, (await kitchenStaff.PutAsync(RecipesUrl(pid), TikkaRecipe(menu.Tikka.Id, full.Id, stock))).StatusCode);
        Assert.Contains((await kitchenStaff.GetJsonAsync<RecipeListDto>(RecipesUrl(pid))).Dishes, d => d.VariantId == full.Id && d.Cost == 105m);

        // A waiter sells 3 Paneer Tikka (Full) and a naan; the kitchen marks the ticket ready; nothing is used until it is served.
        var order = await OkAsync<OrderDto>(await waiter.PostAsync(OrdersUrl(pid), new CreateOrderRequest("DINE_IN", menu.Table.Id, null, 2, null,
            [new OrderLineRequest(menu.Tikka.Id, full.Id, [menu.Tikka.ModifierGroups[0].Modifiers[0].Id], 3, null), new OrderLineRequest(menu.Naan.Id, null, null, 1, null)])),
            HttpStatusCode.Created);
        order = await OkAsync<OrderDto>(await waiter.PostAsync($"{OrdersUrl(pid)}/{order.Id}/send", new SendToKitchenRequest(null, order.Version)));
        var kot = order.Kots.Single();
        await OkAsync<KotDto>(await kitchenStaff.PostAsync($"/api/v1/properties/{pid}/kitchen/kots/{kot.Id}/ready", new KotStepRequest(null)));
        Assert.Equal(5m, await KitchenOnHandAsync(owner, pid, stock, stock.Paneer));

        // Served: 0.6 kg paneer, 0.15 kg curd, 0.06 kg masala, 0.3 kg onion and capsicum and 0.06 l oil leave the kitchen.
        order = await OkAsync<OrderDto>(await waiter.PostAsync($"{OrdersUrl(pid)}/{order.Id}/kots/{kot.Id}/serve"));
        var inv = await InventoryAsync(owner, pid);
        Assert.Equal(new[] { 6.4m, 3.85m, 0.94m, 7.7m, 4.94m },
            new[] { stock.Paneer, stock.Curd, stock.Masala, stock.Onion, stock.Oil }.Select(s => inv.Items.Single(i => i.Id == s.Id).OnHand));
        Assert.Equal(new[] { (stock.Store.Id, 2m), (stock.Kitchen.Id, 4.4m) }, inv.Items.Single(i => i.Id == stock.Paneer.Id).Levels.Select(l => (l.LocationId, l.OnHand)));

        var ledger = await owner.GetJsonAsync<StockMovementListDto>($"{InventoryUrl(pid)}/movements?itemId={stock.Paneer.Id}");
        var used = ledger.Items[0];
        Assert.Equal(("CONSUMPTION", -0.6m, 400m, -240m, 4.4m, "KITCHEN"), (used.Type, used.Quantity, used.UnitCost, used.Value, used.OnHandAfter, used.LocationCode));
        Assert.Equal($"{order.Number} · {kot.Number} · 3× Paneer Tikka (Full)", used.Note);
        Assert.Equal(new[] { "CONSUMPTION", "RECEIVED", "OPENING" }, ledger.Items.Select(m => m.Type));
        Assert.Equal(5L, await ScalarAsync($"SELECT count(*) FROM stock_movements WHERE kot_id = '{kot.Id}'"));   // the naan has no recipe

        // The food-cost report: 3 sold for ₹840, their stock was worth ₹315 — 37.5 %. The naan and the half portion have no recipe.
        var report = await owner.GetJsonAsync<FoodCostReportDto>($"{InventoryUrl(pid)}/food-cost");
        var line = report.Lines.Single(l => l.VariantId == full.Id);
        Assert.Equal((3, 840m, 315m, 37.5m, 37.5m, 62.5m), (line.Sold, line.Revenue, line.CostOfSales, line.ActualFoodCostPercent, line.FoodCostPercent, line.MarginPercent));
        Assert.Equal(new[] { "Paneer Tikka (Half)", "Butter Naan" }, report.NoRecipe);
        Assert.Equal((3, 840m, 315m, 37.5m), (report.Sold, report.Revenue, report.CostOfSales, report.FoodCostPercent));
        Assert.Equal(full.Id, Assert.Single(report.Highest).VariantId);

        // Serving is once: a second serve is refused and uses nothing more.
        var (code, _) = await ProblemAsync(await waiter.PostAsync($"{OrdersUrl(pid)}/{order.Id}/kots/{kot.Id}/serve"));
        Assert.Equal("INVALID_STATUS_TRANSITION", code);
        Assert.Equal(4.4m, await KitchenOnHandAsync(owner, pid, stock, stock.Paneer));
    }

    // ------------------------------------------------------------------ required: two adjustments at once lose nothing

    [Fact]
    public async Task Two_people_writing_off_the_same_item_at_once_both_count_and_neither_takes_it_below_zero()
    {
        var (owner, pid) = await PropertyAsync();
        var first = await StaffAsync("INVENTORY_MANAGER", pid);
        var second = await StaffAsync("RESTAURANT_MANAGER", pid);
        var stock = await StockAsync(owner, pid);

        // Four rounds of two 1 kg write-offs at the same moment on 10 kg of onion and capsicum (8 kg + 2 kg received).
        await OkAsync<StockAdjustmentDto>(await owner.PostAsync(AdjustUrl(pid), new CreateStockAdjustmentRequest("RECEIVED", stock.Onion.Id, stock.Kitchen.Id,
            null, 2m, 60m, "Market delivery")), HttpStatusCode.Created);
        for (var run = 0; run < 4; run++)
        {
            using var barrier = new Barrier(2);
            var a = Task.Run(() => { barrier.SignalAndWait(); return first.PostAsync(AdjustUrl(pid), Wastage(stock.Onion.Id, stock.Kitchen.Id, 1m)); });
            var b = Task.Run(() => { barrier.SignalAndWait(); return second.PostAsync(AdjustUrl(pid), Wastage(stock.Onion.Id, stock.Kitchen.Id, 1m)); });
            var results = await Task.WhenAll(a, b);
            Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
            var after = (await Task.WhenAll(results.Select(r => r.Content.ReadFromJsonAsync<StockAdjustmentDto>()))).Select(x => x!.OnHandAfter).Order().ToList();
            Assert.Equal(new[] { 10m - 2 * run - 2, 10m - 2 * run - 1 }, after);   // each saw the other's change
        }
        Assert.Equal(2m, await KitchenOnHandAsync(owner, pid, stock, stock.Onion));

        // 2 kg left: two 1.5 kg write-offs at once — one is refused, saying what is there.
        using (var barrier = new Barrier(2))
        {
            var a = Task.Run(() => { barrier.SignalAndWait(); return first.PostAsync(AdjustUrl(pid), Wastage(stock.Onion.Id, stock.Kitchen.Id, 1.5m)); });
            var b = Task.Run(() => { barrier.SignalAndWait(); return second.PostAsync(AdjustUrl(pid), Wastage(stock.Onion.Id, stock.Kitchen.Id, 1.5m)); });
            var results = await Task.WhenAll(a, b);
            Assert.Single(results, r => r.StatusCode == HttpStatusCode.Created);
            Assert.Equal("Kitchen has only 0.5 kg of Onion and capsicum, so 1.5 kg can't be taken out.",
                await FieldErrorAsync(results.Single(r => r.StatusCode != HttpStatusCode.Created), "Quantity"));
        }

        // The materialised level is always the sum of the ledger.
        Assert.Equal(0L, await ScalarAsync($"""
            SELECT count(*) FROM stock_levels l
             WHERE l.property_id = '{pid}'
               AND l.on_hand <> (SELECT sum(m.quantity) FROM stock_movements m WHERE m.item_id = l.item_id AND m.location_id = l.location_id)
            """));
        Assert.Equal(0.5m, await KitchenOnHandAsync(owner, pid, stock, stock.Onion));
    }

    // ------------------------------------------------------------------ adjustments: reason, cost, transfer, audit, ledger

    [Fact]
    public async Task Every_stock_change_needs_a_reason_is_audited_and_the_ledger_can_never_be_changed()
    {
        var (owner, pid) = await PropertyAsync();
        var kitchenStaff = await StaffAsync("KITCHEN", pid);
        var stock = await StockAsync(owner, pid);

        // No reason: refused under the field, saying why.
        Assert.Equal("Say why, in 3–300 characters (e.g. Spoilt in the fridge, Supplier delivery bill 4411). Every stock change needs a reason.",
            await FieldErrorAsync(await owner.PostAsync(AdjustUrl(pid), new CreateStockAdjustmentRequest("WASTAGE", stock.Paneer.Id, stock.Kitchen.Id, null, 1m, null, " ")), "Reason"));
        Assert.Equal("Enter a quantity more than 0.",
            await FieldErrorAsync(await owner.PostAsync(AdjustUrl(pid), new CreateStockAdjustmentRequest("WASTAGE", stock.Paneer.Id, stock.Kitchen.Id, null, -1m, null, "Spoilt")), "Quantity"));
        Assert.Equal("Choose the store the stock goes to.",
            await FieldErrorAsync(await owner.PostAsync(AdjustUrl(pid), new CreateStockAdjustmentRequest("TRANSFER", stock.Paneer.Id, stock.Kitchen.Id, null, 1m, null, "Move")), "ToLocationId"));

        // The kitchen sees stock but can't change it.
        Assert.Equal(HttpStatusCode.Forbidden, (await kitchenStaff.PostAsync(AdjustUrl(pid), Wastage(stock.Paneer.Id, stock.Kitchen.Id, 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await kitchenStaff.GetAsync(InventoryUrl(pid))).StatusCode);

        // 5 kg received at ₹420 on top of 7 kg at ₹400 (5 in the kitchen, 2 in the store): the average cost becomes ₹408.3333.
        var received = await OkAsync<StockAdjustmentDto>(await owner.PostAsync(AdjustUrl(pid),
            new CreateStockAdjustmentRequest("RECEIVED", stock.Paneer.Id, stock.Store.Id, null, 5m, 420m, "Delivery bill 4411")), HttpStatusCode.Created);
        Assert.Matches("^SA[0-9]{5}$", received.Number);
        Assert.Equal(("RECEIVED", 5m, 420m, 2100m, 7m), (received.Kind, received.Quantity, received.UnitCost, received.Value, received.OnHandAfter));
        Assert.Equal(408.3333m, (await InventoryAsync(owner, pid)).Items.Single(i => i.Id == stock.Paneer.Id).UnitCost);

        // A transfer moves stock between stores; a correction can take some off; wastage writes off at the current cost.
        var moved = await OkAsync<StockAdjustmentDto>(await owner.PostAsync(AdjustUrl(pid),
            new CreateStockAdjustmentRequest("TRANSFER", stock.Paneer.Id, stock.Store.Id, stock.Kitchen.Id, 3m, null, "For tonight")), HttpStatusCode.Created);
        Assert.Equal((-3m, "Main store", "Kitchen", 4m), (moved.Quantity, moved.LocationName, moved.ToLocationName, moved.OnHandAfter));
        var corrected = await OkAsync<StockAdjustmentDto>(await owner.PostAsync(AdjustUrl(pid),
            new CreateStockAdjustmentRequest("CORRECTION", stock.Paneer.Id, stock.Kitchen.Id, null, -0.25m, null, "Entered twice yesterday")), HttpStatusCode.Created);
        Assert.Equal(7.75m, corrected.OnHandAfter);
        var wasted = await OkAsync<StockAdjustmentDto>(await owner.PostAsync(AdjustUrl(pid),
            new CreateStockAdjustmentRequest("EXPIRED", stock.Paneer.Id, stock.Kitchen.Id, null, 0.5m, null, "Past its date")), HttpStatusCode.Created);
        Assert.Equal((-0.5m, 408.3333m, -204.17m, 7.25m), (wasted.Quantity, wasted.UnitCost, wasted.Value, wasted.OnHandAfter));

        var inv = (await InventoryAsync(owner, pid)).Items.Single(i => i.Id == stock.Paneer.Id);
        Assert.Equal(new[] { (stock.Store.Id, 4m), (stock.Kitchen.Id, 7.25m) }, inv.Levels.Select(l => (l.LocationId, l.OnHand)));
        var ledger = await owner.GetJsonAsync<StockMovementListDto>($"{InventoryUrl(pid)}/movements?itemId={stock.Paneer.Id}");
        Assert.Equal(new[] { ("WASTAGE", wasted.Number), ("ADJUSTMENT", corrected.Number), ("TRANSFER_IN", moved.Number), ("TRANSFER_OUT", moved.Number),
                             ("RECEIVED", received.Number), ("RECEIVED", "SA00001"), ("OPENING", null) },
            ledger.Items.Select(m => (m.Type, m.Source)));

        // Every change is audited with what and why.
        Assert.Equal($"{wasted.Number} expired: 0.5 kg Paneer in Kitchen — Past its date",
            await ScalarAsync($"SELECT reason FROM audit_logs WHERE entity_type = 'StockAdjustment' AND entity_id = '{wasted.Id}'"));

        // The ledger and the adjustments can't be changed or deleted, even directly in the database.
        foreach (var sql in new[] { $"UPDATE stock_movements SET quantity = 100 WHERE item_id = '{stock.Paneer.Id}'",
                     $"DELETE FROM stock_movements WHERE item_id = '{stock.Paneer.Id}'", $"UPDATE stock_adjustments SET reason = 'x' WHERE id = '{wasted.Id}'" })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(sql));
            Assert.Contains("append-only", ex.MessageText);
        }
    }

    // ------------------------------------------------------------------ counts

    [Fact]
    public async Task A_count_sets_stock_to_what_was_counted_and_a_difference_needs_a_reason()
    {
        var (owner, pid) = await PropertyAsync();
        var stock = await StockAsync(owner, pid);
        var url = $"{InventoryUrl(pid)}/counts";

        Assert.Equal("Paneer: the system has 5 kg and you counted 4.8 kg. Say why they differ (e.g. spillage, not recorded).",
            await FieldErrorAsync(await owner.PostAsync(url, new CreateStockCountRequest(stock.Kitchen.Id,
                [new StockCountLineRequest(stock.Curd.Id, 4m, null), new StockCountLineRequest(stock.Paneer.Id, 4.8m, null)])), "Lines[1].Reason"));

        var result = await OkAsync<StockCountResultDto>(await owner.PostAsync(url, new CreateStockCountRequest(stock.Kitchen.Id,
            [new StockCountLineRequest(stock.Curd.Id, 4m, null), new StockCountLineRequest(stock.Paneer.Id, 4.8m, "Trimmings not recorded")])), HttpStatusCode.Created);
        Assert.Equal(new[] { (4m, 4m, 0m, 0m, (string?)null), (5m, 4.8m, -0.2m, -80m, "Trimmings not recorded") },
            result.Lines.Select(l => (l.SystemQuantity, l.CountedQuantity, l.Difference, l.Value, l.Reason)));
        Assert.All(result.Lines, l => Assert.Matches("^SC[0-9]{5}$", l.Number));
        Assert.Equal(-80m, result.Value);

        var inv = await InventoryAsync(owner, pid);
        Assert.Equal((4.8m, 4m), (inv.Items.Single(i => i.Id == stock.Paneer.Id).Levels.Single(l => l.LocationId == stock.Kitchen.Id).OnHand,
            inv.Items.Single(i => i.Id == stock.Curd.Id).OnHand));
        var counted = await owner.GetJsonAsync<StockMovementListDto>($"{InventoryUrl(pid)}/movements?type=COUNT");
        Assert.Equal((stock.Paneer.Id, -0.2m, result.Lines[1].Number), (Assert.Single(counted.Items).ItemId, counted.Items[0].Quantity, counted.Items[0].Source));
    }

    // ------------------------------------------------------------------ recipes and items

    [Fact]
    public async Task A_recipe_uses_units_of_the_items_kind_and_a_portion_can_have_its_own()
    {
        var (owner, pid) = await PropertyAsync();
        var stock = await StockAsync(owner, pid);
        var menu = await MenuAsync(owner, pid);
        var half = menu.Tikka.Variants.Single(v => v.Name == "Half");

        Assert.Equal("Paneer is kept in kg, so its quantity must be in kg or g.",
            await FieldErrorAsync(await owner.PutAsync(RecipesUrl(pid), new SaveRecipeRequest(menu.Tikka.Id, null, null,
                [new SaveRecipeIngredient(stock.Paneer.Id, 200m, stock.Unit("ml"))], null)), "Ingredients[0].UnitId"));
        Assert.Equal("Paneer is in the recipe twice. Put the whole quantity on one line.",
            await FieldErrorAsync(await owner.PutAsync(RecipesUrl(pid), new SaveRecipeRequest(menu.Tikka.Id, null, null,
                [new SaveRecipeIngredient(stock.Paneer.Id, 100m, stock.Unit("g")), new SaveRecipeIngredient(stock.Paneer.Id, 0.1m, stock.Unit("kg"))], null)), "Ingredients[1].ItemId"));
        Assert.Equal($"Add at least one ingredient to the recipe of Paneer Tikka.",
            await FieldErrorAsync(await owner.PutAsync(RecipesUrl(pid), new SaveRecipeRequest(menu.Tikka.Id, null, null, [], null)), "Ingredients"));

        // A recipe for the dish applies to both portions until a portion has its own.
        var rows = await OkAsync<List<CostedDishDto>>(await owner.PutAsync(RecipesUrl(pid), new SaveRecipeRequest(menu.Tikka.Id, null, "Marinate overnight",
            [new SaveRecipeIngredient(stock.Paneer.Id, 0.15m, stock.Unit("kg"))], null)));
        Assert.Equal(new[] { ("Paneer Tikka (Half)", 60m, false), ("Paneer Tikka (Full)", 60m, false) }, rows.Select(r => (r.Name, r.Cost!.Value, r.OwnRecipe)));
        rows = await OkAsync<List<CostedDishDto>>(await owner.PutAsync(RecipesUrl(pid), new SaveRecipeRequest(menu.Tikka.Id, half.Id, null,
            [new SaveRecipeIngredient(stock.Paneer.Id, 120m, stock.Unit("g")), new SaveRecipeIngredient(stock.Oil.Id, 12m, stock.Unit("ml"))], null)));
        var halfRow = rows.Single(r => r.VariantId == half.Id);
        Assert.Equal((50.4m, 31.5m, true), (halfRow.Cost!.Value, halfRow.FoodCostPercent!.Value, halfRow.OwnRecipe));

        // Two people editing the same recipe: the one with the old version gets 409.
        var version = halfRow.Recipe!.Version;
        await OkAsync<List<CostedDishDto>>(await owner.PutAsync(RecipesUrl(pid), new SaveRecipeRequest(menu.Tikka.Id, half.Id, null,
            [new SaveRecipeIngredient(stock.Paneer.Id, 125m, stock.Unit("g"))], version)));
        var stale = await owner.PutAsync(RecipesUrl(pid), new SaveRecipeRequest(menu.Tikka.Id, half.Id, null, [new SaveRecipeIngredient(stock.Paneer.Id, 130m, stock.Unit("g"))], version));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(("CONCURRENCY_CONFLICT", "Someone else changed the recipe of Paneer Tikka (Half) a moment ago. Reload to see it, then make your change again."),
            await ProblemAsync(stale));

        // An empty list takes the half's own recipe off: it is made by the dish's recipe again.
        rows = await OkAsync<List<CostedDishDto>>(await owner.PutAsync(RecipesUrl(pid), new SaveRecipeRequest(menu.Tikka.Id, half.Id, null, [], null)));
        Assert.Equal((60m, false), (rows.Single(r => r.VariantId == half.Id).Cost!.Value, rows.Single(r => r.VariantId == half.Id).OwnRecipe));

        // An item in a recipe can't be switched off, and an item with movements can't change its unit.
        var paneer = (await InventoryAsync(owner, pid)).Items.Single(i => i.Id == stock.Paneer.Id);
        Assert.Equal("Paneer is used in a recipe. Take it out of the recipes first, then switch it off.",
            await FieldErrorAsync(await owner.PutAsync($"{InventoryUrl(pid)}/items/{paneer.Id}", new SaveInventoryItemRequest(paneer.CategoryId, paneer.Code, paneer.Name,
                paneer.UnitId, paneer.UnitCost, null, false, null, null, paneer.Version)), "IsActive"));
        Assert.Equal("Paneer already has stock movements in kg, so its unit can't change. Add a new item for the new unit.",
            await FieldErrorAsync(await owner.PutAsync($"{InventoryUrl(pid)}/items/{paneer.Id}", new SaveInventoryItemRequest(paneer.CategoryId, paneer.Code, paneer.Name,
                stock.Unit("g"), paneer.UnitCost, null, true, null, null, paneer.Version)), "UnitId"));
        var edited = await OkAsync<InventoryItemDto>(await owner.PutAsync($"{InventoryUrl(pid)}/items/{paneer.Id}", new SaveInventoryItemRequest(paneer.CategoryId, paneer.Code,
            "Paneer (fresh)", paneer.UnitId, 410m, 2m, true, null, null, paneer.Version)));
        Assert.Equal(("Paneer (fresh)", 410m, 2m, false), (edited.Name, edited.UnitCost, edited.ReorderLevel, edited.Low));
        var conflict = await owner.PutAsync($"{InventoryUrl(pid)}/items/{paneer.Id}", new SaveInventoryItemRequest(paneer.CategoryId, paneer.Code, "Paneer", paneer.UnitId,
            400m, null, true, null, null, paneer.Version));
        Assert.Equal(("CONCURRENCY_CONFLICT", "Someone else changed Paneer (fresh) a moment ago. Reload to see their change, then make yours again."), await ProblemAsync(conflict));
    }

    [Fact]
    public async Task Only_one_store_is_the_kitchen_and_a_store_with_stock_stays_on()
    {
        var (owner, pid) = await PropertyAsync();
        var stock = await StockAsync(owner, pid);
        var store = stock.Store;

        var kitchen = await OkAsync<StockLocationDto>(await owner.PutAsync($"{InventoryUrl(pid)}/locations/{store.Id}",
            new SaveStockLocationRequest(store.Code, store.Name, true, store.SortOrder)));
        Assert.True(kitchen.IsKitchen);
        var locations = (await InventoryAsync(owner, pid)).Locations;
        Assert.Equal(new[] { (store.Id, true), (stock.Kitchen.Id, false) }, locations.Select(l => (l.Id, l.IsKitchen)));

        Assert.Equal("Kitchen still has stock. Move it to another store (a transfer) or count it to zero before switching the store off.",
            await FieldErrorAsync(await owner.PutAsync($"{InventoryUrl(pid)}/locations/{stock.Kitchen.Id}",
                new SaveStockLocationRequest("KITCHEN", "Kitchen", false, 2, false)), "IsActive"));
        Assert.Equal("Another store already has the code KITCHEN.",
            await FieldErrorAsync(await owner.PostAsync($"{InventoryUrl(pid)}/locations", new SaveStockLocationRequest("kitchen", "Second kitchen", false, 3)), "Code"));
    }

    // ------------------------------------------------------------------ helpers

    internal sealed record Stock(StockLocationDto Store, StockLocationDto Kitchen, InventoryItemDto Paneer, InventoryItemDto Curd, InventoryItemDto Masala,
        InventoryItemDto Onion, InventoryItemDto Oil, IReadOnlyList<InventoryUnitDto> Units)
    {
        public Guid Unit(string code) => Units.Single(u => u.Code == code).Id;
    }

    internal sealed record Menu(MenuItemDto Tikka, MenuItemDto Naan, RestaurantTableDto Table);

    /// <summary>A Main store and a Kitchen; paneer (₹400/kg: 5 kg in the kitchen, 2 kg in the store), curd (₹100/kg, 4 kg),
    /// tikka masala (₹500/kg, 1 kg), onion and capsicum (₹60/kg, 8 kg), oil (₹200/l, 5 l).</summary>
    private static async Task<Stock> StockAsync(TestClient owner, Guid pid)
    {
        var url = InventoryUrl(pid);
        var store = await OkAsync<StockLocationDto>(await owner.PostAsync($"{url}/locations", new SaveStockLocationRequest("STORE", "Main store", false, 1)), HttpStatusCode.Created);
        var kitchen = await OkAsync<StockLocationDto>(await owner.PostAsync($"{url}/locations", new SaveStockLocationRequest("KITCHEN", "Kitchen", true, 2)), HttpStatusCode.Created);
        var group = await OkAsync<InventoryCategoryDto>(await owner.PostAsync($"{url}/categories", new SaveInventoryCategoryRequest("Kitchen stock", 1)), HttpStatusCode.Created);
        var units = (await InventoryAsync(owner, pid)).Units;
        async Task<InventoryItemDto> Item(string code, string name, string unit, decimal cost, decimal opening) =>
            await OkAsync<InventoryItemDto>(await owner.PostAsync($"{url}/items", new SaveInventoryItemRequest(group.Id, code, name, units.Single(u => u.Code == unit).Id,
                cost, null, true, opening, null, null)), HttpStatusCode.Created);
        var paneer = await Item("PANEER", "Paneer", "kg", 400m, 5m);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsync(AdjustUrl(pid),
            new CreateStockAdjustmentRequest("RECEIVED", paneer.Id, store.Id, null, 2m, 400m, "Opening in the store"))).StatusCode);
        return new Stock(store, kitchen, paneer, await Item("CURD", "Curd", "kg", 100m, 4m), await Item("TIKKA-MASALA", "Tikka masala", "kg", 500m, 1m),
            await Item("ONION-CAPSICUM", "Onion and capsicum", "kg", 60m, 8m), await Item("OIL", "Cooking oil", "l", 200m, 5m), units);
    }

    private static SaveRecipeRequest TikkaRecipe(Guid dish, Guid variant, Stock s) => new(dish, variant, null,
        [new SaveRecipeIngredient(s.Paneer.Id, 200m, s.Unit("g")), new SaveRecipeIngredient(s.Curd.Id, 50m, s.Unit("g")),
         new SaveRecipeIngredient(s.Masala.Id, 20m, s.Unit("g")), new SaveRecipeIngredient(s.Onion.Id, 100m, s.Unit("g")),
         new SaveRecipeIngredient(s.Oil.Id, 20m, s.Unit("ml"))], null);

    private static CreateStockAdjustmentRequest Wastage(Guid item, Guid location, decimal quantity) =>
        new("WASTAGE", item, location, null, quantity, null, "Spoilt in the cold room");

    /// <summary>Paneer Tikka (Half ₹160, Full ₹280, with a spice level), Butter Naan ₹60 and a table.</summary>
    private static async Task<Menu> MenuAsync(TestClient owner, Guid pid)
    {
        var cat = await OkAsync<MenuCategoryDto>(await owner.PostAsync($"/api/v1/properties/{pid}/menu/categories", new SaveMenuCategoryRequest("Starters", 1)),
            HttpStatusCode.Created);
        var tikka = await OkAsync<MenuItemDto>(await owner.PostAsync($"/api/v1/properties/{pid}/menu/items", new SaveMenuItemRequest(cat.Id, "PANEER-TIKKA", "Paneer Tikka",
            null, "VEG", 280m, true, 1, [new SaveMenuVariant(null, "Half", 160m), new SaveMenuVariant(null, "Full", 280m)],
            [new SaveModifierGroup(null, "Spice level", 1, 1, [new SaveModifier(null, "Less spicy", 0m), new SaveModifier(null, "Extra spicy", 0m)])], null)), HttpStatusCode.Created);
        var naan = await OkAsync<MenuItemDto>(await owner.PostAsync($"/api/v1/properties/{pid}/menu/items", new SaveMenuItemRequest(cat.Id, "BUTTER-NAAN", "Butter Naan",
            null, "VEG", 60m, true, 2, null, null, null)), HttpStatusCode.Created);
        var table = await OkAsync<RestaurantTableDto>(await owner.PostAsync($"/api/v1/properties/{pid}/menu/tables", new SaveRestaurantTableRequest("T1", 4, "Garden", 0)),
            HttpStatusCode.Created);
        return new Menu(tikka, naan, table);
    }

    private static Task<InventoryDto> InventoryAsync(TestClient c, Guid pid) => c.GetJsonAsync<InventoryDto>(InventoryUrl(pid));

    private static async Task<decimal> KitchenOnHandAsync(TestClient c, Guid pid, Stock s, InventoryItemDto item) =>
        (await InventoryAsync(c, pid)).Items.Single(i => i.Id == item.Id).Levels.Single(l => l.LocationId == s.Kitchen.Id).OnHand;

    private async Task<(TestClient Owner, Guid PropertyId)> PropertyAsync()
    {
        var owner = await app.SignInOwnerAsync();
        var code = "P" + Suffix();
        var res = await owner.PostAsync("/api/v1/properties", new CreatePropertyRequest(code, $"Beach Shack {code}", null, "RESORT", "Asia/Kolkata", "INR",
            null, null, null, null, null, "30", null, "IN", null, null, new TimeOnly(14, 0), new TimeOnly(11, 0), null, null));
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (owner, (await res.Content.ReadFromJsonAsync<PropertyDto>())!.Id);
    }

    private async Task<TestClient> StaffAsync(string role, Guid pid)
    {
        var (email, pw) = await app.CreateUserAsync(role, pid);
        return await app.SignInAsync(email, pw);
    }

    private static string InventoryUrl(Guid pid) => $"/api/v1/properties/{pid}/inventory";
    private static string AdjustUrl(Guid pid) => $"{InventoryUrl(pid)}/adjustments";
    private static string RecipesUrl(Guid pid) => $"{InventoryUrl(pid)}/recipes";
    private static string OrdersUrl(Guid pid) => $"/api/v1/properties/{pid}/orders";

    private static async Task<T> OkAsync<T>(HttpResponseMessage res, HttpStatusCode status = HttpStatusCode.OK)
    {
        Assert.True(res.StatusCode == status, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private static Task<(string? Code, string? Message)> ProblemAsync(HttpResponseMessage res) => FrontDeskTests.ProblemAsync(res);

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
