using System.Globalization;
using Resort.Domain.Common;

namespace Resort.Domain.Leave;

public static class LeaveRequestStatuses
{
    public const string Pending = "PENDING";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Cancelled = "CANCELLED";
    public static readonly string[] All = [Pending, Approved, Rejected, Cancelled];
}

public static class LeaveEntryTypes
{
    public const string Opening = "OPENING";
    public const string Accrual = "ACCRUAL";
    public const string Taken = "TAKEN";
    public const string Returned = "RETURNED";
    public const string Adjustment = "ADJUSTMENT";
}

public static class LeaveAccruals
{
    public const string Monthly = "MONTHLY";
    public const string Yearly = "YEARLY";
    public const string None = "NONE";
    public static readonly string[] All = [Monthly, Yearly, None];
}

/// <summary>A kind of leave: Casual (CL), Sick (SL), Earned (EL), Leave without pay (LOP).</summary>
public class LeaveType : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public bool IsPaid { get; set; } = true;
    public bool NeedsBalance { get; set; } = true;
    public bool AllowHalfDay { get; set; } = true;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }
}

/// <summary>How many days a year a leave type gives and how they are earned. EmploymentType null: everyone.</summary>
public class LeavePolicy : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid LeaveTypeId { get; set; }
    public string? EmploymentType { get; set; }
    public decimal DaysPerYear { get; set; }
    public string Accrual { get; set; } = LeaveAccruals.Monthly;
    public decimal? MaxBalance { get; set; }
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }
}

/// <summary>Leave asked for. CountedDates: the dates that count (not holidays or the weekly day off); Days: their total
/// with the half days.</summary>
public class LeaveRequest : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Code { get; set; } = default!;
    public Guid EmployeeId { get; set; }
    public Guid LeaveTypeId { get; set; }
    public DateOnly FromDate { get; set; }
    public DateOnly ToDate { get; set; }
    public bool HalfDayStart { get; set; }
    public bool HalfDayEnd { get; set; }
    public decimal Days { get; set; }
    public DateOnly[] CountedDates { get; set; } = [];
    public string? Reason { get; set; }
    public string Status { get; set; } = LeaveRequestStatuses.Pending;
    public Guid? RequestedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public Guid? DecidedBy { get; set; }
    public string? DecisionNote { get; set; }
    public short ShiftsCancelled { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledBy { get; set; }
    public string? CancelReason { get; set; }
    public uint Version { get; set; }

    /// <summary>How much of a counted date the leave takes: 0.5 on a half day at either end, else 1.</summary>
    public decimal DayFraction(DateOnly d) => LeaveMath.Fraction(d, FromDate, ToDate, HalfDayStart, HalfDayEnd);
}

/// <summary>One change to a balance, never changed. Days: + earned or given back, − taken.</summary>
public class LeaveLedgerEntry : Entity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid EmployeeId { get; set; }
    public Guid LeaveTypeId { get; set; }
    public string EntryType { get; set; } = default!;
    public decimal Days { get; set; }
    public DateOnly EntryDate { get; set; }
    public string? Period { get; set; }
    public Guid? LeaveRequestId { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

/// <summary>The balance per employee and leave type: kept equal to the ledger's sum by a database trigger. Read-only here.</summary>
public class LeaveBalance : Entity, IPropertyScoped
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid EmployeeId { get; set; }
    public Guid LeaveTypeId { get; set; }
    public decimal Balance { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A public holiday at the property.</summary>
public class Holiday : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public DateOnly HolidayDate { get; set; }
    public string Name { get; set; } = default!;
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }
}

/// <summary>A monthly payroll file that was made, kept as it was sent.</summary>
public class PayrollExport : Entity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public string Format { get; set; } = PayrollCsv.Format;
    public string FileName { get; set; } = default!;
    public byte[] Content { get; set; } = [];
    public string Sha256 { get; set; } = default!;
    public int Employees { get; set; }
    public int TimesheetsLocked { get; set; }
    public string[] Warnings { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

/// <summary>A leave range worked out: the dates that count, the total, and the dates left out with why.</summary>
public sealed record LeaveDays(IReadOnlyList<DateOnly> Counted, decimal Days, IReadOnlyList<(DateOnly Date, string Why)> Skipped);

/// <summary>
/// Pure leave arithmetic (brief §45).
/// <list type="bullet">
/// <item>A range counts every date from–to except the property's holidays and the person's weekly day off. Half days: the
/// first date counts 0.5 when it starts after lunch, the last 0.5 when it ends at lunch (a one-day request is one or the
/// other). Example: Mon 2 – Fri 6 Nov with Wed a holiday and a half day on Friday = 3.5 days.</item>
/// <item>Monthly accrual: a twelfth of the year's days on the 1st of each month for people who worked here on the 1st.
/// Yearly: all of them on 1 January, or for someone who joins in the year the months from the first full one, rounded down
/// to a half day. Neither goes above the policy's maximum balance.</item>
/// </list>
/// </summary>
public static class LeaveMath
{
    public const int MaxRangeDays = 90;

    public static decimal Fraction(DateOnly d, DateOnly from, DateOnly to, bool halfStart, bool halfEnd) =>
        (d == from && halfStart) || (d == to && halfEnd) ? 0.5m : 1m;

    public static LeaveDays Count(DateOnly from, DateOnly to, bool halfStart, bool halfEnd, IReadOnlyDictionary<DateOnly, string> holidays,
        short? weeklyOff)
    {
        var counted = new List<DateOnly>();
        var skipped = new List<(DateOnly, string)>();
        decimal days = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (holidays.TryGetValue(d, out var name)) { skipped.Add((d, name)); continue; }
            if (weeklyOff is { } off && IsoDay(d) == off) { skipped.Add((d, "weekly day off")); continue; }
            counted.Add(d);
            days += Fraction(d, from, to, halfStart, halfEnd);
        }
        return new LeaveDays(counted, days, skipped);
    }

    /// <summary>A month's accrual: a twelfth of the year's days, to two decimals.</summary>
    public static decimal Monthly(decimal daysPerYear) => Math.Round(daysPerYear / 12m, 2, MidpointRounding.ToZero);

    /// <summary>The year's accrual for someone who joined on <paramref name="joinedOn"/>: all of it if they joined before the
    /// year began, else the months from the first one they worked from its 1st, rounded down to a half day.</summary>
    public static decimal Yearly(decimal daysPerYear, int year, DateOnly joinedOn)
    {
        var first = new DateOnly(year, 1, 1);
        if (joinedOn <= first) return daysPerYear;
        if (joinedOn.Year > year) return 0;
        var fromMonth = joinedOn.Day == 1 ? joinedOn.Month : joinedOn.Month + 1;
        var months = Math.Max(0, 13 - fromMonth);
        return Math.Floor(daysPerYear * months / 12m * 2m) / 2m;
    }

    /// <summary>The accrual capped so the balance doesn't go above the maximum (never below zero).</summary>
    public static decimal Capped(decimal amount, decimal balance, decimal? max) =>
        max is { } m ? Math.Max(0, Math.Min(amount, m - balance)) : amount;

    public static short IsoDay(DateOnly d) => (short)(d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek);

    /// <summary>"1", "0.5", "3.5" days.</summary>
    public static string D(decimal days) => days.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>"1 day", "0.5 day", "3.5 days".</summary>
    public static string Days(decimal days) => $"{D(days)} {(days is 1m or 0.5m ? "day" : "days")}";
}

/// <summary>One employee's month for payroll.</summary>
public sealed record PayrollRow(
    string EmployeeCode, string EmployeeName, string Department, string EmploymentType, DateOnly PeriodStart, DateOnly PeriodEnd, int EmployedDays,
    int WeeklyOffs, int Holidays, int DaysWorked, int WorkedMinutes, int OvertimeMinutes, int LateMinutes, decimal PaidLeaveDays,
    decimal UnpaidLeaveDays, int AbsentDays, decimal PayableDays, string LeaveDetail);

/// <summary>
/// The generic payroll CSV (format GENERIC_CSV_V1, docs/PAYROLL.md): one row per employee who worked here during the month,
/// UTF-8 with a header row, comma separated, dates as yyyy-MM-dd, hours as decimals with two places. No pay amounts: the
/// payroll provider holds the salaries. Cells that start with = + - @ are prefixed with ' so a spreadsheet doesn't run them.
/// </summary>
public static class PayrollCsv
{
    public const string Format = "GENERIC_CSV_V1";

    public static readonly string[] Header =
    [
        "employee_code", "employee_name", "department", "employment_type", "period_start", "period_end", "employed_days", "weekly_offs",
        "holidays", "days_worked", "worked_hours", "overtime_hours", "late_minutes", "paid_leave_days", "unpaid_leave_days", "absent_days",
        "payable_days", "leave_detail",
    ];

    /// <summary>Payable days: the days employed in the month, less unpaid leave and days rostered but not worked.</summary>
    public static decimal Payable(int employedDays, decimal unpaidLeave, int absent) => Math.Max(0, employedDays - unpaidLeave - absent);

    public static string Write(IEnumerable<PayrollRow> rows)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(string.Join(',', Header)).Append("\r\n");
        foreach (var r in rows)
        {
            string[] cells =
            [
                r.EmployeeCode, r.EmployeeName, r.Department, r.EmploymentType, Day(r.PeriodStart), Day(r.PeriodEnd), I(r.EmployedDays), I(r.WeeklyOffs),
                I(r.Holidays), I(r.DaysWorked), H(r.WorkedMinutes), H(r.OvertimeMinutes), I(r.LateMinutes), LeaveMath.D(r.PaidLeaveDays),
                LeaveMath.D(r.UnpaidLeaveDays), I(r.AbsentDays), LeaveMath.D(r.PayableDays), r.LeaveDetail,
            ];
            sb.Append(string.Join(',', cells.Select(Cell))).Append("\r\n");
        }
        return sb.ToString();
    }

    public static string Cell(string value)
    {
        var v = value.Length > 0 && "=+-@".Contains(value[0]) ? "'" + value : value;
        return v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    private static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string I(int n) => n.ToString(CultureInfo.InvariantCulture);
    private static string H(int minutes) => (minutes / 60m).ToString("0.00", CultureInfo.InvariantCulture);
}
