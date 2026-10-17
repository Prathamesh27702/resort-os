using Microsoft.EntityFrameworkCore;
using Resort.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Resort.Infrastructure.Persistence;
using Resort.Tests.Support;

namespace Resort.Tests.Integration;

/// <summary>Own class (own database) because it fills the bootstrap property with demo rooms, guests and rates.</summary>
public class DevSeedTests(ResortApiFactory app) : IClassFixture<ResortApiFactory>
{
    [Fact]
    public async Task Dev_seed_creates_6_rooms_a_10_bed_dorm_and_20_guests_once()
    {
        var seeder = app.Services.GetRequiredService<DevSeeder>();
        await seeder.RunAsync(CancellationToken.None);
        await seeder.RunAsync(CancellationToken.None);   // second run must change nothing

        var pid = await app.MainPropertyIdAsync();
        var (types, rooms, beds) = await app.WithDbAsync(async db => (
            await db.AccommodationTypes.IgnoreQueryFilters().CountAsync(t => t.PropertyId == pid),
            await db.AccommodationUnits.IgnoreQueryFilters().Where(u => u.PropertyId == pid).Select(u => u.Code).OrderBy(c => c).ToListAsync(),
            await db.Beds.IgnoreQueryFilters().CountAsync(b => b.PropertyId == pid)));

        Assert.Equal(3, types);
        Assert.Equal(new[] { "101", "102", "103", "104", "105", "106", "DORM" }, rooms.ToArray());
        Assert.Equal(10, beds);

        var (guests, foreign, withAddress) = await app.WithDbAsync(async db => (
            await db.Guests.IgnoreQueryFilters().CountAsync(),
            await db.Guests.IgnoreQueryFilters().CountAsync(g => g.Nationality != "IN"),
            await db.GuestAddresses.IgnoreQueryFilters().CountAsync()));
        Assert.Equal(20, guests);
        Assert.Equal(6, foreign);
        Assert.Equal(20, withAddress);
    }

    /// <summary>Sprint 4 acceptance: a quote for Room 103, 2 adults, 3 nights returns a nightly breakdown matching the
    /// rate grid (Thursday arrival, so the stay crosses into the weekend uplift).</summary>
    [Fact]
    public async Task Dev_seed_rates_quote_room_103_for_3_nights_matching_the_grid()
    {
        await app.Services.GetRequiredService<DevSeeder>().RunAsync(CancellationToken.None);
        var owner = await app.SignInOwnerAsync();
        var pid = await app.MainPropertyIdAsync();

        var ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ist).DateTime);
        var thu = today.AddDays(((int)DayOfWeek.Thursday - (int)today.DayOfWeek + 7) % 7 + 7);

        var plans = await owner.GetJsonAsync<List<RatePlanDto>>($"/api/v1/properties/{pid}/rate-plans");
        var bar = Assert.Single(plans);
        Assert.Equal("BAR", bar.Code);
        Assert.True(bar.IsDefault);

        var types = await owner.GetJsonAsync<List<AccommodationTypeDto>>($"/api/v1/properties/{pid}/accommodation-types");
        var standard = types.Single(t => t.Code == "STANDARD_ROOM");
        var grid = (await owner.GetJsonAsync<List<RatePriceDto>>(
                $"/api/v1/properties/{pid}/rate-plans/{bar.Id}/prices?from={thu:yyyy-MM-dd}&to={thu.AddDays(3):yyyy-MM-dd}"))
            .Where(p => p.AccommodationTypeId == standard.Id).ToList();
        Assert.Equal([3500m, 4200m, 4200m], grid.Select(g => g.Price));

        var quote = await owner.GetJsonAsync<RateQuoteDto>(
            $"/api/v1/properties/{pid}/rates/quote?room=103&from={thu:yyyy-MM-dd}&to={thu.AddDays(3):yyyy-MM-dd}&adults=2");
        Assert.Equal("Standard room", quote.AccommodationTypeName);
        Assert.Equal(grid.Select(g => (g.Date, g.Price)), quote.Breakdown.Select(n => (n.Date, n.Total)));
        Assert.Equal(11900m, quote.Total);

        var packages = await owner.GetJsonAsync<List<PackageDto>>($"/api/v1/properties/{pid}/packages");
        Assert.Equal(["COUPLE", "ROOM_BREAKFAST"], packages.Select(p => p.Code));
        Assert.All(packages, p => Assert.Equal(p.Price, p.Components.Sum(c => c.Amount)));
    }
    /// <summary>Sprint 5: six demo bookings; Deluxe has one room (102) left tonight for the two-browser demo.</summary>
    [Fact]
    public async Task Dev_seed_reservations_leave_one_deluxe_room_tonight()
    {
        await app.Services.GetRequiredService<DevSeeder>().RunAsync(CancellationToken.None);
        await app.Services.GetRequiredService<DevSeeder>().RunAsync(CancellationToken.None);   // idempotent
        var owner = await app.SignInOwnerAsync();
        var pid = await app.MainPropertyIdAsync();
        var ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ist).DateTime);

        var list = await owner.GetJsonAsync<PagedResult<ReservationListItemDto>>($"/api/v1/properties/{pid}/reservations?from={today.AddDays(-1):yyyy-MM-dd}");
        Assert.Equal(["R00001", "R00002", "R00003", "R00004", "R00005", "R00006"], list.Items.Select(r => r.Reference).Order());
        Assert.Equal("Dormitory · 2 beds", list.Items.Single(r => r.Reference == "R00004").Rooms);

        var av = await owner.GetJsonAsync<AvailabilityDto>(
            $"/api/v1/properties/{pid}/availability?from={today:yyyy-MM-dd}&to={today.AddDays(2):yyyy-MM-dd}&adults=2");
        var deluxe = av.Types.Single(t => t.Code == "DELUXE_ROOM");
        Assert.Equal("102", Assert.Single(deluxe.FreeRooms).Code);
        Assert.Equal(8, av.Types.Single(t => t.Code == "DORMITORY").Free);

        // the seeded prices match what the API would charge
        var r1 = await owner.GetJsonAsync<ReservationDto>($"/api/v1/properties/{pid}/reservations/{list.Items.Single(r => r.Reference == "R00001").Id}");
        Assert.Equal(r1.Breakdown.Sum(n => n.Total), r1.TotalAmount);
        Assert.Equal(2, r1.Breakdown.Count);
    }
    /// <summary>Sprint 6: the calendar shows the demo bookings and Room 106's maintenance block from tomorrow.</summary>
    [Fact]
    public async Task Dev_seed_calendar_has_bookings_and_a_maintenance_block()
    {
        await app.Services.GetRequiredService<DevSeeder>().RunAsync(CancellationToken.None);
        await app.Services.GetRequiredService<DevSeeder>().RunAsync(CancellationToken.None);   // idempotent
        var owner = await app.SignInOwnerAsync();
        var pid = await app.MainPropertyIdAsync();
        var ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ist).DateTime);

        var cal = await owner.GetJsonAsync<CalendarDto>($"/api/v1/properties/{pid}/calendar?from={today:yyyy-MM-dd}&to={today.AddDays(7):yyyy-MM-dd}");
        var room106 = cal.Rooms.Single(r => r.Code == "106");
        var block = Assert.Single(cal.Blocks, b => b.UnitId == room106.Id);
        Assert.Equal(("MAINTENANCE", "Plumbing repair", today.AddDays(1), today.AddDays(3)), (block.BlockType, block.Reason, block.StartDate, block.EndDate));
        // R00001–R00004 hold rooms (R00004 two dorm beds); the enquiry and the cancelled booking are not drawn
        Assert.Equal(["R00001", "R00002", "R00003", "R00004", "R00004"], cal.Bookings.Select(b => b.Reference).Order());
    }
}
