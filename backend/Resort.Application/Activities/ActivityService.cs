using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Reservations;
using Resort.Application.Validation;
using Resort.Contracts;
using Resort.Domain.Activities;
using Resort.Domain.Organizations;
using Resort.Domain.Rates;
using Resort.Domain.Reservations;

namespace Resort.Application.Activities;

/// <summary>
/// Activities and their sessions (Sprint 20, brief §48): what the resort offers, the sessions put on for each day, the day's
/// board with the seats left, one session with its bookings, and the stays that can be booked on a date. Bookings
/// themselves are in <see cref="ActivityBookingService"/>.
/// </summary>
public sealed class ActivityService(IAppDbContext db, CallerAccess access, TimeProvider clock)
{
    public const int MaxSessionDays = 92;
    public const int MoveWindowDays = 14;

    // ------------------------------------------------------------------ activities

    public async Task<IReadOnlyList<ActivityDto>> ActivitiesAsync(Guid propertyId, CancellationToken ct)
    {
        await RequirePropertyAsync(propertyId, ct);
        return await ActivityDtosAsync(propertyId, ct);
    }

    public async Task<ActivityDto> SaveActivityAsync(Guid propertyId, Guid? id, SaveActivityRequest r, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var code = r.Code.Trim().ToUpperInvariant();
        var name = r.Name.Trim();
        Activity a;
        if (id is { } existing)
        {
            a = await db.Activities.SingleOrDefaultAsync(x => x.Id == existing && x.PropertyId == propertyId, ct)
                ?? throw new NotFoundAppException("That activity doesn't exist at this property. Reload the page.");
            if (r.Version is { } v) ConcurrencyGuard.ExpectVersion(db, a, v);
        }
        else
        {
            a = new Activity { OrganizationId = property.OrganizationId, PropertyId = propertyId };
            db.Activities.Add(a);
        }
        var others = db.Activities.AsNoTracking().Where(x => x.PropertyId == propertyId && x.Id != a.Id);
        if (await others.Where(x => x.Code == code).Select(x => x.Name).FirstOrDefaultAsync(ct) is { } sameCode)
            throw ReservationService.Field(nameof(r.Code), $"{sameCode} already has the code {code}. Choose another code.");
        if ((await others.Select(x => x.Name).ToListAsync(ct)).Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            throw ReservationService.Field(nameof(r.Name), $"There is already an activity called {name}.");
        if (r.EquipmentTypeId is { } eq && !await db.EquipmentTypes.AnyAsync(t => t.Id == eq && t.PropertyId == propertyId, ct))
            throw ReservationService.Field(nameof(r.EquipmentTypeId), "That kind of equipment isn't at this property. Reload and choose again.");

        a.Code = code;
        a.Name = name;
        a.Description = ReservationService.Clean(r.Description);
        a.DurationMinutes = (short)r.DurationMinutes;
        a.Capacity = (short)r.Capacity;
        a.Price = r.Price;
        a.MeetingPoint = ReservationService.Clean(r.MeetingPoint);
        a.WeatherDependent = r.WeatherDependent;
        a.EquipmentTypeId = r.EquipmentTypeId;
        a.SortOrder = r.SortOrder;
        a.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return (await ActivityDtosAsync(propertyId, ct)).Single(x => x.Id == a.Id);
    }

    // ------------------------------------------------------------------ sessions

    /// <summary>Puts sessions on for every day of the range at each time. Times already taken (or already past today) are
    /// skipped and listed.</summary>
    public async Task<CreateSessionsResultDto> CreateSessionsAsync(Guid propertyId, CreateSessionsRequest r, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var now = clock.GetUtcNow();
        var today = property.LocalDate(now);
        var activity = await db.Activities.AsNoTracking().SingleOrDefaultAsync(a => a.Id == r.ActivityId && a.PropertyId == propertyId, ct)
                       ?? throw ReservationService.Field(nameof(r.ActivityId), "Choose an activity of this property.");
        if (!activity.IsActive)
            throw ReservationService.Field(nameof(r.ActivityId), $"{activity.Name} is switched off. Switch it on first, then put sessions on.");
        if (r.FromDate < today)
            throw ReservationService.Field(nameof(r.FromDate), $"Sessions can start from today ({QuoteEngine.Day(today)}).");
        if (r.ToDate.DayNumber - r.FromDate.DayNumber >= MaxSessionDays)
            throw ReservationService.Field(nameof(r.ToDate), $"Put sessions on for at most {MaxSessionDays} days at a time (until {QuoteEngine.Day(r.FromDate.AddDays(MaxSessionDays - 1))}).");

        var tz = Tz(property);
        var from = Instant(r.FromDate, TimeOnly.MinValue, tz);
        var to = Instant(r.ToDate.AddDays(1), TimeOnly.MinValue, tz);
        var taken = (await db.ActivitySchedules.AsNoTracking()
                .Where(s => s.ActivityId == activity.Id && s.Status == ActivitySessionStatuses.Scheduled && s.StartsAt >= from && s.StartsAt < to)
                .Select(s => s.StartsAt).ToListAsync(ct))
            .ToHashSet();
        var skipped = new List<string>();
        var created = 0;
        for (var day = r.FromDate; day <= r.ToDate; day = day.AddDays(1))
            foreach (var time in r.Times.Distinct().Order())
            {
                var start = Instant(day, time, tz);
                if (taken.Contains(start)) { skipped.Add($"{Short(day)} {Hm(time)}: already on"); continue; }
                if (start <= now) { skipped.Add($"{Short(day)} {Hm(time)}: already past"); continue; }
                db.ActivitySchedules.Add(new ActivitySchedule
                {
                    OrganizationId = property.OrganizationId,
                    PropertyId = propertyId,
                    ActivityId = activity.Id,
                    SessionDate = day,
                    StartsAt = start,
                    EndsAt = start.AddMinutes(activity.DurationMinutes),
                    Capacity = (short)(r.Capacity ?? activity.Capacity),
                    Price = r.Price ?? activity.Price,
                });
                created++;
            }
        await db.SaveChangesAsync(ct);
        return new CreateSessionsResultDto(created, skipped);
    }

    /// <summary>Changes a session's seats, its price for new bookings, and its note. The seats can't go below what is booked.</summary>
    public async Task<ActivitySessionDto> UpdateSessionAsync(Guid propertyId, Guid id, UpdateSessionRequest r, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var s = await LockSessionAsync(propertyId, id, ct);
        var activity = await db.Activities.AsNoTracking().SingleAsync(a => a.Id == s.ActivityId, ct);
        if (r.Version is { } v && s.Version != v)
            throw new ConflictAppException("CONCURRENCY_CONFLICT",
                $"Someone else changed {activity.Name} at {Hm(s, property)} a moment ago (or a guest was booked on it). Reload to see it, then try again.");
        if (s.Status != ActivitySessionStatuses.Scheduled)
            throw new BusinessRuleAppException("SESSION_CANCELLED", $"{Label(activity, s, property)} was cancelled, so it can't be changed.");
        if (clock.GetUtcNow() >= s.EndsAt)
            throw new BusinessRuleAppException("SESSION_OVER", $"{Label(activity, s, property)} is over, so it can't be changed.");
        if (r.Capacity < s.SeatsBooked)
            throw ReservationService.Field(nameof(r.Capacity),
                $"{Seats(s.SeatsBooked)} already booked, so it can't have fewer than {s.SeatsBooked}. Cancel bookings first to make it smaller.");
        s.Capacity = (short)r.Capacity;
        s.Price = r.Price;
        s.Note = ReservationService.Clean(r.Note);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await SessionDtosAsync(property, [await db.ActivitySchedules.AsNoTracking().SingleAsync(x => x.Id == id, ct)], ct))[0];
    }

    /// <summary>A day's sessions (default today) in start order, with the seats left and what the caller can do.</summary>
    public async Task<ActivityDayDto> DayAsync(Guid propertyId, DateOnly? date, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var today = property.LocalDate(clock.GetUtcNow());
        var day = date ?? today;
        var sessions = await db.ActivitySchedules.AsNoTracking().Where(s => s.PropertyId == propertyId && s.SessionDate == day)
            .OrderBy(s => s.StartsAt).ToListAsync(ct);
        var activities = await ActivityDtosAsync(propertyId, ct);
        var order = activities.Select((a, i) => (a.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var dtos = (await SessionDtosAsync(property, sessions, ct))
            .OrderBy(s => s.StartsAt).ThenBy(s => order.GetValueOrDefault(s.ActivityId)).ToList();
        return new ActivityDayDto(day, today, dtos, activities);
    }

    public async Task<ActivitySessionDetailDto> SessionAsync(Guid propertyId, Guid id, ActivityBookingService bookings, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var s = await db.ActivitySchedules.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.PropertyId == propertyId, ct)
                ?? throw new NotFoundAppException("That session doesn't exist at this property. Reload the page.");
        var list = await db.ActivityBookings.AsNoTracking().Where(b => b.ScheduleId == id)
            .OrderBy(b => b.Status == ActivityBookingStatuses.Booked ? 0 : 1).ThenBy(b => b.Code).ToListAsync(ct);
        var now = clock.GetUtcNow();
        var targets = s.Status == ActivitySessionStatuses.Scheduled
            ? await db.ActivitySchedules.AsNoTracking()
                .Where(x => x.ActivityId == s.ActivityId && x.Id != s.Id && x.Status == ActivitySessionStatuses.Scheduled && x.StartsAt > now
                            && x.StartsAt < now.AddDays(MoveWindowDays))
                .OrderBy(x => x.StartsAt).Take(30).ToListAsync(ct)
            : [];
        return new ActivitySessionDetailDto((await SessionDtosAsync(property, [s], ct))[0], await bookings.DtosAsync(property, list, ct),
            await SessionDtosAsync(property, targets, ct));
    }

    /// <summary>Stays that can be booked on activities on a date: confirmed or checked in, the date from arrival to departure day.</summary>
    public async Task<IReadOnlyList<ActivityStayDto>> StaysAsync(Guid propertyId, DateOnly? date, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var day = date ?? property.LocalDate(clock.GetUtcNow());
        var stays = await db.Reservations.AsNoTracking()
            .Where(r => r.PropertyId == propertyId && (r.Status == ReservationStatuses.CheckedIn || r.Status == ReservationStatuses.Confirmed)
                        && r.CheckIn <= day && r.CheckOut >= day)
            .ToListAsync(ct);
        var rooms = await RoomsTextAsync(stays.Select(s => s.Id), ct);
        var guestIds = stays.Select(s => s.PrimaryGuestId).Distinct().ToList();
        var guests = await db.Guests.AsNoTracking().Where(g => guestIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.FullName, ct);
        return stays
            .Select(s => new ActivityStayDto(s.Id, s.Reference, s.Status, guests.GetValueOrDefault(s.PrimaryGuestId, ""), rooms.GetValueOrDefault(s.Id, ""),
                s.CheckIn, s.CheckOut, s.Adults + s.Children))
            .OrderBy(s => s.Status == ReservationStatuses.CheckedIn ? 0 : 1).ThenBy(s => s.Rooms, StringComparer.Ordinal).ThenBy(s => s.Reference, StringComparer.Ordinal)
            .ToList();
    }

    // ------------------------------------------------------------------ shared

    internal async Task<Property> RequirePropertyAsync(Guid propertyId, CancellationToken ct) =>
        await db.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == propertyId, ct) ?? throw new NotFoundAppException("Property", propertyId);

    /// <summary>Takes the session's row lock (bookings, moves and changes of one session queue here) and loads it, tracked.</summary>
    internal async Task<ActivitySchedule> LockSessionAsync(Guid propertyId, Guid id, CancellationToken ct, string field = "SessionId")
    {
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM activity_schedules WHERE id = {id} AND property_id = {propertyId} FOR UPDATE", ct);
        return await db.ActivitySchedules.SingleOrDefaultAsync(s => s.Id == id && s.PropertyId == propertyId, ct)
               ?? throw ReservationService.Field(field, "That session doesn't exist at this property. Reload the page and choose again.");
    }

    internal async Task<List<ActivitySessionDto>> SessionDtosAsync(Property property, IReadOnlyCollection<ActivitySchedule> sessions, CancellationToken ct)
    {
        if (sessions.Count == 0) return [];
        var ids = sessions.Select(s => s.ActivityId).Distinct().ToList();
        var activities = await db.Activities.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var users = await UserNamesAsync(sessions.Select(s => s.CancelledBy), ct);
        var a = await access.GetAsync(ct);
        var canBook = a.Has(Perm.ActivityBookingCreate, property.Id);
        var canManage = a.Has(Perm.ActivityManage, property.Id);
        var now = clock.GetUtcNow();
        var tz = Tz(property);
        return sessions.Select(s =>
        {
            var act = activities[s.ActivityId];
            var open = s.Status == ActivitySessionStatuses.Scheduled;
            var started = now >= s.StartsAt;
            var actions = new List<string>();
            if (open && !started && s.SeatsLeft > 0 && canBook) actions.Add("book");
            if (open && now < s.EndsAt && canManage) { actions.Add("edit"); actions.Add("weather-cancel"); }
            return new ActivitySessionDto(s.Id, act.Id, act.Code, act.Name, s.SessionDate, s.StartsAt, s.EndsAt, Hm(s.StartsAt, tz), Hm(s.EndsAt, tz),
                s.Capacity, s.SeatsBooked, s.SeatsLeft, s.Price, s.Status, s.Note, s.CancelReason, s.CancelledAt,
                s.CancelledBy is { } u ? users.GetValueOrDefault(u) : null, act.WeatherDependent, act.MeetingPoint, act.EquipmentTypeId, started, actions,
                s.Version);
        }).ToList();
    }

    internal async Task<Dictionary<Guid, string>> UserNamesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.OfType<Guid>().Distinct().ToList();
        return list.Count == 0 ? [] : await db.Users.IgnoreQueryFilters().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
    }

    /// <summary>"Room 104", "Room 104, Room 105", "Dorm 1 bed B02" per reservation.</summary>
    internal async Task<Dictionary<Guid, string>> RoomsTextAsync(IEnumerable<Guid> reservationIds, CancellationToken ct)
    {
        var ids = reservationIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var rows = await db.ReservationUnits.AsNoTracking().Where(u => ids.Contains(u.ReservationId) && u.SupersededAt == null).ToListAsync(ct);
        var unitIds = rows.Select(r => r.UnitId).Distinct().ToList();
        var units = await db.AccommodationUnits.AsNoTracking().Include(u => u.Beds).Where(u => unitIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
        return rows.GroupBy(r => r.ReservationId).ToDictionary(g => g.Key, g => string.Join(", ", g
            .OrderBy(r => units[r.UnitId].SortOrder).ThenBy(r => units[r.UnitId].Code, StringComparer.Ordinal)
            .Select(r => r.BedId is { } b ? $"{units[r.UnitId].Name} bed {units[r.UnitId].Beds.First(x => x.Id == b).Code}" : units[r.UnitId].Name)
            .Distinct()));
    }

    private async Task<List<ActivityDto>> ActivityDtosAsync(Guid propertyId, CancellationToken ct)
    {
        var list = await db.Activities.AsNoTracking().Where(a => a.PropertyId == propertyId).OrderBy(a => a.SortOrder).ThenBy(a => a.Name).ToListAsync(ct);
        var types = await db.EquipmentTypes.AsNoTracking().Where(t => t.PropertyId == propertyId).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        return list.Select(a => new ActivityDto(a.Id, a.Code, a.Name, a.Description, a.DurationMinutes, a.Capacity, a.Price, a.MeetingPoint, a.WeatherDependent,
            a.EquipmentTypeId, a.EquipmentTypeId is { } t ? types.GetValueOrDefault(t) : null, a.SortOrder, a.IsActive, a.Version)).ToList();
    }

    internal static TimeZoneInfo Tz(Property p) => TimeZoneInfo.FindSystemTimeZoneById(p.Timezone);

    /// <summary>The instant of a local date and time at the property.</summary>
    internal static DateTimeOffset Instant(DateOnly day, TimeOnly time, TimeZoneInfo tz)
    {
        var local = day.ToDateTime(time);
        return new DateTimeOffset(local, tz.GetUtcOffset(local)).ToUniversalTime();
    }

    internal static string Hm(DateTimeOffset at, TimeZoneInfo tz) => TimeZoneInfo.ConvertTime(at, tz).ToString("HH:mm", CultureInfo.InvariantCulture);
    internal static string Hm(ActivitySchedule s, Property p) => Hm(s.StartsAt, Tz(p));
    private static string Hm(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"Tue 3 Nov".</summary>
    internal static string Short(DateOnly d) => d.ToString("ddd d MMM", CultureInfo.InvariantCulture);

    /// <summary>"Kayaking at 07:00 on Tue 3 Nov".</summary>
    internal static string Label(Activity a, ActivitySchedule s, Property p) => $"{a.Name} at {Hm(s, p)} on {Short(s.SessionDate)}";

    /// <summary>"1 seat is", "3 seats are".</summary>
    internal static string Seats(int n) => n == 1 ? "1 seat is" : $"{n} seats are";

    internal static string Rupees(decimal v) => Money.Rupees(v);
}
