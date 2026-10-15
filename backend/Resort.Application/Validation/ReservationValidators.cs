using System.Linq.Expressions;
using FluentValidation;
using Resort.Application.Reservations;
using Resort.Contracts;
using Resort.Domain.Reservations;

namespace Resort.Application.Validation;

// Field rules mirror the CHECK constraints in database/migrations/V007__reservations.sql, so users get a field message
// that says why, instead of a database error. Rules that need the property's data (today's date, rooms, guests) are in
// ReservationService and are reported under the same field names.

public sealed class CreateReservationRequestValidator : AbstractValidator<CreateReservationRequest>
{
    public CreateReservationRequestValidator()
    {
        StayRules.Apply(this, x => x.CheckIn, x => x.CheckOut, x => x.Rooms, x => x.Rooms, x => x.SpecialRequests);
        RuleFor(x => x.GuestIds).NotEmpty().WithMessage("Choose the guest the booking is for.")
            .Must(ids => ids is null || ids.Count <= 50).WithMessage("At most 50 guests can be named on one booking.")
            .Must(ids => ids is null || ids.All(id => id != Guid.Empty)).WithMessage("One of the guests is empty. Search for the guest again.")
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count).WithMessage("The same guest is named twice.");
        RuleFor(x => x.SourceId).NotEmpty().WithMessage("Choose where the booking came from (walk-in, phone, an OTA …).");
        RuleFor(x => x.RatePlanId).Must(id => id is null || id != Guid.Empty).WithMessage("Choose a rate plan, or leave it empty for the default.");
        RuleFor(x => x.Status)
            .Must(s => string.IsNullOrWhiteSpace(s) || ReservationStatuses.Initial.Contains(s.Trim().ToUpperInvariant()))
            .WithMessage("A new booking can be CONFIRMED, PENDING (holds the rooms until confirmed) or INQUIRY (holds nothing).");
        RuleFor(x => x.Note).MaximumLength(2000).WithMessage("The note can be at most 2000 characters.");
    }
}

public sealed class UpdateReservationRequestValidator : AbstractValidator<UpdateReservationRequest>
{
    public UpdateReservationRequestValidator() =>
        StayRules.Apply(this, x => x.CheckIn, x => x.CheckOut, x => x.Rooms, x => x.Rooms, x => x.SpecialRequests);
}

public sealed class ReservationStatusRequestValidator : AbstractValidator<ReservationStatusRequest>
{
    public ReservationStatusRequestValidator() =>
        RuleFor(x => x.Reason).MaximumLength(500).WithMessage("The reason can be at most 500 characters.");
}

public sealed class AddReservationNoteRequestValidator : AbstractValidator<AddReservationNoteRequest>
{
    public AddReservationNoteRequestValidator() =>
        RuleFor(x => x.Note).Must(n => !string.IsNullOrWhiteSpace(n)).WithMessage("Write the note first.")
            .MaximumLength(2000).WithMessage("A note can be at most 2000 characters.");
}

internal static class StayRules
{
    public const int MaxRooms = 20;

    public static void Apply<T>(AbstractValidator<T> v, Expression<Func<T, DateOnly>> checkIn, Expression<Func<T, DateOnly>> checkOut,
        Expression<Func<T, IReadOnlyList<ReservationRoomRequest>>> rooms, Expression<Func<T, IEnumerable<ReservationRoomRequest>>> eachRoom,
        Expression<Func<T, string?>> specialRequests)
    {
        var inF = checkIn.Compile();
        v.RuleFor(checkOut).Must((x, o) => o > inF(x)).WithMessage("The check-out date must be after check-in.")
            .Must((x, o) => o.DayNumber - inF(x).DayNumber <= ReservationService.MaxNights)
            .WithMessage(x => $"A stay can be at most {ReservationService.MaxNights} nights; this one is " +
                              $"{checkOut.Compile()(x).DayNumber - inF(x).DayNumber}.");
        v.RuleFor(rooms).NotEmpty().WithMessage("Choose at least one room.")
            .Must(r => r is null || r.Count <= MaxRooms).WithMessage($"One booking can have at most {MaxRooms} rooms; split a bigger group.");
        v.RuleForEach(eachRoom).ChildRules(room =>
        {
            room.RuleFor(r => r.AccommodationTypeId).NotEmpty().WithMessage("Choose a room type.");
            room.RuleFor(r => r.UnitId).Must(id => id is null || id != Guid.Empty).WithMessage("Choose a room, or leave it empty for any free one.");
            room.RuleFor(r => r.Adults).GreaterThanOrEqualTo((short)1).WithMessage("Each room needs at least one adult.")
                .LessThanOrEqualTo((short)50).WithMessage("At most 50 adults in one room line.");
            room.RuleFor(r => r.Children).GreaterThanOrEqualTo((short)0).WithMessage("Children can't be negative.")
                .LessThanOrEqualTo((short)50).WithMessage("At most 50 children in one room line.");
        });
        v.RuleFor(specialRequests).MaximumLength(2000).WithMessage("Special requests can be at most 2000 characters.");
    }
}
