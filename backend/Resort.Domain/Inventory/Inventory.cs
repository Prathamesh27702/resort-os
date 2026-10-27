using Resort.Domain.Common;

namespace Resort.Domain.Inventory;

public static class UnitDimensions
{
    public const string Mass = "MASS";
    public const string Volume = "VOLUME";
    public const string Count = "COUNT";
}

/// <summary>Why stock moved. Every change of stock is one row of the ledger with one of these.</summary>
public static class MovementTypes
{
    public const string Opening = "OPENING";
    public const string Received = "RECEIVED";
    /// <summary>A served dish's recipe (Sprint 14 order lines, used when the waiter serves the kitchen ticket).</summary>
    public const string Consumption = "CONSUMPTION";
    public const string Wastage = "WASTAGE";
    /// <summary>A correction, either way.</summary>
    public const string Adjustment = "ADJUSTMENT";
    /// <summary>The difference a physical count found.</summary>
    public const string Count = "COUNT";
    public const string TransferOut = "TRANSFER_OUT";
    public const string TransferIn = "TRANSFER_IN";
}

/// <summary>What a person records against stock. Every kind needs a reason (brief §58).</summary>
public static class AdjustmentKinds
{
    public const string Received = "RECEIVED";
    public const string Wastage = "WASTAGE";
    public const string Expired = "EXPIRED";
    public const string Damaged = "DAMAGED";
    public const string Correction = "CORRECTION";
    public const string Transfer = "TRANSFER";
    public static readonly string[] All = [Received, Wastage, Expired, Damaged, Correction, Transfer];
    /// <summary>Kinds that take stock out (written off).</summary>
    public static readonly string[] WriteOff = [Wastage, Expired, Damaged];

    public static string Words(string kind) => kind switch
    {
        Received => "stock received",
        Wastage => "wastage",
        Expired => "expired",
        Damaged => "damaged",
        Correction => "correction",
        _ => "transfer",
    };
}

/// <summary>A unit stock is counted in (g, kg, ml, l, pcs, dozen): a fixed list. Factor: base units in one (kg = 1000 g).</summary>
public class StockUnit : Entity
{
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Dimension { get; set; } = default!;
    public decimal Factor { get; set; }
    public int SortOrder { get; set; }
}

public class InventoryCategory : AuditableEntity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Name { get; set; } = default!;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A store. The one marked IsKitchen gives the ingredients of every dish the restaurant serves.</summary>
public class StockLocation : AuditableEntity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public bool IsKitchen { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A stocked item. UnitCost: what one unit (UnitId) costs before GST, a moving average of what was received.</summary>
public class InventoryItem : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid CategoryId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public Guid UnitId { get; set; }
    public decimal UnitCost { get; set; }
    public decimal? ReorderLevel { get; set; }
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }
}

/// <summary>One row of the stock ledger. Never changed or deleted (a database trigger); OnHandAfter is set by the trigger
/// that keeps <see cref="StockLevel"/>.</summary>
public class StockMovement : Entity, IPropertyScoped
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid ItemId { get; set; }
    public Guid LocationId { get; set; }
    public string MovementType { get; set; } = default!;
    /// <summary>In the item's unit: positive in, negative out.</summary>
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal Value { get; set; }
    public decimal OnHandAfter { get; set; }
    public string? Note { get; set; }
    public Guid? AdjustmentId { get; set; }
    public Guid? CountId { get; set; }
    public Guid? OrderItemId { get; set; }
    public Guid? KotId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

/// <summary>On hand of one item at one location: the sum of its movements, kept by a database trigger. Read-only here.</summary>
public class StockLevel : IPropertyScoped
{
    public Guid ItemId { get; set; }
    public Guid LocationId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public decimal OnHand { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A person's change of stock with its reason, numbered SA00001 … Quantity as applied to LocationId (out is
/// negative); a transfer also puts the same quantity into ToLocationId.</summary>
public class StockAdjustment : Entity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Number { get; set; } = default!;
    public string Kind { get; set; } = default!;
    public Guid ItemId { get; set; }
    public Guid LocationId { get; set; }
    public Guid? ToLocationId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public string Reason { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

/// <summary>A physical count of one item at one location, numbered SC00001 …</summary>
public class StockCount : Entity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Number { get; set; } = default!;
    public Guid ItemId { get; set; }
    public Guid LocationId { get; set; }
    public decimal SystemQuantity { get; set; }
    public decimal CountedQuantity { get; set; }
    public decimal Difference { get; set; }
    public decimal UnitCost { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CountedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

/// <summary>What one portion of a dish uses. VariantId: the recipe of one portion (Half, Full), which wins over the dish's.</summary>
public class Recipe : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid MenuItemId { get; set; }
    public Guid? VariantId { get; set; }
    public string? Note { get; set; }
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }
    public List<RecipeIngredient> Ingredients { get; set; } = [];
}

public class RecipeIngredient : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; set; }
    public Guid RecipeId { get; set; }
    public Guid ItemId { get; set; }
    /// <summary>In <see cref="UnitId"/>, which has the item's dimension (200 g of an item stocked in kg).</summary>
    public decimal Quantity { get; set; }
    public Guid UnitId { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Stock and costing arithmetic that needs no database. Pure, unit-tested.</summary>
public static class InventoryMath
{
    /// <summary><paramref name="quantity"/> in a unit of factor <paramref name="from"/>, in a unit of factor <paramref name="to"/>
    /// (200 g → 0.2 kg). Kept to the gram: stock quantities have three decimals.</summary>
    public static decimal Convert(decimal quantity, decimal from, decimal to) => decimal.Round(quantity * from / to, 3, MidpointRounding.AwayFromZero);

    /// <summary>Value of a quantity at a unit cost, to the paisa.</summary>
    public static decimal Value(decimal quantity, decimal unitCost) => decimal.Round(quantity * unitCost, 2, MidpointRounding.AwayFromZero);

    /// <summary>Food cost as a share of the price, in percent to two decimals (₹105 of ₹280 → 37.5). Null when the price is 0.</summary>
    public static decimal? FoodCostPercent(decimal price, decimal cost) =>
        price <= 0 ? null : decimal.Round(cost / price * 100m, 2, MidpointRounding.AwayFromZero);

    /// <summary>What a dish leaves after its ingredients: price − cost.</summary>
    public static decimal Contribution(decimal price, decimal cost) => price - cost;

    /// <summary>Contribution as a share of the price, in percent to two decimals (₹175 of ₹280 → 62.5). Null when the price is 0.</summary>
    public static decimal? MarginPercent(decimal price, decimal cost) =>
        price <= 0 ? null : decimal.Round((price - cost) / price * 100m, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The moving average cost after receiving <paramref name="received"/> at <paramref name="cost"/> when
    /// <paramref name="onHand"/> was in stock at <paramref name="current"/>. Stock below zero counts as none (it was used
    /// before it was recorded, so its cost is unknown).
    /// </summary>
    public static decimal AverageCost(decimal onHand, decimal current, decimal received, decimal cost)
    {
        var have = Math.Max(0m, onHand);
        return have + received <= 0 ? cost : decimal.Round((have * current + received * cost) / (have + received), 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>"1.250 kg" without trailing zeros: "1.25 kg", "3 pcs".</summary>
    public static string Quantity(decimal quantity, string unit) => $"{quantity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} {unit}";
}
