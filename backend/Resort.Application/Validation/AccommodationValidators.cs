using FluentValidation;
using Resort.Contracts;
using Resort.Domain.Accommodation;

namespace Resort.Application.Validation;

// Field rules mirror the CHECK constraints in database/migrations/V004__accommodation.sql, so users get a field
// message instead of a database error.

public sealed class CreateAccommodationTypeRequestValidator : AbstractValidator<CreateAccommodationTypeRequest>
{
    public CreateAccommodationTypeRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().Matches("^[A-Za-z0-9_]{2,30}$")
            .WithMessage("Code: 2–30 letters, digits or underscore, e.g. DELUXE_ROOM.");
        TypeRules.Apply(this, x => x.Name, x => x.Description, x => x.InventoryMode, x => x.BaseOccupancy,
            x => x.MaxOccupancy, x => x.SizeSqm, x => x.SortOrder);
    }
}

public sealed class UpdateAccommodationTypeRequestValidator : AbstractValidator<UpdateAccommodationTypeRequest>
{
    public UpdateAccommodationTypeRequestValidator() =>
        TypeRules.Apply(this, x => x.Name, x => x.Description, x => x.InventoryMode, x => x.BaseOccupancy,
            x => x.MaxOccupancy, x => x.SizeSqm, x => x.SortOrder);
}

internal static class TypeRules
{
    public static void Apply<T>(AbstractValidator<T> v,
        System.Linq.Expressions.Expression<Func<T, string>> name,
        System.Linq.Expressions.Expression<Func<T, string?>> description,
        System.Linq.Expressions.Expression<Func<T, string>> mode,
        System.Linq.Expressions.Expression<Func<T, short>> baseOcc,
        System.Linq.Expressions.Expression<Func<T, short>> maxOcc,
        System.Linq.Expressions.Expression<Func<T, decimal?>> size,
        System.Linq.Expressions.Expression<Func<T, int>> sortOrder)
    {
        var getMode = mode.Compile();
        var getBase = baseOcc.Compile();
        v.RuleFor(name).NotEmpty().MaximumLength(100);
        v.RuleFor(description).MaximumLength(2000);
        v.RuleFor(mode).Must(m => InventoryModes.All.Contains(m))
            .WithMessage("Inventory mode must be UNIT (sell the whole room) or BED (sell each bed).");
        v.RuleFor(baseOcc).InclusiveBetween((short)1, (short)50);
        v.RuleFor(maxOcc).InclusiveBetween((short)1, (short)50)
            .Must((x, max) => max >= getBase(x)).WithMessage("Maximum guests can't be less than standard guests.");
        v.RuleFor(maxOcc).Must((x, max) => getMode(x) != InventoryModes.Bed || (max == 1 && getBase(x) == 1))
            .WithMessage("A bed-based type sleeps one guest per bed: set standard and maximum guests to 1.");
        v.RuleFor(size).Must(s => s is null || (s > 0 && s < 100000)).WithMessage("Size must be a positive number of m².");
        v.RuleFor(sortOrder).InclusiveBetween(0, 100000);
    }
}

public sealed class CreateRoomRequestValidator : AbstractValidator<CreateRoomRequest>
{
    public CreateRoomRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().Matches("^[A-Za-z0-9_-]{1,20}$")
            .WithMessage("Room number/code: up to 20 letters, digits, '-' or '_', e.g. 101 or DORM.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.AccommodationTypeId).NotEmpty();
        RuleFor(x => x.Floor).MaximumLength(20);
        RuleFor(x => x.Notes).MaximumLength(1000);
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 100000);
    }
}

public sealed class UpdateRoomRequestValidator : AbstractValidator<UpdateRoomRequest>
{
    public UpdateRoomRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.AccommodationTypeId).NotEmpty();
        RuleFor(x => x.Floor).MaximumLength(20);
        RuleFor(x => x.Notes).MaximumLength(1000);
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 100000);
    }
}

public sealed class CreateBedRequestValidator : AbstractValidator<CreateBedRequest>
{
    public CreateBedRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().Matches("^[A-Za-z0-9_-]{1,20}$").WithMessage("Bed code: up to 20 letters, digits, '-' or '_', e.g. B01.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.BedType).Must(t => BedTypes.All.Contains(t))
            .WithMessage($"Bed type must be one of: {string.Join(", ", BedTypes.All)}.");
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 100000);
    }
}

public sealed class UpdateBedRequestValidator : AbstractValidator<UpdateBedRequest>
{
    public UpdateBedRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.BedType).Must(t => BedTypes.All.Contains(t))
            .WithMessage($"Bed type must be one of: {string.Join(", ", BedTypes.All)}.");
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 100000);
    }
}

public sealed class CreateBlockRequestValidator : AbstractValidator<CreateBlockRequest>
{
    public CreateBlockRequestValidator()
    {
        RuleFor(x => x.UnitId).NotEmpty();
        RuleFor(x => x.BlockType).Must(t => BlockTypes.All.Contains(t))
            .WithMessage($"Block type must be one of: {string.Join(", ", BlockTypes.All)}.");
        RuleFor(x => x.EndDate).GreaterThan(x => x.StartDate).WithMessage("End date must be after the start date (the end date is not included).");
        RuleFor(x => x.EndDate).Must((x, end) => end.DayNumber - x.StartDate.DayNumber <= 366)
            .WithMessage("A block can't be longer than 366 nights.");
        RuleFor(x => x.Reason).Must(r => r is not null && r.Trim().Length >= 3).WithMessage("Give a reason (at least 3 characters).")
            .MaximumLength(500);
    }
}

public sealed class ReleaseBlockRequestValidator : AbstractValidator<ReleaseBlockRequest>
{
    public ReleaseBlockRequestValidator() =>
        RuleFor(x => x.Reason).Must(r => r is not null && r.Trim().Length >= 3).WithMessage("Give a reason (at least 3 characters).")
            .MaximumLength(500);
}
