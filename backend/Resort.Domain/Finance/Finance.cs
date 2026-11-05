using Resort.Domain.Common;

namespace Resort.Domain.Finance;

/// <summary>An expense waits for approval (SUBMITTED), then is APPROVED (it counts in the budget's actuals), REJECTED (with
/// the reason) or CANCELLED by the person who recorded it. Once decided it never changes: the database trigger
/// fn_expenses_frozen refuses any change to a row that isn't SUBMITTED.</summary>
public static class ExpenseStatuses
{
    public const string Submitted = "SUBMITTED";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Cancelled = "CANCELLED";

    public static readonly (string From, string To)[] Moves = [(Submitted, Approved), (Submitted, Rejected), (Submitted, Cancelled)];

    public static string Words(string s) => s switch
    {
        Submitted => "Waiting for approval",
        Approved => "Approved",
        Rejected => "Rejected",
        Cancelled => "Cancelled",
        _ => s,
    };
}

/// <summary>How the money went out.</summary>
public static class ExpensePaymentMethods
{
    public const string Cash = "CASH";
    public const string Upi = "UPI";
    public const string Card = "CARD";
    public const string BankTransfer = "BANK_TRANSFER";
    public const string Cheque = "CHEQUE";

    public static readonly string[] All = [Cash, Upi, Card, BankTransfer, Cheque];

    public static string Words(string s) => s switch
    {
        Cash => "Cash",
        Upi => "UPI",
        Card => "Card",
        BankTransfer => "Bank transfer",
        Cheque => "Cheque",
        _ => s,
    };
}

/// <summary>What an expense was for: Electricity and water, Repairs, Fuel… GstClaimable false: the input tax on it can't be
/// claimed (food and drink for staff, for example, under section 17(5) of the CGST Act), so its full amount is the cost.</summary>
public class ExpenseCategory : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public bool GstClaimable { get; set; } = true;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }
}

/// <summary>A department that spends and has a budget: Front office, Housekeeping, Food and beverage…</summary>
public class CostCentre : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }
}

/// <summary>
/// Money spent (EX00001). Amount is what was paid, GST included. On a GST bill (the vendor's GSTIN and the bill number),
/// GstAmount is split into CGST + SGST (same state) or IGST (another state), and TaxableValue = Amount − GstAmount.
/// ItcEligible: the property can claim the input tax (it is a regular GST registrant on the date, and the category allows
/// it). CostAmount is what counts against the budget: the taxable value when the tax comes back as input credit, otherwise
/// the whole amount. Only APPROVED expenses count.
/// </summary>
public class Expense : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Code { get; set; } = default!;
    public DateOnly ExpenseDate { get; set; }
    public Guid CategoryId { get; set; }
    public Guid CostCentreId { get; set; }
    public string Description { get; set; } = default!;
    public string Payee { get; set; } = default!;
    public string PaymentMethod { get; set; } = ExpensePaymentMethods.Cash;
    public string? PaymentReference { get; set; }
    public decimal Amount { get; set; }
    public string? VendorGstin { get; set; }
    public string? BillNumber { get; set; }
    public decimal GstAmount { get; set; }
    public decimal TaxableValue { get; set; }
    public decimal Cgst { get; set; }
    public decimal Sgst { get; set; }
    public decimal Igst { get; set; }
    public bool ItcEligible { get; set; }
    public decimal CostAmount { get; set; }
    public string? Note { get; set; }
    public string Status { get; set; } = ExpenseStatuses.Submitted;
    public DateTimeOffset? DecidedAt { get; set; }
    public Guid? DecidedBy { get; set; }
    public string? DecisionReason { get; set; }
    public uint Version { get; set; }
}

/// <summary>A receipt or bill (a photo shrunk on the phone, or a PDF). The bytes are encrypted in the file store; only the
/// key is here. Added while the expense waits for approval; never changed or removed.</summary>
public class ExpenseAttachment : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; set; }
    public Guid ExpenseId { get; set; }
    public string StorageKey { get; set; } = default!;
    public string KeyId { get; set; } = default!;
    public string FileName { get; set; } = default!;
    public string ContentType { get; set; } = default!;
    public int SizeBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

/// <summary>A property's budget for one financial year (April to March). Total is the sum of its lines, kept by the service
/// so that every change of a department's plan shows in the audit trail and moves the version.</summary>
public class Budget : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    /// <summary>1 April of the year the budget starts: 2026-04-01 is the financial year 2026-27.</summary>
    public DateOnly YearStart { get; set; }
    public string Name { get; set; } = default!;
    public string? Note { get; set; }
    public decimal Total { get; set; }
    public uint Version { get; set; }
}

/// <summary>What a department plans to spend in one month of the budget's year. Month is the first of the month.</summary>
public class BudgetLine : AuditableEntity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid BudgetId { get; set; }
    public Guid CostCentreId { get; set; }
    public DateOnly Month { get; set; }
    public decimal Amount { get; set; }
}

/// <summary>Money and calendar rules for expenses and budgets, without the database. Pure, unit-tested.</summary>
public static class FinanceMath
{
    public const int MaxReceiptBytes = 2 * 1024 * 1024;
    public const int MaxReceiptsPerExpense = 4;
    /// <summary>An expense can be dated up to this many days back (a bill found late in a drawer).</summary>
    public const int MaxDaysBack = 400;

    /// <summary>1 April of the financial year <paramref name="date"/> falls in (India: April to March).</summary>
    public static DateOnly YearStart(DateOnly date) => new(date.Month >= 4 ? date.Year : date.Year - 1, 4, 1);

    /// <summary>"2026-27" for the year starting 1 April 2026.</summary>
    public static string YearLabel(DateOnly yearStart) => $"{yearStart.Year}-{(yearStart.Year + 1) % 100:00}";

    /// <summary>The twelve months of the year, April first.</summary>
    public static IReadOnlyList<DateOnly> Months(DateOnly yearStart) => Enumerable.Range(0, 12).Select(yearStart.AddMonths).ToList();

    /// <summary>GST on a bill: CGST and SGST halves within the state (SGST takes the odd paisa), IGST from another state.</summary>
    public static (decimal Cgst, decimal Sgst, decimal Igst) SplitGst(decimal gst, bool intraState)
    {
        if (!intraState) return (0, 0, gst);
        var cgst = decimal.Round(gst / 2, 2, MidpointRounding.ToZero);
        return (cgst, gst - cgst, 0);
    }

    /// <summary>What counts against the budget: the taxable value when the tax is claimed back as input credit, otherwise
    /// everything paid.</summary>
    public static decimal Cost(decimal amount, decimal gst, bool itcEligible) => itcEligible ? amount - gst : amount;

    /// <summary>An annual amount spread over twelve months in whole rupees where it can be; March takes what is left.</summary>
    public static decimal[] Spread(decimal annual)
    {
        var month = decimal.Floor(annual / 12);
        var months = Enumerable.Repeat(month, 12).ToArray();
        months[11] = annual - month * 11;
        return months;
    }

    /// <summary>
    /// The simple forecast: what the year will cost if spending goes on at the rate so far. Days counted from 1 April to
    /// <paramref name="asOf"/> (inclusive); the year's own length (365 or 366). Null before any day of the year has passed.
    /// </summary>
    public static decimal? Forecast(decimal actualToDate, DateOnly yearStart, DateOnly asOf)
    {
        var yearDays = yearStart.AddYears(1).DayNumber - yearStart.DayNumber;
        var elapsed = Math.Min(asOf.DayNumber - yearStart.DayNumber + 1, yearDays);
        if (elapsed <= 0) return null;
        return decimal.Round(actualToDate * yearDays / elapsed, 2);
    }

    /// <summary>How a department is doing: OVER (spent more than planned so far), HEADING_OVER (the forecast passes the
    /// year's budget), NO_BUDGET (money spent with nothing planned), ON_TRACK.</summary>
    public static string Standing(decimal budgetToDate, decimal actualToDate, decimal annualBudget, decimal? forecast) =>
        annualBudget == 0 ? "NO_BUDGET"
        : actualToDate > budgetToDate ? "OVER"
        : forecast is { } f && f > annualBudget ? "HEADING_OVER"
        : "ON_TRACK";
}
