using Resort.Domain.Common;

namespace Resort.Domain.Transport;

/// <summary>A trip moves forward only: SCHEDULED → DEPARTED → COMPLETED, or SCHEDULED → CANCELLED. The database trigger
/// fn_vehicle_trips_status allows exactly these moves.</summary>
public static class TripStatuses
{
    public const string Scheduled = "SCHEDULED";
    public const string Departed = "DEPARTED";
    public const string Completed = "COMPLETED";
    public const string Cancelled = "CANCELLED";

    public static readonly (string From, string To)[] Moves = [(Scheduled, Departed), (Departed, Completed), (Scheduled, Cancelled)];

    public static string Words(string s) => s switch
    {
        Scheduled => "Scheduled",
        Departed => "On the road",
        Completed => "Completed",
        Cancelled => "Cancelled",
        _ => s,
    };
}

/// <summary>A passenger: BOOKED → BOARDED (and back before departure), NO_SHOW (not on board when the trip left), or
/// CANCELLED. The database trigger fn_trip_passengers_seats allows exactly these moves.</summary>
public static class PassengerStatuses
{
    public const string Booked = "BOOKED";
    public const string Boarded = "BOARDED";
    public const string NoShow = "NO_SHOW";
    public const string Cancelled = "CANCELLED";

    public static readonly (string From, string To)[] Moves =
        [(Booked, Boarded), (Boarded, Booked), (Booked, NoShow), (Booked, Cancelled), (Boarded, Cancelled)];

    public static string Words(string s) => s switch
    {
        Booked => "Booked",
        Boarded => "On board",
        NoShow => "No-show",
        Cancelled => "Cancelled",
        _ => s,
    };
}

/// <summary>How a seat is paid: on the stay's bill, free (the trip costs nothing), or complimentary (with the reason).</summary>
public static class TransportSettlements
{
    public const string RoomCharge = "ROOM_CHARGE";
    public const string Free = "FREE";
    public const string Complimentary = "COMPLIMENTARY";
}

/// <summary>What happened to the money when a passenger was cancelled: REVERSED on the bill, or NONE (nothing charged).</summary>
public static class TransportRefunds
{
    public const string Reversed = "REVERSED";
    public const string None = "NONE";
}

/// <summary>A resort vehicle: "Force Traveller", MH07AB1234, 12 passenger seats.</summary>
public class Vehicle : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Registration { get; set; } = default!;
    public short Seats { get; set; }
    public string? Note { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }
}

/// <summary>Where a trip goes. Price (per seat, one way) is what a new trip gets.</summary>
public class TransportRoute : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Origin { get; set; } = default!;
    public string Destination { get; set; } = default!;
    public decimal DistanceKm { get; set; }
    public short DurationMinutes { get; set; }
    public decimal Price { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }
}

/// <summary>One trip. SeatsBooked is kept by a database trigger from the passengers (never set here) and can never pass
/// Capacity.</summary>
public class VehicleTrip : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid RouteId { get; set; }
    public Guid VehicleId { get; set; }
    public Guid? DriverUserId { get; set; }
    public DateOnly TripDate { get; set; }
    public DateTimeOffset DepartsAt { get; set; }
    public DateTimeOffset ArrivesAt { get; set; }
    public short Capacity { get; set; }
    public decimal Price { get; set; }
    public short SeatsBooked { get; set; }
    public string Status { get; set; } = TripStatuses.Scheduled;
    public string? Note { get; set; }
    public DateTimeOffset? DepartedAt { get; set; }
    public Guid? DepartedBy { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? CompletedBy { get; set; }
    public string? CancelReason { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledBy { get; set; }
    public uint Version { get; set; }

    public int SeatsLeft => Status == TripStatuses.Scheduled ? Capacity - SeatsBooked : 0;
}

/// <summary>Seats on a trip (TP00001). Amount = Seats × UnitPrice, on the stay's main folio when Settlement is ROOM_CHARGE.</summary>
public class TripPassenger : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Code { get; set; } = default!;
    public Guid TripId { get; set; }
    public Guid? ReservationId { get; set; }
    public Guid? GuestId { get; set; }
    public string GuestName { get; set; } = default!;
    public short Seats { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
    public string Settlement { get; set; } = TransportSettlements.Free;
    public string? ComplimentaryReason { get; set; }
    public Guid? FolioId { get; set; }
    public Guid? FolioItemId { get; set; }
    public string? Note { get; set; }
    public string Status { get; set; } = PassengerStatuses.Booked;
    public DateTimeOffset? BoardedAt { get; set; }
    public Guid? BoardedBy { get; set; }
    public string? CancelReason { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledBy { get; set; }
    public string? Refund { get; set; }
    public Guid? ReversalItemId { get; set; }
    public uint Version { get; set; }
}
