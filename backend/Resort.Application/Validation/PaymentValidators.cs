using FluentValidation;
using Resort.Contracts;
using Resort.Domain.Payments;

namespace Resort.Application.Validation;

// Field rules for payments and the cash drawer (Sprint 10). Rules that need the booking, the folio or the drawer (what a
// folio owes, what is left to refund, whether the drawer is open) are in PaymentService and CashDrawerService and are
// reported under the same field names.

internal static class PaymentRules
{
    public static readonly string MethodMessage =
        $"Choose how the money was paid: {string.Join(", ", PaymentMethods.Desk.Select(PaymentMethods.Words))}.";

    public const string OnlineMessage =
        "Online payments are recorded by Razorpay when the guest pays. Send the guest a payment link from the booking instead.";

    public static bool IsMethod(string? m) => m is not null && PaymentMethods.All.Contains(m.Trim().ToUpperInvariant());

    public static bool IsDeskMethod(string? m) => m is not null && PaymentMethods.Desk.Contains(m.Trim().ToUpperInvariant());

    public static bool IsOnline(string? m) => m?.Trim().ToUpperInvariant() == PaymentMethods.Online;

    /// <summary>A reference for <paramref name="method"/>: at most 100 characters and never a card number (brief §3).</summary>
    public static void Reference<T>(AbstractValidator<T> v, System.Linq.Expressions.Expression<Func<T, string?>> field, Func<T, string?> method)
    {
        v.RuleFor(field).MaximumLength(100).WithMessage("The reference can be at most 100 characters.")
            .Must((x, r) => !IsCard(method(x)) || PaymentMath.LongestDigitRun(r) <= 12)
            .WithMessage("Don't type the card number: card details are never stored. Enter the approval code from the terminal slip (up to 12 digits), or leave it empty.")
            .Must(r => !PaymentMath.LooksLikeCardNumber(r))
            .WithMessage("This looks like a card number, and card details are never stored. Enter the UPI transaction id or bank reference instead.");
    }

    private static bool IsCard(string? method) => method?.Trim().ToUpperInvariant() == PaymentMethods.Card;

    public static void Reason<T>(AbstractValidator<T> v, System.Linq.Expressions.Expression<Func<T, string?>> field, string example, string what)
    {
        v.RuleFor(field).Must(r => r is not null && r.Trim().Length >= 3)
            .WithMessage($"Say why the {what} (at least 3 characters), e.g. {example}.")
            .MaximumLength(500).WithMessage("The reason can be at most 500 characters.");
    }
}

public sealed class CreatePaymentRequestValidator : AbstractValidator<CreatePaymentRequest>
{
    public CreatePaymentRequestValidator()
    {
        RuleFor(x => x.Method).Must(m => !PaymentRules.IsOnline(m)).WithMessage(PaymentRules.OnlineMessage)
            .Must(PaymentRules.IsDeskMethod).WithMessage(PaymentRules.MethodMessage);
        Money.Rules(this, x => x.Amount, "The amount", positive: true);
        RuleFor(x => x.ReservationId).Must((x, id) => x.FolioId is not null || (id is not null && id != Guid.Empty))
            .WithMessage("Choose the folio this pays, or the booking to put the money on account for.");
        RuleFor(x => x.FolioId).Must(id => id is null || id != Guid.Empty).WithMessage("Choose the folio, or leave it empty.");
        PaymentRules.Reference(this, x => x.PaymentReference, x => x.Method);
        RuleFor(x => x.Note).MaximumLength(500).WithMessage("The note can be at most 500 characters.")
            .Must(n => !PaymentMath.LooksLikeCardNumber(n)).WithMessage("This looks like a card number, and card details are never stored. Take it out of the note.");
    }
}

public sealed class AllocatePaymentRequestValidator : AbstractValidator<AllocatePaymentRequest>
{
    public AllocatePaymentRequestValidator()
    {
        RuleFor(x => x.Allocations).NotEmpty().WithMessage("Choose at least one folio and how much goes on it.")
            .Must(a => a is null || a.Count <= 20).WithMessage("Allocate to at most 20 folios at a time.")
            .Must(a => a is null || a.All(l => l.FolioId != Guid.Empty)).WithMessage("Choose the folio for every line.")
            .Must(a => a is null || a.Select(l => l.FolioId).Distinct().Count() == a.Count).WithMessage("Each folio can appear once; add the amounts together.")
            .Must(a => a is null || a.All(l => l.Amount > 0)).WithMessage("Every amount must be more than ₹0.")
            .Must(a => a is null || a.All(l => Money.InPaise(l.Amount))).WithMessage("Amounts can have at most 2 decimal places (paise).");
    }
}

public sealed class RefundPaymentRequestValidator : AbstractValidator<RefundPaymentRequest>
{
    public RefundPaymentRequestValidator()
    {
        Money.Rules(this, x => x.Amount, "The refund", positive: true);
        PaymentRules.Reason(this, x => x.Reason, "Guest cancelled the airport transfer", "money is refunded");
        RuleFor(x => x.Method).Must(m => string.IsNullOrWhiteSpace(m) || PaymentRules.IsMethod(m))
            .WithMessage($"Choose how the money goes back: {string.Join(", ", PaymentMethods.All.Select(PaymentMethods.Words))}.");
        RuleFor(x => x.FolioId).Must(id => id is null || id != Guid.Empty).WithMessage("Choose the folio, or leave it empty.");
        PaymentRules.Reference(this, x => x.Reference, x => x.Method);
    }
}

public sealed class VoidPaymentRequestValidator : AbstractValidator<VoidPaymentRequest>
{
    public VoidPaymentRequestValidator() =>
        PaymentRules.Reason(this, x => x.Reason, "Entered twice by mistake", "payment is voided");
}

public sealed class OpenCashDrawerRequestValidator : AbstractValidator<OpenCashDrawerRequest>
{
    public OpenCashDrawerRequestValidator()
    {
        Money.Rules(this, x => x.OpeningFloat, "The opening float");
        RuleFor(x => x.Note).MaximumLength(500).WithMessage("The note can be at most 500 characters.");
    }
}

public sealed class CloseCashDrawerRequestValidator : AbstractValidator<CloseCashDrawerRequest>
{
    public CloseCashDrawerRequestValidator()
    {
        Money.Rules(this, x => x.CountedAmount, "The counted cash");
        RuleFor(x => x.Note).MaximumLength(500).WithMessage("The note can be at most 500 characters.");
    }
}

public sealed class ApproveVarianceRequestValidator : AbstractValidator<ApproveVarianceRequest>
{
    public ApproveVarianceRequestValidator() =>
        PaymentRules.Reason(this, x => x.Note, "Change given wrongly to a guest; staff told", "variance is approved");
}

// ---------------------------------------------------------------- Razorpay payment links (Sprint 11)

public sealed class CreatePaymentLinkRequestValidator : AbstractValidator<CreatePaymentLinkRequest>
{
    public CreatePaymentLinkRequestValidator()
    {
        RuleFor(x => x.ReservationId).NotEqual(Guid.Empty).WithMessage("Choose the booking the guest is paying for.");
        Money.Rules(this, x => x.Amount, "The amount", positive: true);
        RuleFor(x => x.Amount).Must(a => a <= 0 || a >= 1m).WithMessage("The amount must be at least ₹1, Razorpay's smallest payment.");
        RuleFor(x => x.Note).MaximumLength(500).WithMessage("The note can be at most 500 characters.")
            .Must(n => !PaymentMath.LooksLikeCardNumber(n)).WithMessage("This looks like a card number, and card details are never stored. Take it out of the note.");
    }
}

public sealed class CancelPaymentLinkRequestValidator : AbstractValidator<CancelPaymentLinkRequest>
{
    public CancelPaymentLinkRequestValidator() =>
        PaymentRules.Reason(this, x => x.Reason, "Guest will pay at the desk", "link is cancelled");
}
