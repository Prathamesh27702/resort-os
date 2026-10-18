using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Resort.Contracts;
using Resort.Tests.Support;
using static Resort.Tests.Support.BookingKit;

namespace Resort.Tests.Integration;

/// <summary>Sprint 8 — folios: the bill of a stay. Charges, room nights, packages, reversals, splitting, and the rule that
/// nothing on a bill is ever edited or deleted. Real API, real PostgreSQL. Every test builds its own rooms.</summary>
public class FolioTests(FolioFactory app) : IClassFixture<FolioFactory>
{
    // ------------------------------------------------------------------ the brief's example (required test)

    [Fact]
    public async Task The_example_stay_adds_up_to_10950_rupees_room_food_transport_and_activity()
    {
        var (owner, pid) = await OwnerAsync();
        var (email, pw) = await app.CreateUserAsync("RECEPTIONIST", pid);
        var desk = await app.SignInAsync(email, pw);
        var stay = await SetupAsync(owner, pid, rooms: 1, price: 3500m);
        var guest = await CreateGuestAsync(owner, "Asha", "Rao");
        var booked = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, await WalkInAsync(owner, pid), Today, Today.AddDays(2), adults: 2)));

        // confirmed → the main folio is open, empty
        var folio = await MainFolioAsync(desk, pid, booked.Id);
        Assert.Equal(0m, folio.Total);
        Assert.Matches("^F[0-9]{5}$", folio.Number);

        // check-in puts the whole stay on the bill: 2 nights × ₹3,500
        await CheckInAsync(desk, pid, booked);
        folio = await GetAsync(desk, pid, folio.Id);
        Assert.Equal(new[] { (Today, 3500m), (Today.AddDays(1), 3500m) },
            folio.Items.Where(i => i.Source == "ROOM_NIGHT").Select(i => (i.ServiceDate, i.Total)));
        Assert.All(folio.Items, i => Assert.StartsWith("Room night: Room R", i.Description));

        // reception posts dinner, the airport transfer and two kayaking sessions
        await PostAsync(desk, pid, folio.Id, new PostChargeRequest("FOOD", "Dinner at the beach shack", 1, 1450m));
        await PostAsync(desk, pid, folio.Id, new PostChargeRequest("TRANSPORT", "Airport transfer", 1, 1200m));
        folio = await PostAsync(desk, pid, folio.Id, new PostChargeRequest("ACTIVITY", "Kayaking", 2, 650m));

        Assert.Equal(10950m, folio.Total);
        Assert.Equal(10950m, folio.Balance);
        Assert.Equal(0m, folio.Tax);
        Assert.Equal(new[] { ("ROOM", 7000m), ("FOOD", 1450m), ("ACTIVITY", 1300m), ("TRANSPORT", 1200m) },
            folio.Categories.Select(c => (c.Category, c.Amount)));
        var kayak = folio.Items.Single(i => i.Category == "ACTIVITY");
        Assert.Equal((2m, 650m, 1300m, "RECEPTIONIST user", "MANUAL"), (kayak.Quantity, kayak.UnitPrice, kayak.Amount, kayak.PostedByName, kayak.Source));

        // the front desk sees what is owed, and check-out is refused until it is paid (leaving today, a night early, the
        // second night comes off first: ₹10,950 − ₹3,500)
        var inHouse = await desk.GetJsonAsync<FrontDeskListDto>($"/api/v1/properties/{pid}/front-desk/in-house");
        Assert.Equal(10950m, inHouse.Items.Single(i => i.ReservationId == booked.Id).Balance);
        var res = await desk.PostAsync($"/api/v1/properties/{pid}/reservations/{booked.Id}/check-out", new CheckOutRequest(null, "Leaving after breakfast"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var (code, message) = await ProblemAsync(res);
        Assert.Equal("BALANCE_DUE", code);
        Assert.StartsWith($"{booked.Reference} still owes ₹7,450. Take the payment before check-out", message);
        Assert.Equal(10950m, (await GetAsync(desk, pid, folio.Id)).Total);   // refused, so nothing changed

        // and the list of folios shows it
        var list = await desk.GetJsonAsync<PagedResult<FolioListItemDto>>($"/api/v1/properties/{pid}/folios?q={folio.Number}");
        var row = Assert.Single(list.Items);
        Assert.Equal((booked.Reference, "Asha Rao", 10950m, 5), (row.Reference, row.GuestName, row.Balance, row.Lines));
    }

    // ------------------------------------------------------------------ immutability (required test)

    [Fact]
    public async Task Delete_and_update_of_folio_lines_are_rejected_by_the_database()
    {
        var (owner, pid) = await OwnerAsync();
        var (folio, _) = await InHouseFolioAsync(owner, pid);
        folio = await PostAsync(owner, pid, folio.Id, new PostChargeRequest("BEVERAGE", "Fresh lime soda", 2, 120m));
        var line = folio.Items.Single(i => i.Category == "BEVERAGE");

        foreach (var sql in new[]
                 {
                     $"DELETE FROM folio_items WHERE id = '{line.Id}'",
                     $"UPDATE folio_items SET description = 'changed' WHERE id = '{line.Id}'",
                     $"DELETE FROM folios WHERE id = '{folio.Id}'",
                 })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
        Assert.Equal(240m, (await GetAsync(owner, pid, folio.Id)).Items.Single(i => i.Id == line.Id).Total);

        // the database also refuses a line whose amount isn't quantity × price, or a "reversal" that doesn't negate its charge
        var bad = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"""
             INSERT INTO folio_items (organization_id, property_id, folio_id, reservation_id, item_type, category, description, service_date,
                                      quantity, unit_price, amount, total, source)
             SELECT organization_id, property_id, folio_id, reservation_id, 'CHARGE', 'FOOD', 'x', service_date, 2, 100, 150, 150, 'MANUAL'
               FROM folio_items WHERE id = '{line.Id}'
             """));
        Assert.Equal("ck_folio_items_amounts", bad.ConstraintName);
        var wrong = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"""
             INSERT INTO folio_items (organization_id, property_id, folio_id, reservation_id, item_type, category, description, service_date,
                                      quantity, unit_price, amount, total, source, reverses_item_id, reason)
             SELECT organization_id, property_id, folio_id, reservation_id, 'REVERSAL', category, 'x', service_date, -1, unit_price, -unit_price,
                    -unit_price, source, id, 'half only' FROM folio_items WHERE id = '{line.Id}'
             """));
        Assert.Equal("ck_folio_items_reversal", wrong.ConstraintName);
    }

    // ------------------------------------------------------------------ reversals (required test)

    [Fact]
    public async Task A_reversal_needs_folio_adjust_and_a_reason_and_a_charge_is_reversed_once()
    {
        var (owner, pid) = await OwnerAsync();
        var (email, pw) = await app.CreateUserAsync("RECEPTIONIST", pid);
        var desk = await app.SignInAsync(email, pw);
        var (folio, _) = await InHouseFolioAsync(owner, pid);
        folio = await PostAsync(desk, pid, folio.Id, new PostChargeRequest("FOOD", "Lunch", 1, 900m));
        var lunch = folio.Items.Single(i => i.Category == "FOOD");
        var url = $"/api/v1/properties/{pid}/folios/{folio.Id}/charges/{lunch.Id}/reverse";

        // a receptionist can post but not reverse
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PostAsync(url, new ReverseChargeRequest("Wrong guest"))).StatusCode);
        // a manager must say why
        Assert.Equal("Say why the charge is reversed (at least 3 characters), e.g. Posted to the wrong guest.",
            await FieldErrorAsync(await owner.PostAsync(url, new ReverseChargeRequest(" ")), "Reason"));

        var before = folio.Total;
        folio = await FolioOkAsync(await owner.PostAsync(url, new ReverseChargeRequest("Guest ate at the restaurant on another booking")));
        var original = folio.Items.Single(i => i.Id == lunch.Id);
        var reversal = folio.Items.Single(i => i.ItemType == "REVERSAL");
        Assert.False(original.Live);
        Assert.Equal(reversal.Id, original.ReversedByItemId);
        Assert.Equal("Guest ate at the restaurant on another booking", original.ReversalReason);
        Assert.Equal((lunch.Id, -1m, 900m, -900m, "Reversal: Lunch", "FOOD"),
            (reversal.ReversesItemId!.Value, reversal.Quantity, reversal.UnitPrice, reversal.Total, reversal.Description, reversal.Category));
        Assert.Equal(before - 900m, folio.Total);
        Assert.Equal(-900m, folio.Reversals);
        Assert.DoesNotContain(folio.Categories, c => c.Category == "FOOD" && c.Amount != 0m);

        // only once
        var again = await owner.PostAsync(url, new ReverseChargeRequest("Again"));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var (code, message) = await ProblemAsync(again);
        Assert.Equal("ALREADY_REVERSED", code);
        Assert.Matches("^\"Lunch\" was already reversed at [0-9]{2}:[0-9]{2} on .+ by Test Owner \\(reason: Guest ate at the restaurant on another booking\\)\\. Reload to see it\\.$", message);
        // a reversal can't be reversed
        var (code2, message2) = await ProblemAsync(await owner.PostAsync(
            $"/api/v1/properties/{pid}/folios/{folio.Id}/charges/{reversal.Id}/reverse", new ReverseChargeRequest("Undo")));
        Assert.Equal("NOT_REVERSIBLE", code2);
        Assert.Equal("\"Reversal: Lunch\" is itself a reversal, so it can't be reversed. Post the charge again instead.", message2);

        // both lines are in the audit trail, the reversal with its reason
        Assert.Equal(2L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE entity_type = 'FolioItem' AND entity_id IN ('{lunch.Id}', '{reversal.Id}')"));
        Assert.Equal("Guest ate at the restaurant on another booking",
            await ScalarAsync($"SELECT reason FROM audit_logs WHERE entity_id = '{reversal.Id}'"));
    }

    [Fact]
    public async Task Two_managers_reversing_the_same_charge_at_once_make_exactly_one_reversal_10_times()
    {
        var (owner, pid) = await OwnerAsync();
        var (email, pw) = await app.CreateUserAsync("MANAGER", pid);
        var manager = await app.SignInAsync(email, pw);
        var (folio, _) = await InHouseFolioAsync(owner, pid);
        for (var run = 0; run < 10; run++)
        {
            folio = await PostAsync(owner, pid, folio.Id, new PostChargeRequest("OTHER", $"Phone call {run}", 1, 50m));
            var line = folio.Items.Single(i => i.Description == $"Phone call {run}");
            var url = $"/api/v1/properties/{pid}/folios/{folio.Id}/charges/{line.Id}/reverse";
            using var barrier = new Barrier(2);
            var a = Task.Run(() => { barrier.SignalAndWait(); return owner.PostAsync(url, new ReverseChargeRequest("Not ours")); });
            var b = Task.Run(() => { barrier.SignalAndWait(); return manager.PostAsync(url, new ReverseChargeRequest("Not ours")); });
            var results = await Task.WhenAll(a, b);
            Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
            Assert.Equal("ALREADY_REVERSED", await Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict).ProblemCodeAsync());
            Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM folio_items WHERE reverses_item_id = '{line.Id}'"));
        }
    }

    // ------------------------------------------------------------------ posting

    [Fact]
    public async Task Posting_a_charge_needs_an_idempotency_key_and_a_retry_charges_once()
    {
        var (owner, pid) = await OwnerAsync();
        var (folio, _) = await InHouseFolioAsync(owner, pid);
        var url = $"/api/v1/properties/{pid}/folios/{folio.Id}/charges";
        var body = new PostChargeRequest("TRANSPORT", "Taxi to Panjim", 1, 800m);

        Assert.StartsWith("Send an Idempotency-Key header", await FieldErrorAsync(await owner.PostAsync(url, body), "Idempotency-Key"));

        var key = "taxi-" + Guid.NewGuid();
        var first = await owner.PostAsync(url, body, new Dictionary<string, string> { ["Idempotency-Key"] = key });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var second = await owner.PostAsync(url, body, new Dictionary<string, string> { ["Idempotency-Key"] = key });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Single((await GetAsync(owner, pid, folio.Id)).Items, i => i.Description == "Taxi to Panjim");

        var reused = await owner.PostAsync(url, body with { UnitPrice = 900m }, new Dictionary<string, string> { ["Idempotency-Key"] = key });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", await reused.ProblemCodeAsync());
    }

    [Fact]
    public async Task Charge_errors_say_what_is_wrong_under_the_field()
    {
        var (owner, pid) = await OwnerAsync();
        var (folio, _) = await InHouseFolioAsync(owner, pid);
        var url = $"/api/v1/properties/{pid}/folios/{folio.Id}/charges";
        async Task<string?> Error(PostChargeRequest body, string field) => await FieldErrorAsync(
            await owner.PostAsync(url, body, new Dictionary<string, string> { ["Idempotency-Key"] = "err-" + Guid.NewGuid() }), field);

        Assert.Equal("Choose what the charge is for: Room, Food, Beverage, Activity, Transport, Equipment, Laundry, Other.",
            await Error(new PostChargeRequest("SPA", "Massage", 1, 2000m), "Category"));
        Assert.Equal("Say what the charge is for, e.g. Dinner at the beach shack.", await Error(new PostChargeRequest("FOOD", "  ", 1, 200m), "Description"));
        Assert.Equal("The price must be more than ₹0.", await Error(new PostChargeRequest("FOOD", "Tea", 1, 0m), "UnitPrice"));
        Assert.Equal("The price can have at most 2 decimal places (paise).", await Error(new PostChargeRequest("FOOD", "Tea", 1, 10.005m), "UnitPrice"));
        Assert.Equal("The quantity must be more than 0.", await Error(new PostChargeRequest("FOOD", "Tea", 0, 20m), "Quantity"));
        Assert.Equal("The quantity can have at most 3 decimal places.", await Error(new PostChargeRequest("FOOD", "Tea", 1.2345m, 20m), "Quantity"));
        Assert.Equal($"A charge can't be dated after today ({Day(Today)} at the property). Post it on the day of the service.",
            await Error(new PostChargeRequest("FOOD", "Tea", 1, 20m, Today.AddDays(1)), "ServiceDate"));
        Assert.Equal("A package is sold whole: enter how many, 1 to 50.",
            await Error(new PostChargeRequest(null, null, 1.5m, 0, null, Guid.NewGuid()), "Quantity"));
        Assert.Equal("That package doesn't exist at this property. Reload and choose again.",
            await Error(new PostChargeRequest(null, null, 1, 0, null, Guid.NewGuid()), "PackageId"));
    }

    [Fact]
    public async Task A_package_is_posted_as_one_line_per_component_adding_up_to_its_price()
    {
        var (owner, pid) = await OwnerAsync();
        var (folio, stay) = await InHouseFolioAsync(owner, pid);
        var res = await owner.PostAsync($"/api/v1/properties/{pid}/packages", new CreatePackageRequest(
            "SUNSET_" + Suffix(), "Sunset cruise package", null, stay.Type.Id, 1, 2, 0, 4999m, 0,
            [new PackageComponentInput("ROOM", "Room upgrade", 50m), new PackageComponentInput("FOOD", "Dinner on board", 33.33m),
             new PackageComponentInput("ACTIVITY", "Cruise", 16.67m)]));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var package = (await res.Content.ReadFromJsonAsync<PackageDto>())!;
        var before = folio.Total;

        folio = await PostAsync(owner, pid, folio.Id, new PostChargeRequest(null, null, 2, 0, null, package.Id));
        var lines = folio.Items.Where(i => i.Source == "PACKAGE").ToList();
        Assert.Equal(package.Components.Select(c => (c.Category, $"Sunset cruise package: {c.Description}", c.Amount * 2)),
            lines.Select(l => (l.Category, l.Description, l.Total)));
        Assert.Equal(9998m, lines.Sum(l => l.Total));
        Assert.Single(lines.Select(l => l.GroupId).Distinct());
        Assert.All(lines, l => Assert.Equal("Sunset cruise package", l.PackageName));
        Assert.Equal(before + 9998m, folio.Total);
    }

    // ------------------------------------------------------------------ when the bill opens, and room nights

    [Fact]
    public async Task The_folio_opens_when_the_booking_is_confirmed_and_closes_to_charges_when_it_is_cancelled()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 2);
        var guest = await CreateGuestAsync(owner);
        var source = await WalkInAsync(owner, pid);

        var pending = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, source, Today.AddDays(5), Today.AddDays(6)) with { Status = "PENDING" }));
        Assert.Empty(await owner.GetJsonAsync<List<FolioListItemDto>>($"/api/v1/properties/{pid}/reservations/{pending.Id}/folios"));
        await FrontDeskTests.OkAsync(await owner.PostAsync($"/api/v1/properties/{pid}/reservations/{pending.Id}/confirm", new ReservationStatusRequest(null)));
        var folio = Assert.Single(await owner.GetJsonAsync<List<FolioListItemDto>>($"/api/v1/properties/{pid}/reservations/{pending.Id}/folios"));
        Assert.True(folio.IsPrimary);
        Assert.Equal("Main", folio.Name);

        // a deposit-style charge before arrival is fine; after cancelling, nothing more can be charged
        await PostAsync(owner, pid, folio.Id, new PostChargeRequest("TRANSPORT", "Airport pick-up booked", 1, 1200m));
        await FrontDeskTests.OkAsync(await owner.PostAsync($"/api/v1/properties/{pid}/reservations/{pending.Id}/cancel", new ReservationStatusRequest("Guest changed plans")));
        var res = await owner.PostAsync($"/api/v1/properties/{pid}/folios/{folio.Id}/charges", new PostChargeRequest("FOOD", "Tea", 1, 50m),
            new Dictionary<string, string> { ["Idempotency-Key"] = "closed-" + Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var (code, message) = await ProblemAsync(res);
        Assert.Equal("FOLIO_CLOSED", code);
        Assert.Equal($"{pending.Reference} is cancelled, so nothing can be charged to {folio.Number}.", message);
        var dto = await GetAsync(owner, pid, folio.Id);
        Assert.False(dto.CanPost);
        Assert.Equal(message, dto.CantPostReason);

        // a walk-in is confirmed and checked in in one go: its folio has tonight on it
        var walkIn = await owner.PostAsync($"/api/v1/properties/{pid}/walk-ins",
            new WalkInRequest(guest.Id, null, Today.AddDays(1), [new ReservationRoomRequest(stay.Type.Id, null, 1, 0)], stay.Plan.Id, null, null),
            new Dictionary<string, string> { ["Idempotency-Key"] = "walkin-" + Guid.NewGuid() });
        var walked = await CreatedAsync(walkIn);
        var walkFolio = await MainFolioAsync(owner, pid, walked.Id);
        Assert.Equal(new[] { Today }, walkFolio.Items.Select(i => i.ServiceDate));
        Assert.Equal(3500m, walkFolio.Total);
    }

    [Fact]
    public async Task Nightly_posting_charges_each_night_once_and_check_out_completes_the_bill()
    {
        var (owner, pid) = await OwnerAsync();
        await PutSettingAsync(owner, pid, "folio.room_charge_posting", JsonSerializer.SerializeToElement("NIGHTLY"));
        await PutSettingAsync(owner, pid, "frontdesk.allow_checkout_with_balance", JsonSerializer.SerializeToElement(true));
        try
        {
            var stay = await SetupAsync(owner, pid, rooms: 1, price: 2500m);
            var guest = await CreateGuestAsync(owner);
            var booked = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, await WalkInAsync(owner, pid), Today, Today.AddDays(3))));
            await CheckInAsync(owner, pid, booked);
            var folio = await MainFolioAsync(owner, pid, booked.Id);
            Assert.Equal(new[] { Today }, folio.Items.Select(i => i.ServiceDate));   // tonight only

            Assert.Equal(0, await RunForAsync(booked.Id));                          // nothing new due today
            app.Clock.At = DateTimeOffset.UtcNow.AddDays(1);
            try
            {
                Assert.Equal(1, await RunForAsync(booked.Id));                      // tomorrow: the second night
                Assert.Equal(0, await RunForAsync(booked.Id));                      // and only once
            }
            finally { app.Clock.At = null; }
            folio = await GetAsync(owner, pid, folio.Id);
            Assert.Equal(new[] { Today, Today.AddDays(1) }, folio.Items.Select(i => i.ServiceDate));

            // the database itself refuses a second charge for the same night
            var dup = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
                $"""
                 INSERT INTO folio_items (organization_id, property_id, folio_id, reservation_id, item_type, category, description, service_date,
                                          quantity, unit_price, amount, total, source)
                 SELECT organization_id, property_id, folio_id, reservation_id, 'CHARGE', 'ROOM', 'again', service_date, 1, 1, 1, 1, 'ROOM_NIGHT'
                   FROM folio_items WHERE folio_id = '{folio.Id}' LIMIT 1
                 """));
            Assert.Equal("ux_folio_items_room_night", dup.ConstraintName);
        }
        finally
        {
            await PutSettingAsync(owner, pid, "folio.room_charge_posting", JsonSerializer.SerializeToElement("AT_CHECK_IN"));
            await PutSettingAsync(owner, pid, "frontdesk.allow_checkout_with_balance", JsonSerializer.SerializeToElement(false));
        }
    }

    [Fact]
    public async Task Leaving_early_reverses_the_nights_not_stayed()
    {
        var (owner, pid) = await OwnerAsync();
        var stay = await SetupAsync(owner, pid, rooms: 1, price: 3000m);
        var guest = await CreateGuestAsync(owner);
        var booked = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, await WalkInAsync(owner, pid), Today, Today.AddDays(3))));
        await CheckInAsync(owner, pid, booked);
        var folio = await MainFolioAsync(owner, pid, booked.Id);
        Assert.Equal(9000m, folio.Total);

        await PutSettingAsync(owner, pid, "frontdesk.allow_checkout_with_balance", JsonSerializer.SerializeToElement(true));
        try
        {
            // checking out on the arrival day still pays the first night; the other two come off the bill
            await FrontDeskTests.OkAsync(await owner.PostAsync($"/api/v1/properties/{pid}/reservations/{booked.Id}/check-out",
                new CheckOutRequest(null, "Family emergency")));
        }
        finally { await PutSettingAsync(owner, pid, "frontdesk.allow_checkout_with_balance", JsonSerializer.SerializeToElement(false)); }

        folio = await GetAsync(owner, pid, folio.Id);
        var reversals = folio.Items.Where(i => i.ItemType == "REVERSAL").ToList();
        Assert.Equal(new[] { Today.AddDays(1), Today.AddDays(2) }, reversals.Select(r => r.ServiceDate));
        Assert.All(reversals, r => Assert.Equal($"Left early on {Day(Today.AddDays(1))}: Family emergency", r.Reason));
        Assert.Equal(3000m, folio.Total);
        Assert.Equal("CHECKED_OUT", folio.ReservationStatus);
        Assert.True(folio.CanPost);   // a late charge (minibar found at cleaning) can still go on
    }

    // ------------------------------------------------------------------ split

    [Fact]
    public async Task Splitting_moves_lines_to_another_folio_and_keeps_the_bookings_total()
    {
        var (owner, pid) = await OwnerAsync();
        var (email, pw) = await app.CreateUserAsync("RECEPTIONIST", pid);
        var desk = await app.SignInAsync(email, pw);
        var (folio, _) = await InHouseFolioAsync(owner, pid);
        folio = await PostAsync(desk, pid, folio.Id, new PostChargeRequest("BEVERAGE", "Bar", 1, 600m));
        var rooms = folio.Items.Where(i => i.Source == "ROOM_NIGHT").ToList();
        var bookingTotal = folio.Total;

        var res = await desk.PostAsync($"/api/v1/properties/{pid}/folios/{folio.Id}/split",
            new SplitFolioRequest(rooms.Select(i => i.Id).ToList(), null, "Company: Acme Travels"));
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var split = (await res.Content.ReadFromJsonAsync<SplitFolioResultDto>())!;

        Assert.Equal(600m, split.From.Total);
        Assert.Equal(rooms.Sum(i => i.Total), split.To.Total);
        Assert.Equal(bookingTotal, split.From.Total + split.To.Total);
        Assert.Equal(("Company: Acme Travels", false), (split.To.Name, split.To.IsPrimary));
        Assert.All(split.From.Items.Where(i => i.ItemType == "REVERSAL"), i => Assert.Equal($"Moved to {split.To.Number} (Company: Acme Travels)", i.Reason));
        Assert.All(rooms, r => Assert.Equal(split.To.Number, split.From.Items.Single(i => i.Id == r.Id).MovedTo));
        Assert.All(split.To.Items, i => Assert.Equal(split.From.Number, i.MovedFrom));
        Assert.Equal(new[] { split.From.Number, split.To.Number }, split.To.Folios.Select(f => f.Number));

        // a moved line can't be moved or reversed again from the old folio
        var again = await desk.PostAsync($"/api/v1/properties/{pid}/folios/{folio.Id}/split", new SplitFolioRequest([rooms[0].Id], split.To.Id, null));
        Assert.StartsWith($"\"{rooms[0].Description}\" was moved to {split.To.Number} at ", await FieldErrorAsync(again, "ItemIds"));
        Assert.Equal("Choose the folio to move the lines to, or name a new one (one of the two).", await FieldErrorAsync(
            await desk.PostAsync($"/api/v1/properties/{pid}/folios/{folio.Id}/split", new SplitFolioRequest([rooms[0].Id], split.To.Id, "Both")), "TargetFolioId"));

        // the night moved with its line: posting room nights again adds nothing
        Assert.Equal(0, await RunForAsync(folio.ReservationId));
        Assert.Equal(bookingTotal, (await desk.GetJsonAsync<FrontDeskListDto>($"/api/v1/properties/{pid}/front-desk/in-house"))
            .Items.Single(i => i.ReservationId == folio.ReservationId).Balance);
    }

    // ------------------------------------------------------------------ permissions and tenancy

    [Fact]
    public async Task Housekeepers_cant_see_folios_and_another_organisation_cant_reach_this_one()
    {
        var (owner, pid) = await OwnerAsync();
        var (folio, _) = await InHouseFolioAsync(owner, pid);
        var (email, pw) = await app.CreateUserAsync("HOUSEKEEPER", pid);
        var hk = await app.SignInAsync(email, pw);
        Assert.Equal(HttpStatusCode.Forbidden, (await hk.GetAsync($"/api/v1/properties/{pid}/folios/{folio.Id}")).StatusCode);

        var other = await app.CreateSecondTenantAsync();
        var stranger = await app.SignInAsync(other.OwnerEmail, other.Password);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/properties/{other.PropertyId}/folios/{folio.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/properties/{pid}/folios/{folio.Id}")).StatusCode);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(TestClient Owner, Guid PropertyId)> OwnerAsync() => (await app.SignInOwnerAsync(), await app.MainPropertyIdAsync());

    /// <summary>A guest checked in today for two nights at ₹3,500, and their main folio (₹7,000 of room nights).</summary>
    private async Task<(FolioDto Folio, Stay Stay)> InHouseFolioAsync(TestClient owner, Guid pid)
    {
        var stay = await SetupAsync(owner, pid, rooms: 1);
        var guest = await CreateGuestAsync(owner);
        var booked = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, await WalkInAsync(owner, pid), Today, Today.AddDays(2))));
        await CheckInAsync(owner, pid, booked);
        return (await MainFolioAsync(owner, pid, booked.Id), stay);
    }

    private static async Task CheckInAsync(TestClient c, Guid pid, ReservationDto r) =>
        await FrontDeskTests.OkAsync(await c.PostAsync(FrontDeskTests.CheckInUrl(pid, r), new CheckInRequest(null)));

    private static async Task<FolioDto> MainFolioAsync(TestClient c, Guid pid, Guid reservationId)
    {
        var list = await c.GetJsonAsync<List<FolioListItemDto>>($"/api/v1/properties/{pid}/reservations/{reservationId}/folios");
        return await GetAsync(c, pid, list.Single(f => f.IsPrimary).Id);
    }

    private static Task<FolioDto> GetAsync(TestClient c, Guid pid, Guid folioId) => c.GetJsonAsync<FolioDto>($"/api/v1/properties/{pid}/folios/{folioId}");

    private static async Task<FolioDto> PostAsync(TestClient c, Guid pid, Guid folioId, PostChargeRequest body)
    {
        var res = await c.PostAsync($"/api/v1/properties/{pid}/folios/{folioId}/charges", body,
            new Dictionary<string, string> { ["Idempotency-Key"] = "charge-" + Guid.NewGuid() });
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<FolioDto>())!;
    }

    private static async Task<FolioDto> FolioOkAsync(HttpResponseMessage res)
    {
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<FolioDto>())!;
    }

    /// <summary>Runs the nightly job and returns how many nights it posted for this booking (other tests' guests are in house too).</summary>
    private async Task<int> RunForAsync(Guid reservationId)
    {
        var before = (long)(await ScalarAsync($"SELECT count(*) FROM folio_items WHERE reservation_id = '{reservationId}'"))!;
        await app.RunRoomChargesAsync();
        return (int)((long)(await ScalarAsync($"SELECT count(*) FROM folio_items WHERE reservation_id = '{reservationId}'"))! - before);
    }

    private static async Task PutSettingAsync(TestClient c, Guid pid, string key, JsonElement value) =>
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsync($"/api/v1/properties/{pid}/settings/{key}", new PutPropertySettingRequest(value))).StatusCode);

    private static string Day(DateOnly d) => FrontDeskTests.Day(d);

    private static async Task<(string? Code, string? Message)> ProblemAsync(HttpResponseMessage res) => await FrontDeskTests.ProblemAsync(res);

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
