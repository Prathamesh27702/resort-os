using System.Text.RegularExpressions;
using FluentValidation;
using Resort.Contracts;
using Resort.Domain.Activities;

namespace Resort.Application.Validation;

// Field rules for activities and equipment (Sprint 20). Rules that need data (the seats left, the stay's dates, an item's
// status) are in ActivityBookingService and EquipmentService under the same field names.

internal static class ActivityRules
{
    public static bool Code(string? c) => c is not null && Regex.IsMatch(c.Trim().ToUpperInvariant(), "^[A-Z][A-Z0-9]{0,9}$");
    public static bool Text(string? s, int min, int max) => s is null || s.Trim().Length == 0 || s.Trim().Length is var n && n >= min && n <= max;
    public static bool Required(string? s, int min, int max) => s is not null && s.Trim().Length >= min && s.Trim().Length <= max;
}

public sealed class SaveActivityRequestValidator : AbstractValidator<SaveActivityRequest>
{
    public SaveActivityRequestValidator()
    {
        RuleFor(x => x.Code).Must(ActivityRules.Code).WithMessage("The code is 1–10 letters or digits starting with a letter, e.g. KAYAK.");
        RuleFor(x => x.Name).Must(n => ActivityRules.Required(n, 2, 60)).WithMessage("Give the activity a name, 2–60 characters, e.g. Kayaking.");
        RuleFor(x => x.Description).Must(d => ActivityRules.Text(d, 1, 500)).WithMessage("The description is at most 500 characters.");
        RuleFor(x => x.MeetingPoint).Must(d => ActivityRules.Text(d, 1, 100)).WithMessage("Where to meet is at most 100 characters.");
        RuleFor(x => x.DurationMinutes).InclusiveBetween(15, 720).WithMessage("An activity lasts 15 minutes to 12 hours (15–720 minutes).");
        RuleFor(x => x.Capacity).InclusiveBetween(1, 200).WithMessage("Seats per session are 1–200.");
        Money.Rules(this, x => x.Price, "The price per person");
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 1000).WithMessage("The order is 0–1000.");
    }
}

public sealed class CreateSessionsRequestValidator : AbstractValidator<CreateSessionsRequest>
{
    public CreateSessionsRequestValidator()
    {
        RuleFor(x => x.ActivityId).NotEmpty().WithMessage("Choose the activity.");
        RuleFor(x => x.FromDate).Must(d => d.Year is >= 2000 and <= 2100).WithMessage("Choose the first day.");
        RuleFor(x => x.ToDate).Must((x, d) => d >= x.FromDate).WithMessage("The last day is before the first day.");
        RuleFor(x => x.Times).Must(t => t is { Count: >= 1 and <= 12 }).WithMessage("Choose 1–12 start times, e.g. 07:00.");
        RuleFor(x => x.Capacity).Must(c => c is null or (>= 1 and <= 200)).WithMessage("Seats per session are 1–200.");
        RuleFor(x => x.Price).Must(p => p is null or (>= 0 and <= Money.Max) && (p is null || Money.InPaise(p.Value)))
            .WithMessage("The price per person is ₹0 or more, in rupees and paise.");
    }
}

public sealed class UpdateSessionRequestValidator : AbstractValidator<UpdateSessionRequest>
{
    public UpdateSessionRequestValidator()
    {
        RuleFor(x => x.Capacity).InclusiveBetween(1, 200).WithMessage("Seats per session are 1–200.");
        Money.Rules(this, x => x.Price, "The price per person");
        RuleFor(x => x.Note).Must(n => ActivityRules.Text(n, 1, 200)).WithMessage("The note is at most 200 characters.");
    }
}

public sealed class BookActivityRequestValidator : AbstractValidator<BookActivityRequest>
{
    public BookActivityRequestValidator()
    {
        RuleFor(x => x.SessionId).NotEmpty().WithMessage("Choose the session.");
        RuleFor(x => x.Seats).InclusiveBetween(1, 50).WithMessage("Book 1–50 seats.");
        RuleFor(x => x.GuestName).Must(n => ActivityRules.Text(n, 2, 100)).WithMessage("The name is 2–100 characters.");
        RuleFor(x => x.ComplimentaryReason).Must((x, r) => !x.Complimentary || ActivityRules.Required(r, 3, 200))
            .WithMessage("Say why it is complimentary (3–200 characters), e.g. Honeymoon welcome.");
        RuleFor(x => x.Note).Must(n => ActivityRules.Text(n, 1, 200)).WithMessage("The note is at most 200 characters.");
    }
}

public sealed class CancelActivityBookingRequestValidator : AbstractValidator<CancelActivityBookingRequest>
{
    public CancelActivityBookingRequestValidator() =>
        RuleFor(x => x.Reason).Must(r => ActivityRules.Required(r, 3, 300)).WithMessage("Say why it is cancelled (3–300 characters), e.g. Guest changed plans.");
}

public sealed class WeatherCancelRequestValidator : AbstractValidator<WeatherCancelRequest>
{
    public WeatherCancelRequestValidator()
    {
        RuleFor(x => x.Reason).Must(r => ActivityRules.Required(r, 3, 300)).WithMessage("Say what the weather is doing (3–300 characters), e.g. Rough sea, red flag.");
        RuleFor(x => x.Moves).Must(m => m is null || m.All(x => x.BookingId != Guid.Empty && x.ToSessionId != Guid.Empty))
            .WithMessage("Choose the session each moved booking goes to.");
    }
}

public sealed class SaveEquipmentTypeRequestValidator : AbstractValidator<SaveEquipmentTypeRequest>
{
    public SaveEquipmentTypeRequestValidator()
    {
        RuleFor(x => x.Code).Must(ActivityRules.Code).WithMessage("The code is 1–10 letters or digits starting with a letter, e.g. LJ.");
        RuleFor(x => x.Name).Must(n => ActivityRules.Required(n, 2, 60)).WithMessage("Give it a name, 2–60 characters, e.g. Life jacket.");
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 1000).WithMessage("The order is 0–1000.");
    }
}

public sealed class AddEquipmentRequestValidator : AbstractValidator<AddEquipmentRequest>
{
    public AddEquipmentRequestValidator()
    {
        RuleFor(x => x.EquipmentTypeId).NotEmpty().WithMessage("Choose the kind of equipment.");
        RuleFor(x => x.Tag).Must(t => t is null || t.Trim().Length == 0 || Regex.IsMatch(t.Trim().ToUpperInvariant(), "^[A-Z0-9][A-Z0-9-]{0,19}$"))
            .WithMessage("A tag is 1–20 letters, digits or dashes, e.g. LJ-07.");
        RuleFor(x => x.Prefix).Must((x, p) => !string.IsNullOrWhiteSpace(x.Tag) || (p is not null && Regex.IsMatch(p.Trim().ToUpperInvariant(), "^[A-Z0-9]{1,8}$")))
            .WithMessage("Give a tag, or a prefix of 1–8 letters or digits (e.g. LJ) to number new items.");
        RuleFor(x => x.Count).Must((x, c) => !string.IsNullOrWhiteSpace(x.Tag) || c is >= 1 and <= 50).WithMessage("Add 1–50 items at a time.");
        RuleFor(x => x.Note).Must(n => ActivityRules.Text(n, 1, 200)).WithMessage("The note is at most 200 characters.");
    }
}

public sealed class ChangeEquipmentStatusRequestValidator : AbstractValidator<ChangeEquipmentStatusRequest>
{
    public ChangeEquipmentStatusRequestValidator()
    {
        RuleFor(x => x.Status).Must(s => s is not null && EquipmentStatuses.ByHand.Contains(s.Trim().ToUpperInvariant()))
            .WithMessage("Choose available, damaged, in maintenance, lost or retired.");
        RuleFor(x => x.Note).Must((x, n) => x.Status?.Trim().ToUpperInvariant() == EquipmentStatuses.Available ? ActivityRules.Text(n, 3, 300) : ActivityRules.Required(n, 3, 300))
            .WithMessage("Say what is wrong or what was done (3–300 characters), e.g. Strap torn.");
    }
}

public sealed class IssueEquipmentRequestValidator : AbstractValidator<IssueEquipmentRequest>
{
    public IssueEquipmentRequestValidator()
    {
        RuleFor(x => x.IssuedTo).Must(n => ActivityRules.Text(n, 2, 100)).WithMessage("The name is 2–100 characters.");
        RuleFor(x => x.EquipmentIds).Must((x, ids) => ids is { Count: > 0 } || x.EquipmentTypeId is not null)
            .WithMessage("Choose the items, or the kind of equipment and how many.");
        RuleFor(x => x.EquipmentIds).Must(ids => ids is null || ids.Count <= Activities.EquipmentService.MaxIssue).WithMessage($"Issue at most {Activities.EquipmentService.MaxIssue} items at a time.");
        RuleFor(x => x.Quantity).Must((x, q) => x.EquipmentIds is { Count: > 0 } || q is >= 1 and <= Activities.EquipmentService.MaxIssue)
            .WithMessage($"Say how many, 1–{Activities.EquipmentService.MaxIssue}.");
    }
}

public sealed class ReturnEquipmentRequestValidator : AbstractValidator<ReturnEquipmentRequest>
{
    public ReturnEquipmentRequestValidator()
    {
        RuleFor(x => x.Condition).Must(c => c is not null && ReturnConditions.All.Contains(c.Trim().ToUpperInvariant()))
            .WithMessage("Say how it came back: good, damaged or lost.");
        RuleFor(x => x.Note).Must((x, n) => x.Condition?.Trim().ToUpperInvariant() == ReturnConditions.Good ? ActivityRules.Text(n, 3, 300) : ActivityRules.Required(n, 3, 300))
            .WithMessage("Say what happened (3–300 characters), e.g. Buckle broken.");
    }
}
