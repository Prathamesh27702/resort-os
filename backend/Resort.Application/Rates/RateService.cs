using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Validation;
using Resort.Contracts;
using Resort.Domain.Accommodation;
using Resort.Domain.Rates;

namespace Resort.Application.Rates;

/// <summary>
/// Rate plans, the rate grid (plan × room type × night) and quotes for one property.
/// Every method takes the route's propertyId: the permission policy has already checked the caller's grant for that
/// property, and every query here is filtered by it, so an id from another property returns 404.
/// Every price change is written to audit_logs with its old and new values (RatePlanPrice is [IAudited]).
/// </summary>
public sealed class RateService(IAppDbContext db, ICurrentUser caller, IValidator<RateQuoteQuery> quoteValidator)
{
    public const int MaxGridNights = 366;

    // ------------------------------------------------------------------ reservation sources

    public async Task<IReadOnlyList<ReservationSourceDto>> ListSourcesAsync(Guid propertyId, CancellationToken ct)
    {
        await RequirePropertyAsync(propertyId, ct);
        var orgId = caller.RequireOrganizationId();
        return await db.ReservationSources
            .Where(s => s.IsActive && (s.OrganizationId == null || s.OrganizationId == orgId))
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .Select(s => new ReservationSourceDto(s.Id, s.Code, s.Name, s.Channel, s.CommissionPercent))
            .ToListAsync(ct);
    }

    // ------------------------------------------------------------------ rate plans

    public async Task<IReadOnlyList<RatePlanDto>> ListPlansAsync(Guid propertyId, CancellationToken ct)
    {
        await RequirePropertyAsync(propertyId, ct);
        var plans = await db.RatePlans.Where(p => p.PropertyId == propertyId)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name).ToListAsync(ct);
        return plans.Select(ToDto).ToList();
    }

    public async Task<RatePlanDto> GetPlanAsync(Guid propertyId, Guid id, CancellationToken ct) =>
        ToDto(await LoadPlanAsync(propertyId, id, ct));

    public async Task<RatePlanDto> CreatePlanAsync(Guid propertyId, CreateRatePlanRequest r, CancellationToken ct)
    {
        await RequirePropertyAsync(propertyId, ct);
        var code = r.Code.Trim().ToUpperInvariant();
        if (await db.RatePlans.AnyAsync(p => p.PropertyId == propertyId && p.Code == code, ct))
            throw new ConflictAppException("RATE_PLAN_CODE_TAKEN", $"A rate plan with code '{code}' already exists at this property.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var plan = new RatePlan { OrganizationId = caller.RequireOrganizationId(), PropertyId = propertyId, Code = code };
        Apply(plan, r.Name, r.Description, r.MealPlan, r.SortOrder, isActive: true);
        // The first plan of a property becomes the default, so quotes work straight away.
        var isDefault = r.IsDefault || !await db.RatePlans.AnyAsync(p => p.PropertyId == propertyId && p.IsDefault, ct);
        if (isDefault) await ClearDefaultAsync(propertyId, ct);
        plan.IsDefault = isDefault;
        db.RatePlans.Add(plan);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(plan);
    }

    public async Task<RatePlanDto> UpdatePlanAsync(Guid propertyId, Guid id, UpdateRatePlanRequest r, CancellationToken ct)
    {
        var plan = await LoadPlanAsync(propertyId, id, ct);
        ConcurrencyGuard.ExpectVersion(db, plan, r.Version);

        if (plan.IsDefault && !r.IsDefault)
            throw Field(nameof(UpdateRatePlanRequest.IsDefault),
                $"{plan.Name} is the default plan that quotes use. Make another plan the default instead; that clears this one.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (r.IsDefault && !plan.IsDefault) await ClearDefaultAsync(propertyId, ct);

        Apply(plan, r.Name, r.Description, r.MealPlan, r.SortOrder, r.IsActive);
        plan.IsDefault = r.IsDefault;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(plan);
    }

    // ------------------------------------------------------------------ rate grid

    /// <summary>The plan's prices for nights in [from, to), for every room type that has any.</summary>
    public async Task<IReadOnlyList<RatePriceDto>> GetPricesAsync(Guid propertyId, Guid planId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var plan = await LoadPlanAsync(propertyId, planId, ct);
        var failures = new List<ValidationFailure>();
        if (from is null) failures.Add(new("From", "Enter the first night to show (from=YYYY-MM-DD)."));
        if (to is null) failures.Add(new("To", "Enter the day after the last night to show (to=YYYY-MM-DD)."));
        if (from is not null && to is not null)
        {
            if (to <= from) failures.Add(new("To", "The end date must be after the start date."));
            else if (to.Value.DayNumber - from.Value.DayNumber > MaxGridNights)
                failures.Add(new("To", $"The grid can show at most {MaxGridNights} nights at a time; this asks for {to.Value.DayNumber - from.Value.DayNumber}."));
        }
        if (failures.Count > 0) throw new ValidationException(failures);

        return await db.RatePlanPrices
            .Where(x => x.RatePlanId == plan.Id && x.StayDate >= from!.Value && x.StayDate < to!.Value)
            .OrderBy(x => x.AccommodationTypeId).ThenBy(x => x.StayDate)
            .Select(x => new RatePriceDto(x.AccommodationTypeId, x.StayDate, x.Price, x.ExtraAdult, x.ExtraChild, x.MinStay, x.UpdatedAt))
            .ToListAsync(ct);
    }

    /// <summary>Bulk edit: the same values on every chosen night for every chosen room type. Creates missing cells,
    /// changes the ones that differ (each change audited with old and new values) and leaves equal ones alone.</summary>
    public async Task<SetRatePricesResult> SetPricesAsync(Guid propertyId, Guid planId, SetRatePricesRequest r, CancellationToken ct)
    {
        var plan = await LoadPlanAsync(propertyId, planId, ct);
        var typeIds = r.AccommodationTypeIds.Distinct().ToList();
        var types = await db.AccommodationTypes.Where(t => t.PropertyId == propertyId && typeIds.Contains(t.Id)).ToListAsync(ct);
        if (types.Count != typeIds.Count)
            throw Field(nameof(SetRatePricesRequest.AccommodationTypeIds),
                $"{typeIds.Count - types.Count} of the chosen room types don't exist at this property. Reload the page and choose again.");

        var dorm = types.FirstOrDefault(t => t.IsBedBased);
        if (dorm is not null && (r.ExtraAdult != 0 || r.ExtraChild != 0))
            throw Field(r.ExtraAdult != 0 ? nameof(SetRatePricesRequest.ExtraAdult) : nameof(SetRatePricesRequest.ExtraChild),
                $"{dorm.Name} is sold per bed, so extra adult and child prices don't apply to it. Set them to 0, or change {dorm.Name} separately.");

        var days = r.DaysOfWeek is { Count: > 0 } d ? d.ToHashSet() : null;
        var nights = Enumerable.Range(0, r.To.DayNumber - r.From.DayNumber).Select(i => r.From.AddDays(i))
            .Where(n => days is null || days.Contains(n.DayOfWeek)).ToList();
        if (nights.Count == 0)
            throw Field(nameof(SetRatePricesRequest.DaysOfWeek),
                $"None of the nights from {QuoteEngine.Day(r.From)} to {QuoteEngine.Day(r.To.AddDays(-1))} falls on {string.Join(" or ", days!)}.");

        var existing = await db.RatePlanPrices
            .Where(x => x.RatePlanId == plan.Id && typeIds.Contains(x.AccommodationTypeId) && x.StayDate >= r.From && x.StayDate < r.To)
            .ToDictionaryAsync(x => (x.AccommodationTypeId, x.StayDate), ct);

        int created = 0, changed = 0, unchanged = 0;
        foreach (var (typeId, night) in typeIds.SelectMany(t => nights.Select(n => (t, n))))
        {
            if (existing.TryGetValue((typeId, night), out var cell))
            {
                if (cell.Price == r.Price && cell.ExtraAdult == r.ExtraAdult && cell.ExtraChild == r.ExtraChild && cell.MinStay == r.MinStay)
                {
                    unchanged++;
                    continue;
                }
                changed++;
            }
            else
            {
                cell = new RatePlanPrice
                {
                    OrganizationId = plan.OrganizationId,
                    PropertyId = propertyId,
                    RatePlanId = plan.Id,
                    AccommodationTypeId = typeId,
                    StayDate = night,
                };
                db.RatePlanPrices.Add(cell);
                created++;
            }
            cell.Price = r.Price;
            cell.ExtraAdult = r.ExtraAdult;
            cell.ExtraChild = r.ExtraChild;
            cell.MinStay = r.MinStay;
        }
        await db.SaveChangesAsync(ct);
        return new SetRatePricesResult(created, changed, unchanged);
    }

    // ------------------------------------------------------------------ quote

    public async Task<RateQuoteDto> QuoteAsync(Guid propertyId, RateQuoteQuery q, CancellationToken ct)
    {
        await quoteValidator.ValidateAndThrowAsync(q, ct);
        var property = await db.Properties.SingleOrDefaultAsync(p => p.Id == propertyId, ct)
                       ?? throw new NotFoundAppException("Property", propertyId);

        AccommodationUnit? room = null;
        AccommodationType type;
        if (!string.IsNullOrWhiteSpace(q.Room))
        {
            var key = q.Room.Trim();
            var byId = Guid.TryParse(key, out var roomId);
            var code = key.ToUpperInvariant();
            room = await db.AccommodationUnits.Include(u => u.Type)
                       .SingleOrDefaultAsync(u => u.PropertyId == propertyId && (byId ? u.Id == roomId : u.Code == code), ct)
                   ?? throw Field(nameof(RateQuoteQuery.Room), $"There is no room '{key}' at this property.");
            if (!room.IsActive)
                throw new BusinessRuleAppException("ROOM_INACTIVE", $"{room.Name} is inactive, so it can't be quoted.");
            type = room.Type;
        }
        else
        {
            var key = q.Type!.Trim();
            var byId = Guid.TryParse(key, out var typeId);
            var code = key.ToUpperInvariant();
            type = await db.AccommodationTypes
                       .SingleOrDefaultAsync(t => t.PropertyId == propertyId && (byId ? t.Id == typeId : t.Code == code), ct)
                   ?? throw Field(nameof(RateQuoteQuery.Type), $"There is no room type '{key}' at this property.");
        }
        if (!type.IsActive)
            throw new BusinessRuleAppException("TYPE_INACTIVE", $"Room type '{type.Name}' is inactive, so it can't be quoted.");

        RatePlan plan;
        if (!string.IsNullOrWhiteSpace(q.RatePlan))
        {
            var key = q.RatePlan.Trim();
            var byId = Guid.TryParse(key, out var planId);
            var code = key.ToUpperInvariant();
            plan = await db.RatePlans
                       .SingleOrDefaultAsync(p => p.PropertyId == propertyId && (byId ? p.Id == planId : p.Code == code), ct)
                   ?? throw Field(nameof(RateQuoteQuery.RatePlan), $"There is no rate plan '{key}' at this property.");
            if (!plan.IsActive)
                throw new BusinessRuleAppException("RATE_PLAN_INACTIVE", $"Rate plan '{plan.Name}' is inactive, so it can't be quoted.");
        }
        else
        {
            plan = await db.RatePlans.SingleOrDefaultAsync(p => p.PropertyId == propertyId && p.IsDefault, ct)
                   ?? throw new BusinessRuleAppException("NO_DEFAULT_RATE_PLAN",
                       "This property has no default rate plan. Mark a plan as the default in Settings → Rates, or name the plan to quote.");
        }

        var from = q.From!.Value;
        var to = q.To!.Value;
        var grid = await db.RatePlanPrices
            .Where(x => x.RatePlanId == plan.Id && x.AccommodationTypeId == type.Id && x.StayDate >= from && x.StayDate < to)
            .ToDictionaryAsync(x => x.StayDate, x => x.ToNightRate(), ct);

        var input = new QuoteInput(plan.Name, type.Name, type.InventoryMode, type.BaseOccupancy, type.MaxOccupancy,
            from, to, q.Adults!.Value, q.Children ?? 0);
        var result = QuoteEngine.Price(input, grid);
        if (result.Problem is { } p)
            throw new BusinessRuleAppException(p.Code, p.Message, p.Dates.Count > 0 ? new { dates = p.Dates } : null);

        var units = result.Nights[0].Units;
        return new RateQuoteDto(plan.Id, plan.Code, plan.Name, plan.MealPlan, type.Id, type.Name, type.InventoryMode, room?.Code,
            from, to, input.Nights, input.Adults, input.Children, units,
            result.Nights.Select(n => new RateQuoteNightDto(n.Date, n.Units, n.UnitPrice, n.BaseAmount, n.ExtraAdults, n.ExtraAdultRate,
                n.ExtraAdultAmount, n.ExtraChildren, n.ExtraChildRate, n.ExtraChildAmount, n.Total)).ToList(),
            result.Total, property.Currency);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A request that passed the field rules but is wrong for this property's data: still reported under the
    /// field it concerns (400 VALIDATION_FAILED), so the form shows the reason next to the input.</summary>
    internal static ValidationException Field(string field, string message) => new([new ValidationFailure(field, message)]);

    private async Task RequirePropertyAsync(Guid propertyId, CancellationToken ct)
    {
        if (!await db.Properties.AnyAsync(p => p.Id == propertyId, ct))
            throw new NotFoundAppException("Property", propertyId);
    }

    private async Task<RatePlan> LoadPlanAsync(Guid propertyId, Guid id, CancellationToken ct) =>
        await db.RatePlans.SingleOrDefaultAsync(p => p.Id == id && p.PropertyId == propertyId, ct)
        ?? throw new NotFoundAppException("Rate plan", id);

    private async Task ClearDefaultAsync(Guid propertyId, CancellationToken ct)
    {
        var current = await db.RatePlans.Where(p => p.PropertyId == propertyId && p.IsDefault).ToListAsync(ct);
        foreach (var p in current) p.IsDefault = false;
        // The partial unique index allows one default per property, and EF orders updates arbitrarily: clear first
        // (inside the caller's transaction, so a failure later leaves the old default in place).
        if (current.Count > 0) await db.SaveChangesAsync(ct);
    }

    private static void Apply(RatePlan p, string name, string? description, string mealPlan, int sortOrder, bool isActive)
    {
        p.Name = name.Trim();
        p.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        p.MealPlan = mealPlan;
        p.SortOrder = sortOrder;
        p.IsActive = isActive;
    }

    private static RatePlanDto ToDto(RatePlan p) =>
        new(p.Id, p.Code, p.Name, p.Description, p.MealPlan, p.IsDefault, p.SortOrder, p.IsActive, p.Version);
}
