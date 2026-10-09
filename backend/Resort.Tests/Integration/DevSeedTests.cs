using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Resort.Infrastructure.Persistence;
using Resort.Tests.Support;

namespace Resort.Tests.Integration;

/// <summary>Own class (own database) because it fills the bootstrap property with demo rooms and guests.</summary>
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
}
