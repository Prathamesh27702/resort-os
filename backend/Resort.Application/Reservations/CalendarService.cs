using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Contracts;
using Resort.Domain.Accommodation;
using Resort.Domain.Rates;
using Resort.Domain.Reservations;

namespace Resort.Application.Reservations;

/// <summary>
/// The reservation calendar's read model: rooms as rows, and the bookings and blocks that fall in [from, to).
/// Read-only, no locks. Each layer is one indexed query (no query per room or per booking):
///   rooms + beds                 ix_accommodation_units_* (one property)
///   bookings                     ix_reservation_units_calendar (property_id, start_date, end_date) WHERE superseded_at IS NULL
///   blocks                       ix_unit_blocks_property_active (property_id, start_date, end_date) WHERE status = 'ACTIVE'
/// then the guests and sources those bookings name, by id.
/// </summary>
public sealed class CalendarService(IAppDbContext db, TimeProvider clock)
{
    /// <summary>The widest window one request may ask for: a month view plus a few days either side.</summary>
    public const int MaxDays = 62;

    /// <summary>Statuses drawn on the calendar: everything that holds (or held) a room. Enquiries hold nothing and are
    /// listed under Reservations → Enquiries instead.</summary>
    public static readonly string[] Shown =
        [ReservationStatuses.Pending, ReservationStatuses.Confirmed, ReservationStatuses.CheckedIn, ReservationStatuses.CheckedOut];

    public async Task<CalendarDto> GetAsync(Guid propertyId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var property = await db.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == propertyId, ct)
                       ?? throw new NotFoundAppException("Property", propertyId);

        var failures = new List<ValidationFailure>();
        if (from is null) failures.Add(new("From", "Enter the first day to show (from=YYYY-MM-DD)."));
        if (to is null) failures.Add(new("To", "Enter the day after the last one to show (to=YYYY-MM-DD)."));
        if (from is not null && to is not null)
        {
            if (to <= from) failures.Add(new("To", "The end of the window must be after its start."));
            else if (to.Value.DayNumber - from.Value.DayNumber > MaxDays)
                failures.Add(new("To", $"The calendar shows at most {MaxDays} days at a time; this window is {to.Value.DayNumber - from.Value.DayNumber}."));
        }
        if (failures.Count > 0) throw new ValidationException(failures);
        var f = from!.Value;
        var t = to!.Value;

        var units = await db.AccommodationUnits.AsNoTracking().Include(u => u.Type).Include(u => u.Beds)
            .Where(u => u.PropertyId == propertyId)
            .ToListAsync(ct);
        var rooms = units
            .OrderBy(u => u.Type.SortOrder).ThenBy(u => u.Type.Name, StringComparer.Ordinal)
            .ThenBy(u => u.SortOrder).ThenBy(u => u.Code, StringComparer.Ordinal)
            .Select(u => new CalendarRoomDto(u.Id, u.Code, u.Name, u.AccommodationTypeId, u.Type.Name, u.Type.InventoryMode,
                u.Type.MaxOccupancy, u.Floor, u.IsActive && u.Type.IsActive,
                u.Beds.OrderBy(b => b.SortOrder).ThenBy(b => b.Code, StringComparer.Ordinal)
                    .Select(b => new CalendarBedDto(b.Id, b.Code, b.Name, b.IsActive)).ToList()))
            .ToList();

        var rows = await db.ReservationUnits.AsNoTracking()
            .Where(u => u.PropertyId == propertyId && u.SupersededAt == null && u.StartDate < t && f < u.EndDate)
            .Join(db.Reservations, u => u.ReservationId, r => r.Id, (u, r) => new { u, r })
            .Where(x => Shown.Contains(x.r.Status))
            .Select(x => new
            {
                x.u.Id, x.u.ReservationId, x.r.Reference, x.r.Status, x.u.UnitId, x.u.BedId, x.u.StartDate, x.u.EndDate,
                x.r.PrimaryGuestId, x.u.Adults, x.u.Children, x.r.SourceId, x.r.TotalAmount, x.r.Version,
            })
            .ToListAsync(ct);

        var guestIds = rows.Select(r => r.PrimaryGuestId).Distinct().ToList();
        var guests = await db.Guests.AsNoTracking().Where(g => guestIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.FullName, ct);
        var sources = await db.ReservationSources.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        var blocks = await db.UnitBlocks.AsNoTracking()
            .Where(b => b.PropertyId == propertyId && b.Status == BlockStatuses.Active && b.StartDate < t && f < b.EndDate)
            .OrderBy(b => b.StartDate)
            .Select(b => new CalendarBlockDto(b.Id, b.UnitId, b.BedId, b.BlockType, b.StartDate, b.EndDate, b.Reason))
            .ToListAsync(ct);

        var bookings = rows.OrderBy(r => r.StartDate).ThenBy(r => r.Reference, StringComparer.Ordinal)
            .Select(r => new CalendarBookingDto(r.Id, r.ReservationId, r.Reference, r.Status, r.UnitId, r.BedId, r.StartDate, r.EndDate,
                guests.GetValueOrDefault(r.PrimaryGuestId, "Guest"), r.Adults, r.Children, sources.GetValueOrDefault(r.SourceId, ""),
                PaymentStatuses.None, r.TotalAmount, r.Version))
            .ToList();

        return new CalendarDto(f, t, property.LocalDate(clock.GetUtcNow()), rooms, bookings, blocks);
    }
}

/// <summary>A booking's payment state as the calendar shows it. Only NONE exists until payments are recorded (Sprint 10).</summary>
public static class PaymentStatuses
{
    public const string None = "NONE";
    public const string Partial = "PARTIAL";
    public const string Paid = "PAID";
}
