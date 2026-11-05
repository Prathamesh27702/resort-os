using System.Text.RegularExpressions;
using FluentValidation;
using Resort.Contracts;

namespace Resort.Application.Validation;

// Field rules for beach transport (Sprint 21). Rules that need data (the seats left, the stay's dates, a vehicle or a
// driver already on another trip) are in TransportService and TransportBookingService under the same field names.

internal static class TransportRules
{
    /// <summary>MH 07 AB 1234 → MH07AB1234 (spaces and dashes dropped, upper case).</summary>
    public static string Registration(string? r) => Regex.Replace(r ?? "", "[\\s-]", "").ToUpperInvariant();
}

public sealed class SaveVehicleRequestValidator : AbstractValidator<SaveVehicleRequest>
{
    public SaveVehicleRequestValidator()
    {
        RuleFor(x => x.Code).Must(ActivityRules.Code).WithMessage("The code is 1–10 letters or digits starting with a letter, e.g. VAN1.");
        RuleFor(x => x.Name).Must(n => ActivityRules.Required(n, 2, 60)).WithMessage("Give the vehicle a name, 2–60 characters, e.g. Force Traveller.");
        RuleFor(x => x.Registration).Must(r => Regex.IsMatch(TransportRules.Registration(r), "^[A-Z0-9]{4,12}$"))
            .WithMessage("The registration number is 4–12 letters and digits, e.g. MH 07 AB 1234.");
        RuleFor(x => x.Seats).InclusiveBetween(1, 60).WithMessage("Passenger seats are 1–60 (not counting the driver).");
        RuleFor(x => x.Note).Must(n => ActivityRules.Text(n, 1, 200)).WithMessage("The note is at most 200 characters.");
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 1000).WithMessage("The order is 0–1000.");
    }
}

public sealed class SaveTransportRouteRequestValidator : AbstractValidator<SaveTransportRouteRequest>
{
    public SaveTransportRouteRequestValidator()
    {
        RuleFor(x => x.Code).Must(ActivityRules.Code).WithMessage("The code is 1–10 letters or digits starting with a letter, e.g. BEACH.");
        RuleFor(x => x.Name).Must(n => ActivityRules.Required(n, 2, 60)).WithMessage("Give the route a name, 2–60 characters, e.g. Beach shuttle.");
        RuleFor(x => x.Origin).Must(n => ActivityRules.Required(n, 2, 60)).WithMessage("Say where it starts, 2–60 characters, e.g. Resort gate.");
        RuleFor(x => x.Destination).Must(n => ActivityRules.Required(n, 2, 60)).WithMessage("Say where it goes, 2–60 characters, e.g. Tarkarli beach.");
        RuleFor(x => x.DistanceKm).Must(d => d > 0 && d <= 500 && decimal.Round(d, 1) == d)
            .WithMessage("The distance is more than 0 and up to 500 km, with at most one decimal, e.g. 8 or 8.5.");
        RuleFor(x => x.DurationMinutes).InclusiveBetween(5, 360).WithMessage("A trip takes 5 minutes to 6 hours (5–360 minutes).");
        Money.Rules(this, x => x.Price, "The price per seat");
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 1000).WithMessage("The order is 0–1000.");
    }
}

public sealed class CreateTripsRequestValidator : AbstractValidator<CreateTripsRequest>
{
    public CreateTripsRequestValidator()
    {
        RuleFor(x => x.RouteId).NotEmpty().WithMessage("Choose the route.");
        RuleFor(x => x.VehicleId).NotEmpty().WithMessage("Choose the vehicle.");
        RuleFor(x => x.FromDate).Must(d => d.Year is >= 2000 and <= 2100).WithMessage("Choose the first day.");
        RuleFor(x => x.ToDate).Must((x, d) => d >= x.FromDate).WithMessage("The last day is before the first day.");
        RuleFor(x => x.Times).Must(t => t is { Count: >= 1 and <= 24 }).WithMessage("Choose 1–24 departure times, e.g. 08:00.");
        RuleFor(x => x.Price).Must(p => p is null or (>= 0 and <= Money.Max) && (p is null || Money.InPaise(p.Value)))
            .WithMessage("The price per seat is ₹0 or more, in rupees and paise.");
    }
}

public sealed class UpdateTripRequestValidator : AbstractValidator<UpdateTripRequest>
{
    public UpdateTripRequestValidator()
    {
        RuleFor(x => x.VehicleId).NotEmpty().WithMessage("Choose the vehicle.");
        RuleFor(x => x.Capacity).InclusiveBetween(1, 60).WithMessage("Seats on a trip are 1–60.");
        Money.Rules(this, x => x.Price, "The price per seat");
        RuleFor(x => x.Note).Must(n => ActivityRules.Text(n, 1, 200)).WithMessage("The note is at most 200 characters.");
    }
}

public sealed class BookTransportRequestValidator : AbstractValidator<BookTransportRequest>
{
    public BookTransportRequestValidator()
    {
        RuleFor(x => x.TripId).NotEmpty().WithMessage("Choose the trip.");
        RuleFor(x => x.Seats).InclusiveBetween(1, 60).WithMessage("Book 1–60 seats.");
        RuleFor(x => x.GuestName).Must(n => ActivityRules.Text(n, 2, 100)).WithMessage("The name is 2–100 characters.");
        RuleFor(x => x.ComplimentaryReason).Must((x, r) => !x.Complimentary || ActivityRules.Required(r, 3, 200))
            .WithMessage("Say why it is complimentary (3–200 characters), e.g. Honeymoon welcome.");
        RuleFor(x => x.Note).Must(n => ActivityRules.Text(n, 1, 200)).WithMessage("The note is at most 200 characters, e.g. Pick up at the cottages.");
    }
}

public sealed class CancelPassengerRequestValidator : AbstractValidator<CancelPassengerRequest>
{
    public CancelPassengerRequestValidator() =>
        RuleFor(x => x.Reason).Must(r => ActivityRules.Required(r, 3, 300)).WithMessage("Say why it is cancelled (3–300 characters), e.g. Guest changed plans.");
}

public sealed class CancelTripRequestValidator : AbstractValidator<CancelTripRequest>
{
    public CancelTripRequestValidator() =>
        RuleFor(x => x.Reason).Must(r => ActivityRules.Required(r, 3, 300)).WithMessage("Say why the trip is cancelled (3–300 characters), e.g. Van broke down.");
}
