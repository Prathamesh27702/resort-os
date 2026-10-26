using FluentValidation;
using Resort.Contracts;
using Resort.Domain.Payments;
using Resort.Domain.Restaurant;

namespace Resort.Application.Validation;

// Field rules for the menu and restaurant orders (Sprint 14). Rules that need the property's data (the menu, the tables,
// who is in house, what is still in the kitchen) are in MenuService and OrderService, under the same field names.

internal static class RestaurantRules
{
    public static void Line<T>(AbstractValidator<T> v, System.Linq.Expressions.Expression<Func<T, short>> quantity)
    {
        v.RuleFor(quantity).InclusiveBetween((short)1, (short)99).WithMessage("The quantity must be 1–99.");
    }

    public static readonly string MethodMessage =
        $"Choose how the bill was paid: {string.Join(", ", PaymentMethods.Desk.Select(PaymentMethods.Words))}.";
}

public sealed class OrderLineRequestValidator : AbstractValidator<OrderLineRequest>
{
    public OrderLineRequestValidator()
    {
        RuleFor(x => x.MenuItemId).NotEmpty().WithMessage("Choose the dish.");
        RestaurantRules.Line(this, x => x.Quantity);
        RuleFor(x => x.ModifierIds).Must(m => m is null || m.Count <= 20).WithMessage("At most 20 choices on one dish.");
        RuleFor(x => x.Note).MaximumLength(200).WithMessage("The note for the kitchen can be at most 200 characters.");
    }
}

public sealed class CreateOrderRequestValidator : AbstractValidator<CreateOrderRequest>
{
    public CreateOrderRequestValidator()
    {
        RuleFor(x => x.OrderType).Must(t => t is not null && OrderTypes.All.Contains(t.Trim().ToUpperInvariant()))
            .WithMessage("Choose where the order is served: DINE_IN (a table), ROOM_SERVICE (an in-house room) or TAKEAWAY.");
        RuleFor(x => x.TableId).Must((x, t) => x.OrderType?.Trim().ToUpperInvariant() != OrderTypes.DineIn || (t is not null && t != Guid.Empty))
            .WithMessage("Choose the table.");
        RuleFor(x => x.UnitId).Must((x, u) => x.OrderType?.Trim().ToUpperInvariant() != OrderTypes.RoomService || (u is not null && u != Guid.Empty))
            .WithMessage("Choose the room the order goes to.");
        RuleFor(x => x.Covers).Must(c => c is null or >= 1 and <= 99).WithMessage("Guests at the table must be 1–99, or leave it empty.");
        RuleFor(x => x.Note).MaximumLength(300).WithMessage("The note can be at most 300 characters.");
        RuleFor(x => x.Items).Must(i => i is null || i.Count <= 50).WithMessage("Add at most 50 dishes at a time.");
        RuleForEach(x => x.Items).SetValidator(new OrderLineRequestValidator());
    }
}

public sealed class AddOrderItemsRequestValidator : AbstractValidator<AddOrderItemsRequest>
{
    public AddOrderItemsRequestValidator()
    {
        RuleFor(x => x.Items).NotEmpty().WithMessage("Choose at least one dish to add.")
            .Must(i => i is null || i.Count <= 50).WithMessage("Add at most 50 dishes at a time.");
        RuleForEach(x => x.Items).SetValidator(new OrderLineRequestValidator());
    }
}

public sealed class UpdateOrderItemRequestValidator : AbstractValidator<UpdateOrderItemRequest>
{
    public UpdateOrderItemRequestValidator()
    {
        RestaurantRules.Line(this, x => x.Quantity);
        RuleFor(x => x.Note).MaximumLength(200).WithMessage("The note for the kitchen can be at most 200 characters.");
    }
}

public sealed class UpdateOrderRequestValidator : AbstractValidator<UpdateOrderRequest>
{
    public UpdateOrderRequestValidator()
    {
        RuleFor(x => x.TableId).Must(t => t is null || t != Guid.Empty).WithMessage("Choose the table, or leave it as it is.");
        RuleFor(x => x.Covers).Must(c => c is null or >= 1 and <= 99).WithMessage("Guests at the table must be 1–99, or leave it empty.");
        RuleFor(x => x.Note).MaximumLength(300).WithMessage("The note can be at most 300 characters.");
    }
}

public sealed class SendToKitchenRequestValidator : AbstractValidator<SendToKitchenRequest>
{
    public SendToKitchenRequestValidator()
    {
        RuleFor(x => x.Note).MaximumLength(300).WithMessage("The note for the kitchen can be at most 300 characters.");
    }
}

public sealed class VoidRequestValidator : AbstractValidator<VoidRequest>
{
    public VoidRequestValidator()
    {
        RuleFor(x => x.Reason).Must(r => r is not null && r.Trim().Length >= 3)
            .WithMessage("Say why it is voided (at least 3 characters), e.g. Guest changed their mind.")
            .MaximumLength(500).WithMessage("The reason can be at most 500 characters.");
    }
}

public sealed class DiscountRequestValidator : AbstractValidator<DiscountRequest>
{
    public DiscountRequestValidator()
    {
        When(x => x.Kind is not null, () =>
        {
            RuleFor(x => x.Kind).Must(k => k!.Trim().ToUpperInvariant() is DiscountKinds.Percent or DiscountKinds.Amount)
                .WithMessage("Choose PERCENT (a share of the bill) or AMOUNT (rupees off).");
            RuleFor(x => x.Value).NotNull().WithMessage("Enter the discount.")
                .Must(v => v is null or > 0).WithMessage("The discount must be more than 0.")
                .Must((x, v) => x.Kind?.Trim().ToUpperInvariant() != DiscountKinds.Percent || v is null or <= 100)
                .WithMessage("A percentage discount can be at most 100%.")
                .Must(v => v is null || v == decimal.Round(v.Value, 2)).WithMessage("The discount can have at most 2 decimal places.")
                .Must(v => v is null or <= Money.Max).WithMessage($"The discount can't be more than {Money.Rupees(Money.Max)}.");
            PaymentRules.Reason(this, x => x.Reason, "Regular guest", "discount is given");
        });
    }
}

public sealed class PayOrderRequestValidator : AbstractValidator<PayOrderRequest>
{
    public PayOrderRequestValidator()
    {
        RuleFor(x => x.Method).Must(m => !PaymentRules.IsOnline(m)).WithMessage("Restaurant bills are paid at the restaurant: choose cash, UPI, card or bank transfer.")
            .Must(PaymentRules.IsDeskMethod).WithMessage(RestaurantRules.MethodMessage);
        PaymentRules.Reference(this, x => x.Reference, x => x.Method);
    }
}

public sealed class ChargeToRoomRequestValidator : AbstractValidator<ChargeToRoomRequest>
{
    public ChargeToRoomRequestValidator()
    {
        RuleFor(x => x.ReservationId).Must(r => r is null || r != Guid.Empty).WithMessage("Choose the room to charge it to.");
    }
}

// ---------------------------------------------------------------- menu

public sealed class SaveMenuCategoryRequestValidator : AbstractValidator<SaveMenuCategoryRequest>
{
    public SaveMenuCategoryRequestValidator()
    {
        RuleFor(x => x.Name).Must(n => n is not null && n.Trim().Length is >= 2 and <= 60)
            .WithMessage("Give the section a name of 2–60 characters, e.g. Starters.");
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 10000).WithMessage("The order must be 0–10000.");
    }
}

public sealed class SaveMenuItemRequestValidator : AbstractValidator<SaveMenuItemRequest>
{
    public SaveMenuItemRequestValidator()
    {
        RuleFor(x => x.CategoryId).NotEmpty().WithMessage("Choose the menu section.");
        RuleFor(x => x.Code).Must(c => c is not null && System.Text.RegularExpressions.Regex.IsMatch(c.Trim().ToUpperInvariant(), "^[A-Z0-9][A-Z0-9_-]{0,19}$"))
            .WithMessage("The code is 1–20 letters, digits, - or _, starting with a letter or digit, e.g. PANEER-TIKKA.");
        RuleFor(x => x.Name).Must(n => n is not null && n.Trim().Length is >= 2 and <= 80)
            .WithMessage("Give the dish a name of 2–80 characters, e.g. Paneer Tikka.");
        RuleFor(x => x.Description).MaximumLength(300).WithMessage("The description can be at most 300 characters.");
        RuleFor(x => x.FoodType).Must(t => t is not null && FoodTypes.All.Contains(t.Trim().ToUpperInvariant()))
            .WithMessage("Choose VEG, NON_VEG or EGG.");
        Money.Rules(this, x => x.Price, "The price");
        RuleFor(x => x.Price).Must(p => p <= 1_000_000m).WithMessage("The price can't be more than ₹10,00,000.");
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 10000).WithMessage("The order must be 0–10000.");
        RuleFor(x => x.Variants).Must(v => v is null || v.Count <= 10).WithMessage("At most 10 portions.")
            .Must(v => v is null || v.Select(x => x.Name?.Trim().ToLowerInvariant()).Distinct().Count() == v.Count)
            .WithMessage("Two portions have the same name.");
        RuleForEach(x => x.Variants).ChildRules(v =>
        {
            v.RuleFor(x => x.Name).Must(n => n is not null && n.Trim().Length is >= 1 and <= 40)
                .WithMessage("Each portion needs a name of 1–40 characters, e.g. Half.");
            Money.Rules(v, x => x.Price, "The portion's price");
        });
        RuleFor(x => x.ModifierGroups).Must(g => g is null || g.Count <= 10).WithMessage("At most 10 groups of choices.");
        RuleForEach(x => x.ModifierGroups).ChildRules(g =>
        {
            g.RuleFor(x => x.Name).Must(n => n is not null && n.Trim().Length is >= 2 and <= 40)
                .WithMessage("Each group of choices needs a name of 2–40 characters, e.g. Spice level.");
            g.RuleFor(x => x.MaxSelect).InclusiveBetween((short)1, (short)20).WithMessage("Up to how many may be picked: 1–20.");
            g.RuleFor(x => x.MinSelect).Must((x, min) => min >= 0 && min <= x.MaxSelect)
                .WithMessage("At least how many must be picked: 0 up to the most that may be picked.");
            g.RuleFor(x => x.Modifiers).NotEmpty().WithMessage("Add at least one choice to the group.")
                .Must(m => m is null || m.Count <= 30).WithMessage("At most 30 choices in a group.")
                .Must((x, m) => m is null || x.MinSelect <= m.Count).WithMessage("The group must have at least as many choices as must be picked.");
            g.RuleForEach(x => x.Modifiers).ChildRules(m =>
            {
                m.RuleFor(x => x.Name).Must(n => n is not null && n.Trim().Length is >= 1 and <= 40)
                    .WithMessage("Each choice needs a name of 1–40 characters, e.g. Extra cheese.");
                m.RuleFor(x => x.PriceDelta).Must(p => p >= 0 && p <= 100_000m && p == decimal.Round(p, 2))
                    .WithMessage("What a choice adds is ₹0–₹1,00,000 in whole paise.");
            });
        });
    }
}

public sealed class SaveRestaurantTableRequestValidator : AbstractValidator<SaveRestaurantTableRequest>
{
    public SaveRestaurantTableRequestValidator()
    {
        RuleFor(x => x.Code).Must(c => c is not null && System.Text.RegularExpressions.Regex.IsMatch(c.Trim().ToUpperInvariant(), "^[A-Z0-9][A-Z0-9_-]{0,9}$"))
            .WithMessage("The table code is 1–10 letters or digits, e.g. T4.");
        RuleFor(x => x.Seats).InclusiveBetween((short)1, (short)50).WithMessage("Seats must be 1–50.");
        RuleFor(x => x.Area).MaximumLength(40).WithMessage("The area can be at most 40 characters, e.g. Garden.");
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 10000).WithMessage("The order must be 0–10000.");
    }
}
