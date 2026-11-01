namespace Resort.Contracts;

// Sprint 20 — activities and equipment (brief §48, §49). Times are the property's local time ("07:00"); prices are per
// person before GST.

// ---------------------------------------------------------------- activities and sessions

/// <summary>An activity. Capacity and Price are what a new session gets. EquipmentTypeId: one of these per person
/// (e.g. a life jacket for kayaking), offered when equipment is issued for a booking.</summary>
public sealed record ActivityDto(
    Guid Id, string Code, string Name, string? Description, int DurationMinutes, int Capacity, decimal Price, string? MeetingPoint,
    bool WeatherDependent, Guid? EquipmentTypeId, string? EquipmentTypeName, int SortOrder, bool IsActive, uint Version);

public sealed record SaveActivityRequest(
    string Code, string Name, string? Description, int DurationMinutes, int Capacity, decimal Price, string? MeetingPoint, bool WeatherDependent,
    Guid? EquipmentTypeId, int SortOrder = 0, bool IsActive = true, uint? Version = null);

/// <summary>Puts sessions on: every day from FromDate to ToDate at each of Times (local). Capacity and Price default to
/// the activity's. A time that already has a session is skipped (and listed).</summary>
public sealed record CreateSessionsRequest(Guid ActivityId, DateOnly FromDate, DateOnly ToDate, IReadOnlyList<TimeOnly> Times, int? Capacity = null,
    decimal? Price = null);

public sealed record CreateSessionsResultDto(int Created, IReadOnlyList<string> Skipped);

/// <summary>Changes a session's seats (never below what is booked), price for new bookings, and note.</summary>
public sealed record UpdateSessionRequest(int Capacity, decimal Price, string? Note, uint? Version = null);

/// <summary>One session. Status SCHEDULED or CANCELLED. Started: it has begun (no more bookings). Actions: what the caller
/// can do now (book, edit, weather-cancel).</summary>
public sealed record ActivitySessionDto(
    Guid Id, Guid ActivityId, string ActivityCode, string ActivityName, DateOnly Date, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string StartTime,
    string EndTime, int Capacity, int SeatsBooked, int SeatsLeft, decimal Price, string Status, string? Note, string? CancelReason,
    DateTimeOffset? CancelledAt, string? CancelledBy, bool WeatherDependent, string? MeetingPoint, Guid? EquipmentTypeId, bool Started,
    IReadOnlyList<string> Actions, uint Version);

/// <summary>A day's sessions, the activities (for putting sessions on) and the property's today.</summary>
public sealed record ActivityDayDto(DateOnly Date, DateOnly Today, IReadOnlyList<ActivitySessionDto> Sessions, IReadOnlyList<ActivityDto> Activities);

/// <summary>A session with its bookings and, for a weather cancellation, the later sessions of the same activity that
/// bookings can move to.</summary>
public sealed record ActivitySessionDetailDto(ActivitySessionDto Session, IReadOnlyList<ActivityBookingDto> Bookings, IReadOnlyList<ActivitySessionDto> MoveTargets);

// ---------------------------------------------------------------- bookings

/// <summary>
/// One booking. Settlement: ROOM_CHARGE (on the stay's main folio, FolioNumber), FREE or COMPLIMENTARY. Status BOOKED or
/// CANCELLED; CancelKind GUEST or WEATHER; Refund REVERSED (the charge came off the bill), MOVED (to MovedToCode, the
/// charge went with it) or NONE. Equipment: what was reserved or issued for it.
/// </summary>
public sealed record ActivityBookingDto(
    Guid Id, string Code, Guid SessionId, string ActivityName, DateOnly Date, string StartTime, Guid? ReservationId, string? ReservationReference,
    string? Room, string GuestName, int Seats, decimal UnitPrice, decimal Amount, string Settlement, string? ComplimentaryReason, string? FolioNumber,
    string? Note, string Status, string? CancelKind, string? CancelReason, DateTimeOffset? CancelledAt, string? Refund, string? MovedToCode,
    string? MovedFromCode, IReadOnlyList<EquipmentAssignmentDto> Equipment, IReadOnlyList<string> Actions, DateTimeOffset CreatedAt, string? By,
    uint Version);

/// <summary>
/// Books seats on a session (needs an Idempotency-Key header). ReservationId: a staying guest (confirmed or checked in, the
/// session's date inside the stay); the price goes on the stay's bill. GuestName: someone without a booking at the resort
/// (only for a free session or a complimentary booking). Complimentary needs ComplimentaryReason.
/// </summary>
public sealed record BookActivityRequest(Guid SessionId, int Seats, Guid? ReservationId, string? GuestName, bool Complimentary = false,
    string? ComplimentaryReason = null, string? Note = null);

/// <summary>Cancels one booking before its session starts; the charge is reversed on the bill.</summary>
public sealed record CancelActivityBookingRequest(string Reason, uint? Version = null);

/// <summary>Cancels a session for the weather. Every booking is refunded on the bill, except those in Moves, which move to
/// another session of the same activity with their charge.</summary>
public sealed record WeatherCancelRequest(string Reason, IReadOnlyList<MoveBookingRequest>? Moves = null, uint? Version = null);

public sealed record MoveBookingRequest(Guid BookingId, Guid ToSessionId);

/// <summary>What a weather cancellation did: bookings refunded (and the amount), moved, and with nothing to refund.</summary>
public sealed record WeatherCancelResultDto(ActivitySessionDto Session, int Refunded, decimal AmountRefunded, int Moved, int NothingToRefund,
    IReadOnlyList<ActivityBookingDto> Bookings);

/// <summary>A stay that can be booked on activities on a date: confirmed or checked in, the date inside the stay.</summary>
public sealed record ActivityStayDto(Guid ReservationId, string Reference, string Status, string GuestName, string Rooms, DateOnly CheckIn,
    DateOnly CheckOut, int Guests);

// ---------------------------------------------------------------- equipment

/// <summary>A kind of equipment with how many items are in each status.</summary>
public sealed record EquipmentTypeDto(Guid Id, string Code, string Name, int SortOrder, bool IsActive, int Total, int Available,
    IReadOnlyDictionary<string, int> ByStatus, uint Version);

public sealed record SaveEquipmentTypeRequest(string Code, string Name, int SortOrder = 0, bool IsActive = true, uint? Version = null);

/// <summary>One item. With: who has it now (reserved or issued). Moves: the statuses it can be set to by hand.</summary>
public sealed record EquipmentItemDto(
    Guid Id, Guid EquipmentTypeId, string TypeName, string Tag, string? Note, string Status, string? StatusNote, DateTimeOffset StatusChangedAt,
    string? With, IReadOnlyList<string> Moves, uint Version);

/// <summary>Adds items of a type: one tag, or Count new tags numbered after the last one with Prefix (LJ → LJ-07, LJ-08).</summary>
public sealed record AddEquipmentRequest(Guid EquipmentTypeId, string? Tag, string? Prefix = null, int? Count = null, string? Note = null);

/// <summary>Sets an item's status by hand: AVAILABLE, DAMAGED, MAINTENANCE, LOST or RETIRED (with a note for all but AVAILABLE).</summary>
public sealed record ChangeEquipmentStatusRequest(string Status, string? Note, uint? Version = null);

/// <summary>An item reserved for or issued to someone. Status RESERVED, ISSUED, RETURNED, LOST or RELEASED. Overdue: still
/// out after DueBackAt.</summary>
public sealed record EquipmentAssignmentDto(
    Guid Id, Guid EquipmentId, string Tag, string TypeName, string IssuedTo, Guid? ActivityBookingId, string? BookingCode, Guid? ReservationId,
    string? ReservationReference, string Status, DateTimeOffset? ReservedAt, DateTimeOffset? IssuedAt, string? IssuedBy, DateTimeOffset? DueBackAt,
    bool Overdue, DateTimeOffset? ClosedAt, string? ClosedBy, string? ReturnCondition, string? ReturnNote, uint Version);

/// <summary>The equipment page: kinds with counts, every item, and what is out now (reserved or issued).</summary>
public sealed record EquipmentBoardDto(IReadOnlyList<EquipmentTypeDto> Types, IReadOnlyList<EquipmentItemDto> Items, IReadOnlyList<EquipmentAssignmentDto> Out);

public sealed record EquipmentItemHistoryDto(EquipmentItemDto Item, IReadOnlyList<EquipmentAssignmentDto> Assignments);

/// <summary>
/// Reserves or issues items. For: an activity booking (the guest's name, stay and the session's end as due back), a stay
/// (ReservationId) or a name (IssuedTo). Which: EquipmentIds, or EquipmentTypeId and Quantity (the first available by tag).
/// ReserveOnly: keep them aside (RESERVED) to hand over later.
/// </summary>
public sealed record IssueEquipmentRequest(Guid? ActivityBookingId, Guid? ReservationId, string? IssuedTo, IReadOnlyList<Guid>? EquipmentIds,
    Guid? EquipmentTypeId = null, int? Quantity = null, bool ReserveOnly = false, DateTimeOffset? DueBackAt = null);

/// <summary>Takes an item back: GOOD (available again), DAMAGED or LOST (out of use; Note says what happened).</summary>
public sealed record ReturnEquipmentRequest(string Condition, string? Note, uint? Version = null);

public sealed record AssignmentStepRequest(uint? Version = null);
