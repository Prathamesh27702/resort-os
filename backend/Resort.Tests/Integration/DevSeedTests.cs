using System.Net.Http.Json;
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
        // R00001–R00006 from Sprint 5; R00007 and R00008 (checked in yesterday) from Sprint 7
        Assert.Equal(["R00001", "R00002", "R00003", "R00004", "R00005", "R00006", "R00007", "R00008"], list.Items.Select(r => r.Reference).Order());
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
        // R00001–R00004 hold rooms (R00004 two dorm beds), R00008 is in house (Sprint 7); the enquiry and the cancelled
        // booking are not drawn, and R00007 left today
        Assert.Equal(["R00001", "R00002", "R00003", "R00004", "R00004", "R00008"], cal.Bookings.Select(b => b.Reference).Order());
    }

    /// <summary>Sprint 7: today's arrivals include R00001 in a dirty room; R00007 departs today; R00008 is in house.</summary>
    [Fact]
    public async Task Dev_seed_front_desk_has_an_arrival_in_a_dirty_room_a_departure_and_a_guest_in_house()
    {
        await app.Services.GetRequiredService<DevSeeder>().RunAsync(CancellationToken.None);
        await app.Services.GetRequiredService<DevSeeder>().RunAsync(CancellationToken.None);   // idempotent
        var owner = await app.SignInOwnerAsync();
        var pid = await app.MainPropertyIdAsync();

        var arrivals = await owner.GetJsonAsync<FrontDeskListDto>($"/api/v1/properties/{pid}/front-desk/arrivals");
        Assert.Equal("Room 101 is dirty, not ready.", arrivals.Items.Single(i => i.Reference == "R00001").CheckInProblem);
        Assert.Null(arrivals.Items.Single(i => i.Reference == "R00004").CheckInProblem);
        var departures = await owner.GetJsonAsync<FrontDeskListDto>($"/api/v1/properties/{pid}/front-desk/departures");
        var r7 = departures.Items.Single(i => i.Reference == "R00007");
        Assert.Equal(("CHECKED_IN", "105"), (r7.Status, r7.Rooms.Single().UnitCode));
        Assert.Equal(r7.Nights, (await owner.GetJsonAsync<ReservationDto>($"/api/v1/properties/{pid}/reservations/{r7.ReservationId}")).Breakdown.Count);
        var inHouse = await owner.GetJsonAsync<FrontDeskListDto>($"/api/v1/properties/{pid}/front-desk/in-house");
        Assert.Equal(["R00007", "R00008"], inHouse.Items.Select(i => i.Reference).Order());

        var dashboard = await owner.GetJsonAsync<FrontDeskDashboardDto>($"/api/v1/properties/{pid}/front-desk/dashboard");
        Assert.Equal(("ARRIVING", "DIRTY"), dashboard.Rooms.Where(r => r.UnitCode == "101").Select(r => (r.Occupancy, r.Housekeeping)).Single());
        Assert.Equal("OCCUPIED", dashboard.Rooms.Single(r => r.UnitCode == "104").Occupancy);
    }

    /// <summary>Sprint 8: every confirmed demo booking has a main folio; the in-house guests' bills have their room nights,
    /// R00008 dinner and a pick-up, R00007 laundry; demo check-out with a balance is allowed until payments (Sprint 10).</summary>
    [Fact]
    public async Task Dev_seed_folios_have_room_nights_and_demo_charges_for_the_guests_in_house()
    {
        await app.Services.GetRequiredService<DevSeeder>().RunAsync(CancellationToken.None);
        await app.Services.GetRequiredService<DevSeeder>().RunAsync(CancellationToken.None);   // idempotent
        var owner = await app.SignInOwnerAsync();
        var pid = await app.MainPropertyIdAsync();

        var all = await owner.GetJsonAsync<PagedResult<FolioListItemDto>>($"/api/v1/properties/{pid}/folios?show=all");
        var billable = await app.WithDbAsync(db => db.Reservations.IgnoreQueryFilters()
            .CountAsync(r => r.PropertyId == pid && (r.Status == "CONFIRMED" || r.Status == "CHECKED_IN" || r.Status == "CHECKED_OUT")));
        Assert.Equal(billable, all.Items.Count(f => f.IsPrimary));
        Assert.Equal(all.Items.Count, all.Items.Select(f => f.Number).Distinct().Count());

        var inHouse = await owner.GetJsonAsync<PagedResult<FolioListItemDto>>($"/api/v1/properties/{pid}/folios");
        Assert.Equal(["R00007", "R00008"], inHouse.Items.Select(f => f.Reference).Order());
        var r8 = await owner.GetJsonAsync<FolioDto>($"/api/v1/properties/{pid}/folios/{inHouse.Items.Single(f => f.Reference == "R00008").Id}");
        Assert.Equal(["ROOM", "ROOM", "ROOM", "TRANSPORT", "FOOD"], r8.Items.OrderBy(i => i.Source != "ROOM_NIGHT").ThenBy(i => i.PostedAt).Select(i => i.Category));
        Assert.Equal(r8.Items.Sum(i => i.Total), r8.Balance);
        var r7 = await owner.GetJsonAsync<FolioDto>($"/api/v1/properties/{pid}/folios/{inHouse.Items.Single(f => f.Reference == "R00007").Id}");
        Assert.Equal(["ROOM", "LAUNDRY"], r7.Items.Select(i => i.Category));
        Assert.Equal(320m, r7.Items.Single(i => i.Category == "LAUNDRY").Total);

        var settings = await owner.GetJsonAsync<List<PropertySettingDto>>($"/api/v1/properties/{pid}/settings");
        Assert.True(settings.Single(x => x.Key == "frontdesk.allow_checkout_with_balance").Value.GetBoolean());
    }

    /// <summary>Sprint 9: the demo property is registered for GST in Goa, so a new charge would carry GST; the Sprint 8 demo
    /// charges were posted before the tax engine and keep none.</summary>
    [Fact]
    public async Task Dev_seed_registers_the_demo_property_for_GST_so_new_charges_are_taxed_and_old_ones_stay_as_posted()
    {
        await app.Services.GetRequiredService<DevSeeder>().RunAsync(CancellationToken.None);
        await app.Services.GetRequiredService<DevSeeder>().RunAsync(CancellationToken.None);   // idempotent
        var owner = await app.SignInOwnerAsync();
        var pid = await app.MainPropertyIdAsync();

        var reg = Assert.Single(await owner.GetJsonAsync<List<GstRegistrationDto>>($"/api/v1/properties/{pid}/gst-registrations"));
        Assert.Equal(("30AABCT1234C1Z9", "30", "REGULAR", true), (reg.Gstin, reg.StateCode, reg.RegistrationType, reg.Current));

        var inHouse = await owner.GetJsonAsync<PagedResult<FolioListItemDto>>($"/api/v1/properties/{pid}/folios");
        var folioId = inHouse.Items.Single(f => f.Reference == "R00008").Id;
        var before = await owner.GetJsonAsync<FolioDto>($"/api/v1/properties/{pid}/folios/{folioId}");
        Assert.Equal(0m, before.Items.Single(i => i.Description == "Dinner at the beach shack").TaxAmount);

        // a breakfast posted now would carry 5% GST (a preview, so the other demo checks keep their bills as seeded)
        var res = await owner.PostAsync("/api/v1/tax/calculate", new TaxCalculationRequest(pid, null, "FOOD", null, 600m));
        Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
        var tax = (await res.Content.ReadFromJsonAsync<TaxCalculationDto>())!;
        Assert.Equal((true, 30m, 630m, "30AABCT1234C1Z9"), (tax.Taxed, tax.Tax, tax.Total, tax.Gstin));
    }
}
