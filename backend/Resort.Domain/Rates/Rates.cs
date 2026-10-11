using Resort.Domain.Accommodation;
using Resort.Domain.Common;

namespace Resort.Domain.Rates;

/// <summary>Indian meal-plan codes printed on rate cards and OTA listings.</summary>
public static class MealPlans
{
    /// <summary>European plan: room only.</summary>
    public const string Ep = "EP";
    /// <summary>Continental plan: room + breakfast.</summary>
    public const string Cp = "CP";
    /// <summary>Modified American plan: room + breakfast + one meal.</summary>
    public const string Map = "MAP";
    /// <summary>American plan: room + all meals.</summary>
    public const string Ap = "AP";
    public static readonly string[] All = [Ep, Cp, Map, Ap];
}

/// <summary>Revenue categories a package price is split into.</summary>
public static class RevenueCategories
{
    public const string Room = "ROOM";
    public const string Food = "FOOD";
    public const string Transport = "TRANSPORT";
    public const string Activity = "ACTIVITY";
    public static readonly string[] All = [Room, Food, Transport, Activity];
}

public static class SourceChannels
{
    public static readonly string[] All = ["DIRECT", "OTA", "TRAVEL_AGENT", "CORPORATE", "OTHER"];
}

/// <summary>How a stay is priced ("Best available rate", "Room + breakfast"). Prices live in <see cref="RatePlanPrice"/>.</summary>
public class RatePlan : AuditableEntity, IPropertyScoped, IVersioned, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public string MealPlan { get; set; } = MealPlans.Ep;
    /// <summary>The plan a quote uses when none is named. One per property.</summary>
    public bool IsDefault { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }
}

/// <summary>One cell of the rate grid: the price of one room type for one night (property-local date) in one plan.
/// Price is per unit (rooms) or per bed (dormitories). Every change is audited with old and new values.</summary>
public class RatePlanPrice : AuditableEntity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid RatePlanId { get; set; }
    public Guid AccommodationTypeId { get; set; }
    public DateOnly StayDate { get; set; }
    public decimal Price { get; set; }
    public decimal ExtraAdult { get; set; }
    public decimal ExtraChild { get; set; }
    public short MinStay { get; set; } = 1;

    public NightRate ToNightRate() => new(Price, ExtraAdult, ExtraChild, MinStay);
}

/// <summary>A fixed-price bundle: <see cref="Nights"/> nights in a room type plus food, transport or activities.</summary>
public class Package : AuditableEntity, IPropertyScoped, IVersioned, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid AccommodationTypeId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public short Nights { get; set; } = 1;
    public short Adults { get; set; } = 2;
    public short Children { get; set; }
    public decimal Price { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }

    public AccommodationType Type { get; set; } = default!;
    public List<PackageComponent> Components { get; set; } = [];
}

/// <summary>A share of a package's price in one revenue category. <see cref="Amount"/> is computed by
/// <see cref="MoneySplit.ByPercent"/>, so a package's amounts always add up to its price.</summary>
public class PackageComponent : AuditableEntity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid PackageId { get; set; }
    public string Category { get; set; } = default!;
    public string Description { get; set; } = default!;
    public decimal SharePercent { get; set; }
    public decimal Amount { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>Where bookings come from. Built-in (OrganizationId null) or organisation-specific.</summary>
public class ReservationSource : Entity
{
    public Guid? OrganizationId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Channel { get; set; } = default!;
    public decimal? CommissionPercent { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
