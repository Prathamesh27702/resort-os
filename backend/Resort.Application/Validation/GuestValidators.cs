using System.Linq.Expressions;
using FluentValidation;
using Resort.Contracts;
using Resort.Domain.Guests;

namespace Resort.Application.Validation;

// Field rules mirror the CHECK constraints in database/migrations/V005__guests.sql, so users get a field
// message instead of a database error.

public sealed class CreateGuestRequestValidator : AbstractValidator<CreateGuestRequest>
{
    public CreateGuestRequestValidator()
    {
        GuestRulesValidation.Apply(this, x => x.FirstName, x => x.LastName, x => x.Phone, x => x.Email, x => x.DateOfBirth,
            x => x.Nationality, x => x.CompanyName, x => x.Gstin, x => x.Notes, x => x.Address, x => x.Preferences);
        RuleForEach(x => x.Preferences).SetValidator(new GuestPreferenceValidator());
    }
}

public sealed class UpdateGuestRequestValidator : AbstractValidator<UpdateGuestRequest>
{
    public UpdateGuestRequestValidator()
    {
        GuestRulesValidation.Apply(this, x => x.FirstName, x => x.LastName, x => x.Phone, x => x.Email, x => x.DateOfBirth,
            x => x.Nationality, x => x.CompanyName, x => x.Gstin, x => x.Notes, x => x.Address, x => x.Preferences);
        RuleForEach(x => x.Preferences).SetValidator(new GuestPreferenceValidator());
    }
}

internal static class GuestRulesValidation
{
    public static void Apply<T>(AbstractValidator<T> v,
        Expression<Func<T, string>> first, Expression<Func<T, string?>> last, Expression<Func<T, string?>> phone,
        Expression<Func<T, string?>> email, Expression<Func<T, DateOnly?>> dob, Expression<Func<T, string?>> nationality,
        Expression<Func<T, string?>> company, Expression<Func<T, string?>> gstin, Expression<Func<T, string?>> notes,
        Expression<Func<T, GuestAddressDto?>> address, Expression<Func<T, IReadOnlyList<GuestPreferenceDto>?>> prefs)
    {
        v.RuleFor(first).Must(s => GuestRules.Clean(s) is { Length: >= 1 and <= 100 })
            .WithMessage("First name is required (up to 100 characters).");
        v.RuleFor(last).Must(s => s is null || GuestRules.Clean(s) is null or { Length: <= 100 })
            .WithMessage("Last name can be up to 100 characters.");
        v.RuleFor(phone).Must(p => string.IsNullOrWhiteSpace(p) || GuestRules.NormalizePhone(p) is not null)
            .WithMessage("Phone number isn't valid. Indian numbers can be typed as 98200 12345; others need + and the country code.");
        v.RuleFor(email).Must(e => string.IsNullOrWhiteSpace(e)
                                   || (e.Trim().Length <= 254 && System.Net.Mail.MailAddress.TryCreate(e.Trim(), out var m) && m.Address == e.Trim()))
            .WithMessage("Email address isn't valid.");
        v.RuleFor(dob).Must(d => d is null || (d >= new DateOnly(1900, 1, 1) && d <= DateOnly.FromDateTime(DateTime.UtcNow)))
            .WithMessage("Date of birth must be between 1900 and today.");
        v.RuleFor(nationality).Matches("^[A-Za-z]{2}$").When(x => !string.IsNullOrWhiteSpace(nationality.Compile()(x)))
            .WithMessage("Nationality is a 2-letter country code, e.g. IN, GB, DE.");
        v.RuleFor(company).MaximumLength(200);
        v.RuleFor(gstin).Matches("^[0-9]{2}[A-Za-z0-9]{13}$").When(x => !string.IsNullOrWhiteSpace(gstin.Compile()(x)))
            .WithMessage("GSTIN is 15 characters: 2-digit state code then 13 letters or digits.");
        v.RuleFor(notes).MaximumLength(2000);
        v.RuleFor(address).SetValidator(new GuestAddressValidator()!).When(x => address.Compile()(x) is not null);
        v.RuleFor(prefs).Must(p => p is null || p.Count <= 30).WithMessage("Up to 30 preferences.");
    }
}

public sealed class GuestAddressValidator : AbstractValidator<GuestAddressDto>
{
    public GuestAddressValidator()
    {
        RuleFor(x => x.Line1).MaximumLength(200);
        RuleFor(x => x.Line2).MaximumLength(200);
        RuleFor(x => x.City).MaximumLength(100);
        RuleFor(x => x.State).MaximumLength(100);
        RuleFor(x => x.StateCode).Matches("^[0-9]{2}$").When(x => !string.IsNullOrWhiteSpace(x.StateCode))
            .WithMessage("GST state code is 2 digits, e.g. 30 for Goa.");
        RuleFor(x => x.PostalCode).MaximumLength(20);
        RuleFor(x => x.PostalCode).Matches("^[1-9][0-9]{5}$")
            .When(x => !string.IsNullOrWhiteSpace(x.PostalCode) && string.Equals((x.CountryCode ?? "IN").Trim(), "IN", StringComparison.OrdinalIgnoreCase))
            .WithMessage("An Indian PIN code is 6 digits.");
        RuleFor(x => x.CountryCode).Matches("^[A-Za-z]{2}$").When(x => !string.IsNullOrWhiteSpace(x.CountryCode))
            .WithMessage("Country is a 2-letter code, e.g. IN.");
    }
}

public sealed class GuestPreferenceValidator : AbstractValidator<GuestPreferenceDto>
{
    public GuestPreferenceValidator()
    {
        RuleFor(x => x.Category).Must(c => PreferenceCategories.All.Contains(c?.Trim().ToUpperInvariant()))
            .WithMessage($"Category must be one of {string.Join(", ", PreferenceCategories.All)}.");
        RuleFor(x => x.Value).Must(s => GuestRules.Clean(s) is { Length: >= 1 and <= 500 }).WithMessage("Preference: 1–500 characters.");
    }
}

public sealed class RecordConsentRequestValidator : AbstractValidator<RecordConsentRequest>
{
    public RecordConsentRequestValidator()
    {
        RuleFor(x => x.Purpose).Must(p => ConsentPurposes.All.Contains(p))
            .WithMessage($"Purpose must be one of {string.Join(", ", ConsentPurposes.All)}.");
        RuleFor(x => x.Source).Must(s => ConsentSources.All.Contains(s))
            .WithMessage($"Source must be one of {string.Join(", ", ConsentSources.All)}.");
    }
}

public sealed class AnonymiseGuestRequestValidator : AbstractValidator<AnonymiseGuestRequest>
{
    public AnonymiseGuestRequestValidator() =>
        RuleFor(x => x.Reason).Must(r => r?.Trim().Length is >= 3 and <= 500).WithMessage("Give a reason (3–500 characters).");
}

public sealed class MergeGuestsRequestValidator : AbstractValidator<MergeGuestsRequest>
{
    public MergeGuestsRequestValidator()
    {
        RuleFor(x => x.SurvivorId).NotEmpty();
        RuleFor(x => x.DuplicateId).NotEmpty().NotEqual(x => x.SurvivorId).WithMessage("Choose two different profiles.");
        RuleFor(x => x.Reason).Must(r => r?.Trim().Length is >= 3 and <= 500).WithMessage("Give a reason (3–500 characters).");
    }
}

/// <summary>Document upload fields (the scan itself is checked by the service: type from its bytes, size ≤ 5 MB).</summary>
public sealed class UploadGuestDocumentValidator : AbstractValidator<Guests.UploadGuestDocumentCommand>
{
    public UploadGuestDocumentValidator()
    {
        RuleFor(x => x.DocumentType).Must(t => DocumentTypes.All.Contains(t?.Trim().ToUpperInvariant()))
            .WithMessage($"Document type must be one of {string.Join(", ", DocumentTypes.All)}.");
        RuleFor(x => x.Number).Must(n => n is null || GuestRules.NormalizeIdNumber(n).Length is >= 4 and <= 30)
            .When(x => !string.IsNullOrWhiteSpace(x.Number))
            .WithMessage("Document number: 4–30 letters or digits.");
        RuleFor(x => x.Number).Must(n => GuestRules.NormalizeIdNumber(n!) is { Length: 4 or 12 } s && s.All(char.IsAsciiDigit))
            .When(x => !string.IsNullOrWhiteSpace(x.Number) && x.DocumentType?.Trim().ToUpperInvariant() == DocumentTypes.Aadhaar)
            .WithMessage("An Aadhaar number has 12 digits. Enter all 12 (spaces are fine) or only the last 4; only the last 4 are kept.");
        RuleFor(x => x.IssuingCountry).Matches("^[A-Za-z]{2}$").When(x => !string.IsNullOrWhiteSpace(x.IssuingCountry))
            .WithMessage("Issuing country is a 2-letter code, e.g. IN.");
        RuleFor(x => x.Details).Must(d => d is null || (d.Count <= 10 && d.All(kv => kv.Key.Length is >= 1 and <= 40 && kv.Value.Length <= 200)))
            .WithMessage("Up to 10 details, names up to 40 and values up to 200 characters.");
    }
}
