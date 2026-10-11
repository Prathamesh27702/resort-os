using Resort.Domain.Rates;

namespace Resort.Tests.Unit;

public class QuoteEngineTests
{
    // Thursday 15 Oct 2026; Friday and Saturday nights are the weekend.
    private static readonly DateOnly Thu = new(2026, 10, 15);

    /// <summary>Weekday ₹3,500, Friday/Saturday ₹4,200; extra adult ₹1,000, extra child ₹500.</summary>
    private static Dictionary<DateOnly, NightRate> Grid(DateOnly from, int nights, short minStay = 1) =>
        Enumerable.Range(0, nights).Select(i => from.AddDays(i)).ToDictionary(d => d, d =>
            new NightRate(d.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday ? 4200m : 3500m, 1000m, 500m, minStay));

    private static QuoteInput Room(DateOnly from, DateOnly to, int adults, int children = 0, int baseOcc = 2, int maxOcc = 3) =>
        new("Best available rate", "Standard room", "UNIT", baseOcc, maxOcc, from, to, adults, children);

    [Fact]
    public void Three_nights_across_a_weekend_boundary_use_each_nights_price()
    {
        // Thu, Fri, Sat nights; departs Sunday
        var r = QuoteEngine.Price(Room(Thu, Thu.AddDays(3), adults: 2), Grid(Thu, 3));

        Assert.True(r.Ok);
        Assert.Equal([Thu, Thu.AddDays(1), Thu.AddDays(2)], r.Nights.Select(n => n.Date));
        Assert.Equal([3500m, 4200m, 4200m], r.Nights.Select(n => n.Total));
        Assert.Equal(11900m, r.Total);
        Assert.All(r.Nights, n => Assert.Equal(1, n.Units));
    }

    [Fact]
    public void Sunday_night_is_a_weekday_again()
    {
        var sat = Thu.AddDays(2);
        var r = QuoteEngine.Price(Room(sat, sat.AddDays(2), adults: 2), Grid(sat, 2));
        Assert.Equal([4200m, 3500m], r.Nights.Select(n => n.Total));
    }

    [Theory]
    // adults, children, base, → extra adults, extra children
    [InlineData(2, 0, 2, 0, 0)]
    [InlineData(3, 0, 2, 1, 0)]  // third adult pays extra
    [InlineData(2, 1, 2, 0, 1)]  // child beyond standard occupancy pays the child rate
    [InlineData(1, 1, 2, 0, 0)]  // child takes the free standard place
    [InlineData(1, 2, 2, 0, 1)]
    [InlineData(4, 1, 2, 2, 1)]
    public void Extra_adults_and_children_are_counted_above_standard_occupancy(int adults, int children, int baseOcc, int extraA, int extraC)
    {
        var occ = QuoteEngine.OccupancyFor("UNIT", baseOcc, adults, children);
        Assert.Equal(1, occ.Units);
        Assert.Equal(extraA, occ.ExtraAdults);
        Assert.Equal(extraC, occ.ExtraChildren);
    }

    [Fact]
    public void Extras_are_charged_per_night()
    {
        var r = QuoteEngine.Price(Room(Thu, Thu.AddDays(2), adults: 2, children: 1), Grid(Thu, 2));
        Assert.True(r.Ok);
        Assert.Equal([4000m, 4700m], r.Nights.Select(n => n.Total));   // 3500 + 500 child; 4200 + 500
        Assert.All(r.Nights, n => Assert.Equal(500m, n.ExtraChildAmount));
        Assert.Equal(8700m, r.Total);
    }

    [Fact]
    public void Too_many_guests_says_how_many_the_room_sleeps()
    {
        var r = QuoteEngine.Price(Room(Thu, Thu.AddDays(1), adults: 3, children: 1, maxOcc: 3), Grid(Thu, 1));
        Assert.False(r.Ok);
        Assert.Equal("TOO_MANY_GUESTS", r.Problem!.Code);
        Assert.Equal("Standard room sleeps at most 3 guests; this quote has 4 (3 adults, 1 child).", r.Problem.Message);
    }

    [Fact]
    public void Missing_prices_name_every_night_without_a_price()
    {
        var grid = Grid(Thu, 3);
        grid.Remove(Thu.AddDays(1));
        var r = QuoteEngine.Price(Room(Thu, Thu.AddDays(4), adults: 2), grid);

        Assert.Equal("RATE_NOT_SET", r.Problem!.Code);
        Assert.Equal([Thu.AddDays(1), Thu.AddDays(3)], r.Problem.Dates);
        Assert.Equal("Best available rate has no price for Standard room on Fri 16 Oct 2026, Sun 18 Oct 2026. Set the price in Settings → Rates.",
            r.Problem.Message);
    }

    [Fact]
    public void Minimum_stay_is_read_from_the_arrival_night()
    {
        var grid = Grid(Thu, 3);
        grid[Thu.AddDays(1)] = grid[Thu.AddDays(1)] with { MinStay = 2 };   // Friday arrivals need 2 nights

        var fridayOneNight = QuoteEngine.Price(Room(Thu.AddDays(1), Thu.AddDays(2), adults: 2), grid);
        Assert.Equal("MIN_STAY_NOT_MET", fridayOneNight.Problem!.Code);
        Assert.Equal("Arriving on Fri 16 Oct 2026, Best available rate needs a stay of at least 2 nights; this quote is for 1.",
            fridayOneNight.Problem.Message);

        // arriving Thursday, the Friday night's minimum doesn't apply
        Assert.True(QuoteEngine.Price(Room(Thu, Thu.AddDays(2), adults: 2), grid).Ok);
    }

    [Fact]
    public void Dormitory_charges_each_guest_a_bed_and_no_extras()
    {
        var grid = new Dictionary<DateOnly, NightRate> { [Thu] = new(800m, 0m, 0m, 1), [Thu.AddDays(1)] = new(950m, 0m, 0m, 1) };
        var q = new QuoteInput("BAR", "Dormitory bed", "BED", 1, 1, Thu, Thu.AddDays(2), 3, 1);

        var r = QuoteEngine.Price(q, grid);

        Assert.True(r.Ok);
        Assert.All(r.Nights, n => Assert.Equal(4, n.Units));
        Assert.Equal([3200m, 3800m], r.Nights.Select(n => n.Total));
        Assert.Equal(7000m, r.Total);
    }
}

public class MoneySplitTests
{
    [Fact]
    public void Split_by_percent_gives_leftover_paise_to_the_largest_remainders()
    {
        // ₹999.99 × 33.33 % = 333.296667 → 333.29 (+0.006667), × 33.34 % = 333.39666 → 333.39 (+0.00666)
        var parts = MoneySplit.ByPercent(999.99m, [33.33m, 33.33m, 33.34m]);
        Assert.Equal(999.99m, parts.Sum());
        Assert.Equal([333.30m, 333.30m, 333.39m], parts);
    }

    [Fact]
    public void One_paisa_split_three_ways_goes_to_the_first_part()
    {
        Assert.Equal([0.01m, 0m, 0m], MoneySplit.Evenly(0.01m, 3));
        Assert.Equal([333.34m, 333.33m, 333.33m], MoneySplit.Evenly(1000m, 3));
        // unequal shares: the largest share has the largest remainder (the web form shows the same)
        Assert.Equal([0m, 0m, 0.01m], MoneySplit.ByPercent(0.01m, [33.33m, 33.33m, 33.34m]));
    }

    [Fact]
    public void Package_split_rounding_never_loses_or_gains_a_paisa()
    {
        var rng = new Random(20261011);
        for (var run = 0; run < 5000; run++)
        {
            var total = rng.Next(1, 10_000_000) / 100m;                   // ₹0.01 … ₹99,999.99
            var parts = rng.Next(1, 8);
            // random shares in hundredths of a percent that add up to exactly 100.00
            var cuts = Enumerable.Range(0, parts - 1).Select(_ => rng.Next(1, 10_000)).Distinct().OrderBy(x => x).ToList();
            var bounds = new List<int> { 0 };
            bounds.AddRange(cuts);
            bounds.Add(10_000);
            var shares = bounds.Zip(bounds.Skip(1), (a, b) => (b - a) / 100m).Where(s => s > 0).ToList();

            var amounts = MoneySplit.ByPercent(total, shares);

            Assert.Equal(total, amounts.Sum());
            for (var i = 0; i < shares.Count; i++)
            {
                var exact = total * shares[i] / 100m;
                Assert.True(Math.Abs(amounts[i] - exact) < 0.01m, $"part {i} of {total} at {shares[i]}% is {amounts[i]}, exact {exact}");
                Assert.Equal(decimal.Round(amounts[i], 2), amounts[i]);
            }
        }
    }

    [Fact]
    public void Shares_must_add_up_to_100() =>
        Assert.Throws<ArgumentException>(() => MoneySplit.ByPercent(100m, [50m, 49.99m]));

    [Fact]
    public void Amounts_must_be_whole_paise() =>
        Assert.Throws<ArgumentException>(() => MoneySplit.Evenly(10.005m, 2));
}
