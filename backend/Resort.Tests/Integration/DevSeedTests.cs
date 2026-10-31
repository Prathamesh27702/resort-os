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

    /// <summary>Sprint 10: yesterday's drawer shift is closed and balanced with R00007's ₹500 cash on its bill; R00002's ₹2,000
    /// UPI advance is on account; no shift is left open, so opening today's drawer is part of the demo.</summary>
    [Fact]
    public async Task Dev_seed_has_a_closed_drawer_shift_a_cash_payment_on_a_bill_and_an_advance_on_account()
    {
        await app.Services.GetRequiredService<DevSeeder>().RunAsync(CancellationToken.None);
        await app.Services.GetRequiredService<DevSeeder>().RunAsync(CancellationToken.None);   // idempotent
        var owner = await app.SignInOwnerAsync();
        var pid = await app.MainPropertyIdAsync();

        var drawer = await owner.GetJsonAsync<CashDrawerDto>($"/api/v1/properties/{pid}/cash-drawer");
        Assert.Null(drawer.Current);
        var shift = Assert.Single(drawer.Recent);
        Assert.Equal(("CLOSED", 5000m, 500m, 5500m, 0m), (shift.Status, shift.OpeningFloat, shift.CashIn, shift.Counted!.Value, shift.Variance!.Value));

        var payments = await owner.GetJsonAsync<PaymentListDto>($"/api/v1/properties/{pid}/payments");
        Assert.Equal(new[] { ("P00001", "R00007", "CASH", 0m), ("P00002", "R00002", "UPI", 2000m) },
            payments.Items.OrderBy(p => p.Number).Select(p => (p.Number, p.Reference, p.Method, p.OnAccount)));
        var inHouse = await owner.GetJsonAsync<PagedResult<FolioListItemDto>>($"/api/v1/properties/{pid}/folios");
        var r7 = await owner.GetJsonAsync<FolioDto>($"/api/v1/properties/{pid}/folios/{inHouse.Items.Single(f => f.Reference == "R00007").Id}");
        Assert.Equal((500m, r7.Total - 500m), (r7.Payments, r7.Balance));

        // the next payment is numbered after the seeded ones
        var next = await app.WithDbAsync(db => db.Database.SqlQuery<long>(
            $"SELECT last_value AS \"Value\" FROM property_counters WHERE property_id = {pid} AND name = 'payment'").SingleAsync());
        Assert.Equal(2L, next);
    }
    /// <summary>Sprint 14: a sample menu with portions, choices and a sold-out drink; six tables; and one open order whose
    /// ticket is on the kitchen display, with GST because the demo property is registered.</summary>
    [Fact]
    public async Task Dev_seed_has_a_sample_menu_tables_and_an_order_in_the_kitchen()
    {
        await app.Services.GetRequiredService<DevSeeder>().RunAsync(CancellationToken.None);
        await app.Services.GetRequiredService<DevSeeder>().RunAsync(CancellationToken.None);   // idempotent
        var owner = await app.SignInOwnerAsync();
        var pid = await app.MainPropertyIdAsync();

        var menu = await owner.GetJsonAsync<MenuDto>($"/api/v1/properties/{pid}/menu");
        Assert.Equal(["Starters", "Mains", "Breads and rice", "Desserts", "Drinks"], menu.Categories.Select(c => c.Name));
        var tikka = menu.Categories[0].Items.Single(i => i.Code == "PANEER-TIKKA");
        Assert.Equal([("Half", 160m), ("Full", 280m)], tikka.Variants.Select(v => (v.Name, v.Price)));
        Assert.Equal(["Spice level", "Add-ons"], tikka.ModifierGroups.Select(g => g.Name));
        Assert.False(menu.Categories[4].Items.Single(i => i.Code == "MASALA-CHAI").IsAvailable);

        var floor = await owner.GetJsonAsync<FloorDto>($"/api/v1/properties/{pid}/orders/floor");
        Assert.Equal(["T1", "T2", "T3", "T4", "B1", "B2"], floor.Tables.Select(t => t.Code));
        var order = await owner.GetJsonAsync<OrderDto>($"/api/v1/properties/{pid}/orders/{floor.Tables[1].Order!.Id}");
        Assert.Equal(("O00001", 850m, 42.5m, 892.5m), (order.Number, order.Subtotal, order.TaxAmount, order.Total));
        Assert.Equal(new[] { "Less spicy" }, order.Items[0].Modifiers);
        var kitchen = await owner.GetJsonAsync<KitchenDto>($"/api/v1/properties/{pid}/kitchen/kots");
        Assert.Equal(("K00001", "Table T2", 3), (kitchen.Tickets[0].Number, kitchen.Tickets[0].Where, kitchen.Tickets[0].Items.Count));
    }

    /// <summary>Sprint 15: two stores, 21 stock items with opening stock (fresh cream at its reorder level), nine recipes for eight
    /// dishes — Paneer Tikka (Full) costs ₹105 of its ₹280, 37.5 % — and one wastage.</summary>
    [Fact]
    public async Task Dev_seed_has_sample_stock_and_recipes_with_paneer_tikka_at_a_37_5_percent_food_cost()
    {
        await app.Services.GetRequiredService<DevSeeder>().RunAsync(CancellationToken.None);
        await app.Services.GetRequiredService<DevSeeder>().RunAsync(CancellationToken.None);   // idempotent
        var owner = await app.SignInOwnerAsync();
        var pid = await app.MainPropertyIdAsync();

        var stock = await owner.GetJsonAsync<InventoryDto>($"/api/v1/properties/{pid}/inventory");
        Assert.Equal([("STORE", false), ("KITCHEN", true)], stock.Locations.Select(l => (l.Code, l.IsKitchen)));
        Assert.Equal(21, stock.Items.Count);
        var paneer = stock.Items.Single(i => i.Code == "PANEER");
        Assert.Equal(("kg", 400m, 5m), (paneer.Unit, paneer.UnitCost, paneer.OnHand));
        Assert.Equal(["CREAM"], stock.Items.Where(i => i.Low).Select(i => i.Code));
        Assert.Equal(27.8m, stock.Items.Single(i => i.Code == "ONION-CAPSICUM").OnHand);   // 8 + 20 − 0.2 spoilt

        var recipes = await owner.GetJsonAsync<RecipeListDto>($"/api/v1/properties/{pid}/inventory/recipes");
        var full = recipes.Dishes.Single(d => d.Name == "Paneer Tikka (Full)");
        Assert.Equal((105m, 37.5m, 175m), (full.Cost!.Value, full.FoodCostPercent!.Value, full.Contribution!.Value));
        Assert.Equal(63m, recipes.Dishes.Single(d => d.Name == "Paneer Tikka (Half)").Cost);
        Assert.Equal(9, recipes.Dishes.Count(d => d.Recipe is not null));   // eight dishes; Paneer Tikka has two portions

        var report = await owner.GetJsonAsync<FoodCostReportDto>($"/api/v1/properties/{pid}/inventory/food-cost");
        Assert.Equal(["Butter Naan", "Veg Biryani", "Dal Makhani"], report.Highest.Select(l => l.Name));
        Assert.Equal(["Goan Fish Curry", "Paneer Tikka (Half)", "Paneer Tikka (Full)"], report.Lowest.Select(l => l.Name));
        Assert.Equal(6, report.NoRecipe.Count);
    }

    /// <summary>Sprint 16: four suppliers — two in Goa (CGST + SGST), one in Maharashtra (IGST), one unregistered — and the
    /// kitchen's open request PR00001 for vegetables, the start of the demo's purchase cycle.</summary>
    [Fact]
    public async Task Dev_seed_has_sample_suppliers_and_an_open_request_for_vegetables()
    {
        await app.Services.GetRequiredService<DevSeeder>().RunAsync(CancellationToken.None);
        await app.Services.GetRequiredService<DevSeeder>().RunAsync(CancellationToken.None);   // idempotent
        var owner = await app.SignInOwnerAsync();
        var pid = await app.MainPropertyIdAsync();

        var suppliers = await owner.GetJsonAsync<List<SupplierDto>>($"/api/v1/properties/{pid}/purchasing/suppliers");
        Assert.Equal(["Anjuna Fishermen", "Goa Dairy Co-operative", "Konkan Spice Traders", "Mapusa Fresh Vegetables"], suppliers.Select(s => s.Name));
        Assert.All(suppliers.Where(s => s.Gstin is not null), s => Assert.Null(Resort.Domain.Tax.Gstin.Problem(s.Gstin)));
        Assert.Equal([("FISH", null, "30"), ("DAIRY", "30AAATG4321B1ZO", "30"), ("SPICES", "27AABCK7788C1ZA", "27"), ("FRESH-VEG", "30AAACF1234A1ZS", "30")],
            suppliers.Select(s => (s.Code, s.Gstin, s.StateCode)));

        var request = Assert.Single(await owner.GetJsonAsync<List<PurchaseRequestDto>>($"/api/v1/properties/{pid}/purchasing/requests?status=OPEN"));
        Assert.Equal(("PR00001", "Main store"), (request.Number, request.LocationName));
        Assert.Equal([("ONION-CAPSICUM", 15m), ("MIXED-VEG", 10m), ("LIME", 100m)], request.Items.Select(i => (i.ItemCode, i.Quantity)));
    }

    /// <summary>Sprint 17: ten employees in five departments with their pay (HR only), shift templates, and this week's
    /// roster with everyone on their usual shift except on their day off, so the shortage view shows the gaps.</summary>
    [Fact]
    public async Task Dev_seed_has_ten_employees_their_pay_and_this_weeks_roster_with_its_gaps()
    {
        await app.Services.GetRequiredService<DevSeeder>().RunAsync(CancellationToken.None);
        await app.Services.GetRequiredService<DevSeeder>().RunAsync(CancellationToken.None);   // idempotent
        var owner = await app.SignInOwnerAsync();
        var pid = await app.MainPropertyIdAsync();

        var departments = await owner.GetJsonAsync<List<DepartmentDto>>($"/api/v1/properties/{pid}/departments");
        Assert.Equal([("FRONT", 2), ("HK", 3), ("KITCHEN", 2), ("REST", 2), ("MAINT", 1)], departments.Select(d => (d.Code, d.EmployeeCount)));
        var employees = await owner.GetJsonAsync<List<EmployeeDto>>($"/api/v1/properties/{pid}/employees");
        Assert.Equal(10, employees.Count);
        Assert.Equal(Enumerable.Range(1, 10).Select(i => $"E{i:D5}"), employees.Select(e => e.Code).Order());
        var asha = employees.Single(e => e.Code == "E00001");
        Assert.Equal(("Asha Naik", "Front office", "Receptionist", (short?)2), (asha.FullName, asha.DepartmentName, asha.DesignationName, asha.WeeklyOff));
        var pay = await owner.GetJsonAsync<EmployeeCompensationDto>($"/api/v1/properties/{pid}/employees/{asha.Id}/compensation");
        Assert.Equal((18000m, 21000m), (pay.Current!.Basic, pay.Current.Gross));

        var week = await owner.GetJsonAsync<RosterWeekDto>($"/api/v1/properties/{pid}/roster");
        Assert.Equal(9, week.Templates.Count);
        // Everyone but Joel works 6 days; Joel (part time, off on Sunday) Friday and Saturday.
        Assert.Equal(9 * 6 + 2, week.Shifts.Count);
        Assert.Empty(week.Warnings);
        Assert.Contains(week.Shortages, s => s.TemplateName == "Morning" && s.DepartmentName == "Front office" && s.Date == week.WeekStart.AddDays(1));
        Assert.Equal(11, week.Shortages.Count);
        var next = await owner.GetJsonAsync<RosterWeekDto>($"/api/v1/properties/{pid}/roster?week={week.WeekStart.AddDays(7):yyyy-MM-dd}");
        Assert.Empty(next.Shifts);
    }

    /// <summary>Sprint 18: a geofence at the gate, last week's attendance with the timesheets waiting for approval (Ravi's
    /// with a missing clock-out, Pooja absent on Tuesday, Joel's still a draft), and this week's clock-ins up to yesterday.</summary>
    [Fact]
    public async Task Dev_seed_has_a_geofence_and_last_weeks_timesheets_waiting_for_approval()
    {
        await app.Services.GetRequiredService<DevSeeder>().RunAsync(CancellationToken.None);
        await app.Services.GetRequiredService<DevSeeder>().RunAsync(CancellationToken.None);   // idempotent
        var owner = await app.SignInOwnerAsync();
        var pid = await app.MainPropertyIdAsync();

        var fence = Assert.Single(await owner.GetJsonAsync<List<GeofenceDto>>($"/api/v1/properties/{pid}/attendance/geofences"));
        Assert.Equal(("Main gate", 300), (fence.Name, fence.RadiusM));

        var thisWeek = await owner.GetJsonAsync<TimesheetWeekDto>($"/api/v1/properties/{pid}/timesheets");
        var last = await owner.GetJsonAsync<TimesheetWeekDto>($"/api/v1/properties/{pid}/timesheets?week={thisWeek.WeekStart.AddDays(-7):yyyy-MM-dd}");
        Assert.Equal(10, last.Timesheets.Count);
        Assert.Equal((9, 1), (last.Counts["SUBMITTED"], last.Counts["DRAFT"]));
        var ravi = last.Timesheets.Single(t => t.EmployeeCode == "E00007");
        Assert.Equal((1, 5), (ravi.OpenDays, ravi.DaysWorked));
        var pooja = await owner.GetJsonAsync<TimesheetDto>($"/api/v1/properties/{pid}/timesheets/{last.Timesheets.Single(t => t.EmployeeCode == "E00005").Id}");
        Assert.Equal("ABSENT", pooja.Lines.Single(l => l.WorkDate == last.WeekStart.AddDays(1)).Status);
        Assert.All(last.Timesheets.Where(t => t.EmployeeCode != "E00005" && t.EmployeeCode != "E00007" && t.EmployeeCode != "E00009"),
            t => Assert.Equal(6, t.DaysWorked));

        // Approving the week: everyone submitted except Ravi, whose Tuesday needs a correction first.
        var approved = await owner.PostAsync($"/api/v1/properties/{pid}/timesheets/approve-week", new ApproveTimesheetWeekRequest(last.WeekStart));
        var result = (await approved.Content.ReadFromJsonAsync<ApproveTimesheetWeekResultDto>())!;
        Assert.Equal(8, result.Approved);
        Assert.StartsWith("Ravi Kamat: ", Assert.Single(result.Skipped));
    }

    /// <summary>Sprint 19: four leave types with their policies, the holidays, everyone's opening balance and this month's
    /// accrual (so running it again adds nothing), and the leave requests: two waiting, one approved, one refused.</summary>
    [Fact]
    public async Task Dev_seed_has_leave_types_balances_holidays_and_requests_waiting_for_approval()
    {
        await app.Services.GetRequiredService<DevSeeder>().RunAsync(CancellationToken.None);
        await app.Services.GetRequiredService<DevSeeder>().RunAsync(CancellationToken.None);   // idempotent
        var owner = await app.SignInOwnerAsync();
        var pid = await app.MainPropertyIdAsync();

        var types = await owner.GetJsonAsync<List<LeaveTypeDto>>($"/api/v1/properties/{pid}/leave/types");
        Assert.Equal(["CL", "SL", "EL", "LOP"], types.Select(t => t.Code));
        Assert.Equal(2, types.Single(t => t.Code == "CL").Policies.Count);
        Assert.Equal(5, (await owner.GetJsonAsync<List<HolidayDto>>($"/api/v1/properties/{pid}/leave/holidays")).Count);

        var list = await owner.GetJsonAsync<LeaveListDto>($"/api/v1/properties/{pid}/leave/requests?from=2000-01-01&to=2100-01-01");
        Assert.Equal((2, 1, 1), (list.Counts["PENDING"], list.Counts["APPROVED"], list.Counts["REJECTED"]));
        Assert.Equal(1.5m, list.Requests.Single(r => r.EmployeeCode == "E00003").Days);

        var balances = await owner.GetJsonAsync<List<EmployeeLeaveBalancesDto>>($"/api/v1/properties/{pid}/leave/balances");
        Assert.Equal(10, balances.Count);
        Assert.All(balances, b => Assert.Equal(6m, b.Balances.Single(x => x.Code == "SL").Balance));
        var month = DateTime.UtcNow.AddHours(5.5).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        var run = await owner.PostAsync($"/api/v1/properties/{pid}/leave/accruals/run", new RunAccrualRequest(month));
        Assert.Equal(0, (await run.Content.ReadFromJsonAsync<AccrualResultDto>())!.Posted);
    }
}
