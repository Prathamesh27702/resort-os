using System.Globalization;
using System.Linq.Expressions;
using FluentValidation;
using Resort.Contracts;
using Resort.Domain.Rates;

namespace Resort.Application.Validation;

// Field rules mirror the CHECK constraints in database/migrations/V006__rates.sql, so users get a field message that
// says why, instead of a database error.

internal static class Money
{
    public const decimal Max = 10_000_000m;

    public static bool InPaise(decimal v) => decimal.Round(v, 2) == v;

    public static string Rupees(decimal v) => "₹" + v.ToString("#,##0.##", CultureInfo.InvariantCulture);

    /// <summary>A money field: 0 (or more than 0 when <paramref name="positive"/>) up to ₹1 crore, in whole paise.</summary>
    public static void Rules<T>(AbstractValidator<T> v, Expression<Func<T, decimal>> field, string label, bool positive = false)
    {
        v.RuleFor(field)
            .Must(x => positive ? x > 0 : x >= 0)
            .WithMessage(positive ? $"{label} must be more than ₹0." : $"{label} can't be negative.")
            .Must(x => x <= Max).WithMessage($"{label} can't be more than {Rupees(Max)}.")
            .Must(InPaise).WithMessage($"{label} can have at most 2 decimal places (paise).");
    }
}

public sealed class CreateRatePlanRequestValidator : AbstractValidator<CreateRatePlanRequest>
{
    public CreateRatePlanRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().WithMessage("Enter a code, e.g. BAR.")
            .Matches("^[A-Za-z0-9_]{2,30}$").WithMessage("Code: 2–30 letters, digits or underscore, e.g. BAR or ROOM_BREAKFAST.");
        RatePlanRules.Apply(this, x => x.Name, x => x.Description, x => x.MealPlan, x => x.SortOrder);
    }
}

public sealed class UpdateRatePlanRequestValidator : AbstractValidator<UpdateRatePlanRequest>
{
    public UpdateRatePlanRequestValidator()
    {
        RatePlanRules.Apply(this, x => x.Name, x => x.Description, x => x.MealPlan, x => x.SortOrder);
        RuleFor(x => x.IsActive).Must((x, active) => active || !x.IsDefault)
            .WithMessage("The default plan must stay active. Make another plan the default first.");
    }
}

internal static class RatePlanRules
{
    public static void Apply<T>(AbstractValidator<T> v, Expression<Func<T, string>> name, Expression<Func<T, string?>> description,
        Expression<Func<T, string>> mealPlan, Expression<Func<T, int>> sortOrder)
    {
        v.RuleFor(name).NotEmpty().WithMessage("Enter a name, e.g. Best available rate.")
            .MaximumLength(100).WithMessage("Name can be at most 100 characters.");
        v.RuleFor(description).MaximumLength(1000).WithMessage("Description can be at most 1000 characters.");
        v.RuleFor(mealPlan).Must(m => MealPlans.All.Contains(m))
            .WithMessage("Meal plan must be EP (room only), CP (breakfast), MAP (breakfast + one meal) or AP (all meals).");
        v.RuleFor(sortOrder).InclusiveBetween(0, 100000).WithMessage("Sort order must be between 0 and 100000.");
    }
}

public sealed class SetRatePricesRequestValidator : AbstractValidator<SetRatePricesRequest>
{
    public const int MaxNights = 366;

    public SetRatePricesRequestValidator()
    {
        RuleFor(x => x.AccommodationTypeIds).NotEmpty().WithMessage("Choose at least one room type.")
            .Must(ids => ids is null || ids.Count <= 50).WithMessage("Choose at most 50 room types at a time.")
            .Must(ids => ids is null || ids.All(id => id != Guid.Empty)).WithMessage("One of the room types is empty.");
        RuleFor(x => x.To).GreaterThan(x => x.From)
            .WithMessage("The end date must be after the start date (the end date itself is not changed).");
        RuleFor(x => x.To).Must((x, to) => to.DayNumber - x.From.DayNumber <= MaxNights)
            .WithMessage(x => $"One change can cover at most {MaxNights} nights; this one covers {x.To.DayNumber - x.From.DayNumber}.");
        RuleFor(x => x.DaysOfWeek).Must(d => d is null || d.All(Enum.IsDefined))
            .WithMessage("Days of the week must be Monday … Sunday.");
        Money.Rules(this, x => x.Price, "Price");
        Money.Rules(this, x => x.ExtraAdult, "Extra adult price");
        Money.Rules(this, x => x.ExtraChild, "Extra child price");
        RuleFor(x => x.MinStay).InclusiveBetween((short)1, (short)30).WithMessage("Minimum stay must be between 1 and 30 nights.");
    }
}

/// <summary>Query string of GET …/rates/quote. Type and Room accept an id or a code; give one of them.</summary>
public sealed record RateQuoteQuery(string? Type, string? Room, string? RatePlan, DateOnly? From, DateOnly? To, int? Adults, int? Children);

public sealed class RateQuoteQueryValidator : AbstractValidator<RateQuoteQuery>
{
    public const int MaxNights = 30;

    public RateQuoteQueryValidator()
    {
        RuleFor(x => x.Type).Must((x, t) => !string.IsNullOrWhiteSpace(t) || !string.IsNullOrWhiteSpace(x.Room))
            .WithMessage("Choose a room type (type=) or a room (room=) to quote.");
        RuleFor(x => x.From).NotNull().WithMessage("Enter the arrival date (from=YYYY-MM-DD).");
        RuleFor(x => x.To).NotNull().WithMessage("Enter the departure date (to=YYYY-MM-DD).");
        When(x => x.From is not null && x.To is not null, () =>
        {
            RuleFor(x => x.To).Must((x, to) => to > x.From).WithMessage("The departure date must be after the arrival date.");
            RuleFor(x => x.To).Must((x, to) => to!.Value.DayNumber - x.From!.Value.DayNumber <= MaxNights)
                .WithMessage(x => $"A quote can cover at most {MaxNights} nights; this one covers {x.To!.Value.DayNumber - x.From!.Value.DayNumber}.");
        });
        RuleFor(x => x.Adults).NotNull().WithMessage("Enter the number of adults.")
            .InclusiveBetween(1, 50).WithMessage("Adults must be between 1 and 50.");
        RuleFor(x => x.Children).InclusiveBetween(0, 50).WithMessage("Children must be between 0 and 50.");
    }
}

public sealed class CreatePackageRequestValidator : AbstractValidator<CreatePackageRequest>
{
    public CreatePackageRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().WithMessage("Enter a code, e.g. COUPLE.")
            .Matches("^[A-Za-z0-9_]{2,30}$").WithMessage("Code: 2–30 letters, digits or underscore, e.g. COUPLE or ROOM_BREAKFAST.");
        PackageRules.Apply(this, x => x.Name, x => x.Description, x => x.AccommodationTypeId, x => x.Nights, x => x.Adults,
            x => x.Children, x => x.Price, x => x.SortOrder, x => x.Components);
    }
}

public sealed class UpdatePackageRequestValidator : AbstractValidator<UpdatePackageRequest>
{
    public UpdatePackageRequestValidator() =>
        PackageRules.Apply(this, x => x.Name, x => x.Description, x => x.AccommodationTypeId, x => x.Nights, x => x.Adults,
            x => x.Children, x => x.Price, x => x.SortOrder, x => x.Components);
}

public sealed class PackageComponentInputValidator : AbstractValidator<PackageComponentInput>
{
    public PackageComponentInputValidator()
    {
        RuleFor(x => x.Category).Must(c => RevenueCategories.All.Contains(c))
            .WithMessage("Category must be ROOM, FOOD, TRANSPORT or ACTIVITY.");
        RuleFor(x => x.Description).NotEmpty().WithMessage("Say what this part includes, e.g. Breakfast for two.")
            .MaximumLength(200).WithMessage("Description can be at most 200 characters.");
        RuleFor(x => x.SharePercent).GreaterThan(0).WithMessage("Share must be more than 0%.")
            .LessThanOrEqualTo(100).WithMessage("Share can't be more than 100%.")
            .Must(Money.InPaise).WithMessage("Share can have at most 2 decimal places, e.g. 12.50.");
    }
}

internal static class PackageRules
{
    public static void Apply<T>(AbstractValidator<T> v, Expression<Func<T, string>> name, Expression<Func<T, string?>> description,
        Expression<Func<T, Guid>> typeId, Expression<Func<T, short>> nights, Expression<Func<T, short>> adults,
        Expression<Func<T, short>> children, Expression<Func<T, decimal>> price, Expression<Func<T, int>> sortOrder,
        Expression<Func<T, IEnumerable<PackageComponentInput>>> components)
    {
        v.RuleFor(name).NotEmpty().WithMessage("Enter a name, e.g. Couple getaway.")
            .MaximumLength(100).WithMessage("Name can be at most 100 characters.");
        v.RuleFor(description).MaximumLength(2000).WithMessage("Description can be at most 2000 characters.");
        v.RuleFor(typeId).NotEmpty().WithMessage("Choose the room type this package includes.");
        v.RuleFor(nights).InclusiveBetween((short)1, (short)30).WithMessage("Nights must be between 1 and 30.");
        v.RuleFor(adults).InclusiveBetween((short)1, (short)50).WithMessage("Adults must be between 1 and 50.");
        v.RuleFor(children).InclusiveBetween((short)0, (short)50).WithMessage("Children must be between 0 and 50.");
        Money.Rules(v, price, "Package price", positive: true);
        v.RuleFor(sortOrder).InclusiveBetween(0, 100000).WithMessage("Sort order must be between 0 and 100000.");

        v.RuleFor(components).NotEmpty().WithMessage("Add at least one component; a package always includes the room.")
            .Must(c => c is null || c.Count() <= 20).WithMessage("A package can have at most 20 components.");
        v.RuleForEach(components).SetValidator(new PackageComponentInputValidator());
        v.RuleFor(components)
            .Must(c => c is null || !c.Any() || c.Any(x => x?.Category == RevenueCategories.Room))
            .WithMessage("Add a ROOM component: every package includes the stay.")
            .Must(c => c is null || !c.Any() || c.Sum(x => x?.SharePercent ?? 0) == 100m)
            .WithMessage((_, c) => $"The shares add up to {c!.Sum(x => x?.SharePercent ?? 0).ToString("0.##", CultureInfo.InvariantCulture)}%; they must add up to exactly 100%.");
    }
}
