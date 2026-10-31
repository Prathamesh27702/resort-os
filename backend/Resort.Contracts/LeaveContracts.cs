namespace Resort.Contracts;

// Sprint 19 — leave, holidays and payroll export (brief §45, §74). Days are decimals with half days (0.5).

// ---------------------------------------------------------------- leave types and policies

/// <summary>A policy: DaysPerYear earned MONTHLY (a twelfth on the 1st), YEARLY (on 1 January) or NONE (by hand only),
/// never above MaxBalance. EmploymentType null: everyone.</summary>
public sealed record LeavePolicyDto(Guid Id, string? EmploymentType, decimal DaysPerYear, string Accrual, decimal? MaxBalance, bool IsActive, uint Version);

/// <summary>A kind of leave. IsPaid: paid in payroll. NeedsBalance: only from a balance (Leave without pay doesn't).</summary>
public sealed record LeaveTypeDto(
    Guid Id, string Code, string Name, bool IsPaid, bool NeedsBalance, bool AllowHalfDay, int SortOrder, bool IsActive,
    IReadOnlyList<LeavePolicyDto> Policies, uint Version);

/// <summary>Creates or changes a leave type with its policy for everyone (DaysPerYear, Accrual, MaxBalance).</summary>
public sealed record SaveLeaveTypeRequest(
    string Code, string Name, bool IsPaid, bool NeedsBalance, bool AllowHalfDay, int SortOrder, bool IsActive, decimal DaysPerYear, string Accrual,
    decimal? MaxBalance, uint? Version = null);

/// <summary>Sets the policy of a leave type for one employment type (or for everyone when EmploymentType is null).</summary>
public sealed record SaveLeavePolicyRequest(
    Guid LeaveTypeId, string? EmploymentType, decimal DaysPerYear, string Accrual, decimal? MaxBalance, bool IsActive = true, uint? Version = null);

// ---------------------------------------------------------------- holidays

public sealed record HolidayDto(Guid Id, DateOnly Date, string Name, bool IsActive, uint Version);

public sealed record SaveHolidayRequest(DateOnly Date, string Name, bool IsActive = true, uint? Version = null);

// ---------------------------------------------------------------- balances and ledger

/// <summary>One balance. Pending: days asked for and not decided yet. Available: Balance − Pending.</summary>
public sealed record LeaveBalanceDto(Guid LeaveTypeId, string Code, string Name, bool NeedsBalance, decimal Balance, decimal Pending, decimal Available,
    decimal TakenThisYear);

public sealed record EmployeeLeaveBalancesDto(Guid EmployeeId, string EmployeeCode, string EmployeeName, string DepartmentName, string EmploymentType,
    IReadOnlyList<LeaveBalanceDto> Balances);

/// <summary>One ledger entry. EntryType: OPENING, ACCRUAL, TAKEN, RETURNED, ADJUSTMENT. BalanceAfter: the running balance.</summary>
public sealed record LeaveLedgerEntryDto(
    Guid Id, string LeaveTypeCode, string EntryType, decimal Days, decimal BalanceAfter, DateOnly EntryDate, string? Period, string? RequestCode,
    string? Reason, DateTimeOffset CreatedAt, string? By);

public sealed record LeaveLedgerDto(EmployeeLeaveBalancesDto Employee, IReadOnlyList<LeaveLedgerEntryDto> Entries);

/// <summary>HR adds (Days &gt; 0) or takes off (Days &lt; 0) days by hand, with the reason. Opening: the balance brought in
/// from before (only positive).</summary>
public sealed record AdjustLeaveRequest(Guid EmployeeId, Guid LeaveTypeId, decimal Days, string Reason, bool Opening = false);

/// <summary>Posts the accrual for a month ("2026-11") for everyone (monthly policies; yearly ones in January). Safe to
/// run again: what is already posted is skipped.</summary>
public sealed record RunAccrualRequest(string Month);

public sealed record AccrualResultDto(string Month, int Posted, int AlreadyPosted, decimal Days, IReadOnlyList<string> Notes);

// ---------------------------------------------------------------- requests

/// <summary>
/// Leave asked for. EmployeeId: someone else (needs leave.approve); empty: the caller. HalfDayStart: the first day starts
/// after lunch. HalfDayEnd: the last day ends at lunch.
/// </summary>
public sealed record CreateLeaveRequest(Guid? EmployeeId, Guid LeaveTypeId, DateOnly FromDate, DateOnly ToDate, bool HalfDayStart, bool HalfDayEnd,
    string? Reason);

/// <summary>A warning about cover or other leave: Kind SHORT_STAFFED (a shift on the roster would have fewer people than it
/// needs), ALSO_OFF (someone else in the department is off), ROSTERED (a shift that approval takes off the roster).</summary>
public sealed record LeaveWarningDto(string Kind, DateOnly Date, string Message);

/// <summary>A request worked out before it is made: the days that count, those left out (holidays, day off) and why, the
/// balance after, and the cover warnings.</summary>
public sealed record LeavePreviewDto(
    decimal Days, IReadOnlyList<DateOnly> CountedDates, IReadOnlyList<string> Skipped, decimal? Balance, decimal? Available, decimal? BalanceAfter,
    IReadOnlyList<LeaveWarningDto> Warnings);

/// <summary>A leave request. Status: PENDING, APPROVED, REJECTED, CANCELLED. Actions: what the caller may do now
/// (approve, reject, cancel). Warnings: worked out now for a pending one.</summary>
public sealed record LeaveRequestDto(
    Guid Id, string Code, Guid EmployeeId, string EmployeeCode, string EmployeeName, string DepartmentName, Guid LeaveTypeId, string LeaveTypeCode,
    string LeaveTypeName, bool IsPaid, DateOnly FromDate, DateOnly ToDate, bool HalfDayStart, bool HalfDayEnd, decimal Days,
    IReadOnlyList<DateOnly> CountedDates, string? Reason, string Status, DateTimeOffset CreatedAt, string? RequestedBy, DateTimeOffset? DecidedAt,
    string? DecidedBy, string? DecisionNote, int ShiftsCancelled, DateTimeOffset? CancelledAt, string? CancelledBy, string? CancelReason,
    decimal? Balance, IReadOnlyList<LeaveWarningDto> Warnings, IReadOnlyList<string> Actions, uint Version);

public sealed record LeaveListDto(IReadOnlyList<LeaveRequestDto> Requests, IReadOnlyDictionary<string, int> Counts);

public sealed record ApproveLeaveRequest(string? Note, uint? Version = null);

public sealed record RejectLeaveRequest(string Reason, uint? Version = null);

public sealed record CancelLeaveRequest(string Reason, uint? Version = null);

/// <summary>The caller's leave: balances, requests (newest first), the coming holidays and the leave types to ask for.</summary>
public sealed record MyLeaveDto(Guid EmployeeId, string EmployeeName, DateOnly Today, IReadOnlyList<LeaveBalanceDto> Balances,
    IReadOnlyList<LeaveRequestDto> Requests, IReadOnlyList<HolidayDto> Holidays, IReadOnlyList<LeaveTypeDto> Types);

/// <summary>A day of leave on the roster: Half true when only half the day is off.</summary>
public sealed record RosterLeaveDto(Guid EmployeeId, DateOnly Date, string LeaveTypeCode, string RequestCode, bool Half);

// ---------------------------------------------------------------- payroll

/// <summary>One employee's month in the payroll file (no pay amounts).</summary>
public sealed record PayrollRowDto(
    string EmployeeCode, string EmployeeName, string Department, string EmploymentType, int EmployedDays, int WeeklyOffs, int Holidays,
    int DaysWorked, int WorkedMinutes, int OvertimeMinutes, int LateMinutes, decimal PaidLeaveDays, decimal UnpaidLeaveDays, int AbsentDays,
    decimal PayableDays, string LeaveDetail);

/// <summary>What the month's file would hold. Warnings: what isn't ready (weeks not approved, leave not decided); their
/// hours or days are left out of the file. ApprovedToLock: approved timesheets the export locks for payroll.</summary>
public sealed record PayrollPreviewDto(
    string Month, DateOnly PeriodStart, DateOnly PeriodEnd, IReadOnlyList<PayrollRowDto> Rows, IReadOnlyList<string> Warnings, int ApprovedToLock,
    IReadOnlyList<HolidayDto> Holidays);

/// <summary>Makes the month's payroll file. With warnings it is refused unless ConfirmWarnings (what they name is left out).</summary>
public sealed record CreatePayrollExportRequest(string Month, bool ConfirmWarnings = false);

public sealed record PayrollExportDto(
    Guid Id, string Month, DateOnly PeriodStart, DateOnly PeriodEnd, string Format, string FileName, string Sha256, int Employees,
    int TimesheetsLocked, IReadOnlyList<string> Warnings, DateTimeOffset CreatedAt, string? By);
