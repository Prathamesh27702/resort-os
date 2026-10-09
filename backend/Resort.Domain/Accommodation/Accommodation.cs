using Resort.Domain.Common;

namespace Resort.Domain.Accommodation;

/// <summary>How a type is sold: the whole unit (rooms) or each bed (dormitories).</summary>
public static class InventoryModes
{
    public const string Unit = "UNIT";
    public const string Bed = "BED";
    public static readonly string[] All = [Unit, Bed];
}

public static class BedTypes
{
    public static readonly string[] All = ["SINGLE", "BUNK_LOWER", "BUNK_UPPER", "DOUBLE"];
}

public static class BlockTypes
{
    public static readonly string[] All = ["MAINTENANCE", "OUT_OF_ORDER", "OWNER_USE", "HOLD", "OTHER"];
}

public static class BlockStatuses
{
    public const string Active = "ACTIVE";
    public const string Released = "RELEASED";
}

/// <summary>Built-in (OrganizationId null) or organisation-specific amenity.</summary>
public class Amenity : Entity
{
    public Guid? OrganizationId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Category { get; set; } = default!;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>What is sold: "Deluxe room", "Dormitory". <see cref="InventoryMode"/> decides whether the unit or each bed is sold.</summary>
public class AccommodationType : AuditableEntity, IPropertyScoped, IVersioned, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public string InventoryMode { get; set; } = InventoryModes.Unit;
    public short BaseOccupancy { get; set; } = 2;
    public short MaxOccupancy { get; set; } = 2;
    public decimal? SizeSqm { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }

    public List<AccommodationTypeAmenity> Amenities { get; set; } = [];

    public bool IsBedBased => InventoryMode == InventoryModes.Bed;
}

public class AccommodationTypeAmenity
{
    public Guid AccommodationTypeId { get; set; }
    public Guid AmenityId { get; set; }
}

/// <summary>A physical room or dormitory.</summary>
public class AccommodationUnit : AuditableEntity, IPropertyScoped, IVersioned, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid AccommodationTypeId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Floor { get; set; }
    public string? Notes { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }

    public AccommodationType Type { get; set; } = default!;
    public List<Bed> Beds { get; set; } = [];
}

/// <summary>A sellable bed inside a bed-based unit.</summary>
public class Bed : AuditableEntity, IPropertyScoped, IVersioned, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid UnitId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string BedType { get; set; } = "SINGLE";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }
}

/// <summary>Photo metadata for a type or a unit; the file is in blob storage.</summary>
public class UnitImage : Entity, IPropertyScoped
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid? AccommodationTypeId { get; set; }
    public Guid? UnitId { get; set; }
    public string StorageKey { get; set; } = default!;
    public string? Caption { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

/// <summary>A unit (BedId null) or one bed taken out of sale for [StartDate, EndDate) in property-local dates.</summary>
public class UnitBlock : AuditableEntity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid UnitId { get; set; }
    public Guid? BedId { get; set; }
    public string BlockType { get; set; } = default!;
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string Reason { get; set; } = default!;
    public string Status { get; set; } = BlockStatuses.Active;
    public DateTimeOffset? ReleasedAt { get; set; }
    public Guid? ReleasedBy { get; set; }
    public string? ReleaseReason { get; set; }

    public bool IsActive => Status == BlockStatuses.Active;

    /// <summary>Half-open interval overlap: [a, b) and [c, d) overlap when a &lt; d and c &lt; b.</summary>
    public bool Overlaps(DateOnly start, DateOnly end) => StartDate < end && start < EndDate;

    public bool Covers(DateOnly date) => StartDate <= date && date < EndDate;
}

/// <summary>Sellable inventory for a type. Rooms sell the unit; dormitories sell each bed.</summary>
public static class InventoryCalculator
{
    public sealed record Capacity(int SellableItems, int MaxGuests);

    /// <param name="mode">UNIT or BED</param>
    /// <param name="maxOccupancy">guests per unit (UNIT mode); ignored for BED mode (1 guest per bed)</param>
    /// <param name="units">active units of the type, each with its number of active beds</param>
    public static Capacity For(string mode, int maxOccupancy, IEnumerable<(bool UnitActive, int ActiveBeds)> units)
    {
        var active = units.Where(u => u.UnitActive).ToList();
        return mode switch
        {
            InventoryModes.Unit => new Capacity(active.Count, active.Count * maxOccupancy),
            InventoryModes.Bed => new Capacity(active.Sum(u => u.ActiveBeds), active.Sum(u => u.ActiveBeds)),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown inventory mode"),
        };
    }
}
