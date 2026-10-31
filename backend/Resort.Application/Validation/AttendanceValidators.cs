using FluentValidation;
using Resort.Contracts;
using Resort.Domain.Attendance;

namespace Resort.Application.Validation;

// Field rules for attendance and timesheets (Sprint 18). Rules that need data (is the phone inside the geofence, is the
// timesheet locked, did the person work that day) are in AttendanceService and TimesheetService, under the same field names.

internal static class AttendanceRules
{
    public static bool Action(string? a) => a is not null && AttendanceEventTypes.All.Contains(a.Trim().ToUpperInvariant());

    public const string ActionWords = "Choose clock in, start break, end break or clock out.";
}

public sealed class SaveGeofenceRequestValidator : AbstractValidator<SaveGeofenceRequest>
{
    public SaveGeofenceRequestValidator()
    {
        RuleFor(x => x.Name).Must(n => n is not null && n.Trim().Length is >= 2 and <= 60)
            .WithMessage("Give the area a name, 2–60 characters, e.g. Main gate.");
        RuleFor(x => x.Latitude).InclusiveBetween(-90m, 90m).WithMessage("The latitude is between -90 and 90, e.g. 15.544000 for Calangute.");
        RuleFor(x => x.Longitude).InclusiveBetween(-180m, 180m).WithMessage("The longitude is between -180 and 180, e.g. 73.755300 for Calangute.");
        RuleFor(x => x.RadiusM).InclusiveBetween(20, 2000).WithMessage("The radius is 20–2000 metres.");
    }
}

public sealed class ClockRequestValidator : AbstractValidator<ClockRequest>
{
    public ClockRequestValidator()
    {
        RuleFor(x => x.Action).Must(AttendanceRules.Action).WithMessage(AttendanceRules.ActionWords);
        RuleFor(x => x.Latitude).Must(l => l is null or (>= -90m and <= 90m)).WithMessage("The phone sent a latitude that isn't on the earth. Try again.");
        RuleFor(x => x.Longitude).Must(l => l is null or (>= -180m and <= 180m)).WithMessage("The phone sent a longitude that isn't on the earth. Try again.");
        RuleFor(x => x.Longitude).Must((x, l) => (l is null) == (x.Latitude is null)).WithMessage("The phone sent only half of its position. Try again.");
        RuleFor(x => x.AccuracyM).Must(a => a is null or >= 0).WithMessage("The phone sent a negative accuracy. Try again.");
    }
}

public sealed class ManagerClockRequestValidator : AbstractValidator<ManagerClockRequest>
{
    public ManagerClockRequestValidator()
    {
        RuleFor(x => x.EmployeeId).NotEmpty().WithMessage("Choose who this is for.");
        RuleFor(x => x.Action).Must(AttendanceRules.Action).WithMessage(AttendanceRules.ActionWords);
        RuleFor(x => x.Reason).Must(PeopleRules.Reason)
            .WithMessage("Say why you are recording this for them, 3–300 characters (e.g. Phone had no signal at the beach).");
    }
}

public sealed class SubmitTimesheetRequestValidator : AbstractValidator<SubmitTimesheetRequest>
{
    public SubmitTimesheetRequestValidator()
    {
        RuleFor(x => x.Note).Must(s => PurchaseRules.Optional(s, 1, 300)).WithMessage("The note can be at most 300 characters.");
    }
}

public sealed class RejectTimesheetRequestValidator : AbstractValidator<RejectTimesheetRequest>
{
    public RejectTimesheetRequestValidator()
    {
        RuleFor(x => x.Reason).Must(PeopleRules.Reason)
            .WithMessage("Say what needs fixing, 3–300 characters (e.g. Thursday's clock-out is missing).");
    }
}

public sealed class ApproveTimesheetWeekRequestValidator : AbstractValidator<ApproveTimesheetWeekRequest>
{
    public ApproveTimesheetWeekRequestValidator()
    {
        RuleFor(x => x.WeekStart).Must(d => d.Year >= 2000).WithMessage("Choose the week to approve.");
    }
}

public sealed class CorrectTimesheetLineRequestValidator : AbstractValidator<CorrectTimesheetLineRequest>
{
    public CorrectTimesheetLineRequestValidator()
    {
        RuleFor(x => x.WorkDate).Must(d => d.Year >= 2000).WithMessage("Choose the day to correct.");
        RuleFor(x => x.ClockOut).Must((x, o) => o is null || x.ClockIn is null || o != x.ClockIn)
            .WithMessage("The clock-out is the same as the clock-in. Give a different time.");
        RuleFor(x => x.BreakMinutes).InclusiveBetween(0, 180).WithMessage("The break is 0–180 minutes.");
        RuleFor(x => x.Reason).Must(PeopleRules.Reason)
            .WithMessage("Say why the times change, 3–300 characters (e.g. Forgot to clock out, left at 16:00).");
    }
}
