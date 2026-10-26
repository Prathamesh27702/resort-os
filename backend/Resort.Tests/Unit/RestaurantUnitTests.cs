using Resort.Domain.Restaurant;

namespace Resort.Tests.Unit;

/// <summary>Sprint 14 — the restaurant rules that need no database: a line's price with its choices, discounts (never more
/// than the bill), the folio line's description, and which kitchen-ticket step is allowed when and what the refusal says.</summary>
public class RestaurantUnitTests
{
    [Fact]
    public void A_line_is_the_portion_price_plus_every_choice_times_the_quantity()
    {
        var unit = RestaurantMath.UnitPrice(160m, [40m, 0m, 20m]);
        Assert.Equal(220m, unit);
        Assert.Equal(660m, RestaurantMath.LineAmount(3, unit));
        Assert.Equal(280m, RestaurantMath.UnitPrice(280m, []));
        Assert.Equal("Paneer Tikka (Half)", RestaurantMath.Label("Paneer Tikka", "Half"));
        Assert.Equal("Butter Naan", RestaurantMath.Label("Butter Naan", null));
    }

    [Theory]
    [InlineData(null, null, "1250", "0")]
    [InlineData("PERCENT", "10", "1250", "125")]
    [InlineData("PERCENT", "12.5", "333.33", "41.67")]    // 41.66625 → 41.67, halves away from zero
    [InlineData("PERCENT", "100", "610", "610")]
    [InlineData("AMOUNT", "250", "1250", "250")]
    [InlineData("AMOUNT", "500", "280", "280")]            // an item voided after a flat discount: never more than the bill
    [InlineData("PERCENT", "10", "0", "0")]
    public void A_discount_comes_off_the_subtotal_and_is_never_more_than_it(string? kind, string? value, string subtotal, string expected) =>
        Assert.Equal(D(expected), RestaurantMath.Discount(kind, value is null ? null : D(value), D(subtotal)));

    private static decimal D(string s) => decimal.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void The_folio_line_names_the_order_the_table_and_the_dishes_in_at_most_200_characters()
    {
        Assert.Equal("Restaurant O00012 · Table T4: 2× Paneer Tikka (Full), 1× Lime soda",
            RestaurantMath.FolioDescription("O00012", "Table T4", [(2, "Paneer Tikka (Full)"), (1, "Lime soda")]));
        Assert.Equal("Restaurant O00013: 1× Club sandwich", RestaurantMath.FolioDescription("O00013", null, [(1, "Club sandwich")]));
        var many = RestaurantMath.FolioDescription("O00014", "Table T1", Enumerable.Range(1, 40).Select(i => ((short)1, $"Dish number {i}")));
        Assert.True(many.Length <= 200);
        Assert.EndsWith("…", many);
    }

    [Theory]
    [InlineData("start", "NEW", null)]
    [InlineData("ready", "NEW", null)]
    [InlineData("ready", "PREPARING", null)]
    [InlineData("serve", "READY", null)]
    [InlineData("start", "PREPARING", "K00007 is already being prepared.")]
    [InlineData("ready", "READY", "K00007 is already ready.")]
    [InlineData("serve", "NEW", "K00007 is still waiting in the kitchen. Serve it when the kitchen marks it ready.")]
    [InlineData("serve", "PREPARING", "K00007 is still being prepared. Serve it when the kitchen marks it ready.")]
    [InlineData("ready", "SERVED", "K00007 is already served.")]
    [InlineData("start", "CANCELLED", "K00007 was cancelled: everything on it was voided.")]
    public void Each_kitchen_step_is_allowed_only_in_its_place_and_a_refusal_says_why(string action, string status, string? expected) =>
        Assert.Equal(expected, RestaurantFlow.KotProblem(action, "K00007", status));

    [Fact]
    public void A_settled_or_voided_order_is_final()
    {
        Assert.Null(RestaurantFlow.OrderProblem("O00001", "OPEN"));
        Assert.Equal("O00001 is already settled, so it can't be changed. Open a new order for anything more.", RestaurantFlow.OrderProblem("O00001", "SETTLED"));
        Assert.Equal("O00001 was voided, so it can't be changed. Open a new order instead.", RestaurantFlow.OrderProblem("O00001", "VOIDED"));
    }
}
