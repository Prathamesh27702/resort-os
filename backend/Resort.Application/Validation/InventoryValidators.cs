using System.Text.RegularExpressions;
using FluentValidation;
using Resort.Contracts;
using Resort.Domain.Inventory;

namespace Resort.Application.Validation;

// Field rules for stock and recipes (Sprint 15). Rules that need the property's data (is there enough stock, is the unit
// of the item's kind) are in InventoryService and RecipeService, under the same field names.

internal static class StockRules
{
    public const decimal MaxQuantity = 1_000_000m;

    /// <summary>Stock quantities have at most three decimals (to the gram or millilitre in kg or l).</summary>
    public static bool ThreeDecimals(decimal v) => decimal.Round(v, 3) == v;

    public static bool Code(string? c) => c is not null && Regex.IsMatch(c.Trim().ToUpperInvariant(), "^[A-Z0-9][A-Z0-9_-]{0,19}$");
}

public sealed class SaveInventoryCategoryRequestValidator : AbstractValidator<SaveInventoryCategoryRequest>
{
    public SaveInventoryCategoryRequestValidator()
    {
        RuleFor(x => x.Name).Must(n => n is not null && n.Trim().Length is >= 2 and <= 60)
            .WithMessage("Give the group a name of 2–60 characters, e.g. Dairy.");
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 10000).WithMessage("The order must be 0–10000.");
    }
}

public sealed class SaveStockLocationRequestValidator : AbstractValidator<SaveStockLocationRequest>
{
    public SaveStockLocationRequestValidator()
    {
        RuleFor(x => x.Code).Must(StockRules.Code)
            .WithMessage("The code is 1–20 letters, digits, - or _, starting with a letter or digit, e.g. STORE.");
        RuleFor(x => x.Name).Must(n => n is not null && n.Trim().Length is >= 2 and <= 60)
            .WithMessage("Give the store a name of 2–60 characters, e.g. Main store.");
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 10000).WithMessage("The order must be 0–10000.");
    }
}

public sealed class SaveInventoryItemRequestValidator : AbstractValidator<SaveInventoryItemRequest>
{
    public SaveInventoryItemRequestValidator()
    {
        RuleFor(x => x.CategoryId).NotEmpty().WithMessage("Choose the stock group.");
        RuleFor(x => x.Code).Must(StockRules.Code)
            .WithMessage("The code is 1–20 letters, digits, - or _, starting with a letter or digit, e.g. PANEER.");
        RuleFor(x => x.Name).Must(n => n is not null && n.Trim().Length is >= 2 and <= 80)
            .WithMessage("Give the item a name of 2–80 characters, e.g. Paneer.");
        RuleFor(x => x.UnitId).NotEmpty().WithMessage("Choose the unit the item is counted in (kg, g, l, ml, pcs or dozen).");
        RuleFor(x => x.UnitCost).Must(c => c is >= 0 and <= 1_000_000m).WithMessage("The cost of one unit must be ₹0–₹10,00,000.")
            .Must(c => decimal.Round(c, 4) == c).WithMessage("The cost of one unit can have at most four decimals.");
        RuleFor(x => x.ReorderLevel).Must(r => r is null || (r >= 0 && r <= StockRules.MaxQuantity && StockRules.ThreeDecimals(r.Value)))
            .WithMessage("The reorder level must be 0–1,000,000 with at most three decimals, or empty.");
        RuleFor(x => x.OpeningQuantity).Must(q => q is null || (q >= 0 && q <= StockRules.MaxQuantity && StockRules.ThreeDecimals(q.Value)))
            .WithMessage("The opening stock must be 0–1,000,000 with at most three decimals, or empty.");
    }
}

public sealed class CreateStockAdjustmentRequestValidator : AbstractValidator<CreateStockAdjustmentRequest>
{
    public CreateStockAdjustmentRequestValidator()
    {
        RuleFor(x => x.Kind).Must(k => k is not null && AdjustmentKinds.All.Contains(k.Trim().ToUpperInvariant()))
            .WithMessage("Choose what happened: RECEIVED, WASTAGE, EXPIRED, DAMAGED, CORRECTION or TRANSFER.");
        RuleFor(x => x.ItemId).NotEmpty().WithMessage("Choose the stock item.");
        RuleFor(x => x.LocationId).NotEmpty().WithMessage("Choose the store.");
        RuleFor(x => x.ToLocationId).Must((x, t) => x.Kind?.Trim().ToUpperInvariant() != AdjustmentKinds.Transfer || (t is not null && t != Guid.Empty))
            .WithMessage("Choose the store the stock goes to.");
        RuleFor(x => x.Quantity)
            .Must((x, q) => x.Kind?.Trim().ToUpperInvariant() == AdjustmentKinds.Correction ? q != 0 : q > 0)
            .WithMessage(x => x.Kind?.Trim().ToUpperInvariant() == AdjustmentKinds.Correction
                ? "Enter how much to add (e.g. 0.5) or take off (e.g. -0.5). It can't be 0."
                : "Enter a quantity more than 0.")
            .Must(q => Math.Abs(q) <= StockRules.MaxQuantity && StockRules.ThreeDecimals(q))
            .WithMessage("The quantity can be at most 1,000,000 with at most three decimals.");
        RuleFor(x => x.UnitCost).Must(c => c is null || (c >= 0 && c <= 1_000_000m && decimal.Round(c.Value, 4) == c))
            .WithMessage("The cost of one unit must be ₹0–₹10,00,000 with at most four decimals, or empty (the item's current cost).");
        RuleFor(x => x.Reason).Must(r => r is not null && r.Trim().Length is >= 3 and <= 300)
            .WithMessage("Say why, in 3–300 characters (e.g. Spoilt in the fridge, Supplier delivery bill 4411). Every stock change needs a reason.");
    }
}

public sealed class StockCountLineRequestValidator : AbstractValidator<StockCountLineRequest>
{
    public StockCountLineRequestValidator()
    {
        RuleFor(x => x.ItemId).NotEmpty().WithMessage("Choose the stock item.");
        RuleFor(x => x.CountedQuantity).Must(q => q >= 0 && q <= StockRules.MaxQuantity && StockRules.ThreeDecimals(q))
            .WithMessage("Enter what you counted: 0–1,000,000 with at most three decimals.");
        RuleFor(x => x.Reason).Must(r => r is null || r.Trim().Length == 0 || r.Trim().Length is >= 3 and <= 300)
            .WithMessage("The reason is 3–300 characters.");
    }
}

public sealed class CreateStockCountRequestValidator : AbstractValidator<CreateStockCountRequest>
{
    public CreateStockCountRequestValidator()
    {
        RuleFor(x => x.LocationId).NotEmpty().WithMessage("Choose the store you counted.");
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Enter at least one counted item.")
            .Must(l => l is null || l.Count <= 300).WithMessage("Count at most 300 items at a time.");
        RuleForEach(x => x.Lines).SetValidator(new StockCountLineRequestValidator());
    }
}

public sealed class SaveRecipeIngredientValidator : AbstractValidator<SaveRecipeIngredient>
{
    public SaveRecipeIngredientValidator()
    {
        RuleFor(x => x.ItemId).NotEmpty().WithMessage("Choose the stock item.");
        RuleFor(x => x.Quantity).Must(q => q > 0 && q <= 100_000m && StockRules.ThreeDecimals(q))
            .WithMessage("Enter how much one portion uses: more than 0, at most 100,000, with at most three decimals.");
        RuleFor(x => x.UnitId).NotEmpty().WithMessage("Choose the unit of the quantity.");
    }
}

public sealed class SaveRecipeRequestValidator : AbstractValidator<SaveRecipeRequest>
{
    public SaveRecipeRequestValidator()
    {
        RuleFor(x => x.MenuItemId).NotEmpty().WithMessage("Choose the dish.");
        RuleFor(x => x.Note).MaximumLength(500).WithMessage("The note can be at most 500 characters.");
        RuleFor(x => x.Ingredients).NotNull().WithMessage("Send the ingredients (an empty list takes the recipe off).")
            .Must(i => i is null || i.Count <= 40).WithMessage("A recipe can have at most 40 ingredients.");
        RuleForEach(x => x.Ingredients).SetValidator(new SaveRecipeIngredientValidator());
    }
}
