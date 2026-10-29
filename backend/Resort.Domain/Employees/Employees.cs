using Resort.Domain.Common;
using Resort.Domain.Guests;

namespace Resort.Domain.Employees;

public static class EmployeeStatuses
{
    public const string Active = "ACTIVE";
    public const string Left = "LEFT";
}

public static class EmploymentTypes
{
    public const string FullTime = "FULL_TIME";
    public const string PartTime = "PART_TIME";
    public const string Contract = "CONTRACT";
    public const string Seasonal = "SEASONAL";
    public static readonly string[] All = [FullTime, PartTime, Contract, Seasonal];
}

public static class EmployeeDocumentTypes
{
    public const string Aadhaar = "AADHAAR";
    public const string Pan = "PAN";
    public const string BankAccount = "BANK_ACCOUNT";
    public const string Passport = "PASSPORT";
    public const string DrivingLicence = "DRIVING_LICENCE";
    public const string OfferLetter = "OFFER_LETTER";
    public const string Other = "OTHER";
    public static readonly string[] All = [Aadhaar, Pan, BankAccount, Passport, DrivingLicence, OfferLetter, Other];
}

public static class EmployeeDocumentStatuses
{
    public const string Active = "ACTIVE";
    public const string Removed = "REMOVED";
}

public static class PayBases
{
    public const string Monthly = "MONTHLY";
    public const string Daily = "DAILY";
    public static readonly string[] All = [Monthly, Daily];
}

public static class RosterShiftStatuses
{
    public const string Scheduled = "SCHEDULED";
    public const string Cancelled = "CANCELLED";
}

/// <summary>Amounts that only people with employee_salary.read may see: the audit trail shows that they changed, never
/// the value.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SalaryDataAttribute : Attribute;

public class Department : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }
}

public class Designation : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid DepartmentId { get; set; }
    public string Name { get; set; } = default!;
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }
}

/// <summary>One person who works at the property, numbered E00001 … UserId: their staff account, if they have one.
/// WeeklyOff: ISO day of the week (1 Monday … 7 Sunday).</summary>
public class Employee : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Code { get; set; } = default!;
    public string FirstName { get; set; } = default!;
    public string? LastName { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    [PersonalData] public DateOnly? DateOfBirth { get; set; }
    public Guid DepartmentId { get; set; }
    public Guid? DesignationId { get; set; }
    public string EmploymentType { get; set; } = EmploymentTypes.FullTime;
    public DateOnly JoinedOn { get; set; }
    public short? WeeklyOff { get; set; }
    public Guid? UserId { get; set; }
    public string? EmergencyContactName { get; set; }
    public string? EmergencyContactPhone { get; set; }
    public string? Address { get; set; }
    public string? Note { get; set; }
    public string Status { get; set; } = EmployeeStatuses.Active;
    public DateOnly? LeftOn { get; set; }
    public string? LeavingReason { get; set; }
    public uint Version { get; set; }

    public string FullName => string.IsNullOrWhiteSpace(LastName) ? FirstName : $"{FirstName} {LastName}";

    /// <summary>Works here on that day: joined on or before it and not left before it.</summary>
    public bool WorksOn(DateOnly day) => day >= JoinedOn && (LeftOn is null || day <= LeftOn);
}

/// <summary>An employee's document. Aadhaar: last 4 digits only. Other numbers encrypted (NumberEncrypted); a scan is
/// an encrypted file (StorageKey). Removing one wipes the number and the scan and keeps the row.</summary>
public class EmployeeDocument : AuditableEntity, IOrganizationScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid EmployeeId { get; set; }
    public string DocumentType { get; set; } = default!;
    [PersonalData] public byte[]? NumberEncrypted { get; set; }
    [PersonalData] public string? NumberLast4 { get; set; }
    public DateOnly? ExpiresOn { get; set; }
    public string? Note { get; set; }
    public string? StorageKey { get; set; }
    [PersonalData] public string? FileName { get; set; }
    public string? ContentType { get; set; }
    public int? SizeBytes { get; set; }
    public string KeyId { get; set; } = default!;
    public string Status { get; set; } = EmployeeDocumentStatuses.Active;
    public DateTimeOffset? RemovedAt { get; set; }
    public string? RemovedReason { get; set; }
}

/// <summary>What an employee is paid from EffectiveFrom: per month or per day, basic plus allowances. Never changed: a
/// raise or a correction is a new row (for one date, the latest recorded wins).</summary>
public class EmployeeCompensation : Entity, IOrganizationScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid EmployeeId { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public string PayBasis { get; set; } = PayBases.Monthly;
    [SalaryData] public decimal Basic { get; set; }
    [SalaryData] public decimal Allowances { get; set; }
    public bool PfApplicable { get; set; }
    public bool EsiApplicable { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

/// <summary>A shift a department works, e.g. Kitchen · Breakfast 06:00–14:00. EndTime ≤ StartTime: ends the next day.
/// MinStaff: how many people it needs every day (0: no minimum).</summary>
public class ShiftTemplate : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid DepartmentId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public short BreakMinutes { get; set; }
    public short MinStaff { get; set; }
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }
}

/// <summary>One person on one shift on one local work date. StartsAt/EndsAt are exact instants (from the property's
/// time zone). OverlapAllowed: a manager allowed it to overlap another of their shifts (with a reason).</summary>
public class RosterShift : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid EmployeeId { get; set; }
    public Guid DepartmentId { get; set; }
    public Guid? TemplateId { get; set; }
    public DateOnly WorkDate { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public short BreakMinutes { get; set; }
    public string Status { get; set; } = RosterShiftStatuses.Scheduled;
    public bool OverlapAllowed { get; set; }
    public string? OverlapReason { get; set; }
    public string? Note { get; set; }
    public string? CancelReason { get; set; }
    public uint Version { get; set; }

    public bool Overlaps(DateTimeOffset start, DateTimeOffset end) => StartsAt < end && start < EndsAt;
}

/// <summary>Pure roster arithmetic: local times to instants, shift lengths, the Monday a week starts on.</summary>
public static class RosterMath
{
    public const int MaxShiftHours = 16;

    /// <summary>The exact start and end of a shift worked on a local date. End ≤ start: it ends the next day.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) Instants(DateOnly day, TimeOnly start, TimeOnly end, TimeZoneInfo tz)
    {
        var s = Local(day.ToDateTime(start), tz);
        var e = Local((end <= start ? day.AddDays(1) : day).ToDateTime(end), tz);
        return (s, e);
    }

    private static DateTimeOffset Local(DateTime local, TimeZoneInfo tz) => new DateTimeOffset(local, tz.GetUtcOffset(local)).ToUniversalTime();

    /// <summary>Minutes between start and end, the next day when end ≤ start.</summary>
    public static int Minutes(TimeOnly start, TimeOnly end)
    {
        var m = (int)(end.ToTimeSpan() - start.ToTimeSpan()).TotalMinutes;
        return m <= 0 ? m + 24 * 60 : m;
    }

    /// <summary>The Monday of the week a day is in.</summary>
    public static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>ISO day of the week: 1 Monday … 7 Sunday.</summary>
    public static short IsoDay(DateOnly day) => (short)(day.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)day.DayOfWeek);

    public static string DayName(short isoDay) => isoDay switch
    {
        1 => "Monday",
        2 => "Tuesday",
        3 => "Wednesday",
        4 => "Thursday",
        5 => "Friday",
        6 => "Saturday",
        _ => "Sunday",
    };

    public static string Hours(int minutes) => minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";
}
