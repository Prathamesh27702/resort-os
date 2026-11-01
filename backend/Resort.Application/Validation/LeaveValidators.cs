using System.Text.RegularExpressions;
using FluentValidation;
using Resort.Contracts;
using Resort.Domain.Employees;
using Resort.Domain.Leave;

namespace Resort.Application.Validation;

// Field rules for leave, holidays and payroll (Sprint 19). Rules that need data (the balance, overlapping leave, holidays
// inside the range, the employee's joining date) are in LeaveRequestService under the same field names.

internal static class LeaveRules
{
    public static bool Accrual(string? a) => a is not null && LeaveAccruals.All.Contains(a.Trim().ToUpperInvariant());

    public static bool HalfDays(decimal d) => d * 2 == Math.Floor(d * 2);

    public static bool Month(string? m) => m is not null && Regex.IsMatch(m.Trim(), "^[0-9]{4}-(0[1-9]|1[0-2])$");

    public const string MonthWords = "Choose the month, e.g. 2026-11.";
}

public sealed class SaveLeaveTypeRequestValidator : AbstractValidator<SaveLeaveTypeRequest>
{
    public SaveLeaveTypeRequestValidator()
    {
        RuleFor(x => x.Code).Must(c => c is not null && Regex.IsMatch(c.Trim().ToUpperInvariant(), "^[A-Z][A-Z0-9]{0,9}$"))
            .WithMessage("The code is 1–10 letters or digits starting with a letter, e.g. CL.");
        RuleFor(x => x.Name).Must(n => n is not null && n.Trim().Length is >= 2 and <= 60).WithMessage("Give the leave a name, 2–60 characters, e.g. Casual leave.");
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 1000).WithMessage("The order is 0–1000.");
        RuleFor(x => x.DaysPerYear).InclusiveBetween(0m, 365m).WithMessage("Days a year are 0–365.");
        RuleFor(x => x.DaysPerYear).Must(LeaveRules.HalfDays).WithMessage("Days a year are in half days, e.g. 12 or 7.5.");
        RuleFor(x => x.Accrual).Must(LeaveRules.Accrual).WithMessage("Choose how it is earned: every month, once a year, or only by hand.");
        RuleFor(x => x.MaxBalance).Must(m => m is null or (>= 0 and <= 999)).WithMessage("The most someone can hold is 0–999 days, or empty for no limit.");
    }
}

public sealed class SaveLeavePolicyRequestValidator : AbstractValidator<SaveLeavePolicyRequest>
{
    public SaveLeavePolicyRequestValidator()
    {
        RuleFor(x => x.LeaveTypeId).NotEmpty().WithMessage("Choose the leave type.");
        RuleFor(x => x.EmploymentType).Must(t => string.IsNullOrWhiteSpace(t) || EmploymentTypes.All.Contains(t.Trim().ToUpperInvariant()))
            .WithMessage("Choose full-time, part-time, contract or seasonal, or leave it empty for everyone.");
        RuleFor(x => x.DaysPerYear).InclusiveBetween(0m, 365m).WithMessage("Days a year are 0–365.");
        RuleFor(x => x.DaysPerYear).Must(LeaveRules.HalfDays).WithMessage("Days a year are in half days, e.g. 12 or 7.5.");
        RuleFor(x => x.Accrual).Must(LeaveRules.Accrual).WithMessage("Choose how it is earned: every month, once a year, or only by hand.");
        RuleFor(x => x.MaxBalance).Must(m => m is null or (>= 0 and <= 999)).WithMessage("The most someone can hold is 0–999 days, or empty for no limit.");
    }
}

public sealed class SaveHolidayRequestValidator : AbstractValidator<SaveHolidayRequest>
{
    public SaveHolidayRequestValidator()
    {
        RuleFor(x => x.Date).Must(d => d.Year is >= 2000 and <= 2100).WithMessage("Choose the holiday's date.");
        RuleFor(x => x.Name).Must(n => n is not null && n.Trim().Length is >= 2 and <= 60).WithMessage("Give the holiday a name, 2–60 characters, e.g. Diwali.");
    }
}

public sealed class AdjustLeaveRequestValidator : AbstractValidator<AdjustLeaveRequest>
{
    public AdjustLeaveRequestValidator()
    {
        RuleFor(x => x.EmployeeId).NotEmpty().WithMessage("Choose whose balance this is.");
        RuleFor(x => x.LeaveTypeId).NotEmpty().WithMessage("Choose the leave type.");
        RuleFor(x => x.Days).Must(d => d != 0 && d >= -365 && d <= 365).WithMessage("Give the days to add (e.g. 2) or take off (e.g. -1), up to 365.");
        RuleFor(x => x.Days).Must(LeaveRules.HalfDays).WithMessage("Days are in half days, e.g. 1.5.");
        RuleFor(x => x.Reason).Must(PeopleRules.Reason).WithMessage("Say why, 3–300 characters (e.g. Worked on Diwali, compensatory day).");
    }
}

public sealed class RunAccrualRequestValidator : AbstractValidator<RunAccrualRequest>
{
    public RunAccrualRequestValidator()
    {
        RuleFor(x => x.Month).Must(LeaveRules.Month).WithMessage(LeaveRules.MonthWords);
    }
}

public sealed class CreateLeaveRequestValidator : AbstractValidator<CreateLeaveRequest>
{
    public CreateLeaveRequestValidator()
    {
        RuleFor(x => x.LeaveTypeId).NotEmpty().WithMessage("Choose the kind of leave.");
        RuleFor(x => x.FromDate).Must(d => d.Year >= 2000).WithMessage("Choose the first day of leave.");
        RuleFor(x => x.ToDate).Must(d => d.Year >= 2000).WithMessage("Choose the last day of leave.");
        RuleFor(x => x.ToDate).Must((x, to) => to >= x.FromDate).When(x => x.FromDate.Year >= 2000 && x.ToDate.Year >= 2000)
            .WithMessage("The last day is before the first day.");
        RuleFor(x => x.ToDate).Must((x, to) => to.DayNumber - x.FromDate.DayNumber < LeaveMath.MaxRangeDays)
            .When(x => x.ToDate >= x.FromDate).WithMessage($"One request covers at most {LeaveMath.MaxRangeDays} days. Split longer leave into two.");
        RuleFor(x => x.HalfDayEnd).Must((x, half) => !(half && x.HalfDayStart && x.FromDate == x.ToDate))
            .WithMessage("For one day, choose either the morning off or the afternoon off, not both (that is a whole day).");
        RuleFor(x => x.Reason).Must(s => PurchaseRules.Optional(s, 1, 300)).WithMessage("The reason can be at most 300 characters.");
    }
}

public sealed class ApproveLeaveRequestValidator : AbstractValidator<ApproveLeaveRequest>
{
    public ApproveLeaveRequestValidator()
    {
        RuleFor(x => x.Note).Must(s => PurchaseRules.Optional(s, 1, 300)).WithMessage("The note can be at most 300 characters.");
    }
}

public sealed class RejectLeaveRequestValidator : AbstractValidator<RejectLeaveRequest>
{
    public RejectLeaveRequestValidator()
    {
        RuleFor(x => x.Reason).Must(PeopleRules.Reason).WithMessage("Say why, 3–300 characters (e.g. Two cooks are already off that week).");
    }
}

public sealed class CancelLeaveRequestValidator : AbstractValidator<CancelLeaveRequest>
{
    public CancelLeaveRequestValidator()
    {
        RuleFor(x => x.Reason).Must(PeopleRules.Reason).WithMessage("Say why it is cancelled, 3–300 characters (e.g. Trip postponed).");
    }
}

public sealed class CreatePayrollExportRequestValidator : AbstractValidator<CreatePayrollExportRequest>
{
    public CreatePayrollExportRequestValidator()
    {
        RuleFor(x => x.Month).Must(LeaveRules.Month).WithMessage(LeaveRules.MonthWords);
    }
}
