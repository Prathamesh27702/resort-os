using System.Text.RegularExpressions;
using FluentValidation;
using Resort.Application.Employees;
using Resort.Contracts;
using Resort.Domain.Employees;

namespace Resort.Application.Validation;

// Field rules for employees and the roster (Sprint 17). Rules that need the property's data (is the account free, does
// the shift clash, did the person join before that day) are in the employee and roster services, under the same field names.

internal static class PeopleRules
{
    public static bool Phone(string? p) => p is null || p.Trim().Length == 0 || Regex.IsMatch(p.Trim(), @"^\+?[0-9][0-9 -]{6,18}[0-9]$");

    public const string PhoneWords = "Enter the phone number with digits only (spaces and - allowed), e.g. +91 98220 12345, or leave it empty.";

    public static bool Reason(string? s) => s is not null && s.Trim().Length is >= 3 and <= 300;
}

public sealed class SaveDepartmentRequestValidator : AbstractValidator<SaveDepartmentRequest>
{
    public SaveDepartmentRequestValidator()
    {
        RuleFor(x => x.Code).Must(StockRules.Code)
            .WithMessage("The code is 1–20 letters, digits, - or _, starting with a letter or digit, e.g. KITCHEN.");
        RuleFor(x => x.Name).Must(n => n is not null && n.Trim().Length is >= 2 and <= 60)
            .WithMessage("Give the department's name, 2–60 characters, e.g. Housekeeping.");
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 999).WithMessage("The position in lists is 0–999.");
    }
}

public sealed class SaveDesignationRequestValidator : AbstractValidator<SaveDesignationRequest>
{
    public SaveDesignationRequestValidator()
    {
        RuleFor(x => x.DepartmentId).NotEmpty().WithMessage("Choose the department this job title belongs to.");
        RuleFor(x => x.Name).Must(n => n is not null && n.Trim().Length is >= 2 and <= 60)
            .WithMessage("Give the job title, 2–60 characters, e.g. Commis chef.");
    }
}

public sealed class SaveEmployeeRequestValidator : AbstractValidator<SaveEmployeeRequest>
{
    public SaveEmployeeRequestValidator()
    {
        RuleFor(x => x.FirstName).Must(n => n is not null && n.Trim().Length is >= 1 and <= 60)
            .WithMessage("Give the first name, up to 60 characters.");
        RuleFor(x => x.LastName).Must(n => n is null || n.Trim().Length <= 60).WithMessage("The last name can be at most 60 characters.");
        RuleFor(x => x.Phone).Must(PeopleRules.Phone).WithMessage(PeopleRules.PhoneWords);
        RuleFor(x => x.EmergencyContactPhone).Must(PeopleRules.Phone).WithMessage(PeopleRules.PhoneWords);
        RuleFor(x => x.EmergencyContactName).Must(s => PurchaseRules.Optional(s, 2, 80)).WithMessage("The emergency contact's name is 2–80 characters, or empty.");
        RuleFor(x => x.Email).Must(e => e is null || e.Trim().Length == 0 || Regex.IsMatch(e.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            .WithMessage("Enter an e-mail address like asha@example.in, or leave it empty.");
        RuleFor(x => x.DepartmentId).NotEmpty().WithMessage("Choose the department they work in.");
        RuleFor(x => x.EmploymentType).Must(t => t is not null && EmploymentTypes.All.Contains(t.Trim().ToUpperInvariant()))
            .WithMessage("Choose full time, part time, contract or seasonal.");
        RuleFor(x => x.JoinedOn).Must(d => d.Year >= 1960).WithMessage("Enter the date they joined.");
        RuleFor(x => x.DateOfBirth).Must(d => d is null || d.Value.Year >= 1920).WithMessage("Check the date of birth: it is before 1920.");
        RuleFor(x => x.WeeklyOff).Must(d => d is null or (>= 1 and <= 7)).WithMessage("Choose the weekly day off (Monday … Sunday), or none.");
        RuleFor(x => x.Address).Must(s => PurchaseRules.Optional(s, 3, 300)).WithMessage("The address is 3–300 characters, or empty.");
        RuleFor(x => x.Note).Must(s => PurchaseRules.Optional(s, 1, 500)).WithMessage("The note can be at most 500 characters.");
    }
}

public sealed class EmployeeLeftRequestValidator : AbstractValidator<EmployeeLeftRequest>
{
    public EmployeeLeftRequestValidator()
    {
        RuleFor(x => x.Reason).Must(PeopleRules.Reason).WithMessage("Say why they are leaving, 3–300 characters (e.g. Resigned, end of season).");
    }
}

public sealed class RecordCompensationRequestValidator : AbstractValidator<RecordCompensationRequest>
{
    public RecordCompensationRequestValidator()
    {
        RuleFor(x => x.PayBasis).Must(b => b is not null && PayBases.All.Contains(b.Trim().ToUpperInvariant()))
            .WithMessage("Choose whether the pay is per month or per day.");
        RuleFor(x => x.Basic).Must(v => v is > 0 and <= 10_000_000m && decimal.Round(v, 2) == v)
            .WithMessage("Enter the basic pay in rupees: more than ₹0, at most ₹1,00,00,000, with at most two decimals (paise).");
        RuleFor(x => x.Allowances).Must(v => v is >= 0 and <= 10_000_000m && decimal.Round(v, 2) == v)
            .WithMessage("Enter the allowances (HRA, food, travel …) in rupees, 0 or more, with at most two decimals.");
        RuleFor(x => x.Note).Must(s => PurchaseRules.Optional(s, 1, 300)).WithMessage("The note can be at most 300 characters.");
    }
}

public sealed class UploadEmployeeDocumentCommandValidator : AbstractValidator<UploadEmployeeDocumentCommand>
{
    public UploadEmployeeDocumentCommandValidator()
    {
        RuleFor(x => x.DocumentType).Must(t => t is not null && EmployeeDocumentTypes.All.Contains(t.Trim().ToUpperInvariant()))
            .WithMessage("Choose the document: Aadhaar, PAN, bank account, passport, driving licence, offer letter or other.");
        RuleFor(x => x.Number).Must(n => n is null || n.Trim().Length <= 40).WithMessage("The number can be at most 40 characters.");
        RuleFor(x => x.Note).Must(s => PurchaseRules.Optional(s, 1, 200)).WithMessage("The note can be at most 200 characters.");
    }
}

public sealed class RemoveEmployeeDocumentRequestValidator : AbstractValidator<RemoveEmployeeDocumentRequest>
{
    public RemoveEmployeeDocumentRequestValidator()
    {
        RuleFor(x => x.Reason).Must(PeopleRules.Reason).WithMessage("Say why the document is removed, 3–300 characters (e.g. Uploaded the wrong file).");
    }
}

public sealed class SaveShiftTemplateRequestValidator : AbstractValidator<SaveShiftTemplateRequest>
{
    public SaveShiftTemplateRequestValidator()
    {
        RuleFor(x => x.DepartmentId).NotEmpty().WithMessage("Choose the department that works this shift.");
        RuleFor(x => x.Code).Must(StockRules.Code).WithMessage("The code is 1–20 letters, digits, - or _, e.g. KIT-AM.");
        RuleFor(x => x.Name).Must(n => n is not null && n.Trim().Length is >= 2 and <= 40)
            .WithMessage("Give the shift a name, 2–40 characters, e.g. Morning.");
        RuleFor(x => x.EndTime).Must((x, e) => e != x.StartTime).WithMessage("The shift ends when it starts. Give a different end time.");
        RuleFor(x => x.BreakMinutes).InclusiveBetween(0, 180).WithMessage("The break is 0–180 minutes.");
        RuleFor(x => x.MinStaff).InclusiveBetween(0, 50).WithMessage("People needed is 0–50 (0: no minimum, never short).");
    }
}

public sealed class SaveRosterShiftRequestValidator : AbstractValidator<SaveRosterShiftRequest>
{
    public SaveRosterShiftRequestValidator()
    {
        RuleFor(x => x.EmployeeId).NotEmpty().WithMessage("Choose who works this shift.");
        RuleFor(x => x.EndTime).Must((x, e) => e is null || x.StartTime is null || e != x.StartTime)
            .WithMessage("The shift ends when it starts. Give a different end time.");
        RuleFor(x => x.BreakMinutes).Must(b => b is null or (>= 0 and <= 180)).WithMessage("The break is 0–180 minutes.");
        RuleFor(x => x.OverlapReason).Must(s => s is null || s.Trim().Length <= 200).WithMessage("The reason can be at most 200 characters.");
        RuleFor(x => x.Note).Must(s => PurchaseRules.Optional(s, 1, 200)).WithMessage("The note can be at most 200 characters.");
    }
}

public sealed class CancelRosterShiftRequestValidator : AbstractValidator<CancelRosterShiftRequest>
{
    public CancelRosterShiftRequestValidator()
    {
        RuleFor(x => x.Reason).Must(PeopleRules.Reason).WithMessage("Say why the shift comes off the roster, 3–300 characters (e.g. Swapped with Ravi).");
    }
}

public sealed class CopyRosterWeekRequestValidator : AbstractValidator<CopyRosterWeekRequest>
{
    public CopyRosterWeekRequestValidator()
    {
        RuleFor(x => x.FromWeek).Must(d => d.Year >= 2000).WithMessage("Choose the week to copy from.");
        RuleFor(x => x.ToWeek).Must(d => d.Year >= 2000).WithMessage("Choose the week to copy into.");
    }
}
