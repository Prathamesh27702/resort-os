namespace Resort.Contracts;

// ---------------------------------------------------------------- departments and designations (Sprint 17)

public sealed record DesignationDto(Guid Id, Guid DepartmentId, string Name, bool IsActive, int EmployeeCount, uint Version);

/// <summary>A department with its job titles. EmployeeCount: active employees in it.</summary>
public sealed record DepartmentDto(
    Guid Id, string Code, string Name, int SortOrder, bool IsActive, int EmployeeCount, IReadOnlyList<DesignationDto> Designations, uint Version);

public sealed record SaveDepartmentRequest(string Code, string Name, int SortOrder = 0, bool IsActive = true, uint? Version = null);

public sealed record SaveDesignationRequest(Guid DepartmentId, string Name, bool IsActive = true, uint? Version = null);

// ---------------------------------------------------------------- employees

/// <summary>
/// An employee profile. Never carries pay: that is behind its own endpoint and employee_salary.read. WeeklyOff: ISO day
/// (1 Monday … 7 Sunday) or null. UserId/UserEmail: the linked staff account, if any. Status ACTIVE or LEFT (LeftOn, the
/// last working day, and LeavingReason).
/// </summary>
public sealed record EmployeeDto(
    Guid Id, string Code, string FirstName, string? LastName, string FullName, string? Phone, string? Email, DateOnly? DateOfBirth,
    Guid DepartmentId, string DepartmentName, Guid? DesignationId, string? DesignationName, string EmploymentType, DateOnly JoinedOn,
    short? WeeklyOff, Guid? UserId, string? UserEmail, string? EmergencyContactName, string? EmergencyContactPhone, string? Address, string? Note,
    string Status, DateOnly? LeftOn, string? LeavingReason, uint Version);

/// <summary>Creates or edits an employee. The code (E00001 …) is given by the API. UserId: link a staff account
/// (optional; one employee per account).</summary>
public sealed record SaveEmployeeRequest(
    string FirstName, string? LastName, string? Phone, string? Email, DateOnly? DateOfBirth, Guid DepartmentId, Guid? DesignationId,
    string EmploymentType, DateOnly JoinedOn, short? WeeklyOff, Guid? UserId, string? EmergencyContactName, string? EmergencyContactPhone,
    string? Address, string? Note, uint? Version = null);

/// <summary>The employee leaves: LeftOn is their last working day.</summary>
public sealed record EmployeeLeftRequest(DateOnly LeftOn, string Reason, uint? Version);

/// <summary>A staff account that can be linked to an employee. LinkedTo: the employee it is linked to now, if any.</summary>
public sealed record LinkableAccountDto(Guid Id, string FullName, string Email, bool IsActive, Guid? LinkedEmployeeId, string? LinkedTo);

// ---------------------------------------------------------------- pay (employee_salary.read only)

/// <summary>
/// Pay from EffectiveFrom. Gross = Basic + Allowances (per month, or per day for PayBasis DAILY). IsCurrent: the pay
/// for today. Replaced: a later entry for the same date replaced it (a correction).
/// </summary>
public sealed record CompensationDto(
    Guid Id, DateOnly EffectiveFrom, string PayBasis, decimal Basic, decimal Allowances, decimal Gross, bool PfApplicable, bool EsiApplicable,
    string? Note, bool IsCurrent, bool Replaced, string? ByName, DateTimeOffset CreatedAt);

/// <summary>An employee's pay: today's (Current, null when nothing is recorded yet or it starts later) and every entry,
/// newest first.</summary>
public sealed record EmployeeCompensationDto(Guid EmployeeId, string EmployeeName, CompensationDto? Current, IReadOnlyList<CompensationDto> History);

/// <summary>Records pay from a date. To correct an entry, record it again with the same date: the newest wins.</summary>
public sealed record RecordCompensationRequest(
    DateOnly EffectiveFrom, string PayBasis, decimal Basic, decimal Allowances, bool PfApplicable, bool EsiApplicable, string? Note);

// ---------------------------------------------------------------- documents (employee_document.read only)

/// <summary>A document as listed: the number masked ("•••• 1234"). Expired: ExpiresOn has passed.</summary>
public sealed record EmployeeDocumentDto(
    Guid Id, string DocumentType, string? MaskedNumber, DateOnly? ExpiresOn, bool Expired, string? Note, bool HasFile, string? FileName,
    string? ContentType, int? SizeBytes, string Status, string? RemovedReason, DateTimeOffset CreatedAt, string? ByName);

/// <summary>One document opened: the number in full (never for Aadhaar: last 4 digits only). Audited.</summary>
public sealed record EmployeeDocumentDetailDto(
    Guid Id, string DocumentType, string? Number, string? MaskedNumber, DateOnly? ExpiresOn, string? Note, bool HasFile, string? FileName,
    string? ContentType, int? SizeBytes, string Status, DateTimeOffset CreatedAt, string? ByName);

/// <summary>Removes a document: the number and the scan are wiped; the entry stays with the reason.</summary>
public sealed record RemoveEmployeeDocumentRequest(string Reason);

// ---------------------------------------------------------------- shift templates

/// <summary>A shift a department works. EndTime ≤ StartTime: ends the next day (CrossesMidnight). PaidMinutes: length
/// minus the break. MinStaff: people needed every day (0: no minimum, never short).</summary>
public sealed record ShiftTemplateDto(
    Guid Id, Guid DepartmentId, string DepartmentName, string Code, string Name, TimeOnly StartTime, TimeOnly EndTime, bool CrossesMidnight,
    int BreakMinutes, int PaidMinutes, int MinStaff, bool IsActive, uint Version);

public sealed record SaveShiftTemplateRequest(
    Guid DepartmentId, string Code, string Name, TimeOnly StartTime, TimeOnly EndTime, int BreakMinutes, int MinStaff, bool IsActive = true,
    uint? Version = null);

// ---------------------------------------------------------------- roster

/// <summary>One person on one shift. StartTime/EndTime: local times at the property (EndTime ≤ StartTime: the next day);
/// StartsAt/EndsAt the exact instants. PaidMinutes: length minus the break.</summary>
public sealed record RosterShiftDto(
    Guid Id, Guid EmployeeId, string EmployeeCode, string EmployeeName, Guid DepartmentId, string DepartmentName, Guid? TemplateId,
    string? TemplateName, DateOnly WorkDate, TimeOnly StartTime, TimeOnly EndTime, DateTimeOffset StartsAt, DateTimeOffset EndsAt, int BreakMinutes,
    int PaidMinutes, bool OverlapAllowed, string? OverlapReason, string? Note, uint Version);

/// <summary>
/// Puts a person on a shift, or changes one. With TemplateId: the template's department, times and break unless given.
/// Without: DepartmentId (default: the employee's), StartTime and EndTime are needed. AllowOverlap + OverlapReason: save it
/// even though it overlaps another of their shifts (e.g. a handover); without them an overlap is refused (409 SHIFT_CLASH).
/// </summary>
public sealed record SaveRosterShiftRequest(
    Guid EmployeeId, DateOnly WorkDate, Guid? TemplateId, Guid? DepartmentId, TimeOnly? StartTime, TimeOnly? EndTime, int? BreakMinutes,
    bool AllowOverlap = false, string? OverlapReason = null, string? Note = null, uint? Version = null);

public sealed record CancelRosterShiftRequest(string Reason, uint? Version);

/// <summary>An employee on the week's roster. PaidMinutes and Shifts: this week's totals.</summary>
public sealed record RosterEmployeeDto(
    Guid Id, string Code, string Name, Guid DepartmentId, string? DesignationName, short? WeeklyOff, DateOnly JoinedOn, DateOnly? LeftOn,
    int PaidMinutes, int Shifts);

public sealed record RosterDepartmentDto(Guid Id, string Code, string Name);

/// <summary>A shift with fewer people than it needs on a day: Needed (the template's MinStaff), Rostered, Missing.</summary>
public sealed record RosterShortageDto(
    DateOnly Date, Guid TemplateId, string TemplateName, Guid DepartmentId, string DepartmentName, TimeOnly StartTime, TimeOnly EndTime,
    int Needed, int Rostered, int Missing);

/// <summary>Something allowed but worth a look. Kind: DAY_OFF (on their weekly day off), LONG_WEEK (over 48 hours in the
/// week), OVERLAP_ALLOWED (a manager allowed two shifts to overlap).</summary>
public sealed record RosterWarningDto(string Kind, DateOnly? Date, Guid EmployeeId, Guid? ShiftId, string Message);

/// <summary>
/// One week of the roster (Monday to Sunday): the departments, the active employees working that week, the shift
/// templates, the scheduled shifts, the shortages (template × day with fewer people than MinStaff) and the warnings.
/// </summary>
public sealed record RosterWeekDto(
    DateOnly WeekStart, DateOnly WeekEnd, DateOnly Today, IReadOnlyList<RosterDepartmentDto> Departments, IReadOnlyList<RosterEmployeeDto> Employees,
    IReadOnlyList<ShiftTemplateDto> Templates, IReadOnlyList<RosterShiftDto> Shifts, IReadOnlyList<RosterShortageDto> Shortages,
    IReadOnlyList<RosterWarningDto> Warnings, IReadOnlyList<RosterLeaveDto> Leave);

/// <summary>Copies one week's shifts into another, day for day. Shifts that would clash, fall on a day the person
/// doesn't work here, or use a template that is switched off are skipped and listed.</summary>
public sealed record CopyRosterWeekRequest(DateOnly FromWeek, DateOnly ToWeek);

public sealed record CopyRosterWeekResultDto(int Copied, IReadOnlyList<string> Skipped, RosterWeekDto Week);

/// <summary>The caller's own shifts (roster.self), from their linked employee record.</summary>
public sealed record MyRosterDto(Guid EmployeeId, string EmployeeName, string DepartmentName, IReadOnlyList<RosterShiftDto> Shifts);
