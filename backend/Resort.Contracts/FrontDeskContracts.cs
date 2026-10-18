namespace Resort.Contracts;

// ---------------------------------------------------------------- front desk (Sprint 7)

/// <summary>A front desk list (arrivals, departures or in-house) for one business date. Today is the property's local
/// date, never the server's; Date is the day the list is for (today unless asked otherwise).</summary>
public sealed record FrontDeskListDto(DateOnly Date, DateOnly Today, IReadOnlyList<FrontDeskStayDto> Items);

/// <summary>One booking on a front desk list.
/// Late: an arrival that was due on an earlier day and hasn't checked in yet (shown on today's arrivals).
/// Overdue: a guest still checked in after their departure day (shown on today's departures and in-house).
/// CheckInProblem: why the guest can't be checked in as things stand (a room not ready, still occupied, the date),
/// or null when check-in will go straight through. Balance: what is still owed (folio, Sprint 8; 0 until then).</summary>
public sealed record FrontDeskStayDto(
    Guid ReservationId, string Reference, string Status, Guid GuestId, string GuestName, string? GuestPhone, bool IsVip,
    DateOnly CheckIn, DateOnly CheckOut, int Nights, short Adults, short Children, IReadOnlyList<FrontDeskRoomDto> Rooms,
    string SourceName, decimal TotalAmount, decimal Balance, string Currency, string? SpecialRequests,
    DateTimeOffset? CheckedInAt, DateTimeOffset? CheckedOutAt, bool Late, bool Overdue, string? CheckInProblem, uint Version);

/// <summary>A room (or dorm bed) of a booking with its cleaning state. OccupiedBy: the reference of another booking
/// whose guest is still checked in there (an overdue departure), which blocks check-in.</summary>
public sealed record FrontDeskRoomDto(
    Guid ReservationUnitId, Guid UnitId, string UnitCode, string UnitName, Guid? BedId, string? BedCode, string TypeName,
    DateOnly StartDate, DateOnly EndDate, string Housekeeping, string? OccupiedBy);

/// <summary>The reception dashboard (brief §54): today's numbers and every room with its state tonight.</summary>
public sealed record FrontDeskDashboardDto(
    DateOnly Today, int ArrivalsExpected, int ArrivalsCheckedIn, int DeparturesExpected, int DeparturesCheckedOut, int Overdue,
    int InHouseStays, int InHouseGuests, int RoomsTotal, int RoomsOccupied, int RoomsReady, int RoomsNotReady, int RoomsBlocked,
    IReadOnlyList<FrontDeskBoardRoomDto> Rooms);

/// <summary>A room (or a dorm bed) on the reception board. Occupancy: VACANT, OCCUPIED (a guest checked in),
/// ARRIVING (a booking arrives today and isn't checked in yet), BLOCKED (an active block tonight) or INACTIVE.
/// Housekeeping: READY, DIRTY … (no row yet = READY). Reference/GuestName: the stay in or arriving.</summary>
public sealed record FrontDeskBoardRoomDto(
    Guid UnitId, string UnitCode, string UnitName, Guid? BedId, string? BedCode, string TypeName, string Occupancy,
    string Housekeeping, Guid? ReservationId, string? Reference, string? GuestName, DateOnly? Until);

/// <summary>A room (or bed) a booking's room could be assigned to: Free = no other booking or block for the nights it
/// would take; Fits = it sleeps the guests; Problem says why not, in words.</summary>
public sealed record RoomOptionDto(
    Guid UnitId, string UnitCode, string UnitName, Guid? BedId, string? BedCode, Guid AccommodationTypeId, string TypeName,
    string Housekeeping, bool Free, bool Fits, string? Problem);

/// <summary>Puts one room (or dorm bed) of a booking in a chosen room before or during the stay.
/// Before check-in: the booking's nights move to that room (the price stays as booked); Reason optional.
/// Checked in: the guest moves from today; the nights already slept stay with the old room, which becomes DIRTY.
/// Reason required then. The target must be READY unless OverrideHousekeeping with a reason.</summary>
public sealed record AssignRoomRequest(
    Guid ReservationUnitId, Guid UnitId, Guid? BedId, string? Reason, uint Version, bool OverrideHousekeeping = false);

/// <summary>Checks a guest in. Every room of the booking must be READY and not still occupied; OverrideHousekeeping with
/// OverrideReason checks in anyway when the property allows it (setting frontdesk.allow_checkin_when_not_ready).
/// A PENDING booking is confirmed on the way. Version: the booking's version as seen (optional).</summary>
public sealed record CheckInRequest(uint? Version, bool OverrideHousekeeping = false, string? OverrideReason = null);

/// <summary>Checks a guest out; their rooms become DIRTY. Before the departure day the remaining nights are released and
/// Reason is required (early departure). Refused while the folio has a balance unless the property allows it
/// (setting frontdesk.allow_checkout_with_balance).</summary>
public sealed record CheckOutRequest(uint? Version, string? Reason = null);

/// <summary>A walk-in in one call: the guest (an existing profile, or a new one), the rooms from today until CheckOut,
/// and check-in — one transaction, so nothing is left half done. Needs an Idempotency-Key header.
/// Source is Walk-in; RatePlanId null = the default plan. Free rooms that are READY are taken first.</summary>
public sealed record WalkInRequest(
    Guid? GuestId, CreateGuestRequest? NewGuest, DateOnly CheckOut, IReadOnlyList<ReservationRoomRequest> Rooms,
    Guid? RatePlanId, string? SpecialRequests, string? Note, bool OverrideHousekeeping = false, string? OverrideReason = null);

/// <summary>Sets a room's (or dorm bed's) cleaning state by hand, e.g. housekeeping says Room 101 is ready.
/// The full housekeeping workflow arrives in Sprint 13.</summary>
public sealed record SetHousekeepingRequest(Guid? BedId, string Status, string? Reason);
