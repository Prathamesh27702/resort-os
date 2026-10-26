using Resort.Domain.Common;

namespace Resort.Domain.Restaurant;

/// <summary>Veg, non-veg or egg: the green, red or yellow mark Indian menus print next to a dish.</summary>
public static class FoodTypes
{
    public const string Veg = "VEG";
    public const string NonVeg = "NON_VEG";
    public const string Egg = "EGG";
    public static readonly string[] All = [Veg, NonVeg, Egg];
}

public static class OrderTypes
{
    /// <summary>At a table in the restaurant.</summary>
    public const string DineIn = "DINE_IN";
    /// <summary>Taken to an in-house guest's room; charged to the room by default.</summary>
    public const string RoomService = "ROOM_SERVICE";
    public const string Takeaway = "TAKEAWAY";
    public static readonly string[] All = [DineIn, RoomService, Takeaway];
}

public static class OrderStatuses
{
    public const string Open = "OPEN";
    public const string Settled = "SETTLED";
    public const string Voided = "VOIDED";
    public static readonly string[] All = [Open, Settled, Voided];
}

/// <summary>How a settled order was paid.</summary>
public static class Settlements
{
    /// <summary>Paid at the restaurant (cash, UPI, card, bank transfer).</summary>
    public const string Paid = "PAID";
    /// <summary>Put on an in-house guest's main folio as one line.</summary>
    public const string RoomCharge = "ROOM_CHARGE";
}

public static class OrderItemStatuses
{
    /// <summary>Added but not sent to the kitchen: can still be changed or removed.</summary>
    public const string New = "NEW";
    /// <summary>On a kitchen ticket: can only be voided (pos_order.void, with a reason).</summary>
    public const string Sent = "SENT";
    public const string Voided = "VOIDED";
}

/// <summary>A kitchen ticket's progress: NEW → PREPARING → READY (kitchen) → SERVED (waiter). CANCELLED when every line
/// on it was voided.</summary>
public static class KotStatuses
{
    public const string New = "NEW";
    public const string Preparing = "PREPARING";
    public const string Ready = "READY";
    public const string Served = "SERVED";
    public const string Cancelled = "CANCELLED";
    public static readonly string[] All = [New, Preparing, Ready, Served, Cancelled];
    /// <summary>Still on the kitchen display.</summary>
    public static readonly string[] Active = [New, Preparing, Ready];

    public static string Words(string status) => status switch
    {
        New => "waiting in the kitchen",
        Preparing => "being prepared",
        Ready => "ready to serve",
        Served => "served",
        _ => "cancelled",
    };
}

public static class DiscountKinds
{
    public const string Percent = "PERCENT";
    public const string Amount = "AMOUNT";
}

public class MenuCategory : AuditableEntity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Name { get; set; } = default!;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A dish or drink. Price is before GST. Never deleted: switched off (IsActive) or marked sold out (IsAvailable).</summary>
public class MenuItem : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid CategoryId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public string FoodType { get; set; } = FoodTypes.Veg;
    public decimal Price { get; set; }
    public bool IsAvailable { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public uint Version { get; set; }
    public List<MenuItemVariant> Variants { get; set; } = [];
    public List<ModifierGroup> ModifierGroups { get; set; } = [];
}

/// <summary>A portion or size with its own price (Half, Full). An item with active variants is ordered by variant.</summary>
public class MenuItemVariant : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; set; }
    public Guid MenuItemId { get; set; }
    public string Name { get; set; } = default!;
    public decimal Price { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Choices on an item (Spice level, Add-ons): between MinSelect and MaxSelect of its modifiers.</summary>
public class ModifierGroup : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; set; }
    public Guid MenuItemId { get; set; }
    public string Name { get; set; } = default!;
    public short MinSelect { get; set; }
    public short MaxSelect { get; set; } = 1;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public List<Modifier> Modifiers { get; set; } = [];
}

public class Modifier : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; set; }
    public Guid GroupId { get; set; }
    public string Name { get; set; } = default!;
    public decimal PriceDelta { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

public class RestaurantTable : AuditableEntity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Code { get; set; } = default!;
    public short Seats { get; set; } = 4;
    public string? Area { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A restaurant bill, numbered O00001 … per property. Every change carries its version (xmin): two waiters
/// editing the same order can't overwrite each other. Final once SETTLED or VOIDED (a database trigger).</summary>
public class Order : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Number { get; set; } = default!;
    public string OrderType { get; set; } = OrderTypes.DineIn;
    public string Status { get; set; } = OrderStatuses.Open;
    public Guid? TableId { get; set; }
    /// <summary>The room (room service, or a dine-in order charged to a room).</summary>
    public Guid? UnitId { get; set; }
    /// <summary>The in-house stay (room service, or the one it was charged to).</summary>
    public Guid? ReservationId { get; set; }
    public short? Covers { get; set; }
    public string? Note { get; set; }
    /// <summary>The property's date the order was opened: the service date of its room charge and its GST.</summary>
    public DateOnly BusinessDate { get; set; }
    public decimal Subtotal { get; set; }
    public string? DiscountKind { get; set; }
    public decimal? DiscountValue { get; set; }
    public decimal DiscountAmount { get; set; }
    public string? DiscountReason { get; set; }
    public Guid? DiscountedBy { get; set; }
    public DateTimeOffset? DiscountedAt { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal Total { get; set; }
    public string? Settlement { get; set; }
    public string? PaymentMethod { get; set; }
    public string? PaymentReference { get; set; }
    public Guid? CashDrawerSessionId { get; set; }
    public Guid? FolioId { get; set; }
    public Guid? FolioItemId { get; set; }
    public DateTimeOffset? SettledAt { get; set; }
    public Guid? SettledBy { get; set; }
    public DateTimeOffset? VoidedAt { get; set; }
    public Guid? VoidedBy { get; set; }
    public string? VoidReason { get; set; }
    public Guid? OpenedBy { get; set; }
    public uint Version { get; set; }
}

/// <summary>One line of an order: a copy of what was ordered and its price at the time.</summary>
public class OrderItem : Entity, IOrganizationScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid OrderId { get; set; }
    public Guid MenuItemId { get; set; }
    public Guid? VariantId { get; set; }
    public string Name { get; set; } = default!;
    public string? VariantName { get; set; }
    public string FoodType { get; set; } = FoodTypes.Veg;
    /// <summary>JSON array of <see cref="ChosenModifier"/>.</summary>
    public string Modifiers { get; set; } = "[]";
    public short Quantity { get; set; }
    /// <summary>The item's (or variant's) price plus its modifiers, before GST.</summary>
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
    public string? Note { get; set; }
    public string Status { get; set; } = OrderItemStatuses.New;
    public Guid? KotId { get; set; }
    public Guid? AddedBy { get; set; }
    public DateTimeOffset? VoidedAt { get; set; }
    public Guid? VoidedBy { get; set; }
    public string? VoidReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A modifier as it was chosen (copied onto the order line).</summary>
public sealed record ChosenModifier(Guid Id, string Group, string Name, decimal Price);

/// <summary>A kitchen order ticket, numbered K00001 … per property.</summary>
public class Kot : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid OrderId { get; set; }
    public string Number { get; set; } = default!;
    public string Status { get; set; } = KotStatuses.New;
    public string? Note { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public Guid? SentBy { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? ReadyAt { get; set; }
    public DateTimeOffset? ServedAt { get; set; }
    public Guid? ServedBy { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public uint Version { get; set; }
}

public class KotItem : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; set; }
    public Guid KotId { get; set; }
    public Guid OrderItemId { get; set; }
    /// <summary>"Paneer Tikka (Full)".</summary>
    public string Name { get; set; } = default!;
    public short Quantity { get; set; }
    /// <summary>"Less spicy, Extra cheese".</summary>
    public string? Modifiers { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset? VoidedAt { get; set; }
    public string? VoidReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Order arithmetic that needs no database. Pure, unit-tested; the database CHECKs the same sums.</summary>
public static class RestaurantMath
{
    /// <summary>The price of one unit: the item's or variant's price plus every chosen modifier.</summary>
    public static decimal UnitPrice(decimal basePrice, IEnumerable<decimal> modifierPrices) => basePrice + modifierPrices.Sum();

    public static decimal LineAmount(short quantity, decimal unitPrice) => unitPrice * quantity;

    /// <summary>
    /// What a discount takes off <paramref name="subtotal"/>: a percentage rounded to the paisa (halves away from zero), or
    /// an amount. Never more than the subtotal, so an item voided after a flat discount can't make the bill negative.
    /// </summary>
    public static decimal Discount(string? kind, decimal? value, decimal subtotal) => kind switch
    {
        DiscountKinds.Percent => Math.Min(subtotal, decimal.Round(subtotal * (value ?? 0) / 100m, 2, MidpointRounding.AwayFromZero)),
        DiscountKinds.Amount => Math.Min(subtotal, value ?? 0),
        _ => 0m,
    };

    /// <summary>"Paneer Tikka (Full)".</summary>
    public static string Label(string name, string? variant) => variant is null ? name : $"{name} ({variant})";

    /// <summary>The folio line for an order charged to a room: "Restaurant O00012 · Table T4: 2× Paneer Tikka, 1× Lime
    /// soda", at most 200 characters (the folio's limit).</summary>
    public static string FolioDescription(string number, string? where, IEnumerable<(short Quantity, string Label)> lines)
    {
        var head = where is null ? $"Restaurant {number}" : $"Restaurant {number} · {where}";
        var text = $"{head}: {string.Join(", ", lines.Select(l => $"{l.Quantity}× {l.Label}"))}";
        return text.Length <= 200 ? text : text[..197] + "…";
    }
}

/// <summary>Why a step can't be done on an order or a kitchen ticket in its current state (null = allowed). The words are
/// what the waiter or the kitchen reads.</summary>
public static class RestaurantFlow
{
    public static string? OrderProblem(string number, string status) => status switch
    {
        OrderStatuses.Open => null,
        OrderStatuses.Settled => $"{number} is already settled, so it can't be changed. Open a new order for anything more.",
        _ => $"{number} was voided, so it can't be changed. Open a new order instead.",
    };

    /// <summary>The kitchen moves a ticket one step: start (NEW → PREPARING) or ready (NEW or PREPARING → READY).
    /// The waiter serves it (READY → SERVED).</summary>
    public static string? KotProblem(string action, string number, string status) => (action, status) switch
    {
        (_, KotStatuses.Cancelled) => $"{number} was cancelled: everything on it was voided.",
        (_, KotStatuses.Served) => $"{number} is already served.",
        ("start", KotStatuses.New) => null,
        ("start", KotStatuses.Preparing) => $"{number} is already being prepared.",
        ("start", KotStatuses.Ready) => $"{number} is already ready.",
        ("ready", KotStatuses.New or KotStatuses.Preparing) => null,
        ("ready", KotStatuses.Ready) => $"{number} is already ready.",
        ("serve", KotStatuses.Ready) => null,
        ("serve", _) => $"{number} is still {KotStatuses.Words(status)}. Serve it when the kitchen marks it ready.",
        _ => $"That step isn't possible for {number} now.",
    };
}
