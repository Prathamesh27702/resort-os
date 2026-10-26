namespace Resort.Contracts;

// ---------------------------------------------------------------- housekeeping (Sprint 13)

/// <summary>The housekeeping board for today (the property's business date): every room and dorm bed with its cleaning
/// state, its open task and any repair keeping it out of order. InspectionRequired: a finished room waits for a supervisor
/// before it is READY (property setting housekeeping.inspection_required, default true).</summary>
public sealed record HousekeepingBoardDto(
    DateOnly Today, bool InspectionRequired, HousekeepingSummaryDto Summary, IReadOnlyList<HousekeepingRoomDto> Rooms);

/// <summary>How many rooms (and dorm beds) are in each state. Mine: open tasks assigned to the caller. Rush: rooms not
/// ready that a guest arrives in today.</summary>
public sealed record HousekeepingSummaryDto(int Dirty, int Cleaning, int ToInspect, int Ready, int OutOfOrder, int Mine, int Rush);

/// <summary>One room (BedId null) or dorm bed on the board. Status: the cleaning state (DIRTY, CLEANING, CLEAN,
/// INSPECTION, READY, OUT_OF_ORDER). Occupied: a guest is checked in there now. DepartureToday / ArrivalToday: a guest
/// leaves or arrives today (no guest names: housekeepers see room numbers only). Rush: not ready and a guest arrives
/// today, so it is cleaned first.</summary>
public sealed record HousekeepingRoomDto(
    Guid UnitId, string UnitCode, string UnitName, Guid? BedId, string? BedCode, string TypeName, string Place, string Status,
    bool Occupied, bool DepartureToday, bool ArrivalToday, bool Rush, HousekeepingTaskDto? Task, RepairBriefDto? Repair);

/// <summary>An unresolved repair on a room, shown on the board.</summary>
public sealed record RepairBriefDto(Guid TicketId, string Number, string Title, bool OutOfOrder, DateOnly? ExpectedReadyOn);

/// <summary>A cleaning task. Status: OPEN, IN_PROGRESS, CLEANED (waiting for inspection), DONE or CANCELLED.
/// NextActions: what the caller may do now (start, finish, inspect, assign).</summary>
public sealed record HousekeepingTaskDto(
    Guid Id, Guid UnitId, Guid? BedId, string Place, string TaskType, string Status, string Source, string? ReservationReference,
    Guid? AssignedTo, string? AssignedToName, DateTimeOffset? StartedAt, string? StartedByName, DateTimeOffset? FinishedAt,
    string? FinishedByName, DateTimeOffset? InspectedAt, string? InspectedByName, string? InspectionNote, int FailedInspections,
    string? Note, IReadOnlyList<ChecklistLineDto> Checklist, DateOnly BusinessDate, DateTimeOffset CreatedAt,
    IReadOnlyList<string> NextActions, uint Version);

public sealed record ChecklistLineDto(Guid Id, string Label, bool Required, bool Done);

/// <summary>Assigns a task to a housekeeper (UserId null takes it off them). Needs housekeeping.assign.</summary>
public sealed record AssignTaskRequest(Guid? UserId, uint? Version);

/// <summary>Starts cleaning: the room becomes CLEANING. An unassigned task is assigned to whoever starts it.</summary>
public sealed record StartTaskRequest(uint? Version);

/// <summary>Finishes cleaning. Checked null = every checklist line done (the one-tap Done); otherwise the ids of the lines
/// done, which must include every required line. The room becomes CLEAN (to inspect), or READY when the property doesn't
/// require inspection.</summary>
public sealed record FinishTaskRequest(IReadOnlyList<Guid>? Checked, string? Note, uint? Version);

/// <summary>A supervisor's inspection (housekeeping.inspect). Result PASS: the room is READY. FAIL: back to DIRTY for the
/// same housekeeper, with a note saying what to redo. START: the room shows INSPECTION while the supervisor checks it.</summary>
public sealed record InspectTaskRequest(string Result, string? Note, uint? Version);

/// <summary>A task made by hand (housekeeping.assign), e.g. a stay-over or deep clean. The room becomes DIRTY.</summary>
public sealed record CreateTaskRequest(Guid UnitId, Guid? BedId, string TaskType, string? Note, Guid? AssignTo);

/// <summary>Someone who can be given work, with how much they have open now.</summary>
public sealed record StaffOptionDto(Guid UserId, string Name, int OpenWork);

/// <summary>A checklist template. AccommodationTypeId null: every room type that has no template of its own.</summary>
public sealed record ChecklistTemplateDto(
    Guid Id, string Name, string TaskType, Guid? AccommodationTypeId, string? AccommodationTypeName, bool IsActive,
    IReadOnlyList<ChecklistItemDto> Items);

public sealed record ChecklistItemDto(Guid Id, string Label, bool IsRequired, int SortOrder);

/// <summary>Creates or replaces a template (its lines in this order). Tasks already made keep the lines they copied.</summary>
public sealed record SaveChecklistTemplateRequest(
    string Name, string TaskType, Guid? AccommodationTypeId, bool IsActive, IReadOnlyList<ChecklistItemRequest> Items);

public sealed record ChecklistItemRequest(string Label, bool IsRequired);

// ---------------------------------------------------------------- maintenance (Sprint 13)

/// <summary>A row on the tickets list. Place: "Room 101", "Pool pump (Pool deck)" or the location typed in.</summary>
public sealed record MaintenanceTicketSummaryDto(
    Guid Id, string Number, string Title, string Category, string Priority, string Status, string Place, Guid? UnitId,
    bool OutOfOrder, DateOnly? ExpectedReadyOn, string? AssignedToName, string? ReportedByName, int Photos,
    DateTimeOffset CreatedAt);

/// <summary>A ticket with its timeline and photos. BlockActive: the room is still blocked by this ticket.
/// NextActions: what the caller may do now (assign, progress, resolve, close, comment, photo).</summary>
public sealed record MaintenanceTicketDto(
    Guid Id, string Number, string Title, string? Description, string Category, string Priority, string Status, string Place,
    Guid? UnitId, string? UnitName, Guid? BedId, string? BedCode, Guid? AssetId, string? AssetName, string? Location,
    bool OutOfOrder, DateOnly? ExpectedReadyOn, Guid? BlockId, bool BlockActive,
    Guid? ReportedBy, string? ReportedByName, Guid? AssignedTo, string? AssignedToName, DateTimeOffset? AssignedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? ResolvedAt, string? ResolvedByName, string? Resolution,
    DateTimeOffset? ClosedAt, string? ClosedByName, DateTimeOffset CreatedAt,
    IReadOnlyList<MaintenanceCommentDto> Comments, IReadOnlyList<MaintenancePhotoDto> Photos, IReadOnlyList<string> NextActions,
    uint Version);

/// <summary>Kind NOTE (someone wrote it) or STATUS (a change of status, with from and to).</summary>
public sealed record MaintenanceCommentDto(
    Guid Id, string Kind, string Body, string? FromStatus, string? ToStatus, DateTimeOffset CreatedAt, string? CreatedByName);

public sealed record MaintenancePhotoDto(Guid Id, string FileName, string ContentType, int SizeBytes, DateTimeOffset CreatedAt, string? CreatedByName);

/// <summary>Reports a problem. Where: a room (UnitId, and BedId for one dorm bed), an asset, or a location in words — at
/// least one. OutOfOrder (a room only): the room is blocked from tonight until ExpectedReadyOn and can't be sold or
/// checked into; resolving the ticket gives it back (to cleaning first).</summary>
public sealed record CreateTicketRequest(
    string Title, string? Description, string Category, string Priority, Guid? UnitId, Guid? BedId, Guid? AssetId,
    string? Location, bool OutOfOrder, DateOnly? ExpectedReadyOn);

/// <summary>Gives a ticket to someone in maintenance (maintenance.assign). UserId null takes it off them.</summary>
public sealed record AssignTicketRequest(Guid? UserId, string? Note, uint? Version);

/// <summary>Work on a ticket (maintenance.update): Status IN_PROGRESS (default) or ON_HOLD (say why, e.g. waiting for a
/// part), and a note for the timeline.</summary>
public sealed record TicketProgressRequest(string? Status, string Note, uint? Version);

/// <summary>The repair is done (maintenance.update). Releases the room's out-of-order block and sends it to cleaning.</summary>
public sealed record ResolveTicketRequest(string Resolution, uint? Version);

/// <summary>Confirms a resolved repair (maintenance.assign) and closes the ticket.</summary>
public sealed record CloseTicketRequest(string? Note, uint? Version);

public sealed record AddTicketCommentRequest(string Body);

public sealed record MaintenanceAssetDto(
    Guid Id, string Code, string Name, string Category, Guid? UnitId, string? UnitName, string? Location, bool IsActive, int OpenTickets);

/// <summary>Creates or updates an asset (maintenance.assign). Code: capitals, digits, - or _, unique at the property.</summary>
public sealed record SaveAssetRequest(string Code, string Name, string Category, Guid? UnitId, string? Location, bool IsActive);
