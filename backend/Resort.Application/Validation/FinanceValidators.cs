using FluentValidation;
using Resort.Contracts;
using Resort.Domain.Finance;
using Resort.Domain.Tax;

namespace Resort.Application.Validation;

// Field rules for expenses and budgets (Sprint 22). Rules that need data (the date at the property, the category and
// department, a bill recorded twice, the receipt limit) are in ExpenseService and BudgetService under the same field names.

public sealed class SaveExpenseCategoryRequestValidator : AbstractValidator<SaveExpenseCategoryRequest>
{
    public SaveExpenseCategoryRequestValidator()
    {
        RuleFor(x => x.Code).Must(ActivityRules.Code).WithMessage("The code is 1–10 letters or digits starting with a letter, e.g. FUEL.");
        RuleFor(x => x.Name).Must(n => ActivityRules.Required(n, 2, 60)).WithMessage("Give the category a name, 2–60 characters, e.g. Fuel.");
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 1000).WithMessage("The order is 0–1000.");
    }
}

public sealed class SaveCostCentreRequestValidator : AbstractValidator<SaveCostCentreRequest>
{
    public SaveCostCentreRequestValidator()
    {
        RuleFor(x => x.Code).Must(ActivityRules.Code).WithMessage("The code is 1–10 letters or digits starting with a letter, e.g. HK.");
        RuleFor(x => x.Name).Must(n => ActivityRules.Required(n, 2, 60)).WithMessage("Give the department a name, 2–60 characters, e.g. Housekeeping.");
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 1000).WithMessage("The order is 0–1000.");
    }
}

public sealed class SaveExpenseRequestValidator : AbstractValidator<SaveExpenseRequest>
{
    public SaveExpenseRequestValidator()
    {
        RuleFor(x => x.ExpenseDate).Must(d => d.Year is >= 2000 and <= 2100).WithMessage("Enter the day the money was spent.");
        RuleFor(x => x.CategoryId).NotEmpty().WithMessage("Choose what the money was for.");
        RuleFor(x => x.CostCentreId).NotEmpty().WithMessage("Choose the department that spent it.");
        RuleFor(x => x.Description).Must(d => ActivityRules.Required(d, 3, 200))
            .WithMessage("Say what was bought or paid for, 3–200 characters, e.g. Diesel for the generator.");
        RuleFor(x => x.Payee).Must(p => ActivityRules.Required(p, 2, 100)).WithMessage("Say who was paid, 2–100 characters, e.g. Malvan Fuels.");
        RuleFor(x => x.PaymentMethod).Must(m => ExpensePaymentMethods.All.Contains((m ?? "").Trim().ToUpperInvariant()))
            .WithMessage("Choose how it was paid: cash, UPI, card, bank transfer or cheque.");
        RuleFor(x => x.PaymentReference).Must(r => ActivityRules.Text(r, 1, 60)).WithMessage("The payment reference is at most 60 characters.");
        Money.Rules(this, x => x.Amount, "The amount paid", positive: true);
        RuleFor(x => x.VendorGstin).Must(g => string.IsNullOrWhiteSpace(g) || Gstin.Problem(g) is null)
            .WithMessage(x => Gstin.Problem(x.VendorGstin) ?? "");
        RuleFor(x => x.BillNumber).Must(b => ActivityRules.Text(b, 1, 30)).WithMessage("The bill number is at most 30 characters.");
        RuleFor(x => x.GstAmount).Must(g => g >= 0).WithMessage("The GST can't be negative.")
            .Must(Money.InPaise).WithMessage("The GST can have at most 2 decimal places (paise).")
            .Must((x, g) => g == 0 || g < x.Amount).WithMessage("The GST is part of the amount paid, so it must be less than the amount.");
        RuleFor(x => x.Note).Must(n => ActivityRules.Text(n, 1, 500)).WithMessage("The note is at most 500 characters.");
    }
}

/// <summary>Reject and cancel say why; approve may carry a note.</summary>
public sealed class ExpenseReasonValidator : AbstractValidator<ExpenseDecisionRequest>
{
    public ExpenseReasonValidator()
    {
        RuleFor(x => x.Reason).Must(r => ActivityRules.Required(r, 3, 300))
            .WithMessage("Say why (3–300 characters), e.g. No bill, or Paid from the owner's account.");
    }
}

public sealed class CreateBudgetRequestValidator : AbstractValidator<CreateBudgetRequest>
{
    public CreateBudgetRequestValidator()
    {
        RuleFor(x => x.Year).InclusiveBetween(2000, 2100).WithMessage("Choose the financial year, e.g. 2026 for 2026-27.");
        RuleFor(x => x.Name).Must(n => ActivityRules.Text(n, 2, 60)).WithMessage("The name is 2–60 characters, e.g. Budget 2026-27.");
        RuleFor(x => x.Note).Must(n => ActivityRules.Text(n, 1, 500)).WithMessage("The note is at most 500 characters.");
        RuleFor(x => x.IncreasePercent).InclusiveBetween(-50, 100).WithMessage("The increase is −50 % to 100 %.")
            .Must(p => decimal.Round(p, 2) == p).WithMessage("The increase can have at most 2 decimal places.");
    }
}

public sealed class SaveBudgetRowRequestValidator : AbstractValidator<SaveBudgetRowRequest>
{
    public SaveBudgetRowRequestValidator()
    {
        RuleFor(x => x.CostCentreId).NotEmpty().WithMessage("Choose the department.");
        RuleFor(x => x.Months).Must(m => m is { Count: 12 }).WithMessage("Give twelve amounts, April to March.");
        RuleForEach(x => x.Months).Must(a => a >= 0).WithMessage("A month's budget can't be negative.")
            .Must(a => a <= Money.Max).WithMessage($"A month's budget can't be more than {Money.Rupees(Money.Max)}.")
            .Must(Money.InPaise).WithMessage("A month's budget can have at most 2 decimal places (paise).");
    }
}
