using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Reservations;
using Resort.Contracts;
using Resort.Domain.Folios;
using Resort.Domain.Organizations;
using Resort.Domain.Rates;
using Resort.Domain.Tax;

namespace Resort.Application.Tax;

/// <summary>
/// GST configuration (Sprint 9): a property's GST registrations, the organisation's tax categories and their rules, and a
/// preview of the tax on a charge. Rules are data with the dates they apply: the accountant adds next month's rule here and
/// it takes effect on its first day, with no deployment. Nothing is deleted, and a saved rule changes only its end date
/// (when the next rule starts) and its verification.
/// </summary>
public sealed class TaxService(IAppDbContext db, ICurrentUser caller, CallerAccess access, TimeProvider clock, TaxCalculator calculator)
{
    /// <summary>GST is Indian law, so "today" for a rule is today in India (properties may sit in other time zones).</summary>
    public const string IndiaTimezone = "Asia/Kolkata";

    public DateOnly TodayInIndia => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(IndiaTimezone)).DateTime);

    // ------------------------------------------------------------------ registrations

    public async Task<IReadOnlyList<GstRegistrationDto>> ListRegistrationsAsync(Guid propertyId, CancellationToken ct)
    {
        var property = await LoadPropertyAsync(propertyId, ct);
        var today = property.LocalDate(clock.GetUtcNow());
        var list = await db.GstRegistrations.AsNoTracking().Where(r => r.PropertyId == propertyId).OrderByDescending(r => r.EffectiveFrom).ToListAsync(ct);
        return list.Select(r => ToDto(r, today)).ToList();
    }

    /// <summary>
    /// Adds a GSTIN to a property. It must be of the property's GST state (a property bills under the GSTIN of its own
    /// state; a property with no state yet takes the GSTIN's). A registration of the property that runs on past the new
    /// one's first day is ended the day before; any other overlap is refused.
    /// </summary>
    public async Task<GstRegistrationDto> CreateRegistrationAsync(Guid propertyId, CreateGstRegistrationRequest r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LoadPropertyAsync(propertyId, ct);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM properties WHERE id = {propertyId} FOR UPDATE", ct);
        var property = await db.Properties.SingleAsync(p => p.Id == propertyId, ct);

        var gstin = Gstin.Normalize(r.Gstin);
        var state = gstin[..2];
        if (property.GstStateCode is { } own && own != state)
            throw ReservationService.Field(nameof(r.Gstin),
                $"This GSTIN is registered in {GstStates.Words(state)}, but {property.Name} is in {GstStates.Words(own)}. A property bills under the GSTIN of its own state.");
        property.GstStateCode ??= state;

        var from = r.EffectiveFrom!.Value;
        var existing = await db.GstRegistrations.Where(x => x.PropertyId == propertyId).ToListAsync(ct);
        foreach (var e in existing.Where(e => Overlaps(e.EffectiveFrom, e.EffectiveTo, from, r.EffectiveTo)).OrderBy(e => e.EffectiveFrom))
        {
            if (e.EffectiveFrom < from && (r.EffectiveTo is null || (e.EffectiveTo is { } end && end <= r.EffectiveTo)))
            {
                e.EffectiveTo = from.AddDays(-1);   // the earlier registration ends when this one starts
                continue;
            }
            throw ReservationService.Field(nameof(r.EffectiveFrom),
                $"{property.Name} is already registered under {e.Gstin} {Period(e.EffectiveFrom, e.EffectiveTo)}, which overlaps. " +
                (e.EffectiveFrom >= from
                    ? $"Start this registration after {QuoteEngine.Day(e.EffectiveFrom)}."
                    : "A registration can only take over from an earlier one, not sit inside it."));
        }

        var reg = new GstRegistration
        {
            OrganizationId = property.OrganizationId,
            PropertyId = propertyId,
            Gstin = gstin,
            LegalName = r.LegalName!.Trim(),
            TradeName = ReservationService.Clean(r.TradeName),
            StateCode = state,
            RegistrationType = (r.RegistrationType ?? RegistrationTypes.Regular).Trim().ToUpperInvariant(),
            EffectiveFrom = from,
            EffectiveTo = r.EffectiveTo,
        };
        db.GstRegistrations.Add(reg);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(reg, property.LocalDate(clock.GetUtcNow()));
    }

    // ------------------------------------------------------------------ categories

    public async Task<IReadOnlyList<TaxCategoryDto>> ListCategoriesAsync(CancellationToken ct)
    {
        var categories = await db.TaxCategories.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
        var rules = await db.TaxRules.AsNoTracking().Include(r => r.Slabs).ToListAsync(ct);
        var dtos = (await ToDtosAsync(rules, categories, ct)).ToDictionary(d => d.Id);
        return categories.Select(c => ToDto(c, rules.Where(r => r.TaxCategoryId == c.Id).ToList(), dtos)).ToList();
    }

    public async Task<TaxCategoryDto> CreateCategoryAsync(CreateTaxCategoryRequest r, CancellationToken ct)
    {
        var orgId = caller.RequireOrganizationId();
        var code = r.Code!.Trim().ToUpperInvariant();
        var charge = string.IsNullOrWhiteSpace(r.ChargeCategory) ? null : r.ChargeCategory.Trim().ToUpperInvariant();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // one category creation at a time per organisation, so the two checks below can't race
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM organizations WHERE id = {orgId} FOR UPDATE", ct);
        if (await db.TaxCategories.AsNoTracking().SingleOrDefaultAsync(c => c.Code == code, ct) is { } same)
            throw ReservationService.Field(nameof(r.Code), $"The code {code} is already used by {same.Name}. Choose another code.");
        if (charge is not null && await db.TaxCategories.AsNoTracking().SingleOrDefaultAsync(c => c.ChargeCategory == charge, ct) is { } taken)
            throw ReservationService.Field(nameof(r.ChargeCategory),
                $"{ChargeCategories.Words(charge)} charges are already taxed under {taken.Name}. A charge category can belong to one tax category only.");

        var category = new TaxCategory
        {
            OrganizationId = orgId,
            Code = code,
            Name = r.Name!.Trim(),
            HsnSac = r.HsnSac!.Trim(),
            ChargeCategory = charge,
            Description = ReservationService.Clean(r.Description),
        };
        db.TaxCategories.Add(category);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(category, [], new Dictionary<Guid, TaxRuleDto>());
    }

    // ------------------------------------------------------------------ rules

    public async Task<IReadOnlyList<TaxRuleDto>> ListRulesAsync(Guid? categoryId, CancellationToken ct)
    {
        var q = db.TaxRules.AsNoTracking().Include(r => r.Slabs).AsQueryable();
        if (categoryId is { } id) q = q.Where(r => r.TaxCategoryId == id);
        var rules = await q.ToListAsync(ct);
        var categories = await db.TaxCategories.AsNoTracking().ToListAsync(ct);
        var names = categories.ToDictionary(c => c.Id, c => c.Name);
        return (await ToDtosAsync(rules, categories, ct))
            .OrderBy(d => names[d.TaxCategoryId]).ThenByDescending(d => d.EffectiveFrom).ToList();
    }

    /// <summary>
    /// Adds a rule from <c>EffectiveFrom</c> (today in India or later): charges already posted keep the rule they were
    /// posted under, so a rule never reaches back. The category's rule that runs on past that day is ended the day before.
    /// </summary>
    public async Task<CreateTaxRuleResultDto> CreateRuleAsync(CreateTaxRuleRequest r, CancellationToken ct)
    {
        var orgId = caller.RequireOrganizationId();
        var today = TodayInIndia;
        var from = r.EffectiveFrom!.Value;
        if (from < today)
            throw ReservationService.Field(nameof(r.EffectiveFrom),
                $"A new rule can start today ({QuoteEngine.Day(today)}) or later. Charges already posted keep the GST they were posted with; to change GST from a date, add a rule from that date.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var category = await db.TaxCategories.AsNoTracking().SingleOrDefaultAsync(c => c.Id == r.TaxCategoryId, ct)
                       ?? throw ReservationService.Field(nameof(r.TaxCategoryId), "That tax category doesn't exist. Reload the page and choose again.");
        // rules of one category are added one at a time, so the overlap check below can't race
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM tax_categories WHERE id = {category.Id} FOR UPDATE", ct);

        TaxRule? closed = null;
        var rules = await db.TaxRules.Where(x => x.TaxCategoryId == category.Id).ToListAsync(ct);
        foreach (var e in rules.Where(e => Overlaps(e.EffectiveFrom, e.EffectiveTo, from, r.EffectiveTo)).OrderBy(e => e.EffectiveFrom))
        {
            if (e.EffectiveFrom < from && (r.EffectiveTo is null || (e.EffectiveTo is { } end && end <= r.EffectiveTo)))
            {
                e.EffectiveTo = from.AddDays(-1);   // the rule that applied until now ends the day before the new one
                closed = e;
                continue;
            }
            throw ReservationService.Field(nameof(r.EffectiveFrom),
                $"{category.Name} already has a rule {Period(e.EffectiveFrom, e.EffectiveTo)}, which overlaps. " +
                (e.EffectiveFrom >= from
                    ? $"Start the new rule after {QuoteEngine.Day(e.EffectiveFrom)}, when that one starts."
                    : $"The new rule would sit inside it: add it with no end date, then add the rule that follows it."));
        }

        var type = r.RuleType!.Trim().ToUpperInvariant();
        var rule = new TaxRule
        {
            OrganizationId = orgId,
            TaxCategoryId = category.Id,
            EffectiveFrom = from,
            EffectiveTo = r.EffectiveTo,
            RuleType = type,
            Source = r.Source!.Trim(),
            Notes = ReservationService.Clean(r.Notes),
        };
        rule.Slabs = r.Slabs!.OrderBy(s => s.UpTo is null).ThenBy(s => s.UpTo).Select((s, i) => new TaxRuleSlab
        {
            OrganizationId = orgId,
            TaxRuleId = rule.Id,
            UpTo = s.UpTo,
            Rate = s.Rate,
            InputTaxCredit = s.InputTaxCredit,
            SortOrder = i,
            CreatedAt = clock.GetUtcNow(),
        }).ToList();
        db.TaxRules.Add(rule);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var categories = new List<TaxCategory> { category };
        var dtos = await ToDtosAsync(closed is null ? [rule] : [rule, closed], categories, ct);
        return new CreateTaxRuleResultDto(dtos[0], closed is null ? null : dtos[1]);
    }

    /// <summary>Records that the caller checked the rule against its notification (seeded rules start unverified).
    /// Verifying a verified rule changes nothing.</summary>
    public async Task<TaxRuleDto> VerifyRuleAsync(Guid id, CancellationToken ct)
    {
        var rule = await db.TaxRules.Include(r => r.Slabs).SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundAppException("Tax rule", id);
        if (rule.VerifiedAt is null)
        {
            rule.VerifiedAt = clock.GetUtcNow();
            rule.VerifiedBy = caller.UserId;
            await db.SaveChangesAsync(ct);
        }
        var category = await db.TaxCategories.AsNoTracking().SingleAsync(c => c.Id == rule.TaxCategoryId, ct);
        return (await ToDtosAsync([rule], [category], ct))[0];
    }

    // ------------------------------------------------------------------ preview

    /// <summary>What GST a charge would carry on a day, with the same engine the folio uses. Changes nothing.</summary>
    public async Task<TaxCalculationDto> CalculateAsync(TaxCalculationRequest r, CancellationToken ct)
    {
        var property = await db.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == r.PropertyId, ct)
                       ?? throw ReservationService.Field(nameof(r.PropertyId), "That property doesn't exist. Reload the page and choose again.");
        await access.RequireAsync(Perm.TaxRead, property.Id, ct);   // the endpoint only checks tax.read somewhere
        if (r.TaxCategoryId is { } cid && !await db.TaxCategories.AnyAsync(c => c.Id == cid, ct))
            throw ReservationService.Field(nameof(r.TaxCategoryId), "That tax category doesn't exist. Reload the page and choose again.");
        var day = r.Date ?? property.LocalDate(clock.GetUtcNow());
        var unit = r.UnitValue ?? r.Amount;
        var charge = r.TaxCategoryId is null ? r.ChargeCategory?.Trim().ToUpperInvariant() : null;
        var pos = string.IsNullOrWhiteSpace(r.PlaceOfSupply) ? null : r.PlaceOfSupply.Trim();
        var o = await calculator.CalculateAsync(property.Id, charge, r.TaxCategoryId, day, [new TaxablePart(r.Amount, unit)], pos, ct);

        var categories = o.Category is null ? new List<TaxCategory>() : [o.Category];
        var ruleDto = o.Rule is null ? null : (await ToDtosAsync([o.Rule], categories, ct))[0];
        var categoryDto = o.Category is null ? null
            : ToDto(o.Category, o.Rule is null ? [] : [o.Rule], ruleDto is null ? new Dictionary<Guid, TaxRuleDto>() : new() { [ruleDto.Id] = ruleDto });
        var slab = o.Rule is null ? null : TaxMath.SlabFor(o.Rule.Slabs, unit);
        var components = o.Lines.Select(l => new TaxComponentDto(l.Tax.Code, l.Tax.Rate, l.Tax.TaxableValue, l.Tax.Amount)).ToList();
        var supplier = o.Registration?.StateCode ?? property.GstStateCode;
        return new TaxCalculationDto(day, r.Amount, unit, o.Taxed, o.NoTaxReason, o.Registration?.Gstin,
            supplier is null ? null : GstStates.Words(supplier), o.PlaceOfSupply is null ? null : GstStates.Words(o.PlaceOfSupply), o.IntraState,
            categoryDto, ruleDto, slab is null ? null : new TaxSlabDto(slab.UpTo, slab.Rate, slab.InputTaxCredit),
            components, o.Total, r.Amount + o.Total);
    }

    // ------------------------------------------------------------------ helpers

    private static bool Overlaps(DateOnly aFrom, DateOnly? aTo, DateOnly bFrom, DateOnly? bTo) =>
        (aTo is null || aTo >= bFrom) && (bTo is null || bTo >= aFrom);

    /// <summary>"from Mon 22 Sep 2025 with no end date" / "from … to …".</summary>
    internal static string Period(DateOnly from, DateOnly? to) =>
        to is { } t ? $"from {QuoteEngine.Day(from)} to {QuoteEngine.Day(t)}" : $"from {QuoteEngine.Day(from)} with no end date";

    private async Task<Property> LoadPropertyAsync(Guid propertyId, CancellationToken ct) =>
        await db.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == propertyId, ct) ?? throw new NotFoundAppException("Property", propertyId);

    private static GstRegistrationDto ToDto(GstRegistration r, DateOnly today) =>
        new(r.Id, r.PropertyId, r.Gstin, r.LegalName, r.TradeName, r.StateCode, GstStates.Names.GetValueOrDefault(r.StateCode, r.StateCode),
            r.RegistrationType, r.EffectiveFrom, r.EffectiveTo, r.CoversDay(today));

    private TaxCategoryDto ToDto(TaxCategory c, List<TaxRule> rules, Dictionary<Guid, TaxRuleDto> dtos)
    {
        var current = rules.FirstOrDefault(r => r.CoversDay(TodayInIndia));
        return new TaxCategoryDto(c.Id, c.Code, c.Name, c.HsnSac, c.ChargeCategory, c.Description, rules.Count,
            current is null ? null : dtos.GetValueOrDefault(current.Id));
    }

    /// <summary>Rule DTOs in the order given, with category and user names.</summary>
    private async Task<List<TaxRuleDto>> ToDtosAsync(List<TaxRule> rules, List<TaxCategory> categories, CancellationToken ct)
    {
        var today = TodayInIndia;
        var names = categories.ToDictionary(c => c.Id, c => c.Name);
        var userIds = rules.SelectMany(r => new[] { r.VerifiedBy, r.CreatedBy }).OfType<Guid>().Distinct().ToList();
        var users = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        return rules.Select(r => new TaxRuleDto(r.Id, r.TaxCategoryId, names.GetValueOrDefault(r.TaxCategoryId, ""), r.EffectiveFrom, r.EffectiveTo,
            r.RuleType, r.Slabs.OrderBy(s => s.SortOrder).Select(s => new TaxSlabDto(s.UpTo, s.Rate, s.InputTaxCredit)).ToList(), r.Source, r.Notes,
            r.EffectiveFrom > today ? "FUTURE" : r.EffectiveTo is { } to && to < today ? "PAST" : "CURRENT",
            r.VerifiedAt, r.VerifiedBy is { } v ? users.GetValueOrDefault(v) : null,
            r.CreatedAt, r.CreatedBy is { } c ? users.GetValueOrDefault(c) : null)).ToList();
    }
}
