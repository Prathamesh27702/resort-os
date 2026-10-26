using System.Globalization;
using Resort.Domain.Inventory;

namespace Resort.Tests.Unit;

/// <summary>Sprint 15 — costing and stock arithmetic that needs no database: food cost %, contribution and margin of a dish,
/// unit conversion for recipes, the value of a movement, and the moving average cost of stock received.</summary>
public class InventoryUnitTests
{
    // ------------------------------------------------------------------ required: Paneer Tikka ₹280 / ₹105 → 37.5 %

    [Fact]
    public void Paneer_tikka_at_280_rupees_costing_105_is_a_37_5_percent_food_cost()
    {
        Assert.Equal(37.5m, InventoryMath.FoodCostPercent(280m, 105m));
        Assert.Equal(175m, InventoryMath.Contribution(280m, 105m));
        Assert.Equal(62.5m, InventoryMath.MarginPercent(280m, 105m));
    }

    [Fact]
    public void The_paneer_tikka_recipe_adds_up_to_105_rupees()
    {
        // 200 g paneer at ₹400/kg, 50 g curd at ₹100/kg, 20 g tikka masala at ₹500/kg, 100 g onion and capsicum at ₹60/kg,
        // 20 ml oil at ₹200/l (the seeded recipe).
        var lines = new[] { (200m, 400m), (50m, 100m), (20m, 500m), (100m, 60m), (20m, 200m) };
        var cost = lines.Sum(l => InventoryMath.Value(InventoryMath.Convert(l.Item1, 1m, 1000m), l.Item2));
        Assert.Equal(105m, cost);
    }

    [Theory]
    [InlineData("160", "63", "39.38", "97", "60.63")]       // Paneer Tikka (Half): 39.375 → 39.38 (halves away from zero)
    [InlineData("60", "9", "15", "51", "85")]                // Butter Naan
    [InlineData("450", "183.5", "40.78", "266.5", "59.22")]  // Goan Fish Curry
    [InlineData("100", "120", "120", "-20", "-20")]          // sold below cost: a negative margin, not hidden
    public void Food_cost_contribution_and_margin_of_a_dish(string price, string cost, string foodCost, string contribution, string margin)
    {
        Assert.Equal(D(foodCost), InventoryMath.FoodCostPercent(D(price), D(cost)));
        Assert.Equal(D(contribution), InventoryMath.Contribution(D(price), D(cost)));
        Assert.Equal(D(margin), InventoryMath.MarginPercent(D(price), D(cost)));
    }

    [Fact]
    public void A_free_dish_has_no_food_cost_percent()
    {
        Assert.Null(InventoryMath.FoodCostPercent(0m, 10m));
        Assert.Null(InventoryMath.MarginPercent(0m, 10m));
    }

    [Theory]
    [InlineData("200", "1", "1000", "0.2")]      // 200 g of an item kept in kg
    [InlineData("1.5", "1000", "1", "1500")]     // 1.5 l in ml
    [InlineData("2", "12", "1", "24")]           // 2 dozen in pieces
    [InlineData("0.4", "1", "1000", "0")]        // under a gram of a kg item rounds away
    [InlineData("12.5", "1", "1000", "0.013")]   // 12.5 g → 0.0125 kg → 0.013 (to the gram, halves away from zero)
    public void Recipe_quantities_convert_to_the_items_unit(string quantity, string from, string to, string expected) =>
        Assert.Equal(D(expected), InventoryMath.Convert(D(quantity), D(from), D(to)));

    [Theory]
    [InlineData("-0.6", "400", "-240")]
    [InlineData("0.333", "100", "33.3")]
    [InlineData("1.005", "1", "1.01")]           // to the paisa, halves away from zero
    public void A_movement_is_valued_to_the_paisa(string quantity, string cost, string expected) =>
        Assert.Equal(D(expected), InventoryMath.Value(D(quantity), D(cost)));

    [Theory]
    [InlineData("5", "400", "5", "420", "410")]     // half the stock at the new price
    [InlineData("0", "400", "2", "380", "380")]     // nothing on hand: the new price
    [InlineData("-1", "400", "2", "380", "380")]    // below zero counts as none
    [InlineData("3", "100", "1", "90", "97.5")]
    public void Stock_received_moves_the_average_cost(string onHand, string current, string received, string cost, string expected) =>
        Assert.Equal(D(expected), InventoryMath.AverageCost(D(onHand), D(current), D(received), D(cost)));

    [Fact]
    public void Quantities_read_without_trailing_zeros()
    {
        Assert.Equal("1.25 kg", InventoryMath.Quantity(1.250m, "kg"));
        Assert.Equal("3 pcs", InventoryMath.Quantity(3.000m, "pcs"));
        Assert.Equal("-0.6 kg", InventoryMath.Quantity(-0.600m, "kg"));
    }

    private static decimal D(string s) => decimal.Parse(s, CultureInfo.InvariantCulture);
}
