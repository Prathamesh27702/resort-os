namespace Resort.Contracts;

// ---------------------------------------------------------------- GST and the tax engine (Sprint 9)

/// <summary>A GSTIN a property bills under. EffectiveTo null = until further notice. Current: it covers today.</summary>
public sealed record GstRegistrationDto(
    Guid Id, Guid PropertyId, string Gstin, string LegalName, string? TradeName, string StateCode, string StateName,
    string RegistrationType, DateOnly EffectiveFrom, DateOnly? EffectiveTo, bool Current);

/// <summary>Adds a registration to a property. The GSTIN's state must be the property's GST state; periods of one
/// property can't overlap. RegistrationType REGULAR (charges GST) or COMPOSITION (charges none).</summary>
public sealed record CreateGstRegistrationRequest(
    string? Gstin, string? LegalName, string? TradeName, string? RegistrationType, DateOnly? EffectiveFrom, DateOnly? EffectiveTo = null);

/// <summary>What is taxed. ChargeCategory: the folio charge category it covers (ROOM, FOOD …), or null. CurrentRule:
/// the rule that applies today, if any.</summary>
public sealed record TaxCategoryDto(
    Guid Id, string Code, string Name, string HsnSac, string? ChargeCategory, string? Description, int Rules, TaxRuleDto? CurrentRule);

public sealed record CreateTaxCategoryRequest(string? Code, string? Name, string? HsnSac, string? ChargeCategory, string? Description);

/// <summary>A rate of a rule: values up to UpTo (included; null = no limit) pay Rate percent.</summary>
public sealed record TaxSlabDto(decimal? UpTo, decimal Rate, bool InputTaxCredit);

/// <summary>A category's rule from EffectiveFrom to EffectiveTo (both included; null = until further notice).
/// State: PAST, CURRENT or FUTURE (by today's date in India). Verified*: who confirmed it against the notification.</summary>
public sealed record TaxRuleDto(
    Guid Id, Guid TaxCategoryId, string CategoryName, DateOnly EffectiveFrom, DateOnly? EffectiveTo, string RuleType,
    IReadOnlyList<TaxSlabDto> Slabs, string Source, string? Notes, string State,
    DateTimeOffset? VerifiedAt, string? VerifiedByName, DateTimeOffset CreatedAt, string? CreatedByName);

/// <summary>
/// Adds a rule from EffectiveFrom (today or later). A rule of the category that runs on past that day is ended the day
/// before (said in ClosedRule of the result); any other overlap is refused. FLAT: one slab with UpTo null. SLAB: two or
/// more slabs, the last with UpTo null. Source: the notification the rule comes from.
/// </summary>
public sealed record CreateTaxRuleRequest(
    Guid TaxCategoryId, DateOnly? EffectiveFrom, DateOnly? EffectiveTo, string? RuleType, IReadOnlyList<TaxSlabDto>? Slabs,
    string? Source, string? Notes = null);

/// <summary>The new rule, and the rule it ended (its new end date), if any.</summary>
public sealed record CreateTaxRuleResultDto(TaxRuleDto Rule, TaxRuleDto? ClosedRule);

/// <summary>
/// A tax preview: what GST a charge would carry. Give the property and either the tax category or the folio charge
/// category. Amount: the taxable value (after discount). UnitValue: the value of one unit for one night, which picks the
/// slab (default: Amount). PlaceOfSupply: the state code where the supply is taxed (default: the property's state, which
/// is the place of supply of accommodation and of anything consumed at the property).
/// </summary>
public sealed record TaxCalculationRequest(
    Guid PropertyId, Guid? TaxCategoryId, string? ChargeCategory, DateOnly? Date, decimal Amount, decimal? UnitValue = null,
    string? PlaceOfSupply = null);

/// <summary>One tax: CGST 2.5% on ₹1,450.00 = ₹36.25.</summary>
public sealed record TaxComponentDto(string Code, decimal Rate, decimal TaxableValue, decimal Amount);

/// <summary>
/// The preview. Taxed false: no GST, and NoTaxReason says why (no registration on that day, composition scheme, no tax
/// category for the charge). Rule/Slab: what applied. IntraState: CGST+SGST (true) or IGST.
/// </summary>
public sealed record TaxCalculationDto(
    DateOnly Date, decimal Amount, decimal UnitValue, bool Taxed, string? NoTaxReason, string? Gstin, string? SupplierState,
    string? PlaceOfSupply, bool IntraState, TaxCategoryDto? Category, TaxRuleDto? Rule, TaxSlabDto? Slab,
    IReadOnlyList<TaxComponentDto> Components, decimal Tax, decimal Total);
