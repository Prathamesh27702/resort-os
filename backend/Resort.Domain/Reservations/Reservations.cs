using Resort.Domain.Common;

namespace Resort.Domain.Reservations;

/// <summary>
/// Reservation statuses and the allowed moves between them (docs/architecture/lifecycles.md, I. Booking).
/// The same graph is a table in the database (reservation_status_transitions) whose trigger rejects anything else;
/// a test checks the two match.
/// </summary>
public static class ReservationStatuses
{
    /// <summary>An enquiry: dates and rooms noted, nothing held.</summary>
    public const string Inquiry = "INQUIRY";
    /// <summary>Booked and holding rooms, awaiting payment or confirmation.</summary>
    public const string Pending = "PENDING";
    public const string Confirmed = "CONFIRMED";
    public const string CheckedIn = "CHECKED_IN";
    public const string CheckedOut = "CHECKED_OUT";
    public const string Cancelled = "CANCELLED";
    public const string NoShow = "NO_SHOW";
    public const string Refunded = "REFUNDED";

    public static readonly string[] All = [Inquiry, Pending, Confirmed, CheckedIn, CheckedOut, Cancelled, NoShow, Refunded];

    /// <summary>Statuses a reservation may be created in.</summary>
    public static readonly string[] Initial = [Inquiry, Pending, Confirmed];

    /// <summary>Statuses in which the reservation's rooms and beds are taken out of availability.</summary>
    public static readonly string[] HoldingInventory = [Pending, Confirmed, CheckedIn];

    /// <summary>Statuses in which dates, rooms and guests can still be changed.</summary>
    public static readonly string[] Changeable = [Inquiry, Pending, Confirmed];

    /// <summary>Every allowed move, from → to.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Transitions = new Dictionary<string, string[]>
    {
        [Inquiry] = [Pending, Confirmed, Cancelled],
        [Pending] = [Confirmed, Cancelled],
        [Confirmed] = [CheckedIn, Cancelled, NoShow],
        [CheckedIn] = [CheckedOut],
        [CheckedOut] = [],
        [Cancelled] = [Refunded],
        [NoShow] = [Refunded],
        [Refunded] = [],
    };

    public static bool HoldsInventory(string status) => HoldingInventory.Contains(status);

    public static bool CanMove(string from, string to) => Transitions.TryGetValue(from, out var next) && next.Contains(to);

    /// <summary>"confirmed", "checked in", "no-show" — for messages.</summary>
    public static string Words(string status) => status switch
    {
        NoShow => "no-show",
        _ => status.Replace('_', ' ').ToLowerInvariant(),
    };
}

/// <summary>One booking. Dates are the property's local calendar dates; <see cref="CheckOut"/> is the departure day
/// (not a night). Never deleted: cancelling or deleting an enquiry is a status (brief §87).</summary>
public class Reservation : AuditableEntity, IPropertyScoped, IVersioned, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    /// <summary>R00001, R00002 … per property.</summary>
    public string Reference { get; set; } = default!;
    public string Status { get; set; } = ReservationStatuses.Confirmed;
    public Guid SourceId { get; set; }
    public Guid PrimaryGuestId { get; set; }
    public Guid RatePlanId { get; set; }
    public DateOnly CheckIn { get; set; }
    public DateOnly CheckOut { get; set; }
    public short Adults { get; set; }
    public short Children { get; set; }
    /// <summary>Sum of the current rooms' nightly prices, before tax.</summary>
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "INR";
    public string? SpecialRequests { get; set; }
    public DateTimeOffset StatusChangedAt { get; set; }
    /// <summary>When the guest arrived and left (Sprint 7). Who did it is in the status history.</summary>
    public DateTimeOffset? CheckedInAt { get; set; }
    public DateTimeOffset? CheckedOutAt { get; set; }
    public uint Version { get; set; }

    public List<ReservationUnit> Units { get; set; } = [];
    public List<ReservationGuest> Guests { get; set; } = [];

    public int Nights => CheckOut.DayNumber - CheckIn.DayNumber;
}

/// <summary>A room, or one dorm bed, held by a reservation for [StartDate, EndDate). When dates or rooms change the
/// old rows are superseded (kept, no longer holding) and new ones inserted.</summary>
public class ReservationUnit : AuditableEntity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid ReservationId { get; set; }
    public Guid AccommodationTypeId { get; set; }
    public Guid UnitId { get; set; }
    public Guid? BedId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public short Adults { get; set; }
    public short Children { get; set; }
    public bool HoldsInventory { get; set; }
    public DateTimeOffset? SupersededAt { get; set; }

    public bool IsCurrent => SupersededAt is null;
}

public class ReservationGuest : AuditableEntity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid ReservationId { get; set; }
    public Guid GuestId { get; set; }
}

/// <summary>The price of one night of one reservation unit, copied from the rate grid when booked.</summary>
public class ReservationRateBreakdown : AuditableEntity, IPropertyScoped
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid ReservationId { get; set; }
    public Guid ReservationUnitId { get; set; }
    public Guid RatePlanId { get; set; }
    public DateOnly StayDate { get; set; }
    public short Units { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal BaseAmount { get; set; }
    public short ExtraAdults { get; set; }
    public decimal ExtraAdultRate { get; set; }
    public decimal ExtraAdultAmount { get; set; }
    public short ExtraChildren { get; set; }
    public decimal ExtraChildRate { get; set; }
    public decimal ExtraChildAmount { get; set; }
    public decimal Total { get; set; }
}

/// <summary>Append-only note on a reservation.</summary>
public class ReservationNote : Entity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid ReservationId { get; set; }
    public string Note { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

/// <summary>One status change: who, when, why. Append-only.</summary>
public class ReservationStatusChange : Entity, IPropertyScoped
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid ReservationId { get; set; }
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = default!;
    public string? Reason { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
    public Guid? ChangedBy { get; set; }
}

/// <summary>The stored outcome of a create request, keyed by the client's Idempotency-Key.</summary>
public class IdempotencyKey : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; set; }
    public string Scope { get; set; } = default!;
    public string Key { get; set; } = default!;
    public byte[] RequestHash { get; set; } = default!;
    public Guid? UserId { get; set; }
    public short? StatusCode { get; set; }
    public string? Response { get; set; }
    public Guid? ResourceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
