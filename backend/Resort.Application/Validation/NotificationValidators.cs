using FluentValidation;
using Resort.Contracts;

namespace Resort.Application.Validation;

// Field rules for templates (Sprint 24). Rules that depend on the template (its channel, the variables its message can use,
// WhatsApp's 1,024 characters) are in MessageService under the same field names.

public sealed class SaveMessageTemplateRequestValidator : AbstractValidator<SaveMessageTemplateRequest>
{
    public SaveMessageTemplateRequestValidator()
    {
        RuleFor(x => x.Body).Must(b => ActivityRules.Required(b, 3, 4000))
            .WithMessage("Write the message, 3–4,000 characters, e.g. Hello {{guest_name}}, your booking {{reference}} is confirmed.");
    }
}

public sealed class FakeWhatsAppStatusRequestValidator : AbstractValidator<FakeWhatsAppStatusRequest>
{
    public FakeWhatsAppStatusRequestValidator()
    {
        RuleFor(x => x.Status).NotEmpty().WithMessage("Choose the test status: DELIVERED, READ or FAILED.");
    }
}
