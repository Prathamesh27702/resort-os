using Microsoft.EntityFrameworkCore;
using Resort.Application.Activities;
using Resort.Application.Common;
using Resort.Application.Invoicing;
using Resort.Application.Reservations;
using Resort.Application.Validation;
using Resort.Contracts;
using Resort.Domain.Organizations;
using Resort.Domain.Rates;
using Resort.Domain.Transport;

namespace Resort.Application.Transport;

/// <summary>
/// Beach transport set-up and reading (Sprint 21, brief §47): vehicles, routes, the trips put on for each day with a
/// vehicle and a driver, the day's board with the seats left, one trip with its passengers (the manifest, also as a PDF),
/// and the driver's own trips. Bookings, boarding, departing and cancelling are in <see cref="TransportBookingService"/>.
/// </summary>
public sealed class TransportService(IAppDbContext db, ICurrentUser caller, CallerAccess access, TimeProvider clock, IPdfRenderer pdf)
{
    public const int MaxTripDays = 92;

    // ------------------------------------------------------------------ vehicles

    public async Task<IReadOnlyList<VehicleDto>> VehiclesAsync(Guid propertyId, CancellationToken ct)
    {
        await RequirePropertyAsync(propertyId, ct);
        return await VehicleDtosAsync(propertyId, ct);
    }

    public async Task<VehicleDto> SaveVehicleAsync(Guid propertyId, Guid? id, SaveVehicleRequest r, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var code = r.Code.Trim().ToUpperInvariant();
        var reg = TransportRules.Registration(r.Registration);
        Vehicle v;
        if (id is { } existing)
        {
            v = await db.Vehicles.SingleOrDefaultAsync(x => x.Id == existing && x.PropertyId == propertyId, ct)
                ?? throw new NotFoundAppException("That vehicle doesn't exist at this property. Reload the page.");
            if (r.Version is { } ver) ConcurrencyGuard.ExpectVersion(db, v, ver);
        }
        else
        {
            v = new Vehicle { OrganizationId = property.OrganizationId, PropertyId = propertyId };
            db.Vehicles.Add(v);
        }
        if (await db.Vehicles.AsNoTracking().Where(x => x.PropertyId == propertyId && x.Id != v.Id && x.Code == code).Select(x => x.Name)
                .FirstOrDefaultAsync(ct) is { } sameCode)
            throw ReservationService.Field(nameof(r.Code), $"{sameCode} already has the code {code}. Choose another code.");
        if (await db.Vehicles.AsNoTracking().Where(x => x.OrganizationId == property.OrganizationId && x.Id != v.Id && x.Registration == reg)
                .Select(x => x.Name).FirstOrDefaultAsync(ct) is { } sameReg)
            throw ReservationService.Field(nameof(r.Registration), $"{sameReg} already has the registration {reg}. Check the number.");
        if (id is not null && r.Seats < v.Seats)
        {
            var now = clock.GetUtcNow();
            var bigger = await db.VehicleTrips.AsNoTracking()
                .Where(t => t.VehicleId == v.Id && t.Status == TripStatuses.Scheduled && t.DepartsAt > now && t.SeatsBooked > r.Seats)
                .OrderBy(t => t.DepartsAt).FirstOrDefaultAsync(ct);
            if (bigger is not null)
                throw ReservationService.Field(nameof(r.Seats),
                    $"The {Hm(bigger.DepartsAt, Tz(property))} trip on {Short(bigger.TripDate)} already has {bigger.SeatsBooked} seats booked in it, so it can't have fewer than {bigger.SeatsBooked} seats. Move those passengers first.");
        }
        v.Code = code;
        v.Name = r.Name.Trim();
        v.Registration = reg;
        v.Seats = (short)r.Seats;
        v.Note = ReservationService.Clean(r.Note);
        v.SortOrder = r.SortOrder;
        v.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return (await VehicleDtosAsync(propertyId, ct)).Single(x => x.Id == v.Id);
    }

    // ------------------------------------------------------------------ routes

    public async Task<IReadOnlyList<TransportRouteDto>> RoutesAsync(Guid propertyId, CancellationToken ct)
    {
        await RequirePropertyAsync(propertyId, ct);
        return await RouteDtosAsync(propertyId, ct);
    }

    public async Task<TransportRouteDto> SaveRouteAsync(Guid propertyId, Guid? id, SaveTransportRouteRequest r, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var code = r.Code.Trim().ToUpperInvariant();
        var name = r.Name.Trim();
        TransportRoute route;
        if (id is { } existing)
        {
            route = await db.TransportRoutes.SingleOrDefaultAsync(x => x.Id == existing && x.PropertyId == propertyId, ct)
                    ?? throw new NotFoundAppException("That route doesn't exist at this property. Reload the page.");
            if (r.Version is { } ver) ConcurrencyGuard.ExpectVersion(db, route, ver);
        }
        else
        {
            route = new TransportRoute { OrganizationId = property.OrganizationId, PropertyId = propertyId };
            db.TransportRoutes.Add(route);
        }
        var others = db.TransportRoutes.AsNoTracking().Where(x => x.PropertyId == propertyId && x.Id != route.Id);
        if (await others.Where(x => x.Code == code).Select(x => x.Name).FirstOrDefaultAsync(ct) is { } sameCode)
            throw ReservationService.Field(nameof(r.Code), $"{sameCode} already has the code {code}. Choose another code.");
        if ((await others.Select(x => x.Name).ToListAsync(ct)).Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            throw ReservationService.Field(nameof(r.Name), $"There is already a route called {name}.");
        route.Code = code;
        route.Name = name;
        route.Origin = r.Origin.Trim();
        route.Destination = r.Destination.Trim();
        route.DistanceKm = r.DistanceKm;
        route.DurationMinutes = (short)r.DurationMinutes;
        route.Price = r.Price;
        route.SortOrder = r.SortOrder;
        route.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return (await RouteDtosAsync(propertyId, ct)).Single(x => x.Id == route.Id);
    }

    // ------------------------------------------------------------------ trips

    /// <summary>Puts trips on for every day of the range at each time. A time already past, or when the vehicle or the
    /// driver is on another trip, is skipped and listed with the reason.</summary>
    public async Task<CreateTripsResultDto> CreateTripsAsync(Guid propertyId, CreateTripsRequest r, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var now = clock.GetUtcNow();
        var today = property.LocalDate(now);
        var route = await db.TransportRoutes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == r.RouteId && x.PropertyId == propertyId, ct)
                    ?? throw ReservationService.Field(nameof(r.RouteId), "Choose a route of this property.");
        if (!route.IsActive)
            throw ReservationService.Field(nameof(r.RouteId), $"{route.Name} is switched off. Switch it on first, then put trips on.");
        var vehicle = await RequireVehicleAsync(propertyId, r.VehicleId, nameof(r.VehicleId), ct);
        var driverName = r.DriverUserId is { } d ? await RequireDriverAsync(property, d, nameof(r.DriverUserId), ct) : null;
        if (r.FromDate < today)
            throw ReservationService.Field(nameof(r.FromDate), $"Trips can start from today ({QuoteEngine.Day(today)}).");
        if (r.ToDate.DayNumber - r.FromDate.DayNumber >= MaxTripDays)
            throw ReservationService.Field(nameof(r.ToDate), $"Put trips on for at most {MaxTripDays} days at a time (until {QuoteEngine.Day(r.FromDate.AddDays(MaxTripDays - 1))}).");

        var tz = Tz(property);
        var from = Instant(r.FromDate, TimeOnly.MinValue, tz);
        var to = Instant(r.ToDate.AddDays(2), TimeOnly.MinValue, tz);
        var busy = await db.VehicleTrips.AsNoTracking()
            .Where(t => t.Status != TripStatuses.Cancelled && t.DepartsAt < to && t.ArrivesAt > from
                        && (t.VehicleId == vehicle.Id || (r.DriverUserId != null && t.DriverUserId == r.DriverUserId)))
            .ToListAsync(ct);
        var routeNames = await db.TransportRoutes.AsNoTracking().Where(x => x.PropertyId == propertyId).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var skipped = new List<string>();
        var created = 0;
        for (var day = r.FromDate; day <= r.ToDate; day = day.AddDays(1))
            foreach (var time in r.Times.Distinct().Order())
            {
                var start = Instant(day, time, tz);
                var end = start.AddMinutes(route.DurationMinutes);
                var at = $"{Short(day)} {Hm(time)}";
                if (start <= now) { skipped.Add($"{at}: already past"); continue; }
                var clash = busy.FirstOrDefault(t => t.DepartsAt < end && t.ArrivesAt > start);
                if (clash is not null)
                {
                    var what = clash.VehicleId == vehicle.Id ? vehicle.Name : driverName;
                    skipped.Add($"{at}: {what} is on the {Hm(clash.DepartsAt, tz)} {routeNames.GetValueOrDefault(clash.RouteId, "trip")}");
                    continue;
                }
                var trip = new VehicleTrip
                {
                    OrganizationId = property.OrganizationId,
                    PropertyId = propertyId,
                    RouteId = route.Id,
                    VehicleId = vehicle.Id,
                    DriverUserId = r.DriverUserId,
                    TripDate = day,
                    DepartsAt = start,
                    ArrivesAt = end,
                    Capacity = vehicle.Seats,
                    Price = r.Price ?? route.Price,
                };
                db.VehicleTrips.Add(trip);
                busy.Add(trip);
                created++;
            }
        await db.SaveChangesAsync(ct);
        return new CreateTripsResultDto(created, skipped);
    }

    /// <summary>Changes a trip's vehicle, driver, seats, price for new bookings and note, before it leaves.</summary>
    public async Task<TripDto> UpdateTripAsync(Guid propertyId, Guid id, UpdateTripRequest r, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var t = await LockTripAsync(propertyId, id, ct);
        var label = await LabelAsync(t, property, ct);
        if (r.Version is { } v && t.Version != v)
            throw new ConflictAppException("CONCURRENCY_CONFLICT", $"Someone else changed {label} a moment ago (or a guest was booked on it). Reload to see it, then try again.");
        if (t.Status != TripStatuses.Scheduled)
            throw new BusinessRuleAppException("TRIP_CLOSED", $"{label} is {TripStatuses.Words(t.Status).ToLowerInvariant()}, so it can't be changed.");
        var vehicle = await RequireVehicleAsync(propertyId, r.VehicleId, nameof(r.VehicleId), ct);
        var driverName = r.DriverUserId is { } d ? await RequireDriverAsync(property, d, nameof(r.DriverUserId), ct) : null;
        if (r.Capacity > vehicle.Seats)
            throw ReservationService.Field(nameof(r.Capacity), $"{vehicle.Name} has {vehicle.Seats} passenger seats, so the trip can't have more than {vehicle.Seats}.");
        if (r.Capacity < t.SeatsBooked)
            throw ReservationService.Field(nameof(r.Capacity),
                $"{ActivityService.Seats(t.SeatsBooked)} already booked, so it can't have fewer than {t.SeatsBooked}. Cancel passengers first to make it smaller.");
        var tz = Tz(property);
        var clashes = await db.VehicleTrips.AsNoTracking()
            .Where(x => x.Id != t.Id && x.Status != TripStatuses.Cancelled && x.DepartsAt < t.ArrivesAt && x.ArrivesAt > t.DepartsAt
                        && (x.VehicleId == vehicle.Id || (r.DriverUserId != null && x.DriverUserId == r.DriverUserId)))
            .ToListAsync(ct);
        if (clashes.FirstOrDefault(x => x.VehicleId == vehicle.Id) is { } vc)
            throw ReservationService.Field(nameof(r.VehicleId), $"{vehicle.Name} is on the {Hm(vc.DepartsAt, tz)} trip then. Choose another vehicle.");
        if (clashes.FirstOrDefault(x => x.DriverUserId == r.DriverUserId) is { } dc)
            throw ReservationService.Field(nameof(r.DriverUserId), $"{driverName} drives the {Hm(dc.DepartsAt, tz)} trip then. Choose another driver.");
        t.VehicleId = vehicle.Id;
        t.DriverUserId = r.DriverUserId;
        t.Capacity = (short)r.Capacity;
        t.Price = r.Price;
        t.Note = ReservationService.Clean(r.Note);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await TripDtosAsync(property, [await db.VehicleTrips.AsNoTracking().SingleAsync(x => x.Id == id, ct)], ct))[0];
    }

    /// <summary>A day's trips (default today) in departure order, with the seats left and what the caller can do.</summary>
    public async Task<TransportDayDto> DayAsync(Guid propertyId, DateOnly? date, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var today = property.LocalDate(clock.GetUtcNow());
        var day = date ?? today;
        var trips = await db.VehicleTrips.AsNoTracking().Where(t => t.PropertyId == propertyId && t.TripDate == day)
            .OrderBy(t => t.DepartsAt).ToListAsync(ct);
        var a = await access.GetAsync(ct);
        var drivers = a.Has(Perm.TransportManage, propertyId) ? await DriversAsync(property, ct) : [];
        return new TransportDayDto(day, today, await TripDtosAsync(property, trips, ct), await RouteDtosAsync(propertyId, ct),
            await VehicleDtosAsync(propertyId, ct), drivers);
    }

    public async Task<TripDetailDto> TripAsync(Guid propertyId, Guid id, TransportBookingService bookings, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var t = await db.VehicleTrips.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.PropertyId == propertyId, ct)
                ?? throw new NotFoundAppException("That trip doesn't exist at this property. Reload the page.");
        return (await DetailsAsync(property, [t], bookings, ct))[0];
    }

    /// <summary>The caller's own trips on a day (default today) with their passengers: the driver's phone view.</summary>
    public async Task<MyTripsDto> MyTripsAsync(Guid propertyId, DateOnly? date, TransportBookingService bookings, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var today = property.LocalDate(clock.GetUtcNow());
        var day = date ?? today;
        var me = caller.RequireUserId();
        var trips = await db.VehicleTrips.AsNoTracking()
            .Where(t => t.PropertyId == propertyId && t.TripDate == day && t.DriverUserId == me)
            .OrderBy(t => t.DepartsAt).ToListAsync(ct);
        return new MyTripsDto(day, today, await DetailsAsync(property, trips, bookings, ct));
    }

    /// <summary>The passenger manifest of a trip as an A4 PDF: who is booked, their room, seats and a box to tick.</summary>
    public async Task<(byte[] Bytes, string FileName)> ManifestPdfAsync(Guid propertyId, Guid id, TransportBookingService bookings, CancellationToken ct)
    {
        var property = await RequirePropertyAsync(propertyId, ct);
        var detail = await TripAsync(propertyId, id, bookings, ct);
        var t = detail.Trip;
        var number = $"{t.RouteCode}-{t.Date:yyyyMMdd}-{t.DepartTime.Replace(":", "", StringComparison.Ordinal)}";
        var live = detail.Passengers.Where(p => p.Status != PassengerStatuses.Cancelled).ToList();
        var rows = live.Select((p, i) => (IReadOnlyList<string>)
        [
            (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), p.Code, p.GuestName,
            p.Room is { } room ? $"{room}\n{p.ReservationReference}" : p.Settlement == TransportSettlements.Complimentary ? "Complimentary" : "Visitor",
            p.Seats.ToString(System.Globalization.CultureInfo.InvariantCulture), p.Note ?? "",
            p.Status == PassengerStatuses.Boarded ? "✔ on board" : p.Status == PassengerStatuses.NoShow ? "No-show" : "☐",
        ]).ToList();
        var address = new[] { property.AddressLine1, property.City, property.Phone is { } ph ? $"Phone {ph}" : null }.OfType<string>().ToList();
        var doc = new PrintDocument(
            "PASSENGER MANIFEST", $"{t.RouteName}: {t.Origin} → {t.Destination}", number, QuoteEngine.Day(t.Date),
            [
                ("Departs", $"{t.DepartTime} (arrives {t.ArriveTime})"),
                ("Vehicle", $"{t.VehicleName}, {t.Registration}"),
                ("Driver", t.DriverName ?? "Not chosen yet"),
                ("Seats", $"{t.SeatsBooked} of {t.Capacity} booked"),
                ("Status", TripStatuses.Words(t.Status)),
            ],
            new PrintParty("Operated by", [property.LegalName ?? property.Name, .. address]), null,
            [new PrintTable($"Passengers ({live.Count})",
                [new("#", 0.8), new("Ref", 1.8), new("Name", 4.2), new("Room / stay", 3.4), new("Seats", 1.2, true), new("Note", 4), new("Boarded", 2.6)],
                rows.Count > 0 ? rows : [["", "", "Nobody is booked on this trip.", "", "", "", ""]])],
            [("Seats booked", t.SeatsBooked.ToString(System.Globalization.CultureInfo.InvariantCulture), false),
             ("On board", t.OnBoard.ToString(System.Globalization.CultureInfo.InvariantCulture), true)],
            null,
            [$"Printed {TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Tz(property)):dd MMM yyyy HH:mm}. Cancelled seats are not listed.",
             "Driver: tick each person as they board; anyone not on board when the trip leaves is a no-show."],
            t.Status == TripStatuses.Cancelled ? $"CANCELLED: {t.CancelReason}" : null, null, $"manifest-{number}.pdf");
        return (pdf.Render(doc), doc.FileName);
    }

    // ------------------------------------------------------------------ shared

    internal async Task<Property> RequirePropertyAsync(Guid propertyId, CancellationToken ct) =>
        await db.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == propertyId, ct) ?? throw new NotFoundAppException("Property", propertyId);

    /// <summary>Takes the trip's row lock (bookings, boarding and changes of one trip queue here) and loads it, tracked.</summary>
    internal async Task<VehicleTrip> LockTripAsync(Guid propertyId, Guid id, CancellationToken ct, string field = "TripId")
    {
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM vehicle_trips WHERE id = {id} AND property_id = {propertyId} FOR UPDATE", ct);
        return await db.VehicleTrips.SingleOrDefaultAsync(t => t.Id == id && t.PropertyId == propertyId, ct)
               ?? throw ReservationService.Field(field, "That trip doesn't exist at this property. Reload the page and choose again.");
    }

    /// <summary>"Beach shuttle at 08:00 on Tue 3 Nov".</summary>
    internal async Task<string> LabelAsync(VehicleTrip t, Property p, CancellationToken ct)
    {
        var name = await db.TransportRoutes.AsNoTracking().Where(x => x.Id == t.RouteId).Select(x => x.Name).SingleAsync(ct);
        return Label(name, t, p);
    }

    internal static string Label(string routeName, VehicleTrip t, Property p) => $"{routeName} at {Hm(t.DepartsAt, Tz(p))} on {Short(t.TripDate)}";

    /// <summary>The caller can board, depart and complete this trip: they run trips here and either manage transport or drive it.</summary>
    internal static bool CanOperate(UserAccess a, Guid? me, VehicleTrip t) =>
        a.Has(Perm.TransportTripOperate, t.PropertyId) && (a.Has(Perm.TransportManage, t.PropertyId) || (me is not null && t.DriverUserId == me));

    internal async Task<List<TripDetailDto>> DetailsAsync(Property property, IReadOnlyCollection<VehicleTrip> trips, TransportBookingService bookings,
        CancellationToken ct)
    {
        if (trips.Count == 0) return [];
        var ids = trips.Select(t => t.Id).ToList();
        var passengers = await db.TripPassengers.AsNoTracking().Where(p => ids.Contains(p.TripId))
            .OrderBy(p => p.Status == PassengerStatuses.Cancelled ? 1 : 0).ThenBy(p => p.Code).ToListAsync(ct);
        var dtos = await TripDtosAsync(property, trips, ct);
        var all = await bookings.DtosAsync(property, passengers, ct);
        return dtos.Select(d => new TripDetailDto(d, all.Where(p => p.TripId == d.Id).ToList())).ToList();
    }

    internal async Task<List<TripDto>> TripDtosAsync(Property property, IReadOnlyCollection<VehicleTrip> trips, CancellationToken ct)
    {
        if (trips.Count == 0) return [];
        var routes = await db.TransportRoutes.AsNoTracking().Where(x => x.PropertyId == property.Id).ToDictionaryAsync(x => x.Id, ct);
        var vehicles = await db.Vehicles.AsNoTracking().Where(x => x.PropertyId == property.Id).ToDictionaryAsync(x => x.Id, ct);
        var users = await UserNamesAsync(trips.Select(t => t.DriverUserId), ct);
        var ids = trips.Select(t => t.Id).ToList();
        var onBoard = await db.TripPassengers.AsNoTracking().Where(p => ids.Contains(p.TripId) && p.Status == PassengerStatuses.Boarded)
            .GroupBy(p => p.TripId).Select(g => new { g.Key, Seats = g.Sum(p => p.Seats) }).ToDictionaryAsync(x => x.Key, x => x.Seats, ct);
        var a = await access.GetAsync(ct);
        var canBook = a.Has(Perm.TransportBookingCreate, property.Id);
        var canManage = a.Has(Perm.TransportManage, property.Id);
        var now = clock.GetUtcNow();
        var today = property.LocalDate(now);
        var tz = Tz(property);
        return trips.Select(t =>
        {
            var route = routes[t.RouteId];
            var vehicle = vehicles[t.VehicleId];
            var open = t.Status == TripStatuses.Scheduled;
            var operate = CanOperate(a, caller.UserId, t);
            var actions = new List<string>();
            if (open && now < t.DepartsAt && t.SeatsLeft > 0 && canBook) actions.Add("book");
            if (open && canManage) { actions.Add("edit"); actions.Add("cancel"); }
            if (open && operate) actions.Add("board");
            if (open && operate && today >= t.TripDate) actions.Add("depart");
            if (t.Status == TripStatuses.Departed && operate) actions.Add("complete");
            actions.Add("manifest");
            return new TripDto(t.Id, route.Id, route.Code, route.Name, route.Origin, route.Destination, t.TripDate, t.DepartsAt, t.ArrivesAt,
                Hm(t.DepartsAt, tz), Hm(t.ArrivesAt, tz), vehicle.Id, vehicle.Name, vehicle.Registration, t.DriverUserId,
                t.DriverUserId is { } d ? users.GetValueOrDefault(d) : null, t.Capacity, t.SeatsBooked, t.SeatsLeft, onBoard.GetValueOrDefault(t.Id),
                t.Price, t.Status, t.Note, t.CancelReason, t.DepartedAt, t.CompletedAt, actions, t.Version);
        }).ToList();
    }

    /// <summary>Active staff accounts of the organisation that can run trips at this property; those with the Driver role first.</summary>
    internal async Task<List<DriverDto>> DriversAsync(Property property, CancellationToken ct)
    {
        var grants = await (from ur in db.UserRoles
                            join rp in db.RolePermissions on ur.RoleId equals rp.RoleId
                            join role in db.Roles on ur.RoleId equals role.Id
                            where rp.PermissionCode == Perm.TransportTripOperate && (ur.PropertyId == null || ur.PropertyId == property.Id)
                            select new { ur.UserId, role.Code }).ToListAsync(ct);
        var ids = grants.Select(g => g.UserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id) && u.OrganizationId == property.OrganizationId && u.IsActive)
            .Select(u => new { u.Id, u.FullName }).ToListAsync(ct);
        var drivers = grants.Where(g => g.Code == "DRIVER").Select(g => g.UserId).ToHashSet();
        return users.Select(u => new DriverDto(u.Id, u.FullName, drivers.Contains(u.Id)))
            .OrderByDescending(d => d.IsDriver).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<Vehicle> RequireVehicleAsync(Guid propertyId, Guid id, string field, CancellationToken ct)
    {
        var v = await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.PropertyId == propertyId, ct)
                ?? throw ReservationService.Field(field, "That vehicle isn't at this property. Reload and choose again.");
        if (!v.IsActive) throw ReservationService.Field(field, $"{v.Name} is switched off. Choose another vehicle or switch it on first.");
        return v;
    }

    /// <summary>The driver's name, when they can run trips here; otherwise the reason under the field.</summary>
    private async Task<string> RequireDriverAsync(Property property, Guid userId, string field, CancellationToken ct)
    {
        var drivers = await DriversAsync(property, ct);
        if (drivers.FirstOrDefault(d => d.UserId == userId) is { } d) return d.Name;
        var name = await db.Users.AsNoTracking().Where(u => u.Id == userId && u.OrganizationId == property.OrganizationId).Select(u => u.FullName)
            .FirstOrDefaultAsync(ct);
        throw ReservationService.Field(field, name is null
            ? "That driver isn't a staff account here. Reload and choose again."
            : $"{name} can't run trips at {property.Name} (no Driver role here, or the account is switched off). Give them the Driver role first.");
    }

    internal async Task<Dictionary<Guid, string>> UserNamesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.OfType<Guid>().Distinct().ToList();
        return list.Count == 0 ? [] : await db.Users.IgnoreQueryFilters().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
    }

    private async Task<List<VehicleDto>> VehicleDtosAsync(Guid propertyId, CancellationToken ct) =>
        (await db.Vehicles.AsNoTracking().Where(v => v.PropertyId == propertyId).OrderBy(v => v.SortOrder).ThenBy(v => v.Name).ToListAsync(ct))
        .Select(v => new VehicleDto(v.Id, v.Code, v.Name, v.Registration, v.Seats, v.Note, v.SortOrder, v.IsActive, v.Version)).ToList();

    private async Task<List<TransportRouteDto>> RouteDtosAsync(Guid propertyId, CancellationToken ct) =>
        (await db.TransportRoutes.AsNoTracking().Where(r => r.PropertyId == propertyId).OrderBy(r => r.SortOrder).ThenBy(r => r.Name).ToListAsync(ct))
        .Select(r => new TransportRouteDto(r.Id, r.Code, r.Name, r.Origin, r.Destination, r.DistanceKm, r.DurationMinutes, r.Price, r.SortOrder,
            r.IsActive, r.Version)).ToList();

    internal static TimeZoneInfo Tz(Property p) => ActivityService.Tz(p);
    internal static DateTimeOffset Instant(DateOnly day, TimeOnly time, TimeZoneInfo tz) => ActivityService.Instant(day, time, tz);
    internal static string Hm(DateTimeOffset at, TimeZoneInfo tz) => ActivityService.Hm(at, tz);
    private static string Hm(TimeOnly t) => t.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    internal static string Short(DateOnly d) => ActivityService.Short(d);
}
