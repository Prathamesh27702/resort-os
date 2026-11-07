namespace Resort.Contracts;

// Sprint 22 — expenses and budgets (brief §40, §41). Amounts are rupees with paise. A financial year runs April to March
// and is named by the year it starts: Year 2026 is 2026-27. Months are the first of the month ("2026-11-01").

// ---------------------------------------------------------------- set-up

/// <summary>What money is spent on. GstClaimable false: the input tax on it can't be claimed back (food for staff, for
/// example), so the whole amount counts against the budget.</summary>
public sealed record ExpenseCategoryDto(Guid Id, string Code, string Name, bool GstClaimable, int SortOrder, bool IsActive, uint Version);

public sealed record SaveExpenseCategoryRequest(string Code, string Name, bool GstClaimable = true, int SortOrder = 0, bool IsActive = true,
    uint? Version = null);

/// <summary>A department that spends and has a budget.</summary>
public sealed record CostCentreDto(Guid Id, string Code, string Name, int SortOrder, bool IsActive, uint Version);

public sealed record SaveCostCentreRequest(string Code, string Name, int SortOrder = 0, bool IsActive = true, uint? Version = null);

/// <summary>Everything the expense form needs. ReceiptRequiredOver: an expense over this needs a receipt before it can be
/// approved (property setting expenses.receipt_required_over). GstRegistered: the property is a regular GST registrant
/// today, so the GST on a vendor's bill can usually be claimed. CanApprove / CanManage: the caller's rights here.</summary>
public sealed record ExpenseSetupDto(IReadOnlyList<ExpenseCategoryDto> Categories, IReadOnlyList<CostCentreDto> CostCentres,
    IReadOnlyList<string> PaymentMethods, decimal ReceiptRequiredOver, bool GstRegistered, string? StateCode, bool CanApprove, bool CanManage);

// ---------------------------------------------------------------- expenses

/// <summary>
/// Records money spent. Amount is what was paid, GST included. For a GST bill give the vendor's GSTIN, the bill number and
/// the GST shown on it (GstAmount); the API splits it into CGST + SGST (the vendor is in the property's state) or IGST.
/// Without a GSTIN the expense has no GST. The receipt is uploaded afterwards (POST …/receipts).
/// </summary>
public sealed record SaveExpenseRequest(DateOnly ExpenseDate, Guid CategoryId, Guid CostCentreId, string Description, string Payee,
    string PaymentMethod, decimal Amount, string? PaymentReference = null, string? VendorGstin = null, string? BillNumber = null,
    decimal GstAmount = 0, string? Note = null, uint? Version = null);

/// <summary>Rejecting or cancelling says why (3–300 characters). Version: the expense as the caller saw it.</summary>
public sealed record ExpenseDecisionRequest(string? Reason = null, uint? Version = null);

public sealed record ExpenseReceiptDto(Guid Id, string FileName, string ContentType, int SizeBytes, DateTimeOffset CreatedAt, string? AddedBy);

/// <summary>
/// One expense. Status SUBMITTED (waiting for approval), APPROVED, REJECTED or CANCELLED. CostAmount is what counts against
/// the budget once approved: the taxable value when ItcEligible (the GST comes back as input credit), otherwise Amount.
/// NeedsReceipt: it is over the receipt limit and has none yet, so it can't be approved. Actions: what the caller can do
/// now (edit, receipt, approve, reject, cancel); a reason an action is refused is in Blocked (e.g. approve → "Add the
/// receipt first").
/// </summary>
public sealed record ExpenseDto(
    Guid Id, string Code, DateOnly ExpenseDate, Guid CategoryId, string CategoryName, Guid CostCentreId, string CostCentreName,
    string Description, string Payee, string PaymentMethod, string? PaymentReference, decimal Amount,
    string? VendorGstin, string? BillNumber, decimal GstAmount, decimal TaxableValue, decimal Cgst, decimal Sgst, decimal Igst,
    bool ItcEligible, decimal CostAmount, string? Note, string Status, string? RecordedBy, Guid? RecordedById, DateTimeOffset RecordedAt,
    DateTimeOffset? DecidedAt, string? DecidedBy, string? DecisionReason, bool NeedsReceipt, IReadOnlyList<ExpenseReceiptDto> Receipts,
    IReadOnlyList<string> Actions, IReadOnlyDictionary<string, string> Blocked, uint Version);

/// <summary>Expenses newest first with the totals of what is listed. Waiting: how many wait for approval at the property
/// (whatever the filter), WaitingAmount their total.</summary>
public sealed record ExpenseListDto(IReadOnlyList<ExpenseDto> Items, decimal Total, decimal CostTotal, int Waiting, decimal WaitingAmount);

// ---------------------------------------------------------------- budgets

/// <summary>A department's plan for the year: Months[0] is April, Months[11] March.</summary>
public sealed record BudgetRowDto(Guid CostCentreId, string CostCentreCode, string CostCentreName, IReadOnlyList<decimal> Months, decimal Total);

/// <summary>A property's budget for one financial year ("2026-27"), with every department's plan by month.</summary>
public sealed record BudgetDto(Guid Id, int Year, string Label, string Name, string? Note, decimal Total, IReadOnlyList<DateOnly> Months,
    IReadOnlyList<BudgetRowDto> Rows, uint Version);

public sealed record BudgetSummaryDto(Guid Id, int Year, string Label, string Name, decimal Total);

/// <summary>Starts the budget for the financial year starting in April of Year. CopyFromId: copy every department's
/// months from another budget, raised by IncreasePercent (e.g. 5 for 5 %).</summary>
public sealed record CreateBudgetRequest(int Year, string? Name = null, string? Note = null, Guid? CopyFromId = null, decimal IncreasePercent = 0);

/// <summary>Sets one department's plan for the year: twelve amounts, April first. Version: the budget as the caller saw it.</summary>
public sealed record SaveBudgetRowRequest(Guid CostCentreId, IReadOnlyList<decimal> Months, uint? Version = null);

/// <summary>
/// Budget against actual for one department, from April to the end of Through. Variance = budget − actual (more than 0:
/// under budget). Forecast: the year's spend if it goes on at the rate so far (actual ÷ days passed × days in the year).
/// Standing: ON_TRACK, OVER (spent more than planned so far), HEADING_OVER (the forecast passes the year's budget) or
/// NO_BUDGET (spent with nothing planned). Waiting: expenses not approved yet, which are not in the actuals.
/// Sprint 23: the actual is approved expenses (Expenses) + supplier bills at cost (SupplierBills) + pay (Pay, estimated;
/// null when the caller may not see pay and then not in the actual). CostCentreId empty with code NONE: bills and pay not
/// given a department.
/// </summary>
public sealed record BudgetVarianceRowDto(Guid CostCentreId, string CostCentreCode, string CostCentreName,
    decimal MonthBudget, decimal MonthActual, decimal BudgetToDate, decimal ActualToDate, decimal Variance, decimal? VariancePercent,
    decimal AnnualBudget, decimal? Forecast, decimal? ForecastVariance, string Standing, decimal Waiting, int WaitingCount,
    IReadOnlyList<BudgetCategoryActualDto> Categories, decimal Expenses = 0, decimal SupplierBills = 0, decimal? Pay = null);

public sealed record BudgetCategoryActualDto(Guid CategoryId, string Name, decimal Actual);

public sealed record BudgetMonthDto(DateOnly Month, decimal Budget, decimal Actual);

/// <summary>The budget report for a year, through a month (Through, the first of the month), with each department, the
/// property's total and the months of the year. BudgetId null: no budget has been made for the year yet (actuals only).
/// AsOf: the day the forecast runs to (today, or the end of Through when that is earlier). PayIncluded: pay is in the
/// actuals (the caller has employee_salary.read).</summary>
public sealed record BudgetReportDto(int Year, string Label, Guid? BudgetId, DateOnly Through, DateOnly AsOf,
    IReadOnlyList<BudgetVarianceRowDto> Rows, BudgetVarianceRowDto Total, IReadOnlyList<BudgetMonthDto> Months, bool PayIncluded = false);
