using FluentValidation;
using Resort.Contracts;
using Resort.Domain.Folios;
using Resort.Domain.Tax;

namespace Resort.Application.Validation;

// Field rules for GST configuration (Sprint 9). Rules that need the database (the property's state, overlapping periods,
// codes already used, today's date) are in TaxService and are reported under the same field names.

public sealed class CreateGstRegistrationRequestValidator : AbstractValidator<CreateGstRegistrationRequest>
{
    public CreateGstRegistrationRequestValidator()
    {
        RuleFor(x => x.Gstin).Must(g => Gstin.Problem(g) is null).WithMessage(x => Gstin.Problem(x.Gstin)!);
        RuleFor(x => x.LegalName).Must(n => !string.IsNullOrWhiteSpace(n))
            .WithMessage("Enter the legal name exactly as on the GST certificate, e.g. Coconut Grove Resorts Pvt Ltd.")
            .MaximumLength(200).WithMessage("The legal name can be at most 200 characters.");
        RuleFor(x => x.TradeName).MaximumLength(200).WithMessage("The trade name can be at most 200 characters.");
        RuleFor(x => x.RegistrationType)
            .Must(t => t is null || RegistrationTypes.All.Contains(t.Trim().ToUpperInvariant()))
            .WithMessage("Choose the registration type: REGULAR (charges GST) or COMPOSITION (charges none).");
        RuleFor(x => x.EffectiveFrom).NotNull().WithMessage("Enter the date the registration is valid from (on the certificate).");
        RuleFor(x => x.EffectiveTo).Must((x, to) => to is null || x.EffectiveFrom is null || to >= x.EffectiveFrom)
            .WithMessage("The end date can't be before the start date. Leave it empty if the registration is still valid.");
    }
}

public sealed class CreateTaxCategoryRequestValidator : AbstractValidator<CreateTaxCategoryRequest>
{
    public CreateTaxCategoryRequestValidator()
    {
        RuleFor(x => x.Code).Must(c => c is not null && System.Text.RegularExpressions.Regex.IsMatch(c.Trim().ToUpperInvariant(), "^[A-Z][A-Z0-9_]{1,29}$"))
            .WithMessage("Enter a code: 2–30 letters, digits or underscore, starting with a letter, e.g. TRANSPORT.");
        RuleFor(x => x.Name).Must(n => !string.IsNullOrWhiteSpace(n)).WithMessage("Enter a name, e.g. Passenger transport.")
            .MaximumLength(100).WithMessage("The name can be at most 100 characters.");
        RuleFor(x => x.HsnSac).Must(c => c is not null && System.Text.RegularExpressions.Regex.IsMatch(c.Trim(), "^([0-9]{4}|[0-9]{6}|[0-9]{8})$"))
            .WithMessage("Enter the SAC or HSN code: 4, 6 or 8 digits, e.g. 996311 for accommodation.");
        RuleFor(x => x.ChargeCategory)
            .Must(c => string.IsNullOrWhiteSpace(c) || ChargeCategories.All.Contains(c.Trim().ToUpperInvariant()))
            .WithMessage($"Choose which bill charges it covers: {string.Join(", ", ChargeCategories.All.Select(ChargeCategories.Words))}, or none.");
        RuleFor(x => x.Description).MaximumLength(500).WithMessage("The description can be at most 500 characters.");
    }
}

public sealed class CreateTaxRuleRequestValidator : AbstractValidator<CreateTaxRuleRequest>
{
    public CreateTaxRuleRequestValidator()
    {
        RuleFor(x => x.TaxCategoryId).NotEmpty().WithMessage("Choose the tax category the rule is for.");
        RuleFor(x => x.EffectiveFrom).NotNull().WithMessage("Enter the first day the rule applies, e.g. the 1st of next month.");
        RuleFor(x => x.EffectiveTo).Must((x, to) => to is null || x.EffectiveFrom is null || to >= x.EffectiveFrom)
            .WithMessage("The last day can't be before the first day. Leave it empty if the rule applies until further notice.");
        RuleFor(x => x.RuleType).Must(t => t is not null && TaxRuleTypes.All.Contains(t.Trim().ToUpperInvariant()))
            .WithMessage("Choose the rule type: FLAT (one rate) or SLAB (the rate depends on the value per unit per night).");
        RuleFor(x => x.Source).Must(s => s is not null && s.Trim().Length >= 3)
            .WithMessage("Say where the rule comes from, e.g. Notification 05/2025-Central Tax (Rate), so it can be checked later.")
            .MaximumLength(500).WithMessage("The source can be at most 500 characters.");
        RuleFor(x => x.Notes).MaximumLength(1000).WithMessage("The notes can be at most 1000 characters.");

        RuleFor(x => x.Slabs).Must(s => s is { Count: > 0 }).WithMessage("Enter the GST rate.");
        When(x => x.Slabs is { Count: > 0 }, () =>
        {
            RuleFor(x => x.Slabs).Must((x, s) => !IsFlat(x) || s!.Count == 1 && s[0].UpTo is null)
                .WithMessage("A flat rule has one rate for any value: remove the value limits, or choose SLAB.");
            RuleFor(x => x.Slabs).Must((x, s) => IsFlat(x) || s!.Count >= 2)
                .WithMessage("A slab rule needs at least two rates, e.g. up to ₹7,500 at 5% and above it at 18%.");
            RuleFor(x => x.Slabs).Must(s => s!.Count(x => x.UpTo is null) == 1)
                .WithMessage("Exactly one rate must have no upper limit (it applies to everything above the other limits).");
            RuleFor(x => x.Slabs).Must(s => s!.Where(x => x.UpTo is not null).Select(x => x.UpTo).Distinct().Count() == s!.Count(x => x.UpTo is not null))
                .WithMessage("Two rates have the same upper limit. Each limit can appear once.");
            RuleFor(x => x.Slabs).Must(s => s!.Count <= 10).WithMessage("A rule can have at most 10 rates.");
            RuleForEach(x => x.Slabs).ChildRules(slab =>
            {
                slab.RuleFor(s => s.Rate).InclusiveBetween(0m, 100m).WithMessage("A GST rate is between 0% and 100%.")
                    .Must(TaxMath.IsSplittable).WithMessage("The rate is split evenly into CGST and SGST, so it must be a multiple of 0.02% (e.g. 5, 18 or 0.1).");
                slab.RuleFor(s => s.UpTo).Must(u => u is null || u > 0 && u <= Money.Max && Money.InPaise(u.Value))
                    .WithMessage($"An upper limit is an amount above ₹0 and at most {Money.Rupees(Money.Max)}, in rupees and paise.");
            });
        });
    }

    private static bool IsFlat(CreateTaxRuleRequest x) => x.RuleType?.Trim().ToUpperInvariant() == TaxRuleTypes.Flat;
}

public sealed class TaxCalculationRequestValidator : AbstractValidator<TaxCalculationRequest>
{
    public TaxCalculationRequestValidator()
    {
        RuleFor(x => x.PropertyId).NotEmpty().WithMessage("Choose the property.");
        RuleFor(x => x.ChargeCategory).Must((x, c) => x.TaxCategoryId is not null || !string.IsNullOrWhiteSpace(c))
            .WithMessage("Choose what is charged (e.g. Room or Food), or a tax category.")
            .Must(c => string.IsNullOrWhiteSpace(c) || ChargeCategories.All.Contains(c.Trim().ToUpperInvariant()))
            .WithMessage($"Choose what is charged: {string.Join(", ", ChargeCategories.All.Select(ChargeCategories.Words))}.");
        Money.Rules(this, x => x.Amount, "The amount");
        RuleFor(x => x.UnitValue).Must(u => u is null || u >= 0 && u <= Money.Max && Money.InPaise(u.Value))
            .WithMessage($"The value per unit per night is an amount from ₹0 to {Money.Rupees(Money.Max)}, in rupees and paise.");
        RuleFor(x => x.PlaceOfSupply).Must(p => string.IsNullOrWhiteSpace(p) || GstStates.Names.ContainsKey(p.Trim()))
            .WithMessage("Choose the place of supply from the list of states (a 2-digit GST state code, e.g. 27 for Maharashtra).");
    }
}
