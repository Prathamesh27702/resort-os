using Resort.Domain.Common;

namespace Resort.Domain.Housekeeping;

public static class MaintenanceCategories
{
    public static readonly string[] All = ["AC", "PLUMBING", "ELECTRICAL", "FURNITURE", "APPLIANCE", "POOL", "CIVIL", "OTHER"];
}

public static class MaintenancePriorities
{
    public const string Low = "LOW";
    public const string Normal = "NORMAL";
    public const string High = "HIGH";
    public const string Urgent = "URGENT";

    public static readonly string[] All = [Low, Normal, High, Urgent];

    /// <summary>Sort key: URGENT first.</summary>
    public static int Rank(string priority) => priority switch { Urgent => 0, High => 1, Normal => 2, _ => 3 };
}

public static class MaintenanceStatuses
{
    public const string Open = "OPEN";
    public const string Assigned = "ASSIGNED";
    public const string InProgress = "IN_PROGRESS";
    public const string OnHold = "ON_HOLD";
    public const string Resolved = "RESOLVED";
    public const string Closed = "CLOSED";

    public static readonly string[] All = [Open, Assigned, InProgress, OnHold, Resolved, Closed];
    /// <summary>Being worked on or waiting to be: a repair still to do.</summary>
    public static readonly string[] Unresolved = [Open, Assigned, InProgress, OnHold];

    public static bool IsUnresolved(string status) => status is Open or Assigned or InProgress or OnHold;

    public static string Words(string status) => status switch
    {
        InProgress => "in progress",
        OnHold => "on hold",
        _ => status.ToLowerInvariant(),
    };
}

/// <summary>Something that can break: an AC unit, a geyser, the pool pump. Optionally in a room.</summary>
public class MaintenanceAsset : AuditableEntity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Category { get; set; } = "OTHER";
    public Guid? UnitId { get; set; }
    public string? Location { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A reported problem, numbered M00001 … per property.</summary>
public class MaintenanceTicket : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Number { get; set; } = default!;
    public string Title { get; set; } = default!;
    public string? Description { get; set; }
    public string Category { get; set; } = "OTHER";
    public string Priority { get; set; } = MaintenancePriorities.Normal;
    public string Status { get; set; } = MaintenanceStatuses.Open;
    public Guid? UnitId { get; set; }
    public Guid? BedId { get; set; }
    public Guid? AssetId { get; set; }
    public string? Location { get; set; }
    public bool OutOfOrder { get; set; }
    public DateOnly? ExpectedReadyOn { get; set; }
    /// <summary>The OUT_OF_ORDER unit block this ticket made (released when the ticket is resolved).</summary>
    public Guid? BlockId { get; set; }
    public Guid? ReportedBy { get; set; }
    public Guid? AssignedTo { get; set; }
    public DateTimeOffset? AssignedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public Guid? ResolvedBy { get; set; }
    public string? Resolution { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public Guid? ClosedBy { get; set; }
    public uint Version { get; set; }
}

/// <summary>A line on a ticket's timeline: a note, or a status change. Append-only.</summary>
public class MaintenanceComment : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; set; }
    public Guid TicketId { get; set; }
    public string Kind { get; set; } = "NOTE";
    public string Body { get; set; } = default!;
    public string? FromStatus { get; set; }
    public string? ToStatus { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

/// <summary>A photo on a ticket. The bytes are encrypted in the file store; only the key is in the database.</summary>
public class MaintenanceAttachment : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; set; }
    public Guid TicketId { get; set; }
    public string StorageKey { get; set; } = default!;
    public string KeyId { get; set; } = default!;
    public string FileName { get; set; } = default!;
    public string ContentType { get; set; } = default!;
    public int SizeBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

/// <summary>Ticket rules without the database. Pure, unit-tested.</summary>
public static class MaintenanceFlow
{
    public const int MaxPhotoBytes = 2 * 1024 * 1024;
    public const int MaxPhotosPerTicket = 6;
    public const int MaxOutOfOrderDays = 90;

    /// <summary>Why a ticket in <paramref name="status"/> can't take <paramref name="action"/> (assign, progress,
    /// resolve, close), or null.</summary>
    public static string? Problem(string action, string number, string status) => (action, status) switch
    {
        (_, MaintenanceStatuses.Closed) => $"{number} is closed. Report a new problem if it is broken again.",
        ("assign" or "progress", MaintenanceStatuses.Resolved) =>
            $"{number} is resolved. Close it, or report a new problem if the repair didn't hold.",
        ("assign" or "progress" or "resolve", _) when MaintenanceStatuses.IsUnresolved(status) => null,
        ("resolve", MaintenanceStatuses.Resolved) => $"{number} is already resolved.",
        ("close", MaintenanceStatuses.Resolved) => null,
        ("close", _) => $"{number} is {MaintenanceStatuses.Words(status)}; resolve it before closing it.",
        _ => $"{number} can't be changed now.",
    };

    /// <summary>Why an out-of-order report until <paramref name="expectedReadyOn"/> can't be made on <paramref name="today"/>.</summary>
    public static string? ExpectedDateProblem(DateOnly? expectedReadyOn, DateOnly today) =>
        expectedReadyOn is not { } d
            ? "Say when the room should be usable again (the room is blocked until then)."
            : d <= today
                ? "The expected date must be after today: the room is blocked from tonight until that day."
                : d.DayNumber - today.DayNumber > MaxOutOfOrderDays
                    ? $"A room can be taken out of order for at most {MaxOutOfOrderDays} days at a time."
                    : null;

    /// <summary>JPEG, PNG or WebP from the first bytes, or null. The file name and the browser's type aren't trusted.</summary>
    public static string? SniffImage(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }
}
