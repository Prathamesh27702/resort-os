using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Domain.Folios;
using Resort.Domain.Organizations;
using Resort.Domain.Rates;
using Resort.Domain.Tax;

namespace Resort.Application.Tax;

/// <summary>
/// What the tax engine found for one charge. <see cref="Lines"/> are the taxes to store (empty when no GST applies, and
/// then <see cref="NoTaxReason"/> says why). <see cref="RuleMissing"/>: the property is registered and the charge's category
/// is taxed, but no rule covers the day — posting such a charge is refused rather than charged without GST.
/// </summary>
public sealed record TaxOutcome(
    DateOnly Day, GstRegistration? Registration, TaxCategory? Category, TaxRule? Rule, string? PlaceOfSupply, bool IntraState,
    IReadOnlyList<(TaxRuleSlab Slab, TaxLine Tax)> Lines, string? NoTaxReason, bool RuleMissing)
{
    public decimal Total => Lines.Sum(l => l.Tax.Amount);
    public bool Taxed => NoTaxReason is null;
}

/// <summary>
/// The one tax engine (brief §84, §86): every module that charges a guest asks it for the GST of a line — the folio now,
/// POS, purchasing and invoices later. Nothing about a rate is in the code: the registration, the category and the rule
/// effective on the day of the service are read from the database.
///
/// Queries ignore the tenant filter and filter by the property's organisation instead: the nightly room charge job runs
/// with no signed-in user.
/// </summary>
public sealed class TaxCalculator(IAppDbContext db)
{
    private readonly Dictionary<Guid, Property> properties = [];

    /// <summary>
    /// The GST on a charge of <paramref name="chargeCategory"/> (or of the tax category <paramref name="taxCategoryId"/>)
    /// served on <paramref name="day"/>. <paramref name="placeOfSupply"/>: the state where it is taxed; null = the
    /// property's own state (accommodation, and anything consumed at the property), which makes it CGST + SGST.
    /// </summary>
    public async Task<TaxOutcome> CalculateAsync(Guid propertyId, string? chargeCategory, Guid? taxCategoryId, DateOnly day,
        IReadOnlyList<TaxablePart> parts, string? placeOfSupply, CancellationToken ct)
    {
        var property = await PropertyAsync(propertyId, ct);
        var category = taxCategoryId is { } cid
            ? await db.TaxCategories.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(c => c.Id == cid && c.OrganizationId == property.OrganizationId, ct)
            : await db.TaxCategories.IgnoreQueryFilters().AsNoTracking()
                .SingleOrDefaultAsync(c => c.OrganizationId == property.OrganizationId && c.ChargeCategory == chargeCategory, ct);
        var rule = category is null ? null
            : await db.TaxRules.IgnoreQueryFilters().AsNoTracking().Include(r => r.Slabs)
                .SingleOrDefaultAsync(r => r.TaxCategoryId == category.Id && r.EffectiveFrom <= day && (r.EffectiveTo == null || r.EffectiveTo >= day), ct);
        var registration = await db.GstRegistrations.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(r => r.PropertyId == property.Id && r.EffectiveFrom <= day && (r.EffectiveTo == null || r.EffectiveTo >= day), ct);

        var supplierState = registration?.StateCode ?? property.GstStateCode;
        var pos = string.IsNullOrWhiteSpace(placeOfSupply) ? supplierState : placeOfSupply.Trim();
        var intra = pos is null || pos == supplierState;
        TaxOutcome None(string reason, bool missing = false) => new(day, registration, category, rule, pos, intra, [], reason, missing);

        var what = category?.Name ?? (chargeCategory is null ? "This" : ChargeCategories.Words(chargeCategory));
        if (category is null)
            return None($"{what} charges carry no GST: no tax category covers them. Add one in Settings → GST if they should be taxed.");
        if (registration is null)
            return None($"{property.Name} has no GST registration on {QuoteEngine.Day(day)}, so it charges no GST. Add its GSTIN in Settings → GST.");
        if (registration.RegistrationType == RegistrationTypes.Composition)
            return None($"{property.Name} is under the composition scheme on {QuoteEngine.Day(day)} ({registration.Gstin}): it charges guests no GST.");
        if (rule is null)
            return None($"There is no GST rule for {category.Name} on {QuoteEngine.Day(day)}. Ask your accountant to add one in Settings → GST.", missing: true);

        return new TaxOutcome(day, registration, category, rule, pos, intra, TaxMath.ForLine(rule, parts, intra), null, false);
    }

    /// <summary>The taxes of an outcome as folio tax rows of <paramref name="item"/> (not added).</summary>
    public static IEnumerable<FolioItemTax> Rows(TaxOutcome outcome, FolioItem item, DateTimeOffset now) =>
        outcome.Lines.Select(l => new FolioItemTax
        {
            OrganizationId = item.OrganizationId,
            PropertyId = item.PropertyId,
            FolioItemId = item.Id,
            TaxRuleId = outcome.Rule!.Id,
            TaxCode = l.Tax.Code,
            Rate = l.Tax.Rate,
            TaxableValue = l.Tax.TaxableValue,
            Amount = l.Tax.Amount,
            CreatedAt = now,
        });

    private async Task<Property> PropertyAsync(Guid id, CancellationToken ct)
    {
        if (properties.TryGetValue(id, out var p)) return p;
        p = await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new NotFoundAppException("Property", id);
        return properties[id] = p;
    }
}
