using Resort.Domain.Folios;

namespace Resort.Tests.Unit;

/// <summary>Sprint 8 — bill arithmetic. The database checks the same rules (ck_folio_items_amounts, ck_folio_items_reversal).</summary>
public class FolioUnitTests
{
    [Theory]
    [InlineData(1, 1450, 1450)]
    [InlineData(2, 650, 1300)]
    [InlineData(0.5, 99.99, 50.00)]      // 49.995 → 50.00: halves go away from zero, as PostgreSQL's round()
    [InlineData(1.5, 0.01, 0.02)]        // 0.015 → 0.02
    [InlineData(-0.5, 99.99, -50.00)]    // a reversal is the exact negative
    [InlineData(2.345, 80, 187.60)]
    public void A_line_amount_is_quantity_times_price_rounded_to_the_paisa(decimal quantity, decimal price, decimal amount) =>
        Assert.Equal(amount, FolioMath.LineAmount(quantity, price));

    [Fact]
    public void A_reversal_negates_quantity_amount_tax_and_total()
    {
        var charge = new FolioItem { Quantity = 2, UnitPrice = 650, Amount = 1300, TaxAmount = 234, Total = 1534 };
        Assert.Equal((-2m, -1300m, -234m, -1534m), FolioMath.Negate(charge));
        Assert.Equal(-charge.Amount, FolioMath.LineAmount(FolioMath.Negate(charge).Quantity, charge.UnitPrice));
    }

    [Fact]
    public void Package_lines_add_up_to_the_package_price_times_quantity_for_any_split()
    {
        var random = new Random(8);
        for (var i = 0; i < 2000; i++)
        {
            var price = random.Next(100, 10_000_000) / 100m;
            var parts = random.Next(1, 5);
            var shares = Enumerable.Range(0, parts).Select(_ => (decimal)random.Next(1, 100)).ToList();
            var sum = shares.Sum();
            var percents = shares.Select(s => decimal.Round(s * 100 / sum, 2)).ToList();
            percents[^1] += 100 - percents.Sum();
            if (percents.Any(p => p <= 0)) continue;
            var amounts = Resort.Domain.Rates.MoneySplit.ByPercent(price, percents);
            var quantity = random.Next(1, 51);
            Assert.Equal(price * quantity, FolioMath.PackageLines(amounts, quantity).Sum());
        }
    }

    [Fact]
    public void Categories_read_as_words_and_match_the_database_list()
    {
        Assert.Equal(["ROOM", "FOOD", "BEVERAGE", "ACTIVITY", "TRANSPORT", "EQUIPMENT", "LAUNDRY", "OTHER"], ChargeCategories.All);
        Assert.Equal("Laundry", ChargeCategories.Words("LAUNDRY"));
        // package components use the first four, so a package line always has a valid category
        Assert.All(Resort.Domain.Rates.RevenueCategories.All, c => Assert.Contains(c, ChargeCategories.All));
    }
}
