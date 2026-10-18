using Resort.Domain.Common;

namespace Resort.Domain.Tax;

/// <summary>How a property is registered for GST.</summary>
public static class RegistrationTypes
{
    /// <summary>Charges GST on its supplies (the normal case).</summary>
    public const string Regular = "REGULAR";
    /// <summary>Composition scheme: pays a flat tax itself and charges guests none (a bill of supply).</summary>
    public const string Composition = "COMPOSITION";
    public static readonly string[] All = [Regular, Composition];
}

/// <summary>FLAT: one rate. SLAB: the rate depends on the value of one unit (room or bed) for one night.</summary>
public static class TaxRuleTypes
{
    public const string Flat = "FLAT";
    public const string Slab = "SLAB";
    public static readonly string[] All = [Flat, Slab];
}

public static class TaxCodes
{
    public const string Cgst = "CGST";
    public const string Sgst = "SGST";
    public const string Igst = "IGST";
    public const string Cess = "CESS";
}

/// <summary>A GSTIN a property bills under, from <see cref="EffectiveFrom"/> to <see cref="EffectiveTo"/> (both days
/// included; null = until further notice). Periods of one property never overlap (database EXCLUDE). Never deleted.</summary>
public class GstRegistration : AuditableEntity, IPropertyScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Gstin { get; set; } = default!;
    public string LegalName { get; set; } = default!;
    public string? TradeName { get; set; }
    /// <summary>The first two digits of the GSTIN.</summary>
    public string StateCode { get; set; } = default!;
    public string RegistrationType { get; set; } = RegistrationTypes.Regular;
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }

    public bool CoversDay(DateOnly day) => EffectiveFrom <= day && (EffectiveTo is null || day <= EffectiveTo);
}

/// <summary>What is taxed (accommodation, restaurant service …) with its SAC/HSN code. <see cref="ChargeCategory"/> is
/// the folio charge category it covers; a charge in a category no tax category covers carries no GST.</summary>
public class TaxCategory : AuditableEntity, IOrganizationScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string HsnSac { get; set; } = default!;
    public string? ChargeCategory { get; set; }
    public string? Description { get; set; }
}

/// <summary>
/// A category's GST from <see cref="EffectiveFrom"/> to <see cref="EffectiveTo"/> (both included; null = until further
/// notice). Rules of one category never overlap. Once saved only the end date (closed when the next rule starts) and the
/// verification change (a database trigger enforces it), so a charge that used a rule can always be traced to it.
/// </summary>
public class TaxRule : AuditableEntity, IOrganizationScoped, IAudited
{
    public Guid OrganizationId { get; set; }
    public Guid TaxCategoryId { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public string RuleType { get; set; } = TaxRuleTypes.Flat;
    /// <summary>Where the rule comes from (the notification), so the accountant can check it.</summary>
    public string Source { get; set; } = default!;
    public string? Notes { get; set; }
    /// <summary>When the owner or accountant confirmed the rule (seeded rules start unverified).</summary>
    public DateTimeOffset? VerifiedAt { get; set; }
    public Guid? VerifiedBy { get; set; }
    public List<TaxRuleSlab> Slabs { get; set; } = [];

    public bool CoversDay(DateOnly day) => EffectiveFrom <= day && (EffectiveTo is null || day <= EffectiveTo);
}

/// <summary>A rate of a rule: values up to <see cref="UpTo"/> (included; null = no limit) pay <see cref="Rate"/>. A FLAT
/// rule has one slab with no limit. Immutable.</summary>
public class TaxRuleSlab : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; set; }
    public Guid TaxRuleId { get; set; }
    public decimal? UpTo { get; set; }
    public decimal Rate { get; set; }
    public bool InputTaxCredit { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One tax on a line: CGST 2.5% on ₹1,450 = ₹36.25.</summary>
public sealed record TaxLine(string Code, decimal Rate, decimal TaxableValue, decimal Amount);

/// <summary>A piece of a charge to tax: its value, and the value one unit for one night (or one item) is sold at, which
/// picks the slab. A room night of two rooms is two parts.</summary>
public sealed record TaxablePart(decimal Value, decimal UnitValue);

/// <summary>
/// GST arithmetic that needs no database (brief §86). Pure and unit-tested.
/// <list type="bullet">
/// <item>Slab: the first slab, in order of limit, whose limit is at or above the unit value (₹7,500.00 is in "up to
/// ₹7,500"; ₹7,500.01 is not).</item>
/// <item>Inside the supplier's state the rate is split evenly into CGST and SGST; across states it is all IGST.</item>
/// <item>Each tax is rounded to the paisa per line, halves away from zero (as PostgreSQL's round()), after adding up the
/// line's parts that pay the same rate.</item>
/// </list>
/// </summary>
public static class TaxMath
{
    /// <summary>The slab a unit value falls in. Slabs need not be sorted; exactly one has no limit.</summary>
    public static TaxRuleSlab SlabFor(IEnumerable<TaxRuleSlab> slabs, decimal unitValue)
    {
        var ordered = slabs.OrderBy(s => s.UpTo is null).ThenBy(s => s.UpTo).ToList();
        return ordered.FirstOrDefault(s => s.UpTo is null || unitValue <= s.UpTo)
               ?? throw new InvalidOperationException("A tax rule needs a slab with no upper limit.");
    }

    public static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>The taxes on <paramref name="taxableValue"/> at <paramref name="rate"/> percent.</summary>
    public static IReadOnlyList<TaxLine> Split(decimal taxableValue, decimal rate, bool intraState)
    {
        if (!intraState) return [new TaxLine(TaxCodes.Igst, rate, taxableValue, Round(taxableValue * rate / 100m))];
        var half = rate / 2m;
        var each = Round(taxableValue * half / 100m);
        return [new TaxLine(TaxCodes.Cgst, half, taxableValue, each), new TaxLine(TaxCodes.Sgst, half, taxableValue, each)];
    }

    /// <summary>The taxes on a line under <paramref name="rule"/>: each part takes its slab's rate, parts at the same
    /// rate are added up, then each tax is rounded once.</summary>
    public static IReadOnlyList<(TaxRuleSlab Slab, TaxLine Tax)> ForLine(TaxRule rule, IReadOnlyList<TaxablePart> parts, bool intraState) =>
        parts.GroupBy(p => SlabFor(rule.Slabs, p.UnitValue))
            .OrderBy(g => g.Key.UpTo is null).ThenBy(g => g.Key.UpTo)
            .SelectMany(g => Split(g.Sum(p => p.Value), g.Key.Rate, intraState).Select(t => (g.Key, t)))
            .ToList();

    /// <summary>A GST rate is split evenly into CGST and SGST, so half of it must still be whole hundredths.</summary>
    public static bool IsSplittable(decimal rate) => rate * 50m == decimal.Truncate(rate * 50m);
}

/// <summary>GSTIN format and check digit (GST rules: 2-digit state, 10-character PAN, entity number, 'Z', check digit).</summary>
public static class Gstin
{
    private const string Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public static string Normalize(string? gstin) => (gstin ?? "").Trim().Replace(" ", "").ToUpperInvariant();

    /// <summary>Null when <paramref name="gstin"/> is valid; otherwise what is wrong with it, in words.</summary>
    public static string? Problem(string? gstin)
    {
        var g = Normalize(gstin);
        if (g.Length == 0) return "Enter the GSTIN (15 characters, e.g. 30AABCT1234C1Z9).";
        if (g.Length != 15) return $"A GSTIN has 15 characters; this one has {g.Length}.";
        if (!char.IsAsciiDigit(g[0]) || !char.IsAsciiDigit(g[1])) return "A GSTIN starts with the 2-digit state code, e.g. 30 for Goa.";
        if (!GstStates.Names.ContainsKey(g[..2])) return $"{g[..2]} isn't a GST state code.";
        var pan = g[2..12];
        if (!pan[..5].All(char.IsAsciiLetterUpper) || !pan[5..9].All(char.IsAsciiDigit) || !char.IsAsciiLetterUpper(pan[9]))
            return $"Characters 3 to 12 are the PAN (5 letters, 4 digits, 1 letter); \"{pan}\" isn't one.";
        if (g[12] == '0' || Chars.IndexOf(g[12]) < 0) return "Character 13 (the registration number for this PAN in the state) must be 1–9 or A–Z.";
        if (g[13] != 'Z') return "Character 14 of a GSTIN is always Z.";
        if (Chars.IndexOf(g[14]) < 0) return "The last character must be a letter or a digit.";
        var check = CheckDigit(g);
        return g[14] == check ? null : $"The last character (check digit) should be {check} for this GSTIN; check it for a typing mistake.";
    }

    /// <summary>The check digit of the first 14 characters (mod-36 weighted sum).</summary>
    public static char CheckDigit(string gstin)
    {
        var sum = 0;
        for (var i = 0; i < 14; i++)
        {
            var product = Chars.IndexOf(gstin[i]) * (i % 2 == 0 ? 1 : 2);
            sum += product / 36 + product % 36;
        }
        return Chars[(36 - sum % 36) % 36];
    }
}

/// <summary>GST state and union territory codes (first two digits of a GSTIN; also the place of supply).</summary>
public static class GstStates
{
    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        ["01"] = "Jammu and Kashmir",
        ["02"] = "Himachal Pradesh",
        ["03"] = "Punjab",
        ["04"] = "Chandigarh",
        ["05"] = "Uttarakhand",
        ["06"] = "Haryana",
        ["07"] = "Delhi",
        ["08"] = "Rajasthan",
        ["09"] = "Uttar Pradesh",
        ["10"] = "Bihar",
        ["11"] = "Sikkim",
        ["12"] = "Arunachal Pradesh",
        ["13"] = "Nagaland",
        ["14"] = "Manipur",
        ["15"] = "Mizoram",
        ["16"] = "Tripura",
        ["17"] = "Meghalaya",
        ["18"] = "Assam",
        ["19"] = "West Bengal",
        ["20"] = "Jharkhand",
        ["21"] = "Odisha",
        ["22"] = "Chhattisgarh",
        ["23"] = "Madhya Pradesh",
        ["24"] = "Gujarat",
        ["26"] = "Dadra and Nagar Haveli and Daman and Diu",
        ["27"] = "Maharashtra",
        ["29"] = "Karnataka",
        ["30"] = "Goa",
        ["31"] = "Lakshadweep",
        ["32"] = "Kerala",
        ["33"] = "Tamil Nadu",
        ["34"] = "Puducherry",
        ["35"] = "Andaman and Nicobar Islands",
        ["36"] = "Telangana",
        ["37"] = "Andhra Pradesh",
        ["38"] = "Ladakh",
        ["97"] = "Other Territory",
    };

    /// <summary>"Goa (30)".</summary>
    public static string Words(string code) => Names.TryGetValue(code, out var n) ? $"{n} ({code})" : code;
}
