using Resort.Domain.Common;

namespace Resort.Domain.Activities;

/// <summary>
/// The states of one equipment item and the moves between them (the database trigger fn_equipment_status_transition has
/// the same list). RESERVED and ISSUED are reached only by reserving or issuing it; the way back from ISSUED is a return.
/// </summary>
public static class EquipmentStatuses
{
    public const string Available = "AVAILABLE";
    public const string Reserved = "RESERVED";
    public const string Issued = "ISSUED";
    public const string Damaged = "DAMAGED";
    public const string Maintenance = "MAINTENANCE";
    public const string Lost = "LOST";
    public const string Retired = "RETIRED";
    public static readonly string[] All = [Available, Reserved, Issued, Damaged, Maintenance, Lost, Retired];

    public static readonly IReadOnlyDictionary<string, string[]> Transitions = new Dictionary<string, string[]>
    {
        [Available] = [Reserved, Issued, Damaged, Maintenance, Lost, Retired],
        [Reserved] = [Issued, Available],
        [Issued] = [Available, Damaged, Lost],
        [Damaged] = [Maintenance, Available, Retired, Lost],
        [Maintenance] = [Available, Damaged, Retired],
        [Lost] = [Available, Retired],
        [Retired] = [],
    };

    /// <summary>The moves a person makes by hand (equipment.manage); the others happen by reserving, issuing and returning.</summary>
    public static readonly string[] ByHand = [Available, Damaged, Maintenance, Lost, Retired];

    public static bool CanMove(string from, string to) => Transitions.TryGetValue(from, out var next) && next.Contains(to);

    /// <summary>"available", "in maintenance" — for messages.</summary>
    public static string Words(string s) => s switch
    {
        Available => "available",
        Reserved => "reserved",
        Issued => "issued",
        Damaged => "damaged",
        Maintenance => "in maintenance",
        Lost => "lost",
        Retired => "retired",
        _ => s.ToLowerInvariant(),
    };
}

public static class AssignmentStatuses
{
    public const string Reserved = "RESERVED";
    public const string Issued = "ISSUED";
    public const string Returned = "RETURNED";
    public const string Lost = "LOST";
    public const string Released = "RELEASED";
    public static readonly string[] Open = [Reserved, Issued];
}

public static class ReturnConditions
{
    public const string Good = "GOOD";
    public const string Damaged = "DAMAGED";
    public const string Lost = "LOST";
    public static readonly string[] All = [Good, Damaged, Lost];
}

public class EquipmentType : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }
}

/// <summary>One item, known by its tag (LJ-01).</summary>
public class EquipmentItem : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid EquipmentTypeId { get; set; }
    public string Tag { get; set; } = default!;
    public string? Note { get; set; }
    public string Status { get; set; } = EquipmentStatuses.Available;
    public string? StatusNote { get; set; }
    public DateTimeOffset StatusChangedAt { get; set; }
    public uint Version { get; set; }
}

/// <summary>An item reserved for or issued to someone, and how it came back.</summary>
public class EquipmentAssignment : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid EquipmentId { get; set; }
    public Guid? ActivityBookingId { get; set; }
    public Guid? ReservationId { get; set; }
    public string IssuedTo { get; set; } = default!;
    public string Status { get; set; } = AssignmentStatuses.Issued;
    public DateTimeOffset? ReservedAt { get; set; }
    public Guid? ReservedBy { get; set; }
    public DateTimeOffset? IssuedAt { get; set; }
    public Guid? IssuedBy { get; set; }
    public DateTimeOffset? DueBackAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public Guid? ClosedBy { get; set; }
    public string? ReturnCondition { get; set; }
    public string? ReturnNote { get; set; }
    public uint Version { get; set; }
}
