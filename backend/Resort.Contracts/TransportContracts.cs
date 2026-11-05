namespace Resort.Contracts;

// Sprint 21 — beach transport (brief §47, §73). Times are the property's local time ("08:00"); prices are per seat, one
// way, before GST (transport has no GST category, so none is added).

// ---------------------------------------------------------------- vehicles and routes

/// <summary>A resort vehicle. Seats: passenger seats, not counting the driver; a new trip gets them as its capacity.</summary>
public sealed record VehicleDto(Guid Id, string Code, string Name, string Registration, int Seats, string? Note, int SortOrder, bool IsActive, uint Version);

/// <summary>Registration is typed with or without spaces (MH 07 AB 1234) and kept without them.</summary>
public sealed record SaveVehicleRequest(string Code, string Name, string Registration, int Seats, string? Note = null, int SortOrder = 0,
    bool IsActive = true, uint? Version = null);

/// <summary>Where a trip goes. Price (per seat, one way) and DurationMinutes are what a new trip gets.</summary>
public sealed record TransportRouteDto(Guid Id, string Code, string Name, string Origin, string Destination, decimal DistanceKm, int DurationMinutes,
    decimal Price, int SortOrder, bool IsActive, uint Version);

public sealed record SaveTransportRouteRequest(string Code, string Name, string Origin, string Destination, decimal DistanceKm, int DurationMinutes,
    decimal Price, int SortOrder = 0, bool IsActive = true, uint? Version = null);

/// <summary>A staff account that can run trips at the property (transport.trip_operate): drivers, and managers who drive.</summary>
public sealed record DriverDto(Guid UserId, string Name, bool IsDriver);

// ---------------------------------------------------------------- trips

/// <summary>Puts trips on: every day from FromDate to ToDate at each of Times (local), with the vehicle and driver. The
/// capacity is the vehicle's seats and the price the route's unless given. A time the vehicle or the driver is already on
/// another trip is skipped (and listed with the reason).</summary>
public sealed record CreateTripsRequest(Guid RouteId, Guid VehicleId, Guid? DriverUserId, DateOnly FromDate, DateOnly ToDate, IReadOnlyList<TimeOnly> Times,
    decimal? Price = null);

public sealed record CreateTripsResultDto(int Created, IReadOnlyList<string> Skipped);

/// <summary>Changes a trip's vehicle, driver, seats (never below what is booked, never above the vehicle's seats), price
/// for new bookings, and note.</summary>
public sealed record UpdateTripRequest(Guid VehicleId, Guid? DriverUserId, int Capacity, decimal Price, string? Note, uint? Version = null);

/// <summary>
/// One trip. Status SCHEDULED, DEPARTED, COMPLETED or CANCELLED. OnBoard: seats ticked as boarded. Actions: what the caller
/// can do now (book, edit, cancel, board, depart, complete, manifest). A driver can board, depart and complete only their
/// own trips; a manager any trip.
/// </summary>
public sealed record TripDto(
    Guid Id, Guid RouteId, string RouteCode, string RouteName, string Origin, string Destination, DateOnly Date, DateTimeOffset DepartsAt,
    DateTimeOffset ArrivesAt, string DepartTime, string ArriveTime, Guid VehicleId, string VehicleName, string Registration, Guid? DriverUserId,
    string? DriverName, int Capacity, int SeatsBooked, int SeatsLeft, int OnBoard, decimal Price, string Status, string? Note, string? CancelReason,
    DateTimeOffset? DepartedAt, DateTimeOffset? CompletedAt, IReadOnlyList<string> Actions, uint Version);

/// <summary>A day's trips, with the routes, vehicles and drivers (for putting trips on) and the property's today.</summary>
public sealed record TransportDayDto(DateOnly Date, DateOnly Today, IReadOnlyList<TripDto> Trips, IReadOnlyList<TransportRouteDto> Routes,
    IReadOnlyList<VehicleDto> Vehicles, IReadOnlyList<DriverDto> Drivers);

/// <summary>A trip with its passengers (the manifest): cancelled ones last.</summary>
public sealed record TripDetailDto(TripDto Trip, IReadOnlyList<TripPassengerDto> Passengers);

/// <summary>The driver's day: their own trips with the passengers.</summary>
public sealed record MyTripsDto(DateOnly Date, DateOnly Today, IReadOnlyList<TripDetailDto> Trips);

/// <summary>Cancels a trip (a breakdown, the road closed). Every passenger's charge is reversed on the bill.</summary>
public sealed record CancelTripRequest(string Reason, uint? Version = null);

public sealed record CancelTripResultDto(TripDto Trip, int Refunded, decimal AmountRefunded, int NothingToRefund, IReadOnlyList<TripPassengerDto> Passengers);

/// <summary>Departing or completing a trip, boarding or un-boarding a passenger. Version: the one on screen (optional).</summary>
public sealed record TripStepRequest(uint? Version = null);

// ---------------------------------------------------------------- passengers

/// <summary>
/// One passenger line. Settlement: ROOM_CHARGE (on the stay's main folio, FolioNumber), FREE or COMPLIMENTARY. Status
/// BOOKED, BOARDED, NO_SHOW or CANCELLED; Refund REVERSED (the charge came off the bill) or NONE.
/// </summary>
public sealed record TripPassengerDto(
    Guid Id, string Code, Guid TripId, string RouteName, DateOnly Date, string DepartTime, Guid? ReservationId, string? ReservationReference,
    string? Room, string GuestName, int Seats, decimal UnitPrice, decimal Amount, string Settlement, string? ComplimentaryReason, string? FolioNumber,
    string? Note, string Status, DateTimeOffset? BoardedAt, string? BoardedBy, string? CancelReason, DateTimeOffset? CancelledAt, string? Refund,
    IReadOnlyList<string> Actions, DateTimeOffset CreatedAt, string? By, uint Version);

/// <summary>
/// Books seats on a trip (needs an Idempotency-Key header). ReservationId: a staying guest (confirmed or checked in, the
/// trip's date inside the stay); the price goes on the stay's bill. GuestName: someone without a booking at the resort
/// (only for a free trip or a complimentary seat). Complimentary needs ComplimentaryReason.
/// </summary>
public sealed record BookTransportRequest(Guid TripId, int Seats, Guid? ReservationId, string? GuestName, bool Complimentary = false,
    string? ComplimentaryReason = null, string? Note = null);

/// <summary>Cancels one passenger before the trip leaves; the charge is reversed on the bill.</summary>
public sealed record CancelPassengerRequest(string Reason, uint? Version = null);
