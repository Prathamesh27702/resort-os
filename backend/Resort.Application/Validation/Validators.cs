using FluentValidation;
using Resort.Contracts;
using Resort.Domain.Organizations;

namespace Resort.Application.Validation;

internal static class Rules
{
    public static IRuleBuilderOptions<T, string> Password<T>(this IRuleBuilder<T, string> rb) =>
        rb.NotEmpty()
          .MinimumLength(10).WithMessage("Password must be at least 10 characters.")
          .MaximumLength(128)
          .Must(p => p.Any(char.IsLetter) && p.Any(char.IsDigit))
          .WithMessage("Password must contain letters and numbers.");

    /// <summary>
    /// A real IANA zone such as <c>Asia/Kolkata</c>, or <c>UTC</c>. Abbreviations like IST, EST or PST are refused:
    /// they are ambiguous (IST is India, Israel and Ireland), and whether the OS accepts them differs (Windows' ICU
    /// treats IST as an alias of Asia/Kolkata; Linux has no IST zone file but does have EST).
    /// </summary>
    public static bool IsIanaTimezone(string? tz) =>
        !string.IsNullOrWhiteSpace(tz)
        && (tz == "UTC" || tz.Contains('/'))
        && TimeZoneInfo.TryFindSystemTimeZoneById(tz, out var info) && info.HasIanaId;

    public static IRuleBuilderOptions<T, string?> OptionalEmail<T>(this IRuleBuilder<T, string?> rb) =>
        rb.Must(e => string.IsNullOrEmpty(e) || (e.Length <= 320 && System.Net.Mail.MailAddress.TryCreate(e, out _)))
          .WithMessage("Email address is not valid.");
}

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(320);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
    }
}

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty();
        RuleFor(x => x.NewPassword).Password()
            .NotEqual(x => x.CurrentPassword).WithMessage("New password must differ from the current one.");
    }
}

public sealed class UpdateOrganizationRequestValidator : AbstractValidator<UpdateOrganizationRequest>
{
    public UpdateOrganizationRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.LegalName).MaximumLength(200);
    }
}

/// <summary>Shared property field rules for create and update.</summary>
public abstract class PropertyFieldsValidator<T> : AbstractValidator<T>
{
    protected void PropertyRules(
        System.Linq.Expressions.Expression<Func<T, string>> name,
        System.Linq.Expressions.Expression<Func<T, string>> type,
        System.Linq.Expressions.Expression<Func<T, string>> timezone,
        System.Linq.Expressions.Expression<Func<T, string>> currency,
        System.Linq.Expressions.Expression<Func<T, string>> country,
        System.Linq.Expressions.Expression<Func<T, string?>> gstState,
        System.Linq.Expressions.Expression<Func<T, string?>> email,
        System.Linq.Expressions.Expression<Func<T, decimal?>> lat,
        System.Linq.Expressions.Expression<Func<T, decimal?>> lng,
        System.Linq.Expressions.Expression<Func<T, string?>> fssai,
        System.Linq.Expressions.Expression<Func<T, string?>> upiId,
        System.Linq.Expressions.Expression<Func<T, string?>> upiPayee)
    {
        RuleFor(fssai).Must(n => string.IsNullOrWhiteSpace(n) || Resort.Domain.Compliance.Upi.IsValidFssai(n.Replace(" ", "", StringComparison.Ordinal)))
            .WithMessage("An FSSAI licence number has 14 digits, as printed on the licence (e.g. 10020042004371). Leave it empty if the property serves no food.");
        RuleFor(upiId).Must(u => string.IsNullOrWhiteSpace(u) || Resort.Domain.Compliance.Upi.IsValidId(u.Trim()))
            .WithMessage("A UPI ID looks like name@bank, e.g. palmgrove@okhdfcbank. Copy it from the property's UPI app or QR sticker.");
        RuleFor(upiPayee).Must(n => string.IsNullOrWhiteSpace(n) || n.Trim().Length is >= 2 and <= 60)
            .WithMessage("The name shown to the payer must have 2 to 60 characters.");
        RuleFor(name).NotEmpty().MaximumLength(200);
        RuleFor(type).Must(t => PropertyTypes.All.Contains(t))
            .WithMessage($"Property type must be one of: {string.Join(", ", PropertyTypes.All)}.");
        RuleFor(timezone).Must(Rules.IsIanaTimezone).WithMessage("Timezone must be an IANA name such as Asia/Kolkata.");
        RuleFor(currency).Matches("^[A-Za-z]{3}$").WithMessage("Currency must be a 3-letter ISO code.");
        RuleFor(country).Matches("^[A-Za-z]{2}$").WithMessage("Country must be a 2-letter ISO code.");
        RuleFor(gstState).Must(s => string.IsNullOrEmpty(s) || System.Text.RegularExpressions.Regex.IsMatch(s, "^[0-9]{2}$"))
            .WithMessage("GST state code is 2 digits, e.g. 30 for Goa.");
        RuleFor(email).OptionalEmail();
        RuleFor(lat).InclusiveBetween(-90m, 90m);
        RuleFor(lng).InclusiveBetween(-180m, 180m);
    }
}

public sealed class CreatePropertyRequestValidator : PropertyFieldsValidator<CreatePropertyRequest>
{
    public CreatePropertyRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().Matches("^[A-Za-z0-9_]{2,30}$")
            .WithMessage("Code: 2–30 letters, digits or underscore.");
        PropertyRules(x => x.Name, x => x.PropertyType, x => x.Timezone, x => x.Currency, x => x.CountryCode,
            x => x.GstStateCode, x => x.Email, x => x.Latitude, x => x.Longitude, x => x.FssaiLicenceNo, x => x.UpiId, x => x.UpiPayeeName);
    }
}

public sealed class UpdatePropertyRequestValidator : PropertyFieldsValidator<UpdatePropertyRequest>
{
    public UpdatePropertyRequestValidator() =>
        PropertyRules(x => x.Name, x => x.PropertyType, x => x.Timezone, x => x.Currency, x => x.CountryCode,
            x => x.GstStateCode, x => x.Email, x => x.Latitude, x => x.Longitude, x => x.FssaiLicenceNo, x => x.UpiId, x => x.UpiPayeeName);
}

public sealed class PropertySettingKeyValidator : AbstractValidator<string>
{
    public PropertySettingKeyValidator() =>
        RuleFor(x => x).Matches("^[a-z][a-z0-9_.]{1,99}$").WithName("key")
            .WithMessage("Setting key: lowercase letters, digits, '_' or '.', e.g. frontdesk.allow_checkout_with_balance.");
}

public sealed class RoleGrantDtoValidator : AbstractValidator<RoleGrantDto>
{
    public RoleGrantDtoValidator() => RuleFor(x => x.RoleCode).NotEmpty().MaximumLength(50);
}

public sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Phone).MaximumLength(30);
        RuleFor(x => x.TemporaryPassword).Password();
        RuleFor(x => x.Roles).NotNull();
        RuleForEach(x => x.Roles).SetValidator(new RoleGrantDtoValidator());
    }
}

public sealed class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserRequestValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Phone).MaximumLength(30);
    }
}

public sealed class SetUserRolesRequestValidator : AbstractValidator<SetUserRolesRequest>
{
    public SetUserRolesRequestValidator()
    {
        RuleFor(x => x.Roles).NotNull();
        RuleForEach(x => x.Roles).SetValidator(new RoleGrantDtoValidator());
    }
}

public sealed class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator() => RuleFor(x => x.TemporaryPassword).Password();
}
