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
/// What can be sold for a stay: per room type, how many rooms (or dorm beds) are free for every night, whether the
/// guests fit, and the default plan's price. A read-only snapshot (no locks): booking checks again under locks.
/// </summary>
public sealed class AvailabilityService(IAppDbContext db, TimeProvider clock)
{
    public async Task<AvailabilityDto> GetAsync(Guid propertyId, DateOnly? from, DateOnly? to, int? adults, int? children, CancellationToken ct)
    {
        var property = await db.Properties.SingleOrDefaultAsync(p => p.Id == propertyId, ct)
                       ?? throw new NotFoundAppException("Property", propertyId);
        var today = property.LocalDate(clock.GetUtcNow());

        var failures = new List<ValidationFailure>();
        if (from is null) failures.Add(new("From", "Enter the check-in date (from=YYYY-MM-DD)."));
        else if (from < today) failures.Add(new("From", $"Check-in can't be before today ({QuoteEngine.Day(today)} at the property)."));
        if (to is null) failures.Add(new("To", "Enter the check-out date (to=YYYY-MM-DD)."));
        if (from is not null && to is not null)
        {
            if (to <= from) failures.Add(new("To", "The check-out date must be after check-in."));
            else if (to.Value.DayNumber - from.Value.DayNumber > ReservationService.MaxNights)
                failures.Add(new("To", $"A stay can be at most {ReservationService.MaxNights} nights; this one is {to.Value.DayNumber - from.Value.DayNumber}."));
        }
        if (adults is null or < 1) failures.Add(new("Adults", "At least one adult is needed."));
        else if (adults > 200) failures.Add(new("Adults", "At most 200 adults in one search."));
        if (children is < 0 or > 200) failures.Add(new("Children", "Children must be between 0 and 200."));
        if (failures.Count > 0) throw new ValidationException(failures);

        var f = from!.Value;
        var t = to!.Value;
        var a = adults!.Value;
        var c = children ?? 0;

        var types = await db.AccommodationTypes.AsNoTracking().Where(x => x.PropertyId == propertyId && x.IsActive)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ToListAsync(ct);
        var inventory = await InventoryLoader.UnitsAsync(db, propertyId, null, ct);
        var taken = await InventoryLoader.TakenAsync(db, inventory.Select(u => u.UnitId).ToList(), f, t, null, ct);
        var free = AvailabilityEngine.Free(inventory, taken, f, t);

        var plan = await db.RatePlans.AsNoTracking().SingleOrDefaultAsync(p => p.PropertyId == propertyId && p.IsDefault, ct);
        var grid = plan is null ? [] : await db.RatePlanPrices.AsNoTracking()
            .Where(x => x.RatePlanId == plan.Id && x.StayDate >= f && x.StayDate < t).ToListAsync(ct);
        var units = await db.AccommodationUnits.AsNoTracking().Where(u => u.PropertyId == propertyId).ToDictionaryAsync(u => u.Id, ct);
        var guests = a + c;

        var rows = types.Select(type =>
        {
            var bedBased = type.IsBedBased;
            var total = bedBased
                ? inventory.Where(u => u.TypeId == type.Id && u.IsActive).Sum(u => u.Beds.Count(b => b.IsActive))
                : inventory.Count(u => u.TypeId == type.Id && u.IsActive);
            var freeCount = free.FreeCount(type.Id, type.InventoryMode);

            string? fit = null;
            if (freeCount == 0)
                fit = total == 0 ? $"{type.Name} has no {(bedBased ? "beds" : "rooms")} set up." : "Fully booked or blocked for these nights.";
            else if (bedBased && freeCount < guests)
                fit = $"Only {freeCount} bed{(freeCount == 1 ? " is" : "s are")} free; {guests} guests need {guests}.";
            else if (!bedBased && guests > type.MaxOccupancy)
            {
                var rooms = (int)Math.Ceiling(guests / (double)type.MaxOccupancy);
                fit = $"Sleeps at most {type.MaxOccupancy}; {guests} guests need {rooms} rooms" +
                      (freeCount >= rooms ? ": book each room as its own booking." : $", and only {freeCount} {(freeCount == 1 ? "is" : "are")} free.");
            }

            decimal? price = null;
            string? priceProblem = null;
            if (plan is null)
                priceProblem = "No default rate plan. Mark one as the default in Settings → Rates.";
            else
            {
                // Price one room for everyone if they fit, else one room at its maximum occupancy.
                var (qa, qc) = bedBased || guests <= type.MaxOccupancy ? (a, c) : (Math.Min(a, type.MaxOccupancy), Math.Min(c, Math.Max(0, type.MaxOccupancy - a)));
                var quote = QuoteEngine.Price(new QuoteInput(plan.Name, type.Name, type.InventoryMode, type.BaseOccupancy, type.MaxOccupancy,
                    f, t, qa, qc), grid.Where(g => g.AccommodationTypeId == type.Id).ToDictionary(g => g.StayDate, g => g.ToNightRate()));
                if (quote.Ok) price = quote.Total;
                else priceProblem = quote.Problem!.Message;
            }

            var freeRooms = bedBased ? [] : free.FreeRooms.Where(u => u.TypeId == type.Id)
                .Select(u => new AvailableRoomDto(u.UnitId, u.Code, u.Name, units[u.UnitId].Floor)).ToList();
            return new AvailabilityTypeDto(type.Id, type.Code, type.Name, type.InventoryMode, type.BaseOccupancy, type.MaxOccupancy,
                total, freeCount, fit is null, fit, plan?.Id, plan?.Name, price, priceProblem, freeRooms);
        }).ToList();

        return new AvailabilityDto(f, t, t.DayNumber - f.DayNumber, a, c, property.Currency, rows);
    }
}
