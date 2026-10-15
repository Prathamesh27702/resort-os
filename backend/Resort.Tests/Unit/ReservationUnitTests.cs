using Resort.Domain.Reservations;

namespace Resort.Tests.Unit;

public class AvailabilityEngineTests
{
    private static readonly Guid Std = Guid.NewGuid();
    private static readonly Guid DormType = Guid.NewGuid();
    private static readonly DateOnly D = new(2027, 1, 7);

    private static InventoryUnit Room(string code, int sort, bool active = true) =>
        new(Guid.NewGuid(), Std, code, "Room " + code, sort, active, "UNIT", []);

    private static InventoryUnit Dorm(string code, int beds, int sort = 200) =>
        new(Guid.NewGuid(), DormType, code, "Dorm " + code, sort, true, "BED",
            Enumerable.Range(1, beds).Select(i => new InventoryBed(Guid.NewGuid(), $"B{i:00}", $"Bed {i}", i, true)).ToList());

    private static Occupation Held(InventoryUnit u, int from, int to, Guid? bed = null, string label = "it is booked on R00001") =>
        new(u.UnitId, bed, D.AddDays(from), D.AddDays(to), label);

    [Fact]
    public void A_stay_ending_on_the_arrival_day_of_another_does_not_overlap()
    {
        var r = Room("101", 1);
        var taken = new[] { Held(r, 0, 2) };
        Assert.Single(AvailabilityEngine.Free([r], taken, D.AddDays(2), D.AddDays(4)).FreeRooms);  // arrives as the other leaves
        Assert.Empty(AvailabilityEngine.Free([r], taken, D.AddDays(1), D.AddDays(3)).FreeRooms);   // shares night 2
    }

    [Fact]
    public void Inactive_rooms_and_whole_dorm_blocks_are_not_free()
    {
        var a = Room("101", 1, active: false);
        var dorm = Dorm("D1", 4);
        var free = AvailabilityEngine.Free([a, dorm], [Held(dorm, 0, 1, label: "it is blocked (painting)")], D, D.AddDays(2));
        Assert.Empty(free.FreeRooms);
        Assert.Empty(free.FreeBeds);
    }

    [Fact]
    public void Rooms_are_taken_in_order_preferring_the_ones_a_booking_already_has()
    {
        var r1 = Room("101", 1);
        var r2 = Room("102", 2);
        var r3 = Room("103", 3);
        var units = new[] { r3, r2, r1 };
        var free = AvailabilityEngine.Free(units, [], D, D.AddDays(1));
        var line = new StayLine(Std, "Standard room", "UNIT", null, 2, 0);

        var first = AvailabilityEngine.Allocate([line, line], free, units, [], D, D.AddDays(1), "tonight");
        Assert.Equal([r1.UnitId, r2.UnitId], first.Allocations.Select(a => a.UnitId));

        var kept = AvailabilityEngine.Allocate([line], free, units, [], D, D.AddDays(1), "tonight", new HashSet<Guid> { r3.UnitId });
        Assert.Equal(r3.UnitId, Assert.Single(kept.Allocations).UnitId);
    }

    [Fact]
    public void A_named_room_that_is_taken_fails_with_what_takes_it()
    {
        var r1 = Room("101", 1);
        var r2 = Room("102", 2);
        var taken = new[] { Held(r1, 0, 3, label: "it is booked on R00042") };
        var units = new[] { r1, r2 };
        var free = AvailabilityEngine.Free(units, taken, D, D.AddDays(1));
        var result = AvailabilityEngine.Allocate([new StayLine(Std, "Standard room", "UNIT", r1.UnitId, 2, 0)], free, units, taken,
            D, D.AddDays(1), "for the night of 7 Jan 2027");
        Assert.False(result.Ok);
        Assert.Equal("Room 101 isn't free for the night of 7 Jan 2027: it is booked on R00042. Choose another room.", result.ProblemMessage);
    }

    [Fact]
    public void Not_enough_rooms_says_how_many_are_free_and_how_many_are_needed()
    {
        var r1 = Room("101", 1);
        var units = new[] { r1 };
        var free = AvailabilityEngine.Free(units, [], D, D.AddDays(1));
        var line = new StayLine(Std, "Standard room", "UNIT", null, 2, 0);
        var result = AvailabilityEngine.Allocate([line, line], free, units, [], D, D.AddDays(1), "tonight");
        Assert.Equal("Only 1 Standard room is free tonight; this booking needs 2.", result.ProblemMessage);
        Assert.Equal(1, result.ProblemLine);
    }

    [Fact]
    public void A_dorm_group_takes_one_bed_each_adults_first_and_stays_together()
    {
        var small = Dorm("D1", 2, sort: 1);
        var big = Dorm("D2", 6, sort: 2);
        var units = new[] { small, big };
        var free = AvailabilityEngine.Free(units, [Held(big, 0, 1, big.Beds[0].BedId)], D, D.AddDays(1));
        Assert.Equal(7, free.FreeCount(DormType, "BED"));

        var result = AvailabilityEngine.Allocate([new StayLine(DormType, "Dormitory", "BED", null, 2, 1)], free, units, [], D, D.AddDays(1), "tonight");
        Assert.True(result.Ok);
        Assert.All(result.Allocations, a => Assert.Equal(big.UnitId, a.UnitId));            // the dorm with room for all of them
        Assert.Equal([(1, 0), (1, 0), (0, 1)], result.Allocations.Select(a => (a.Adults, a.Children)));
        Assert.DoesNotContain(big.Beds[0].BedId, result.Allocations.Select(a => a.BedId!.Value));

        var tooBig = AvailabilityEngine.Allocate([new StayLine(DormType, "Dormitory", "BED", null, 8, 0)], free, units, [], D, D.AddDays(1), "tonight");
        Assert.Equal("Only 7 beds are free in Dormitory tonight; this booking needs 8 (one per guest).", tooBig.ProblemMessage);
    }
}

public class ReservationStatusTests
{
    [Theory]
    [InlineData("INQUIRY", "CONFIRMED", true)]
    [InlineData("PENDING", "CONFIRMED", true)]
    [InlineData("CONFIRMED", "NO_SHOW", true)]
    [InlineData("CANCELLED", "REFUNDED", true)]
    [InlineData("PENDING", "NO_SHOW", false)]
    [InlineData("CANCELLED", "CONFIRMED", false)]
    [InlineData("CHECKED_OUT", "CANCELLED", false)]
    [InlineData("CONFIRMED", "CHECKED_OUT", false)]
    public void Only_listed_moves_are_allowed(string from, string to, bool allowed) =>
        Assert.Equal(allowed, ReservationStatuses.CanMove(from, to));

    [Fact]
    public void Only_pending_confirmed_and_checked_in_hold_rooms() =>
        Assert.Equal(["PENDING", "CONFIRMED", "CHECKED_IN"], ReservationStatuses.All.Where(ReservationStatuses.HoldsInventory));

    [Fact]
    public void Every_status_has_an_entry_in_the_graph() =>
        Assert.Equal(ReservationStatuses.All.Order(), ReservationStatuses.Transitions.Keys.Order());
}
