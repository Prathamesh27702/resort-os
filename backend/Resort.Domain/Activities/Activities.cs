using Resort.Domain.Common;

namespace Resort.Domain.Activities;

public static class ActivitySessionStatuses
{
    public const string Scheduled = "SCHEDULED";
    public const string Cancelled = "CANCELLED";
}

public static class ActivityBookingStatuses
{
    public const string Booked = "BOOKED";
    public const string Cancelled = "CANCELLED";
    public static readonly string[] All = [Booked, Cancelled];
}

/// <summary>How a booking is paid: on the stay's bill, free (the session costs nothing), or complimentary (with the reason).</summary>
public static class ActivitySettlements
{
    public const string RoomCharge = "ROOM_CHARGE";
    public const string Free = "FREE";
    public const string Complimentary = "COMPLIMENTARY";
}

public static class ActivityCancelKinds
{
    public const string Guest = "GUEST";
    public const string Weather = "WEATHER";
}

/// <summary>What happened to the money when a booking was cancelled.</summary>
public static class ActivityRefunds
{
    /// <summary>The charge was reversed on the folio: the guest pays nothing for it.</summary>
    public const string Reversed = "REVERSED";
    /// <summary>The booking moved to another session and the charge went with it (a credit for the new session).</summary>
    public const string Moved = "MOVED";
    /// <summary>Nothing was charged.</summary>
    public const string None = "NONE";
}

/// <summary>An activity the resort offers: Kayaking, Sunrise yoga. Capacity and Price are what a new session gets.</summary>
public class Activity : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public short DurationMinutes { get; set; }
    public short Capacity { get; set; }
    public decimal Price { get; set; }
    public string? MeetingPoint { get; set; }
    public bool WeatherDependent { get; set; } = true;
    public Guid? EquipmentTypeId { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }
}

/// <summary>One session of an activity. SeatsBooked is kept by a database trigger from the bookings (never set here) and
/// can never pass Capacity.</summary>
public class ActivitySchedule : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid ActivityId { get; set; }
    public DateOnly SessionDate { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public short Capacity { get; set; }
    public decimal Price { get; set; }
    public short SeatsBooked { get; set; }
    public string Status { get; set; } = ActivitySessionStatuses.Scheduled;
    public string? Note { get; set; }
    public string? CancelReason { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledBy { get; set; }
    public uint Version { get; set; }

    public int SeatsLeft => Status == ActivitySessionStatuses.Scheduled ? Capacity - SeatsBooked : 0;
}

/// <summary>Seats on a session (AB00001). Amount = Seats × UnitPrice, on the stay's main folio when Settlement is ROOM_CHARGE.</summary>
public class ActivityBooking : AuditableEntity, IPropertyScoped, IAudited, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Code { get; set; } = default!;
    public Guid ScheduleId { get; set; }
    public Guid ActivityId { get; set; }
    public Guid? ReservationId { get; set; }
    public Guid? GuestId { get; set; }
    public string GuestName { get; set; } = default!;
    public short Seats { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
    public string Settlement { get; set; } = ActivitySettlements.Free;
    public string? ComplimentaryReason { get; set; }
    public Guid? FolioId { get; set; }
    public Guid? FolioItemId { get; set; }
    public string? Note { get; set; }
    public string Status { get; set; } = ActivityBookingStatuses.Booked;
    public string? CancelKind { get; set; }
    public string? CancelReason { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledBy { get; set; }
    public string? Refund { get; set; }
    public Guid? ReversalItemId { get; set; }
    public Guid? MovedToBookingId { get; set; }
    public Guid? MovedFromBookingId { get; set; }
    public uint Version { get; set; }
}
