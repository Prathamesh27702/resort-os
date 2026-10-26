using Resort.Domain.Common;
using Resort.Domain.FrontDesk;

namespace Resort.Domain.Housekeeping;

/// <summary>What a cleaning task is for.</summary>
public static class HousekeepingTaskTypes
{
    public const string Departure = "DEPARTURE";
    public const string Stayover = "STAYOVER";
    public const string DeepClean = "DEEP_CLEAN";
    public const string AfterRepair = "AFTER_REPAIR";

    public static readonly string[] All = [Departure, Stayover, DeepClean, AfterRepair];

    public static string Words(string type) => type switch
    {
        Departure => "Departure clean",
        Stayover => "Stay-over clean",
        DeepClean => "Deep clean",
        AfterRepair => "Clean after repair",
        _ => type,
    };
}

/// <summary>A task's progress. Each maps to the room's cleaning state (<see cref="HousekeepingStatuses"/>):
/// OPEN = DIRTY, IN_PROGRESS = CLEANING, CLEANED = CLEAN or INSPECTION, DONE = READY.</summary>
public static class HousekeepingTaskStatuses
{
    public const string Open = "OPEN";
    public const string InProgress = "IN_PROGRESS";
    public const string Cleaned = "CLEANED";
    public const string Done = "DONE";
    public const string Cancelled = "CANCELLED";

    public static readonly string[] Active = [Open, InProgress, Cleaned];
    public static readonly string[] All = [Open, InProgress, Cleaned, Done, Cancelled];

    public static bool IsActive(string status) => status is Open or InProgress or Cleaned;
}

/// <summary>What made a task.</summary>
public static class HousekeepingTaskSources
{
    public const string CheckOut = "CHECK_OUT";
    public const string RoomMove = "ROOM_MOVE";
    public const string Manual = "MANUAL";
    public const string Maintenance = "MAINTENANCE";
}

/// <summary>A piece of cleaning work on one room (BedId null) or one dorm bed.</summary>
public class HousekeepingTask : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid UnitId { get; set; }
    public Guid? BedId { get; set; }
    public string TaskType { get; set; } = HousekeepingTaskTypes.Departure;
    public string Status { get; set; } = HousekeepingTaskStatuses.Open;
    public string Source { get; set; } = HousekeepingTaskSources.Manual;
    /// <summary>The DIRTY change (unit_housekeeping_history) this task was made from; unique, so a change makes one task.</summary>
    public Guid? SourceChangeId { get; set; }
    public Guid? ReservationId { get; set; }
    public Guid? MaintenanceTicketId { get; set; }
    public Guid? TemplateId { get; set; }
    /// <summary>JSON array of <see cref="ChecklistLine"/>, copied from the template when the task is made.</summary>
    public string Checklist { get; set; } = "[]";
    /// <summary>The property's business date the task was made on.</summary>
    public DateOnly BusinessDate { get; set; }
    public string? Note { get; set; }
    public Guid? AssignedTo { get; set; }
    public DateTimeOffset? AssignedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public Guid? StartedBy { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public Guid? FinishedBy { get; set; }
    public DateTimeOffset? InspectedAt { get; set; }
    public Guid? InspectedBy { get; set; }
    public string? InspectionNote { get; set; }
    public int FailedInspections { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string? CloseReason { get; set; }
    public uint Version { get; set; }

    public bool IsActive => HousekeepingTaskStatuses.IsActive(Status);
}

/// <summary>The checklist a property starts with (V015 gives every property a "Departure clean" template with these
/// lines). Used as is when a property has no template at all.</summary>
public static class DefaultChecklist
{
    public static readonly string[] Lines =
    [
        "Strip beds and put on fresh linen",
        "Clean and disinfect the bathroom",
        "Fresh towels and toiletries",
        "Dust surfaces, sweep and mop the floor",
        "Empty bins",
        "Check AC, lights, TV and taps work",
        "Restock water bottles",
    ];
}

/// <summary>One line of a task's checklist (stored as JSON on the task).</summary>
public sealed record ChecklistLine(Guid Id, string Label, bool Required, bool Done);

/// <summary>What to do in a room for one task type, optionally only for one room type.</summary>
public class HousekeepingChecklistTemplate : AuditableEntity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Name { get; set; } = default!;
    public string TaskType { get; set; } = HousekeepingTaskTypes.Departure;
    public Guid? AccommodationTypeId { get; set; }
    public bool IsActive { get; set; } = true;
    public List<HousekeepingChecklistItem> Items { get; set; } = [];
}

public class HousekeepingChecklistItem : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; set; }
    public Guid TemplateId { get; set; }
    public string Label { get; set; } = default!;
    public bool IsRequired { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// The housekeeping workflow without the database. Pure, unit-tested.
/// <code>
///   DIRTY ──start──▶ CLEANING ──finish──▶ CLEAN ──(start inspection)──▶ INSPECTION ──pass──▶ READY
///     ▲                                     │                               │
///     └──────────────── fail (with a note) ─┴───────────────────────────────┘
///   finish goes straight to READY when the property doesn't require inspection.
///   OUT_OF_ORDER comes and goes only through a maintenance ticket (or the front desk by hand).
/// </code>
/// </summary>
public static class HousekeepingFlow
{
    public const string Start = "start";
    public const string Finish = "finish";
    public const string Inspect = "inspect";

    /// <summary>The room state a task in <paramref name="taskStatus"/> stands for.</summary>
    public static string RoomStatusOf(string taskStatus) => taskStatus switch
    {
        HousekeepingTaskStatuses.Open => HousekeepingStatuses.Dirty,
        HousekeepingTaskStatuses.InProgress => HousekeepingStatuses.Cleaning,
        HousekeepingTaskStatuses.Cleaned => HousekeepingStatuses.Clean,
        _ => HousekeepingStatuses.Ready,
    };

    /// <summary>The task status that matches a room state set some other way (front desk, maintenance).
    /// Null: no open task should exist (READY closes it, OUT_OF_ORDER cancels it).</summary>
    public static string? TaskStatusFor(string roomStatus) => roomStatus switch
    {
        HousekeepingStatuses.Dirty => HousekeepingTaskStatuses.Open,
        HousekeepingStatuses.Cleaning => HousekeepingTaskStatuses.InProgress,
        HousekeepingStatuses.Clean or HousekeepingStatuses.Inspection => HousekeepingTaskStatuses.Cleaned,
        _ => null,
    };

    /// <summary>Why <paramref name="action"/> can't be done on a task in <paramref name="taskStatus"/> (room in
    /// <paramref name="roomStatus"/>), or null. <paramref name="place"/> is "Room 101" or "Room 201 bed B02".</summary>
    public static string? Problem(string action, string place, string taskStatus, string roomStatus)
    {
        if (roomStatus == HousekeepingStatuses.OutOfOrder)
            return $"{place} is out of order. It goes back to cleaning when its repair is resolved.";
        return (action, taskStatus) switch
        {
            (_, HousekeepingTaskStatuses.Done) => $"{place} is already cleaned and ready.",
            (_, HousekeepingTaskStatuses.Cancelled) => $"This task for {place} was cancelled. Reload to see the room's current task.",
            (Start, HousekeepingTaskStatuses.Open) => null,
            (Start, HousekeepingTaskStatuses.InProgress) => $"{place} is already being cleaned.",
            (Start, HousekeepingTaskStatuses.Cleaned) => $"{place} is already cleaned; it is waiting for inspection.",
            (Finish, HousekeepingTaskStatuses.InProgress) => null,
            (Finish, HousekeepingTaskStatuses.Open) => $"Cleaning of {place} hasn't been started. Tap Start first.",
            (Finish, HousekeepingTaskStatuses.Cleaned) => $"{place} is already cleaned; it is waiting for inspection.",
            (Inspect, HousekeepingTaskStatuses.Cleaned) => null,
            (Inspect, _) => $"{place} is {HousekeepingStatuses.Words(roomStatus)}; it can be inspected once the cleaning is finished.",
            _ => $"Can't {action} {place} now.",
        };
    }

    /// <summary>The checklist after a finish: every line done when <paramref name="ticked"/> is null (the housekeeper's
    /// one-tap "Done"), otherwise only the ticked lines. Returns the labels of required lines left unticked.</summary>
    public static (List<ChecklistLine> Lines, List<string> MissingRequired) Complete(IReadOnlyList<ChecklistLine> lines, IReadOnlyCollection<Guid>? ticked)
    {
        var done = lines.Select(l => l with { Done = ticked is null || ticked.Contains(l.Id) }).ToList();
        return (done, done.Where(l => l.Required && !l.Done).Select(l => l.Label).ToList());
    }

    /// <summary>"Room 101" or "Room 201 bed B02".</summary>
    public static string Place(string unitName, string? bedCode) => bedCode is null ? unitName : $"{unitName} bed {bedCode}";
}
