using Resort.Domain.Common;

namespace Resort.Domain.Organizations;

public class Organization : AuditableEntity, ISoftDelete, IVersioned, IAudited
{
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? LegalName { get; set; }
    public string BaseCurrency { get; set; } = "INR";
    public string CountryCode { get; set; } = "IN";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? DeletedAt { get; set; }
    public uint Version { get; set; }
}

public static class PropertyTypes
{
    public const string Resort = "RESORT";
    public static readonly string[] All = ["RESORT", "HOTEL", "HOMESTAY", "HOSTEL", "VILLA", "OTHER"];
}

public class Property : AuditableEntity, IOrganizationScoped, ISoftDelete, IVersioned, IAudited
{
    public Guid OrganizationId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? LegalName { get; set; }
    public string PropertyType { get; set; } = PropertyTypes.Resort;
    public string Timezone { get; set; } = "Asia/Kolkata";
    public string Currency { get; set; } = "INR";
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? District { get; set; }
    public string? StateName { get; set; }
    public string? GstStateCode { get; set; }
    public string? PostalCode { get; set; }
    public string CountryCode { get; set; } = "IN";
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public TimeOnly CheckInTime { get; set; } = new(14, 0);
    public TimeOnly CheckOutTime { get; set; } = new(11, 0);
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? DeletedAt { get; set; }
    public uint Version { get; set; }

    /// <summary>The property's local calendar date for a UTC instant. Business dates (arrival, night audit, reports)
    /// always go through this — never the server's clock timezone.</summary>
    public DateOnly LocalDate(DateTimeOffset utcNow)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(Timezone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, tz).DateTime);
    }

    /// <summary>The instant a local calendar day starts at the property (midnight there), for filtering by business day.</summary>
    public DateTimeOffset StartOfDay(DateOnly day)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(Timezone);
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, tz.GetUtcOffset(local)).ToUniversalTime();
    }
}

public class PropertySetting : AuditableEntity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Key { get; set; } = default!;
    /// <summary>JSON value (jsonb).</summary>
    public string Value { get; set; } = default!;
}

public class SystemSetting
{
    public string Key { get; set; } = default!;
    public string Value { get; set; } = default!;
    public string? Description { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}
