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
}
