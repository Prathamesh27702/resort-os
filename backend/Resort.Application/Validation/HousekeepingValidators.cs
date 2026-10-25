using FluentValidation;
using Resort.Contracts;
using Resort.Domain.Housekeeping;

namespace Resort.Application.Validation;

// Field rules for housekeeping and maintenance (Sprint 13). Rules that need the property's data (rooms, today's date, who
// is on staff, the ticket's status) are in HousekeepingService and MaintenanceService, under the same field names.

public sealed class FinishTaskRequestValidator : AbstractValidator<FinishTaskRequest>
{
    public FinishTaskRequestValidator()
    {
        RuleFor(x => x.Checked).Must(c => c is null || c.Count <= 100).WithMessage("At most 100 checklist lines.");
        RuleFor(x => x.Note).MaximumLength(500).WithMessage("The note can be at most 500 characters.");
    }
}

public sealed class InspectTaskRequestValidator : AbstractValidator<InspectTaskRequest>
{
    public InspectTaskRequestValidator()
    {
        RuleFor(x => x.Result).Must(r => r is not null && r.Trim().ToUpperInvariant() is "PASS" or "FAIL" or "START")
            .WithMessage("Choose PASS (the room is ready), FAIL (send it back) or START (inspecting now).");
        RuleFor(x => x.Note).MaximumLength(500).WithMessage("The note can be at most 500 characters.");
    }
}

public sealed class CreateTaskRequestValidator : AbstractValidator<CreateTaskRequest>
{
    public CreateTaskRequestValidator()
    {
        RuleFor(x => x.UnitId).NotEmpty().WithMessage("Choose the room to clean.");
        RuleFor(x => x.BedId).Must(id => id is null || id != Guid.Empty).WithMessage("Choose the bed, or leave it empty for a room.");
        RuleFor(x => x.TaskType).Must(t => HousekeepingTaskTypes.All.Contains(t))
            .WithMessage($"Choose what kind of cleaning: {string.Join(", ", HousekeepingTaskTypes.All)}.");
        RuleFor(x => x.Note).MaximumLength(500).WithMessage("The note can be at most 500 characters.");
        RuleFor(x => x.AssignTo).Must(id => id is null || id != Guid.Empty).WithMessage("Choose who cleans it, or leave it empty.");
    }
}

public sealed class SaveChecklistTemplateRequestValidator : AbstractValidator<SaveChecklistTemplateRequest>
{
    public SaveChecklistTemplateRequestValidator()
    {
        RuleFor(x => x.Name).Must(n => n is not null && n.Trim().Length is >= 2 and <= 80)
            .WithMessage("Give the checklist a name of 2–80 characters, e.g. Departure clean.");
        RuleFor(x => x.TaskType).Must(t => HousekeepingTaskTypes.All.Contains(t))
            .WithMessage($"Choose the kind of cleaning it is for: {string.Join(", ", HousekeepingTaskTypes.All)}.");
        RuleFor(x => x.Items).NotEmpty().WithMessage("Add at least one line to the checklist.")
            .Must(i => i is null || i.Count <= 40).WithMessage("A checklist can have at most 40 lines.");
        RuleForEach(x => x.Items).ChildRules(item =>
            item.RuleFor(i => i.Label).Must(l => l is not null && l.Trim().Length is >= 2 and <= 120)
                .WithMessage("Each line needs 2–120 characters, e.g. Fresh towels."));
    }
}

public sealed class CreateTicketRequestValidator : AbstractValidator<CreateTicketRequest>
{
    public CreateTicketRequestValidator()
    {
        RuleFor(x => x.Title).Must(t => t is not null && t.Trim().Length is >= 3 and <= 120)
            .WithMessage("Say what is wrong in 3–120 characters, e.g. AC not cooling.");
        RuleFor(x => x.Description).MaximumLength(2000).WithMessage("The details can be at most 2000 characters.");
        RuleFor(x => x.Category).Must(c => MaintenanceCategories.All.Contains(c))
            .WithMessage($"Choose a kind of problem: {string.Join(", ", MaintenanceCategories.All)}.");
        RuleFor(x => x.Priority).Must(p => MaintenancePriorities.All.Contains(p))
            .WithMessage($"Choose how urgent it is: {string.Join(", ", MaintenancePriorities.All)}.");
        RuleFor(x => x.Location).MaximumLength(120).WithMessage("The location can be at most 120 characters.");
        RuleFor(x => x.Location).Must((x, l) => x.UnitId is not null || x.AssetId is not null || !string.IsNullOrWhiteSpace(l))
            .WithMessage("Say where the problem is: choose a room or equipment, or type the place (e.g. Pool deck).");
        RuleFor(x => x.UnitId).Must(id => id is null || id != Guid.Empty).WithMessage("Choose a room, or leave it empty.");
        RuleFor(x => x.AssetId).Must(id => id is null || id != Guid.Empty).WithMessage("Choose the equipment, or leave it empty.");
        RuleFor(x => x.ExpectedReadyOn).NotNull().When(x => x.OutOfOrder)
            .WithMessage("Say when the room should be usable again (the room is blocked until then).");
    }
}

public sealed class AssignTicketRequestValidator : AbstractValidator<AssignTicketRequest>
{
    public AssignTicketRequestValidator()
    {
        RuleFor(x => x.UserId).Must(id => id is null || id != Guid.Empty).WithMessage("Choose who does the repair.");
        RuleFor(x => x.Note).MaximumLength(500).WithMessage("The note can be at most 500 characters.");
    }
}

public sealed class TicketProgressRequestValidator : AbstractValidator<TicketProgressRequest>
{
    public TicketProgressRequestValidator()
    {
        RuleFor(x => x.Status).Must(s => s is null || s.Trim().ToUpperInvariant() is MaintenanceStatuses.InProgress or MaintenanceStatuses.OnHold)
            .WithMessage("Choose IN_PROGRESS (working on it) or ON_HOLD (waiting, e.g. for a part).");
        RuleFor(x => x.Note).Must(n => n is not null && n.Trim().Length is >= 3 and <= 2000)
            .WithMessage("Say what was done or what it is waiting for (3–2000 characters).");
    }
}

public sealed class ResolveTicketRequestValidator : AbstractValidator<ResolveTicketRequest>
{
    public ResolveTicketRequestValidator()
    {
        RuleFor(x => x.Resolution).Must(n => n is not null && n.Trim().Length is >= 3 and <= 2000)
            .WithMessage("Say what was fixed (3–2000 characters), e.g. Replaced the AC capacitor.");
    }
}

public sealed class CloseTicketRequestValidator : AbstractValidator<CloseTicketRequest>
{
    public CloseTicketRequestValidator()
    {
        RuleFor(x => x.Note).MaximumLength(500).WithMessage("The note can be at most 500 characters.");
    }
}

public sealed class AddTicketCommentRequestValidator : AbstractValidator<AddTicketCommentRequest>
{
    public AddTicketCommentRequestValidator()
    {
        RuleFor(x => x.Body).Must(n => n is not null && n.Trim().Length is >= 1 and <= 2000)
            .WithMessage("Write the comment (at most 2000 characters).");
    }
}

public sealed class SaveAssetRequestValidator : AbstractValidator<SaveAssetRequest>
{
    public SaveAssetRequestValidator()
    {
        RuleFor(x => x.Code).Must(c => c is not null && System.Text.RegularExpressions.Regex.IsMatch(c.Trim().ToUpperInvariant(), "^[A-Z0-9][A-Z0-9_-]{0,19}$"))
            .WithMessage("Use a code of 1–20 capitals, digits, - or _, starting with a letter or digit, e.g. AC-101.");
        RuleFor(x => x.Name).Must(n => n is not null && n.Trim().Length is >= 2 and <= 80)
            .WithMessage("Name the equipment in 2–80 characters, e.g. Split AC, Room 101.");
        RuleFor(x => x.Category).Must(c => MaintenanceCategories.All.Contains(c))
            .WithMessage($"Choose a kind: {string.Join(", ", MaintenanceCategories.All)}.");
        RuleFor(x => x.Location).MaximumLength(120).WithMessage("The location can be at most 120 characters.");
        RuleFor(x => x.UnitId).Must(id => id is null || id != Guid.Empty).WithMessage("Choose a room, or leave it empty.");
    }
}
