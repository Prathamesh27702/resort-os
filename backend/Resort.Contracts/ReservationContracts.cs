namespace Resort.Contracts;

// ---------------------------------------------------------------- availability (Sprint 5)
/// <summary>What can be sold for a stay [From, To) for this many guests. Dates are property-local; To is the departure day.</summary>
public sealed record AvailabilityDto(
    DateOnly From, DateOnly To, int Nights, int Adults, int Children, string Currency, IReadOnlyList<AvailabilityTypeDto> Types);

/// <summary>One room type. Free = rooms (or dorm beds) free for every night of the stay; Total = active ones.
/// Fits: whether the guests can be booked here (rooms: within the maximum occupancy; dorms: enough free beds).
/// Price: the default plan's price for the stay (one room, or one bed per guest), or null with PriceProblem saying why.
/// FreeRooms lists the free rooms (rooms only) so a specific one can be chosen.</summary>
public sealed record AvailabilityTypeDto(
    Guid AccommodationTypeId, string Code, string Name, string InventoryMode, short BaseOccupancy, short MaxOccupancy,
    int Total, int Free, bool Fits, string? FitProblem, Guid? RatePlanId, string? RatePlanName, decimal? Price, string? PriceProblem,
    IReadOnlyList<AvailableRoomDto> FreeRooms);

public sealed record AvailableRoomDto(Guid Id, string Code, string Name, string? Floor);

// ---------------------------------------------------------------- reservations
/// <summary>One room line of a booking. Rooms: one room of the type (UnitId picks a specific one, else the first free).
/// Dormitories: one bed per guest (UnitId optionally picks the dorm).</summary>
public sealed record ReservationRoomRequest(Guid AccommodationTypeId, Guid? UnitId, short Adults, short Children);

/// <summary>Status: CONFIRMED (default), PENDING (holds rooms until confirmed) or INQUIRY (holds nothing).
/// RatePlanId: null = the property's default plan. SourceId: where the booking came from (reservation-sources).
/// GuestIds: the primary guest first, then anyone else staying (existing guest profiles).
/// Send an Idempotency-Key header (any unique text, 8–100 characters): a retry with the same key and body returns the
/// same reservation instead of booking twice.</summary>
public sealed record CreateReservationRequest(
    DateOnly CheckIn, DateOnly CheckOut, IReadOnlyList<ReservationRoomRequest> Rooms, IReadOnlyList<Guid> GuestIds,
    Guid SourceId, Guid? RatePlanId, string? Status, string? SpecialRequests, string? Note);

/// <summary>Changes dates, rooms or guest counts; availability and prices are checked again. Rooms the booking already
/// holds are kept where they are still free. Version must match what the user saw.</summary>
public sealed record UpdateReservationRequest(
    DateOnly CheckIn, DateOnly CheckOut, IReadOnlyList<ReservationRoomRequest> Rooms, string? SpecialRequests, uint Version);

/// <summary>Reason: required to cancel and to mark a no-show (kept in the status history and the audit trail).</summary>
public sealed record ReservationStatusRequest(string? Reason);

public sealed record AddReservationNoteRequest(string Note);

/// <summary>A row of the reservations list. Rooms: "Room 103", "Dormitory · 2 beds".</summary>
public sealed record ReservationListItemDto(
    Guid Id, string Reference, string Status, Guid PrimaryGuestId, string GuestName, string? GuestPhone,
    DateOnly CheckIn, DateOnly CheckOut, int Nights, short Adults, short Children, string Rooms, string SourceName,
    decimal TotalAmount, string Currency, DateTimeOffset CreatedAt);

/// <summary>A held room or bed. Current = part of the booking now (false = replaced by a change of dates or rooms).</summary>
public sealed record ReservationUnitDto(
    Guid Id, Guid AccommodationTypeId, string AccommodationTypeName, string InventoryMode, Guid UnitId, string UnitCode,
    string UnitName, Guid? BedId, string? BedCode, DateOnly StartDate, DateOnly EndDate, short Adults, short Children,
    bool HoldsInventory, bool Current, decimal Total);

public sealed record ReservationNightDto(
    Guid ReservationUnitId, DateOnly Date, int Units, decimal UnitPrice, decimal BaseAmount, int ExtraAdults, decimal ExtraAdultRate,
    decimal ExtraAdultAmount, int ExtraChildren, decimal ExtraChildRate, decimal ExtraChildAmount, decimal Total);

public sealed record ReservationGuestDto(Guid GuestId, string FullName, string? Phone, string? Email, bool IsPrimary);

public sealed record ReservationNoteDto(Guid Id, string Note, DateTimeOffset CreatedAt, string? CreatedByName);

public sealed record ReservationStatusChangeDto(string? FromStatus, string ToStatus, string? Reason, DateTimeOffset ChangedAt, string? ChangedByName);

/// <summary>A reservation in full. Breakdown covers the current rooms only. NextStatuses: the moves the status graph
/// allows from here (the caller's permissions are checked when they try one).</summary>
public sealed record ReservationDto(
    Guid Id, string Reference, string Status, DateOnly CheckIn, DateOnly CheckOut, int Nights, short Adults, short Children,
    Guid SourceId, string SourceName, Guid RatePlanId, string RatePlanName, string MealPlan, decimal TotalAmount, string Currency,
    string? SpecialRequests, IReadOnlyList<ReservationGuestDto> Guests, IReadOnlyList<ReservationUnitDto> Units,
    IReadOnlyList<ReservationNightDto> Breakdown, IReadOnlyList<ReservationNoteDto> Notes,
    IReadOnlyList<ReservationStatusChangeDto> History, IReadOnlyList<string> NextStatuses,
    DateTimeOffset CreatedAt, string? CreatedByName, uint Version);
