using Resort.Domain.Accommodation;
using Resort.Infrastructure.Persistence;

namespace Resort.Tests.Unit;

public class InventoryCalculatorTests
{
    [Fact]
    public void Rooms_sell_one_item_per_active_unit()
    {
        // 3 active rooms + 1 inactive, up to 3 guests each
        var cap = InventoryCalculator.For("UNIT", 3, [(true, 0), (true, 0), (true, 0), (false, 0)]);
        Assert.Equal(3, cap.SellableItems);
        Assert.Equal(9, cap.MaxGuests);
    }

    [Fact]
    public void Dormitories_sell_each_active_bed_in_active_units()
    {
        // active dorm with 10 active beds; inactive dorm with 4 beds doesn't count
        var cap = InventoryCalculator.For("BED", 1, [(true, 10), (false, 4)]);
        Assert.Equal(10, cap.SellableItems);
        Assert.Equal(10, cap.MaxGuests);
    }

    [Fact]
    public void Unknown_mode_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => InventoryCalculator.For("ROOM", 2, []));
}

public class UnitBlockDateTests
{
    private static UnitBlock Block(int startDay, int endDay) => new()
    {
        StartDate = new DateOnly(2026, 10, startDay),
        EndDate = new DateOnly(2026, 10, endDay),
        BlockType = "MAINTENANCE",
        Reason = "test",
    };

    [Fact]
    public void End_date_is_exclusive_like_a_checkout_date()
    {
        var b = Block(10, 12);                                                   // nights of 10th and 11th
        Assert.True(b.Covers(new DateOnly(2026, 10, 11)));
        Assert.False(b.Covers(new DateOnly(2026, 10, 12)));
        Assert.False(b.Overlaps(new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 14)));   // back to back
        Assert.True(b.Overlaps(new DateOnly(2026, 10, 11), new DateOnly(2026, 10, 13)));
        Assert.True(b.Overlaps(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));     // fully inside
    }
}

public class DevSeedDiscoveryTests
{
    [Fact]
    public void Dev_seed_files_are_embedded_in_order()
    {
        var seeds = DevSeeder.Discover();
        Assert.Contains(seeds, s => s.Name == "S001__accommodation");
        Assert.Equal(seeds.Select(s => s.Name).Order(StringComparer.Ordinal), seeds.Select(s => s.Name));
        Assert.All(seeds, s => Assert.Contains("{{PROPERTY_CODE}}", s.Sql));
    }
}
