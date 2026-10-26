namespace Resort.Contracts;

// ---------------------------------------------------------------- menu (Sprint 14)

/// <summary>The menu by section. Inactive sections and items are left out unless asked for (the menu editor).</summary>
public sealed record MenuDto(IReadOnlyList<MenuCategoryDto> Categories);

public sealed record MenuCategoryDto(Guid Id, string Name, int SortOrder, bool IsActive, IReadOnlyList<MenuItemDto> Items);

/// <summary>A dish or drink. Price is before GST. IsAvailable false: sold out for now (the kitchen switches it back).
/// An item with variants is ordered by variant (its own price is then unused).</summary>
public sealed record MenuItemDto(
    Guid Id, Guid CategoryId, string Code, string Name, string? Description, string FoodType, decimal Price, bool IsAvailable,
    bool IsActive, int SortOrder, IReadOnlyList<MenuVariantDto> Variants, IReadOnlyList<ModifierGroupDto> ModifierGroups, uint Version);

public sealed record MenuVariantDto(Guid Id, string Name, decimal Price);

/// <summary>Choices on an item: pick at least MinSelect and at most MaxSelect.</summary>
public sealed record ModifierGroupDto(Guid Id, string Name, short MinSelect, short MaxSelect, IReadOnlyList<ModifierDto> Modifiers);

public sealed record ModifierDto(Guid Id, string Name, decimal PriceDelta);

public sealed record SaveMenuCategoryRequest(string Name, int SortOrder, bool IsActive = true);

/// <summary>Creates or replaces a menu item with its variants and choices. A variant, group or choice sent with its Id is
/// kept (and renamed or repriced); one left out is switched off (never deleted, old orders point to it).</summary>
public sealed record SaveMenuItemRequest(
    Guid CategoryId, string Code, string Name, string? Description, string FoodType, decimal Price, bool IsActive, int SortOrder,
    IReadOnlyList<SaveMenuVariant>? Variants, IReadOnlyList<SaveModifierGroup>? ModifierGroups, uint? Version);

public sealed record SaveMenuVariant(Guid? Id, string Name, decimal Price);

public sealed record SaveModifierGroup(Guid? Id, string Name, short MinSelect, short MaxSelect, IReadOnlyList<SaveModifier> Modifiers);

public sealed record SaveModifier(Guid? Id, string Name, decimal PriceDelta);

/// <summary>Sold out (false) or back on (true). Needs menu.update.</summary>
public sealed record SetAvailabilityRequest(bool IsAvailable);

public sealed record RestaurantTableDto(Guid Id, string Code, short Seats, string? Area, int SortOrder, bool IsActive);

public sealed record SaveRestaurantTableRequest(string Code, short Seats, string? Area, int SortOrder, bool IsActive = true);

// ---------------------------------------------------------------- orders

/// <summary>The restaurant floor: every active table with its open order, and the open room-service and takeaway orders.</summary>
public sealed record FloorDto(DateOnly Today, IReadOnlyList<FloorTableDto> Tables, IReadOnlyList<OrderSummaryDto> OtherOrders);

public sealed record FloorTableDto(Guid Id, string Code, short Seats, string? Area, OrderSummaryDto? Order);

/// <summary>An order in a list. Where: "Table T4", "Room 104" or "Takeaway". Ready: kitchen tickets ready to be served.
/// InKitchen: tickets the kitchen is still working on. Unsent: lines not sent to the kitchen yet.</summary>
public sealed record OrderSummaryDto(
    Guid Id, string Number, string OrderType, string Status, string Where, string? GuestName, short? Covers, int Lines, int Unsent,
    int InKitchen, int Ready, decimal Total, string? Settlement, string? OpenedByName, DateTimeOffset CreatedAt, DateTimeOffset? SettledAt);

/// <summary>A page of orders with what they add up to (live orders only: voided ones count for nothing).</summary>
public sealed record OrderListDto(IReadOnlyList<OrderSummaryDto> Items, decimal SettledTotal, decimal OpenTotal);

/// <summary>An in-house stay a restaurant bill can be charged to (checked in now).</summary>
public sealed record InHouseRoomDto(Guid UnitId, string UnitCode, string UnitName, Guid ReservationId, string Reference, string GuestName, DateOnly CheckOut);

/// <summary>
/// One order. Money: Subtotal (live lines) − DiscountAmount = Net; Taxes are the GST on Net (restaurant service, from the
/// tax engine); Total = Net + TaxAmount. TaxNote says why no GST is charged when Taxes is empty. NextActions: what the
/// caller may do now (add, send, serve, discount, pay, charge, void, edit). Version: send it back with every change.
/// </summary>
public sealed record OrderDto(
    Guid Id, string Number, string OrderType, string Status, string Where, Guid? TableId, string? TableCode, Guid? UnitId, string? UnitName,
    Guid? ReservationId, string? ReservationReference, string? GuestName, short? Covers, string? Note, DateOnly BusinessDate,
    IReadOnlyList<OrderItemDto> Items, IReadOnlyList<OrderKotDto> Kots,
    decimal Subtotal, string? DiscountKind, decimal? DiscountValue, decimal DiscountAmount, string? DiscountReason, string? DiscountedByName,
    decimal Net, IReadOnlyList<DocumentTaxDto> Taxes, decimal TaxAmount, decimal Total, string? TaxNote,
    string? Settlement, string? PaymentMethod, string? PaymentReference, Guid? FolioId, string? FolioNumber, DateTimeOffset? SettledAt,
    string? SettledByName, DateTimeOffset? VoidedAt, string? VoidedByName, string? VoidReason, string? OpenedByName, DateTimeOffset CreatedAt,
    IReadOnlyList<string> NextActions, uint Version);

/// <summary>An order line. Status NEW (not sent yet), SENT or VOIDED. KitchenStatus: its ticket's status (NEW, PREPARING,
/// READY, SERVED) once sent.</summary>
public sealed record OrderItemDto(
    Guid Id, Guid MenuItemId, string Name, string? VariantName, string FoodType, IReadOnlyList<string> Modifiers, short Quantity,
    decimal UnitPrice, decimal Amount, string? Note, string Status, Guid? KotId, string? KotNumber, string? KitchenStatus,
    string? VoidReason, string? VoidedByName);

public sealed record OrderKotDto(Guid Id, string Number, string Status, DateTimeOffset SentAt, DateTimeOffset? ReadyAt, DateTimeOffset? ServedAt, int Lines);

/// <summary>A new order. DINE_IN needs TableId; ROOM_SERVICE needs the in-house room (UnitId); TAKEAWAY neither. Items may
/// be added now or later. An Idempotency-Key header is optional (an order sent twice on a weak signal is opened once).</summary>
public sealed record CreateOrderRequest(string OrderType, Guid? TableId, Guid? UnitId, short? Covers, string? Note, IReadOnlyList<OrderLineRequest>? Items);

/// <summary>A dish to add. VariantId: required when the item has variants. ModifierIds: the choices picked.</summary>
public sealed record OrderLineRequest(Guid MenuItemId, Guid? VariantId, IReadOnlyList<Guid>? ModifierIds, short Quantity, string? Note);

public sealed record AddOrderItemsRequest(IReadOnlyList<OrderLineRequest> Items, uint? Version);

/// <summary>Changes a line not sent to the kitchen yet.</summary>
public sealed record UpdateOrderItemRequest(short Quantity, string? Note, uint? Version);

/// <summary>Moves the order to another table or changes covers and the note.</summary>
public sealed record UpdateOrderRequest(Guid? TableId, short? Covers, string? Note, uint? Version);

/// <summary>Sends every new line to the kitchen as one ticket (KOT).</summary>
public sealed record SendToKitchenRequest(string? Note, uint? Version);

/// <summary>Voids a line already sent, or the whole order. Needs pos_order.void and a reason.</summary>
public sealed record VoidRequest(string? Reason, uint? Version);

/// <summary>A discount on the order (pos.discount): Kind PERCENT (Value 0–100) or AMOUNT (rupees), with a reason. Kind null
/// takes the discount off.</summary>
public sealed record DiscountRequest(string? Kind, decimal? Value, string? Reason, uint? Version);

/// <summary>Settles the order at the restaurant: CASH (into the open drawer), UPI, CARD or BANK_TRANSFER. Reference: the UPI
/// transaction id or the card terminal's approval code (never a card number). Idempotency-Key header optional.</summary>
public sealed record PayOrderRequest(string Method, string? Reference, uint? Version);

/// <summary>Settles the order on an in-house guest's main folio. ReservationId: the stay (default: the room-service
/// order's own). Idempotency-Key header optional.</summary>
public sealed record ChargeToRoomRequest(Guid? ReservationId, uint? Version);

// ---------------------------------------------------------------- kitchen

/// <summary>The kitchen display: tickets still in the kitchen (NEW, PREPARING, READY), oldest first.</summary>
public sealed record KitchenDto(DateTimeOffset Now, IReadOnlyList<KotDto> Tickets);

public sealed record KotDto(
    Guid Id, string Number, Guid OrderId, string OrderNumber, string Where, string Status, DateTimeOffset SentAt, string? SentByName,
    DateTimeOffset? StartedAt, DateTimeOffset? ReadyAt, string? Note, IReadOnlyList<KotItemDto> Items, uint Version);

/// <summary>A ticket line. Voided: struck through on the display (stop making it), with the reason.</summary>
public sealed record KotItemDto(Guid Id, string Name, short Quantity, string? Modifiers, string? Note, string FoodType, bool Voided, string? VoidReason);

/// <summary>The kitchen moves a ticket on (start, ready). Version optional.</summary>
public sealed record KotStepRequest(uint? Version);
