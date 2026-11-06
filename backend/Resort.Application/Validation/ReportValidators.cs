using FluentValidation;
using Resort.Contracts;

namespace Resort.Application.Validation;

public sealed class SaveCommissionRatesRequestValidator : AbstractValidator<SaveCommissionRatesRequest>
{
    public SaveCommissionRatesRequestValidator()
    {
        RuleFor(x => x.Rates).NotNull().WithMessage("Send the commission rates to save.")
            .Must(r => r is { Count: <= 100 }).WithMessage("Send at most 100 commission rates at a time.");
        RuleForEach(x => x.Rates).ChildRules(r =>
        {
            r.RuleFor(x => x.SourceId).NotEmpty().WithMessage("Choose the booking source.");
            r.RuleFor(x => x.Percent).InclusiveBetween(0, 50).When(x => x.Percent is not null)
                .WithMessage("Enter the commission as 0 to 50 %, e.g. 18.");
        });
    }
}
