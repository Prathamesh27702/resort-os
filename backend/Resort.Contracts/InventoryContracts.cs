namespace Resort.Contracts;

// ---------------------------------------------------------------- inventory (Sprint 15)

/// <summary>A unit stock is counted in. Dimension: MASS, VOLUME or COUNT; Factor: base units (g, ml, pcs) in one.</summary>
public sealed record InventoryUnitDto(Guid Id, string Code, string Name, string Dimension, decimal Factor);

public sealed record InventoryCategoryDto(Guid Id, string Name, int SortOrder, bool IsActive);

public sealed record StockLocationDto(Guid Id, string Code, string Name, bool IsKitchen, int SortOrder, bool IsActive);

/// <summary>On hand of an item at one location (in the item's unit).</summary>
public sealed record StockAtDto(Guid LocationId, string LocationCode, decimal OnHand);

/// <summary>
/// A stocked item with what is on hand. UnitCost: one unit (Unit) before GST, a moving average of stock received.
/// Value = OnHand × UnitCost. Low: on hand is at or below the reorder level. Negative: more was used than was recorded
/// (count it).
/// </summary>
public sealed record InventoryItemDto(
    Guid Id, Guid CategoryId, string CategoryName, string Code, string Name, Guid UnitId, string Unit, string Dimension, decimal UnitCost,
    decimal? ReorderLevel, bool IsActive, decimal OnHand, decimal Value, bool Low, bool Negative, IReadOnlyList<StockAtDto> Levels, uint Version);

/// <summary>Everything the inventory screen needs: units, categories, locations and items with their stock. Value: the
/// whole stock at unit cost.</summary>
public sealed record InventoryDto(
    IReadOnlyList<InventoryUnitDto> Units, IReadOnlyList<InventoryCategoryDto> Categories, IReadOnlyList<StockLocationDto> Locations,
    IReadOnlyList<InventoryItemDto> Items, decimal Value, int LowCount);

public sealed record SaveInventoryCategoryRequest(string Name, int SortOrder, bool IsActive = true);

/// <summary>IsKitchen: served dishes use their ingredients from this location (one per property; marking another moves it).</summary>
public sealed record SaveStockLocationRequest(string Code, string Name, bool IsKitchen, int SortOrder, bool IsActive = true);

/// <summary>Creates or edits a stocked item. The unit can't change once the item has stock movements. On create, an
/// opening quantity (at UnitCost) can be put into OpeningLocationId (the kitchen when left out).</summary>
public sealed record SaveInventoryItemRequest(
    Guid CategoryId, string Code, string Name, Guid UnitId, decimal UnitCost, decimal? ReorderLevel, bool IsActive,
    decimal? OpeningQuantity, Guid? OpeningLocationId, uint? Version);

/// <summary>
/// A change of stock with its reason (required). Kind: RECEIVED (in, at UnitCost, which updates the item's average cost),
/// WASTAGE / EXPIRED / DAMAGED (out), CORRECTION (Quantity signed: + found, − missing) or TRANSFER (from LocationId to
/// ToLocationId). Quantity is in the item's unit and positive except for a correction.
/// </summary>
public sealed record CreateStockAdjustmentRequest(
    string Kind, Guid ItemId, Guid LocationId, Guid? ToLocationId, decimal Quantity, decimal? UnitCost, string Reason);

public sealed record StockAdjustmentDto(
    Guid Id, string Number, string Kind, Guid ItemId, string ItemName, string Unit, Guid LocationId, string LocationName,
    Guid? ToLocationId, string? ToLocationName, decimal Quantity, decimal UnitCost, decimal Value, string Reason, decimal OnHandAfter,
    string? ByName, DateTimeOffset CreatedAt);

/// <summary>A physical count of one location. Each line: what was counted; a line whose count differs from the system
/// needs a reason.</summary>
public sealed record CreateStockCountRequest(Guid LocationId, IReadOnlyList<StockCountLineRequest> Lines);

public sealed record StockCountLineRequest(Guid ItemId, decimal CountedQuantity, string? Reason);

public sealed record StockCountDto(
    Guid Id, string Number, Guid ItemId, string ItemName, string Unit, decimal SystemQuantity, decimal CountedQuantity,
    decimal Difference, decimal Value, string? Reason);

public sealed record StockCountResultDto(string LocationName, IReadOnlyList<StockCountDto> Lines, decimal Value);

/// <summary>One row of the stock ledger. Source: "SA00004", "O00012 · K00009 · Paneer Tikka (Full)" or "SC00002".</summary>
public sealed record StockMovementDto(
    Guid Id, DateTimeOffset At, Guid ItemId, string ItemName, string Unit, string LocationCode, string Type, decimal Quantity,
    decimal UnitCost, decimal Value, decimal OnHandAfter, string? Source, string? Note, string? ByName);

public sealed record StockMovementListDto(IReadOnlyList<StockMovementDto> Items, bool More);

// ---------------------------------------------------------------- recipes and food cost

/// <summary>An ingredient with its cost: Quantity in Unit; ItemQuantity the same in the item's own unit; Cost at the
/// item's current unit cost.</summary>
public sealed record RecipeIngredientDto(
    Guid Id, Guid ItemId, string ItemCode, string ItemName, decimal Quantity, Guid UnitId, string Unit, decimal ItemQuantity, string ItemUnit, decimal Cost);

/// <summary>A recipe (one portion). Cost = Σ ingredients' cost; FoodCostPercent and MarginPercent on Price (before GST).</summary>
public sealed record RecipeDto(
    Guid Id, Guid MenuItemId, Guid? VariantId, string? Note, IReadOnlyList<RecipeIngredientDto> Ingredients, decimal Cost, uint Version);

/// <summary>A dish or one of its portions, as it is costed: its price and the recipe that applies (its own, or the dish's
/// for a portion without one). Recipe null: nothing is used from stock when it is served.</summary>
public sealed record CostedDishDto(
    Guid MenuItemId, Guid? VariantId, string Name, string Category, string FoodType, decimal Price, bool OwnRecipe, RecipeDto? Recipe,
    decimal? Cost, decimal? FoodCostPercent, decimal? Contribution, decimal? MarginPercent);

public sealed record RecipeListDto(IReadOnlyList<CostedDishDto> Dishes, IReadOnlyList<InventoryUnitDto> Units);

/// <summary>Saves the recipe of a dish (VariantId null) or of one portion. An empty list of ingredients takes the recipe off.</summary>
public sealed record SaveRecipeRequest(Guid MenuItemId, Guid? VariantId, string? Note, IReadOnlyList<SaveRecipeIngredient> Ingredients, uint? Version);

public sealed record SaveRecipeIngredient(Guid ItemId, decimal Quantity, Guid UnitId);

/// <summary>
/// One dish (or portion) in the food-cost report. Price, Cost, FoodCostPercent, Contribution and MarginPercent are per
/// portion at today's costs. Sold: portions served in the period; Revenue: what they sold for before GST and discounts
/// (with their add-ons); CostOfSales: what the stock they used was worth when it was used.
/// </summary>
public sealed record FoodCostLineDto(
    Guid MenuItemId, Guid? VariantId, string Name, string Category, decimal Price, decimal? Cost, decimal? FoodCostPercent, decimal? Contribution,
    decimal? MarginPercent, int Sold, decimal Revenue, decimal CostOfSales, decimal? ActualFoodCostPercent);

/// <summary>
/// The food-cost report for From–To (property dates). Lines with a recipe, highest margin first; Highest and Lowest: the
/// three with the highest and lowest margin %. NoRecipe: dishes on the menu without one (their stock isn't tracked).
/// Wastage: stock written off in the period.
/// </summary>
public sealed record FoodCostReportDto(
    DateOnly From, DateOnly To, IReadOnlyList<FoodCostLineDto> Lines, IReadOnlyList<FoodCostLineDto> Highest, IReadOnlyList<FoodCostLineDto> Lowest,
    IReadOnlyList<string> NoRecipe, int Sold, decimal Revenue, decimal CostOfSales, decimal? FoodCostPercent, decimal Wastage);
