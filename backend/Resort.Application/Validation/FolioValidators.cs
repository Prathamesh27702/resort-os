using FluentValidation;
using Resort.Contracts;
using Resort.Domain.Folios;

namespace Resort.Application.Validation;

// Field rules for folios (Sprint 8). Rules that need the booking (its status, the property's date, the package) are in
// FolioService and are reported under the same field names.

public sealed class PostChargeRequestValidator : AbstractValidator<PostChargeRequest>
{
    public PostChargeRequestValidator()
    {
        When(x => x.PackageId is null, () =>
        {
            RuleFor(x => x.Category).Must(c => c is not null && ChargeCategories.All.Contains(c.Trim().ToUpperInvariant()))
                .WithMessage($"Choose what the charge is for: {string.Join(", ", ChargeCategories.All.Select(ChargeCategories.Words))}.");
            RuleFor(x => x.Description).Must(d => !string.IsNullOrWhiteSpace(d))
                .WithMessage("Say what the charge is for, e.g. Dinner at the beach shack.")
                .MaximumLength(200).WithMessage("The description can be at most 200 characters.");
            RuleFor(x => x.Quantity).GreaterThan(0).WithMessage("The quantity must be more than 0.")
                .LessThanOrEqualTo(Folios.FolioService.MaxQuantity).WithMessage($"The quantity can be at most {Folios.FolioService.MaxQuantity}.")
                .Must(q => decimal.Round(q, 3) == q).WithMessage("The quantity can have at most 3 decimal places.");
            Money.Rules(this, x => x.UnitPrice, "The price", positive: true);
        });
        When(x => x.PackageId is not null, () =>
        {
            RuleFor(x => x.PackageId).Must(id => id != Guid.Empty).WithMessage("Choose a package.");
            RuleFor(x => x.Quantity).Must(q => q == decimal.Truncate(q) && q >= 1 && q <= Folios.FolioService.MaxPackageQuantity)
                .WithMessage($"A package is sold whole: enter how many, 1 to {Folios.FolioService.MaxPackageQuantity}.");
        });
    }
}

public sealed class ReverseChargeRequestValidator : AbstractValidator<ReverseChargeRequest>
{
    public ReverseChargeRequestValidator()
    {
        RuleFor(x => x.Reason).Must(r => r is not null && r.Trim().Length >= 3)
            .WithMessage("Say why the charge is reversed (at least 3 characters), e.g. Posted to the wrong guest.")
            .MaximumLength(500).WithMessage("The reason can be at most 500 characters.");
    }
}

public sealed class SplitFolioRequestValidator : AbstractValidator<SplitFolioRequest>
{
    public SplitFolioRequestValidator()
    {
        RuleFor(x => x.ItemIds).NotEmpty().WithMessage("Choose at least one line to move.")
            .Must(ids => ids is null || ids.Count <= 500).WithMessage("Move at most 500 lines at a time.")
            .Must(ids => ids is null || ids.All(id => id != Guid.Empty)).WithMessage("One of the chosen lines is empty. Reload the folio and choose again.");
        RuleFor(x => x.TargetFolioId).Must((x, id) => (id is not null) != !string.IsNullOrWhiteSpace(x.NewFolioName))
            .WithMessage("Choose the folio to move the lines to, or name a new one (one of the two).");
        RuleFor(x => x.NewFolioName).MaximumLength(100).WithMessage("The folio name can be at most 100 characters.");
        RuleFor(x => x.BillToGuestId).Must(id => id is null || id != Guid.Empty).WithMessage("Choose the guest, or leave it empty.");
    }
}
