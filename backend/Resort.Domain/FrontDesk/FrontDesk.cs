using Resort.Domain.Common;

namespace Resort.Domain.FrontDesk;

/// <summary>The cleaning state of a room or dorm bed (docs/architecture/lifecycles.md). Sprint 7 uses READY and DIRTY;
/// Sprint 13 adds the steps between them on the housekeeping board.</summary>
public static class HousekeepingStatuses
{
    public const string Dirty = "DIRTY";
    public const string Cleaning = "CLEANING";
    public const string Clean = "CLEAN";
    public const string Inspection = "INSPECTION";
    public const string Ready = "READY";
    public const string OutOfOrder = "OUT_OF_ORDER";

    public static readonly string[] All = [Dirty, Cleaning, Clean, Inspection, Ready, OutOfOrder];

    /// <summary>"dirty", "being cleaned", "out of order" — for messages.</summary>
    public static string Words(string status) => status switch
    {
        Cleaning => "being cleaned",
        Clean => "cleaned but not inspected",
        Inspection => "waiting for inspection",
        OutOfOrder => "out of order",
        _ => status.ToLowerInvariant(),
    };
}

/// <summary>Why a housekeeping state changed.</summary>
public static class HousekeepingSources
{
    public const string CheckOut = "CHECK_OUT";
    public const string RoomMove = "ROOM_MOVE";
    public const string Manual = "MANUAL";
}

/// <summary>The cleaning state of one room (BedId null) or one dorm bed. No row = READY (never used).</summary>
public class UnitHousekeepingStatus : AuditableEntity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid UnitId { get; set; }
    public Guid? BedId { get; set; }
    public string Status { get; set; } = HousekeepingStatuses.Ready;
    public string? Reason { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
    public Guid? ChangedBy { get; set; }
}

/// <summary>One change of a room's (or bed's) cleaning state. Append-only. A check-out row is the event Sprint 13 turns
/// into a cleaning task (it sets ConsumedAt).</summary>
public class UnitHousekeepingChange : Entity, IPropertyScoped
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid UnitId { get; set; }
    public Guid? BedId { get; set; }
    public string FromStatus { get; set; } = default!;
    public string ToStatus { get; set; } = default!;
    public string Source { get; set; } = default!;
    public Guid? ReservationId { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
    public Guid? ChangedBy { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

/// <summary>
/// Front desk rules that need no database. Pure, unit-tested.
/// Dates are the property's local business date (<c>Property.LocalDate</c>), never the server's.
/// </summary>
public static class FrontDeskRules
{
    /// <summary>Why a booking staying [checkIn, checkOut) can't be checked in on <paramref name="today"/>, or null.
    /// A late arrival (after the arrival day, before departure) is allowed: the nights are already booked.</summary>
    public static string? CheckInDateProblem(string reference, DateOnly checkIn, DateOnly checkOut, DateOnly today) =>
        today < checkIn
            ? $"{reference} arrives on {Day(checkIn)}; it is {Day(today)} at the property. Change the arrival date on the booking to check the guest in today."
            : today >= checkOut
                ? $"{reference} was due to leave on {Day(checkOut)}, so it can't be checked in now. Mark it no-show, or change its dates first."
                : null;

    /// <summary>The departure day after an early check-out on <paramref name="today"/>: today, but never before the first
    /// night has passed (a stay is at least one night). Equal to <paramref name="checkOut"/> when nothing is released.</summary>
    public static DateOnly DepartureOnCheckOut(DateOnly checkIn, DateOnly checkOut, DateOnly today)
    {
        var earliest = checkIn.AddDays(1);
        var day = today < earliest ? earliest : today;
        return day < checkOut ? day : checkOut;
    }

    private static string Day(DateOnly d) => Rates.QuoteEngine.Day(d);
}
