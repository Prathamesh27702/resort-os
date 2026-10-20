using Resort.Domain.Tax;

namespace Resort.Tests.Unit;

/// <summary>Sprint 9 — GST arithmetic: slab boundaries, the CGST/SGST vs IGST split, rounding per line, and GSTINs.
/// The rates here are test data, not the law: the engine reads real rates from the database.</summary>
public class TaxUnitTests
{
    private static TaxRule Slabs(params (decimal? UpTo, decimal Rate)[] slabs) => new()
    {
        RuleType = TaxRuleTypes.Slab,
        Source = "test",
        Slabs = slabs.Select((s, i) => new TaxRuleSlab { UpTo = s.UpTo, Rate = s.Rate, SortOrder = i }).ToList(),
    };

    private static readonly TaxRule Accommodation = Slabs((7500m, 5m), (null, 18m));

    // ------------------------------------------------------------------ slab boundaries (required test)

    [Theory]
    [InlineData(0, 5)]
    [InlineData(1000, 5)]
    [InlineData(7499.99, 5)]
    [InlineData(7500.00, 5)]        // exactly at the threshold: "up to ₹7,500" includes ₹7,500
    [InlineData(7500.01, 18)]       // one paisa above it
    [InlineData(25000, 18)]
    public void A_value_exactly_at_a_slab_limit_takes_that_slab_and_one_paisa_more_takes_the_next(decimal unitValue, decimal rate) =>
        Assert.Equal(rate, TaxMath.SlabFor(Accommodation.Slabs, unitValue).Rate);

    [Fact]
    public void Slabs_are_matched_in_order_of_their_limit_whatever_order_they_were_entered_in()
    {
        var rule = Slabs((null, 28m), (2500m, 0m), (7500m, 12m), (1000m, 0m));
        Assert.Equal(new[] { 0m, 0m, 0m, 12m, 12m, 28m },
            new[] { 999.99m, 1000m, 2500m, 2500.01m, 7500m, 7500.01m }.Select(v => TaxMath.SlabFor(rule.Slabs, v).Rate));
    }

    // ------------------------------------------------------------------ intra- vs inter-state (required test)

    [Fact]
    public void Inside_the_state_the_rate_is_split_evenly_into_CGST_and_SGST()
    {
        var taxes = TaxMath.Split(1450m, 5m, intraState: true);
        Assert.Equal(new[] { new TaxLine("CGST", 2.5m, 1450m, 36.25m), new TaxLine("SGST", 2.5m, 1450m, 36.25m) }, taxes);
    }

    [Fact]
    public void Across_states_it_is_all_IGST_at_the_full_rate()
    {
        var taxes = TaxMath.Split(1450m, 5m, intraState: false);
        Assert.Equal(new[] { new TaxLine("IGST", 5m, 1450m, 72.50m) }, taxes);
    }

    [Theory]
    [InlineData(8000, 18, 720, 1440)]
    [InlineData(3500, 12, 210, 420)]
    [InlineData(0, 5, 0, 0)]
    public void The_halves_add_up_to_the_IGST_when_the_tax_divides_evenly(decimal value, decimal rate, decimal each, decimal igst)
    {
        Assert.All(TaxMath.Split(value, rate, true), t => Assert.Equal(each, t.Amount));
        Assert.Equal(igst, TaxMath.Split(value, rate, false).Single().Amount);
    }

    // ------------------------------------------------------------------ rounding per line (required test)

    [Theory]
    [InlineData(333.33, 8.33, 16.67)]   // 2.5% = 8.33325 → 8.33 each (CGST+SGST 16.66); 5% = 16.6665 → 16.67
    [InlineData(1.00, 0.03, 0.05)]      // 0.025 → 0.03: halves go away from zero, as PostgreSQL's round()
    [InlineData(-1.00, -0.03, -0.05)]   // a negative value (a reversal) rounds to the exact negative
    [InlineData(99.99, 2.50, 5.00)]     // 2.49975 → 2.50; 4.9995 → 5.00
    public void Each_tax_is_rounded_to_the_paisa_on_its_own(decimal value, decimal eachHalf, decimal igst)
    {
        Assert.All(TaxMath.Split(value, 5m, true), t => Assert.Equal(eachHalf, t.Amount));
        Assert.Equal(igst, TaxMath.Split(value, 5m, false).Single().Amount);
    }

    [Fact]
    public void Parts_of_a_line_at_the_same_rate_are_added_up_before_rounding_once()
    {
        // two rooms at ₹100.10: 2.5% of each is ₹2.5025; rounded per room that would be 2 × ₹2.50, per line it is ₹5.01
        var taxes = TaxMath.ForLine(Accommodation, [new TaxablePart(100.10m, 100.10m), new TaxablePart(100.10m, 100.10m)], true);
        Assert.Equal(new[] { ("CGST", 200.20m, 5.01m), ("SGST", 200.20m, 5.01m) },
            taxes.Select(t => (t.Tax.Code, t.Tax.TaxableValue, t.Tax.Amount)));
    }

    [Fact]
    public void A_night_of_two_rooms_in_different_slabs_is_taxed_at_each_rooms_rate()
    {
        var taxes = TaxMath.ForLine(Accommodation, [new TaxablePart(9000m, 9000m), new TaxablePart(5000m, 5000m)], true);
        Assert.Equal(new[] { ("CGST", 2.5m, 5000m, 125m), ("SGST", 2.5m, 5000m, 125m), ("CGST", 9m, 9000m, 810m), ("SGST", 9m, 9000m, 810m) },
            taxes.Select(t => (t.Tax.Code, t.Tax.Rate, t.Tax.TaxableValue, t.Tax.Amount)));
        Assert.Equal(1870m, taxes.Sum(t => t.Tax.Amount));
    }

    [Fact]
    public void The_slab_goes_by_the_unit_value_not_by_the_value_of_the_whole_line()
    {
        // a ₹15,000 line made of two ₹7,500 rooms stays in the ₹7,500 slab
        var taxes = TaxMath.ForLine(Accommodation, [new TaxablePart(7500m, 7500m), new TaxablePart(7500m, 7500m)], true);
        Assert.All(taxes, t => Assert.Equal(2.5m, t.Tax.Rate));
        Assert.Equal(750m, taxes.Sum(t => t.Tax.Amount));
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(18, true)]
    [InlineData(0.1, true)]
    [InlineData(12.5, true)]
    [InlineData(0.25, false)]   // CGST would be 0.125%
    [InlineData(5.01, false)]
    public void A_rate_must_split_into_two_equal_halves_of_whole_hundredths(decimal rate, bool ok) =>
        Assert.Equal(ok, TaxMath.IsSplittable(rate));

    // ------------------------------------------------------------------ GSTIN

    [Theory]
    [InlineData("27AAPFU0939F1ZV")]     // the format example published by the GST portal
    [InlineData("30AABCT1234C1Z9")]     // the demo property's
    [InlineData(" 30aabct1234c1z9 ")]   // typed in lower case with spaces
    public void A_valid_GSTIN_passes(string gstin) => Assert.Null(Gstin.Problem(gstin));

    [Theory]
    [InlineData("", "Enter the GSTIN")]
    [InlineData("30AABCT1234C1Z", "A GSTIN has 15 characters; this one has 14.")]
    [InlineData("99AABCT1234C1Z9", "99 isn't a GST state code.")]
    [InlineData("30AAB1T1234C1Z9", "Characters 3 to 12 are the PAN")]
    [InlineData("30AABCT1234C1Y9", "Character 14 of a GSTIN is always Z.")]
    [InlineData("30AABCT1234C1Z8", "The last character (check digit) should be 9 for this GSTIN")]
    public void An_invalid_GSTIN_says_exactly_what_is_wrong(string gstin, string message) =>
        Assert.StartsWith(message, Gstin.Problem(gstin));
}
