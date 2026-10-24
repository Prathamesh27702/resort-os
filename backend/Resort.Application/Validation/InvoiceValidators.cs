using FluentValidation;
using Resort.Contracts;
using Resort.Domain.Invoicing;
using Resort.Domain.Tax;

namespace Resort.Application.Validation;

// Field rules for invoices, credit and debit notes (Sprint 12). Rules that need the invoice (what is left to credit on a
// line, whether it is already cancelled) are in InvoiceService and are reported under the same field names.

public sealed class IssueInvoiceRequestValidator : AbstractValidator<IssueInvoiceRequest>
{
    public IssueInvoiceRequestValidator()
    {
        RuleFor(x => x.CustomerName).MaximumLength(200).WithMessage("The customer name can be at most 200 characters.");
        RuleFor(x => x.CustomerGstin).Must(g => string.IsNullOrWhiteSpace(g) || Gstin.Problem(g) is null)
            .WithMessage(x => Gstin.Problem(x.CustomerGstin) ?? "")
            .Must((x, g) => string.IsNullOrWhiteSpace(g) || !string.IsNullOrWhiteSpace(x.CustomerName))
            .WithMessage("Enter the customer's name (the company's legal name for a B2B invoice) with the GSTIN.");
        RuleFor(x => x.CustomerAddress).MaximumLength(300).WithMessage("The address can be at most 300 characters.");
        RuleFor(x => x.CustomerStateCode).Must(s => string.IsNullOrWhiteSpace(s) || GstStates.Names.ContainsKey(s.Trim()))
            .WithMessage(x => $"{x.CustomerStateCode} isn't a GST state code. Choose the state from the list.");
        RuleFor(x => x.Notes).MaximumLength(500).WithMessage("The note can be at most 500 characters.");
    }
}

public sealed class CreateCreditNoteRequestValidator : AbstractValidator<CreateCreditNoteRequest>
{
    public CreateCreditNoteRequestValidator()
    {
        RuleFor(x => x.Type).Must(t => t is not null && CreditNoteTypes.All.Contains(t.Trim().ToUpperInvariant()))
            .WithMessage("Choose what the credit note does: CANCEL (the whole invoice, to issue it again) or REDUCE (some lines or part of them).");
        PaymentRules.Reason(this, x => x.Reason, "Guest was charged for a dinner they didn't have", "credit note is issued");
        RuleForEach(x => x.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.LineId).NotEmpty().WithMessage("Choose the line to credit.");
            l.RuleFor(x => x.Amount).Must(a => a is null || a > 0).WithMessage("The amount to credit must be more than ₹0, or leave it empty to credit the whole line.")
                .Must(a => a is null || Money.InPaise(a.Value)).WithMessage("The amount can have at most 2 decimals (paise).");
        });
    }
}

public sealed class CreateDebitNoteRequestValidator : AbstractValidator<CreateDebitNoteRequest>
{
    public CreateDebitNoteRequestValidator()
    {
        PaymentRules.Reason(this, x => x.Reason, "Minibar used on the last night, found after check-out", "debit note is issued");
    }
}
