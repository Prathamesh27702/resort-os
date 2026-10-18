using FluentValidation;
using Resort.Contracts;
using Resort.Domain.FrontDesk;

namespace Resort.Application.Validation;

// Field rules for the front desk (Sprint 7). Rules that need the property's data (today's date, rooms, housekeeping,
// settings) are in FrontDeskService and are reported under the same field names.

public sealed class AssignRoomRequestValidator : AbstractValidator<AssignRoomRequest>
{
    public AssignRoomRequestValidator()
    {
        RuleFor(x => x.ReservationUnitId).NotEmpty().WithMessage("Choose which room of the booking to assign.");
        RuleFor(x => x.UnitId).NotEmpty().WithMessage("Choose the room to put the guest in.");
        RuleFor(x => x.Reason).MaximumLength(500).WithMessage("The reason can be at most 500 characters.");
    }
}

public sealed class WalkInRequestValidator : AbstractValidator<WalkInRequest>
{
    public WalkInRequestValidator()
    {
        RuleFor(x => x.GuestId).Must((x, id) => (id is not null) != (x.NewGuest is not null))
            .WithMessage("Choose a returning guest, or enter the new guest's details (one of the two).")
            .Must(id => id is null || id != Guid.Empty).WithMessage("That guest is empty. Search for the guest again.");
        RuleFor(x => x.NewGuest!).SetValidator(new CreateGuestRequestValidator()).When(x => x.NewGuest is not null);
        RuleFor(x => x.Rooms).NotEmpty().WithMessage("Choose at least one room.")
            .Must(r => r is null || r.Count <= StayRules.MaxRooms).WithMessage($"A walk-in can have at most {StayRules.MaxRooms} rooms; split a bigger group.");
        RuleForEach(x => x.Rooms).ChildRules(room =>
        {
            room.RuleFor(r => r.AccommodationTypeId).NotEmpty().WithMessage("Choose a room type.");
            room.RuleFor(r => r.UnitId).Must(id => id is null || id != Guid.Empty).WithMessage("Choose a room, or leave it empty for any ready one.");
            room.RuleFor(r => r.Adults).GreaterThanOrEqualTo((short)1).WithMessage("Each room needs at least one adult.")
                .LessThanOrEqualTo((short)50).WithMessage("At most 50 adults in one room line.");
            room.RuleFor(r => r.Children).GreaterThanOrEqualTo((short)0).WithMessage("Children can't be negative.")
                .LessThanOrEqualTo((short)50).WithMessage("At most 50 children in one room line.");
        });
        RuleFor(x => x.RatePlanId).Must(id => id is null || id != Guid.Empty).WithMessage("Choose a rate plan, or leave it empty for the default.");
        RuleFor(x => x.SpecialRequests).MaximumLength(2000).WithMessage("Special requests can be at most 2000 characters.");
        RuleFor(x => x.Note).MaximumLength(2000).WithMessage("The note can be at most 2000 characters.");
        RuleFor(x => x.OverrideReason).MaximumLength(500).WithMessage("The reason can be at most 500 characters.");
    }
}

public sealed class SetHousekeepingRequestValidator : AbstractValidator<SetHousekeepingRequest>
{
    public SetHousekeepingRequestValidator()
    {
        RuleFor(x => x.Status).Must(s => s is not null && HousekeepingStatuses.All.Contains(s.Trim().ToUpperInvariant()))
            .WithMessage($"Choose a cleaning state: {string.Join(", ", HousekeepingStatuses.All)}.");
        RuleFor(x => x.Reason).MaximumLength(500).WithMessage("The reason can be at most 500 characters.");
        RuleFor(x => x.BedId).Must(id => id is null || id != Guid.Empty).WithMessage("Choose the bed, or leave it empty for a room.");
    }
}
