using System.Globalization;
using Resort.Domain.Accommodation;

namespace Resort.Domain.Rates;

/// <summary>The rate grid's values for one night of one room type in one plan.</summary>
public sealed record NightRate(decimal Price, decimal ExtraAdult, decimal ExtraChild, short MinStay);

/// <summary>What is being priced. Dates are property-local; <see cref="To"/> is the departure date (not a night).</summary>
public sealed record QuoteInput(
    string PlanName, string TypeName, string InventoryMode, int BaseOccupancy, int MaxOccupancy,
    DateOnly From, DateOnly To, int Adults, int Children)
{
    public int Nights => To.DayNumber - From.DayNumber;
}

/// <summary>How the guests are charged. Units = rooms (always 1 for a room quote) or beds (one per guest in a dormitory).</summary>
public sealed record Occupancy(int Units, int ExtraAdults, int ExtraChildren);

public sealed record QuoteNight(
    DateOnly Date, int Units, decimal UnitPrice, decimal BaseAmount,
    int ExtraAdults, decimal ExtraAdultRate, decimal ExtraAdultAmount,
    int ExtraChildren, decimal ExtraChildRate, decimal ExtraChildAmount,
    decimal Total);

/// <summary>Why a stay can't be quoted. <see cref="Dates"/> lists the nights concerned, if any.</summary>
public sealed record QuoteProblem(string Code, string Message, IReadOnlyList<DateOnly> Dates);

public sealed record QuoteResult(IReadOnlyList<QuoteNight> Nights, decimal Total, QuoteProblem? Problem)
{
    public bool Ok => Problem is null;
}

/// <summary>
/// Prices a stay night by night from the rate grid. Pure: no database, no clock — every rule is unit-tested.
///
/// Rules
///  * Rooms (UNIT mode): the night's price covers the type's standard occupancy. Each adult above it pays the extra-adult
///    rate; children fill any standard places the adults leave free, and each child beyond that pays the extra-child
///    rate. Adults + children may not exceed the type's maximum occupancy.
///  * Dormitories (BED mode): every guest (adult or child) takes a bed and pays the per-bed price. No extras.
///  * Every night of the stay needs a price in the plan; the quote lists every night that has none.
///  * Minimum stay is read from the arrival night.
/// </summary>
public static class QuoteEngine
{
    public static string Day(DateOnly d) => d.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture);

    public static Occupancy OccupancyFor(string inventoryMode, int baseOccupancy, int adults, int children)
    {
        if (inventoryMode == InventoryModes.Bed) return new Occupancy(adults + children, 0, 0);
        var extraAdults = Math.Max(0, adults - baseOccupancy);
        var freePlaces = Math.Max(0, baseOccupancy - adults);
        return new Occupancy(1, extraAdults, Math.Max(0, children - freePlaces));
    }

    public static QuoteResult Price(QuoteInput q, IReadOnlyDictionary<DateOnly, NightRate> grid)
    {
        if (q.Nights < 1)
            return Fail("INVALID_DATES", "The departure date must be after the arrival date.");

        var guests = q.Adults + q.Children;
        if (q.InventoryMode == InventoryModes.Unit && guests > q.MaxOccupancy)
            return Fail("TOO_MANY_GUESTS",
                $"{q.TypeName} sleeps at most {q.MaxOccupancy} guest{(q.MaxOccupancy == 1 ? "" : "s")}; " +
                $"this quote has {guests} ({Guests(q.Adults, q.Children)}).");

        var nights = Enumerable.Range(0, q.Nights).Select(i => q.From.AddDays(i)).ToList();
        var missing = nights.Where(d => !grid.ContainsKey(d)).ToList();
        if (missing.Count > 0)
            return Fail("RATE_NOT_SET",
                $"{q.PlanName} has no price for {q.TypeName} on {Join(missing)}. Set the price in Settings → Rates.", missing);

        var minStay = grid[q.From].MinStay;
        if (q.Nights < minStay)
            return Fail("MIN_STAY_NOT_MET",
                $"Arriving on {Day(q.From)}, {q.PlanName} needs a stay of at least {minStay} nights; this quote is for {q.Nights}.",
                [q.From]);

        var occ = OccupancyFor(q.InventoryMode, q.BaseOccupancy, q.Adults, q.Children);
        var rows = nights.Select(d =>
        {
            var r = grid[d];
            var baseAmount = r.Price * occ.Units;
            var ea = r.ExtraAdult * occ.ExtraAdults;
            var ec = r.ExtraChild * occ.ExtraChildren;
            return new QuoteNight(d, occ.Units, r.Price, baseAmount, occ.ExtraAdults, r.ExtraAdult, ea,
                occ.ExtraChildren, r.ExtraChild, ec, baseAmount + ea + ec);
        }).ToList();
        return new QuoteResult(rows, rows.Sum(n => n.Total), null);
    }

    private static QuoteResult Fail(string code, string message, IReadOnlyList<DateOnly>? dates = null) =>
        new([], 0m, new QuoteProblem(code, message, dates ?? []));

    private static string Guests(int adults, int children) =>
        $"{adults} adult{(adults == 1 ? "" : "s")}" + (children > 0 ? $", {children} child{(children == 1 ? "" : "ren")}" : "");

    private static string Join(List<DateOnly> dates) =>
        dates.Count <= 5
            ? string.Join(", ", dates.Select(Day))
            : string.Join(", ", dates.Take(5).Select(Day)) + $" and {dates.Count - 5} more night{(dates.Count == 6 ? "" : "s")}";
}

/// <summary>
/// Splits an amount of money into parts that add up to exactly the original, to the paisa.
///
/// Rounding rule (largest remainder): work in paise; give every part the whole paise of its exact share (rounded down);
/// then hand the paise left over, one each, to the parts whose discarded fractions were largest — ties go to the
/// earlier part. Nothing is lost or gained, and no part is more than one paisa away from its exact share.
/// </summary>
public static class MoneySplit
{
    /// <summary>Splits <paramref name="total"/> by percentages that add up to exactly 100.</summary>
    public static IReadOnlyList<decimal> ByPercent(decimal total, IReadOnlyList<decimal> percents)
    {
        if (percents.Sum() != 100m)
            throw new ArgumentException($"Percentages must add up to 100, not {percents.Sum()}.", nameof(percents));
        return ByWeights(total, percents);
    }

    /// <summary>Splits <paramref name="total"/> into <paramref name="parts"/> equal shares (e.g. a package price per night).</summary>
    public static IReadOnlyList<decimal> Evenly(decimal total, int parts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(parts, 1);
        return ByWeights(total, Enumerable.Repeat(1m, parts).ToList());
    }

    public static IReadOnlyList<decimal> ByWeights(decimal total, IReadOnlyList<decimal> weights)
    {
        if (total < 0 || decimal.Round(total, 2) != total)
            throw new ArgumentException("The amount must be zero or more, in whole paise.", nameof(total));
        if (weights.Count == 0 || weights.Any(w => w < 0) || weights.Sum() <= 0)
            throw new ArgumentException("Weights must be zero or more and not all zero.", nameof(weights));

        var paise = total * 100m;
        var sum = weights.Sum();
        var exact = weights.Select(w => paise * w / sum).ToList();
        var whole = exact.Select(decimal.Floor).ToList();
        var left = (int)(paise - whole.Sum());
        foreach (var i in Enumerable.Range(0, weights.Count)
                     .OrderByDescending(i => exact[i] - whole[i]).ThenBy(i => i).Take(left))
            whole[i] += 1;
        return whole.Select(p => p / 100m).ToList();
    }
}
