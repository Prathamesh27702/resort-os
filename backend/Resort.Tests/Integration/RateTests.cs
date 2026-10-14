using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Resort.Contracts;
using Resort.Tests.Support;

namespace Resort.Tests.Integration;

/// <summary>Sprint 4 — rate plans, the rate grid, quotes and packages. Real API, real PostgreSQL.</summary>
public class RateTests(ResortApiFactory app) : IClassFixture<ResortApiFactory>
{
    private static readonly DateOnly Thu = new(2027, 1, 7);   // a Thursday

    // ------------------------------------------------------------------ rate plans

    [Fact]
    public async Task First_plan_becomes_the_default_and_making_another_default_clears_it()
    {
        var (owner, pid) = await OwnerAsync();
        var first = await CreatePlanAsync(owner, pid);
        Assert.True(first.IsDefault);

        var second = await CreatePlanAsync(owner, pid, isDefault: true);
        var plans = await owner.GetJsonAsync<List<RatePlanDto>>($"/api/v1/properties/{pid}/rate-plans");
        Assert.True(plans.Single(p => p.Id == second.Id).IsDefault);
        Assert.False(plans.Single(p => p.Id == first.Id).IsDefault);

        // the default can't simply be switched off: the reason says what to do instead
        var res = await owner.PutAsync($"/api/v1/properties/{pid}/rate-plans/{second.Id}", UpdateOf(second) with { IsDefault = false });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var err = await FieldErrorAsync(res, "IsDefault");
        Assert.Contains("Make another plan the default", err);
    }

    // ------------------------------------------------------------------ rate grid

    [Fact]
    public async Task Price_change_is_audited_with_old_and_new_values()
    {
        var (owner, pid) = await OwnerAsync();
        var type = await CreateTypeAsync(owner, pid);
        var plan = await CreatePlanAsync(owner, pid);

        var set = await SetPricesAsync(owner, pid, plan.Id, type.Id, Thu, Thu.AddDays(1), 3500m);
        Assert.Equal(new SetRatePricesResult(1, 0, 0), set);
        set = await SetPricesAsync(owner, pid, plan.Id, type.Id, Thu, Thu.AddDays(1), 3800m);
        Assert.Equal(new SetRatePricesResult(0, 1, 0), set);
        set = await SetPricesAsync(owner, pid, plan.Id, type.Id, Thu, Thu.AddDays(1), 3800m);
        Assert.Equal(new SetRatePricesResult(0, 0, 1), set);   // no change, no audit row

        var cellId = await ScalarAsync(
            $"SELECT id::text FROM rate_plan_prices WHERE rate_plan_id = '{plan.Id}' AND stay_date = '{Thu:yyyy-MM-dd}'");
        var rows = await RowsAsync(
            $"SELECT action, changes::text, user_id::text FROM audit_logs WHERE entity_type = 'RatePlanPrice' AND entity_id = '{cellId}' ORDER BY id");
        Assert.Equal(2, rows.Count);
        Assert.Equal("INSERT", rows[0][0]);
        Assert.Equal("UPDATE", rows[1][0]);
        using var changes = JsonDocument.Parse((string)rows[1][1]!);
        var price = changes.RootElement.GetProperty("Price");
        Assert.Equal(3500m, price.GetProperty("old").GetDecimal());
        Assert.Equal(3800m, price.GetProperty("new").GetDecimal());
        Assert.Equal(owner.Me!.UserId.ToString(), rows[1][2]);
        Assert.False(changes.RootElement.TryGetProperty("ExtraAdult", out _));   // only what changed
    }

    [Fact]
    public async Task Weekend_uplift_by_days_of_week_and_quote_matches_the_grid()
    {
        var (owner, pid) = await OwnerAsync();
        var type = await CreateTypeAsync(owner, pid, baseOcc: 2, maxOcc: 3);
        var room = await CreateRoomAsync(owner, pid, type.Id);
        var plan = await CreatePlanAsync(owner, pid, isDefault: true);

        // whole fortnight at ₹3,500, then Friday and Saturday nights at ₹4,200
        await SetPricesAsync(owner, pid, plan.Id, type.Id, Thu, Thu.AddDays(14), 3500m, extraAdult: 1000m, extraChild: 500m);
        var weekend = await SetPricesAsync(owner, pid, plan.Id, type.Id, Thu, Thu.AddDays(14), 4200m, extraAdult: 1000m, extraChild: 500m,
            days: [DayOfWeek.Friday, DayOfWeek.Saturday]);
        Assert.Equal(new SetRatePricesResult(0, 4, 0), weekend);

        var grid = await owner.GetJsonAsync<List<RatePriceDto>>(
            $"/api/v1/properties/{pid}/rate-plans/{plan.Id}/prices?from={Thu:yyyy-MM-dd}&to={Thu.AddDays(3):yyyy-MM-dd}");
        Assert.Equal([3500m, 4200m, 4200m], grid.Select(g => g.Price));

        // quote by room code, the default plan, 2 adults, 3 nights (Thu, Fri, Sat)
        var q = await owner.GetJsonAsync<RateQuoteDto>(
            $"/api/v1/properties/{pid}/rates/quote?room={room.Code}&from={Thu:yyyy-MM-dd}&to={Thu.AddDays(3):yyyy-MM-dd}&adults=2");
        Assert.Equal(3, q.Nights);
        Assert.Equal(room.Code, q.RoomCode);
        Assert.Equal(plan.Id, q.RatePlanId);
        Assert.Equal(grid.Select(g => (g.Date, g.Price)), q.Breakdown.Select(n => (n.Date, n.Total)));
        Assert.Equal(11900m, q.Total);
        Assert.Equal("INR", q.Currency);

        // third adult pays the extra-adult rate every night
        var three = await owner.GetJsonAsync<RateQuoteDto>(
            $"/api/v1/properties/{pid}/rates/quote?type={type.Code}&ratePlan={plan.Code}&from={Thu:yyyy-MM-dd}&to={Thu.AddDays(3):yyyy-MM-dd}&adults=3");
        Assert.Equal(11900m + 3 * 1000m, three.Total);
    }

    [Fact]
    public async Task Quote_errors_say_exactly_why()
    {
        var (owner, pid) = await OwnerAsync();
        var type = await CreateTypeAsync(owner, pid, baseOcc: 2, maxOcc: 2);
        var plan = await CreatePlanAsync(owner, pid);
        await SetPricesAsync(owner, pid, plan.Id, type.Id, Thu, Thu.AddDays(1), 3500m);
        var url = $"/api/v1/properties/{pid}/rates/quote?type={type.Id}&ratePlan={plan.Id}";

        var missing = await owner.GetAsync($"{url}&from={Thu:yyyy-MM-dd}&to={Thu.AddDays(2):yyyy-MM-dd}&adults=2");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        var body = await missing.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("RATE_NOT_SET", body.GetProperty("code").GetString());
        Assert.Contains("Fri 8 Jan 2027", body.GetProperty("message").GetString());
        Assert.Equal(Thu.AddDays(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), body.GetProperty("details").GetProperty("dates")[0].GetString());

        var crowded = await owner.GetAsync($"{url}&from={Thu:yyyy-MM-dd}&to={Thu.AddDays(1):yyyy-MM-dd}&adults=2&children=1");
        Assert.Equal("TOO_MANY_GUESTS", await crowded.ProblemCodeAsync());

        var noDates = await owner.GetAsync($"{url}&adults=2");
        Assert.Equal(HttpStatusCode.BadRequest, noDates.StatusCode);
        Assert.Equal("Enter the arrival date (from=YYYY-MM-DD).", await FieldErrorAsync(noDates, "From"));

        var unknown = await owner.GetAsync($"/api/v1/properties/{pid}/rates/quote?room=999&from={Thu:yyyy-MM-dd}&to={Thu.AddDays(1):yyyy-MM-dd}&adults=2");
        Assert.Equal("There is no room '999' at this property.", await FieldErrorAsync(unknown, "Room"));
    }

    [Fact]
    public async Task Dormitory_prices_have_no_extras_and_the_reason_is_given()
    {
        var (owner, pid) = await OwnerAsync();
        var dorm = await CreateTypeAsync(owner, pid, mode: "BED", baseOcc: 1, maxOcc: 1);
        var plan = await CreatePlanAsync(owner, pid);

        var res = await owner.PutAsync($"/api/v1/properties/{pid}/rate-plans/{plan.Id}/prices",
            new SetRatePricesRequest([dorm.Id], Thu, Thu.AddDays(2), null, 800m, 200m, 0m, 1));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("is sold per bed", await FieldErrorAsync(res, "ExtraAdult"));

        await SetPricesAsync(owner, pid, plan.Id, dorm.Id, Thu, Thu.AddDays(2), 800m);
        var q = await owner.GetJsonAsync<RateQuoteDto>(
            $"/api/v1/properties/{pid}/rates/quote?type={dorm.Id}&ratePlan={plan.Code}&from={Thu:yyyy-MM-dd}&to={Thu.AddDays(2):yyyy-MM-dd}&adults=3&children=1");
        Assert.Equal(4, q.Units);
        Assert.Equal(2 * 4 * 800m, q.Total);
    }

    [Fact]
    public async Task Bulk_edit_field_rules_give_reasons()
    {
        var (owner, pid) = await OwnerAsync();
        var type = await CreateTypeAsync(owner, pid);
        var plan = await CreatePlanAsync(owner, pid);

        var res = await owner.PutAsync($"/api/v1/properties/{pid}/rate-plans/{plan.Id}/prices",
            new SetRatePricesRequest([type.Id], Thu, Thu, null, -1m, 0.001m, 0m, 0));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var details = body.GetProperty("details");
        Assert.Equal("The end date must be after the start date (the end date itself is not changed).", details.GetProperty("To")[0].GetString());
        Assert.Equal("Price can't be negative.", details.GetProperty("Price")[0].GetString());
        Assert.Equal("Extra adult price can have at most 2 decimal places (paise).", details.GetProperty("ExtraAdult")[0].GetString());
        Assert.Equal("Minimum stay must be between 1 and 30 nights.", details.GetProperty("MinStay")[0].GetString());
        Assert.DoesNotContain("fix", body.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);

        var sunday = await owner.PutAsync($"/api/v1/properties/{pid}/rate-plans/{plan.Id}/prices",
            new SetRatePricesRequest([type.Id], Thu, Thu.AddDays(2), [DayOfWeek.Sunday], 100m, 0m, 0m, 1));
        Assert.Equal("None of the nights from Thu 7 Jan 2027 to Fri 8 Jan 2027 falls on Sunday.", await FieldErrorAsync(sunday, "DaysOfWeek"));
    }

    [Fact]
    public async Task Receptionist_can_quote_but_not_change_prices()
    {
        var (owner, pid) = await OwnerAsync();
        var type = await CreateTypeAsync(owner, pid);
        var plan = await CreatePlanAsync(owner, pid);
        await SetPricesAsync(owner, pid, plan.Id, type.Id, Thu, Thu.AddDays(1), 3500m);

        var (email, pw) = await app.CreateUserAsync("RECEPTIONIST", pid);
        var desk = await app.SignInAsync(email, pw);
        var q = await desk.GetAsync($"/api/v1/properties/{pid}/rates/quote?type={type.Id}&ratePlan={plan.Id}&from={Thu:yyyy-MM-dd}&to={Thu.AddDays(1):yyyy-MM-dd}&adults=1");
        Assert.Equal(HttpStatusCode.OK, q.StatusCode);
        var change = await desk.PutAsync($"/api/v1/properties/{pid}/rate-plans/{plan.Id}/prices",
            new SetRatePricesRequest([type.Id], Thu, Thu.AddDays(1), null, 1m, 0m, 0m, 1));
        Assert.Equal(HttpStatusCode.Forbidden, change.StatusCode);

        // another organisation can't even see that this property exists
        var other = await app.CreateSecondTenantAsync();
        var outsider = await app.SignInAsync(other.OwnerEmail, other.Password);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/v1/properties/{pid}/rate-plans")).StatusCode);
    }

    // ------------------------------------------------------------------ packages

    [Fact]
    public async Task Package_split_adds_up_to_the_price_to_the_paisa()
    {
        var (owner, pid) = await OwnerAsync();
        var type = await CreateTypeAsync(owner, pid, baseOcc: 2, maxOcc: 3);

        var res = await owner.PostAsync($"/api/v1/properties/{pid}/packages", NewPackage(type.Id, 999.99m,
            [new("ROOM", "1 night", 33.33m), new("FOOD", "Breakfast", 33.33m), new("ACTIVITY", "Kayak", 33.34m)]));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var pkg = (await res.Content.ReadFromJsonAsync<PackageDto>())!;
        Assert.Equal([333.30m, 333.30m, 333.39m], pkg.Components.Select(c => c.Amount));
        Assert.Equal(999.99m, pkg.Components.Sum(c => c.Amount));
        Assert.Equal(333.30m, pkg.RevenueByCategory["FOOD"]);

        // editing the price recomputes the split; components are replaced as a set
        var upd = await owner.PutAsync($"/api/v1/properties/{pid}/packages/{pkg.Id}", new UpdatePackageRequest(
            pkg.Name, null, type.Id, 2, 2, 0, 14999m, 0,
            [new("ROOM", "2 nights", 70m), new("FOOD", "Meals", 15m), new("TRANSPORT", "Pick-up", 5m), new("ACTIVITY", "Spa", 10m)],
            true, pkg.Version));
        Assert.Equal(HttpStatusCode.OK, upd.StatusCode);
        var edited = (await upd.Content.ReadFromJsonAsync<PackageDto>())!;
        Assert.Equal([10499.30m, 2249.85m, 749.95m, 1499.90m], edited.Components.Select(c => c.Amount));
        Assert.Equal(4L, await ScalarAsync($"SELECT count(*) FROM package_components WHERE package_id = '{pkg.Id}'"));

        // the database refuses a split that doesn't add up, even if someone bypasses the API
        var ex = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(
            $"UPDATE package_components SET amount = amount + 0.01 WHERE package_id = '{pkg.Id}' AND category = 'ROOM'"));
        Assert.Equal("ck_packages_split_sum", ex.ConstraintName);
    }

    [Fact]
    public async Task Package_errors_say_why_under_the_field()
    {
        var (owner, pid) = await OwnerAsync();
        var type = await CreateTypeAsync(owner, pid, baseOcc: 2, maxOcc: 2);
        var url = $"/api/v1/properties/{pid}/packages";

        var shares = await owner.PostAsync(url, NewPackage(type.Id, 4100m, [new("ROOM", "Room", 80m), new("FOOD", "Breakfast", 15.5m)]));
        Assert.Equal("The shares add up to 95.5%; they must add up to exactly 100%.", await FieldErrorAsync(shares, "Components"));

        var noRoom = await owner.PostAsync(url, NewPackage(type.Id, 4100m, [new("FOOD", "Breakfast", 100m)]));
        Assert.Equal("Add a ROOM component: every package includes the stay.", await FieldErrorAsync(noRoom, "Components"));

        var badPart = await owner.PostAsync(url, NewPackage(type.Id, 4100m, [new("ROOM", "", 100m)]));
        Assert.Equal("Say what this part includes, e.g. Breakfast for two.", await FieldErrorAsync(badPart, "Components[0].Description"));

        var crowded = await owner.PostAsync(url, NewPackage(type.Id, 4100m, [new("ROOM", "Room", 100m)]) with { Adults = 3 });
        Assert.Equal($"{type.Name} sleeps at most 2 guests; this package includes 3.", await FieldErrorAsync(crowded, "Adults"));

        var ok = await owner.PostAsync(url, NewPackage(type.Id, 4100m, [new("ROOM", "Room", 85m), new("FOOD", "Breakfast", 15m)]));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var pkg = (await ok.Content.ReadFromJsonAsync<PackageDto>())!;
        var dup = await owner.PostAsync(url, NewPackage(type.Id, 4100m, [new("ROOM", "Room", 100m)]) with { Code = pkg.Code });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Equal("PACKAGE_CODE_TAKEN", await dup.ProblemCodeAsync());
    }

    [Fact]
    public async Task Reservation_sources_list_the_built_in_channels()
    {
        var (owner, pid) = await OwnerAsync();
        var sources = await owner.GetJsonAsync<List<ReservationSourceDto>>($"/api/v1/properties/{pid}/reservation-sources");
        Assert.Contains(sources, s => s.Code == "WALK_IN" && s.Channel == "DIRECT");
        Assert.Contains(sources, s => s.Code == "MAKEMYTRIP" && s.Channel == "OTA");
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(TestClient Owner, Guid PropertyId)> OwnerAsync() =>
        (await app.SignInOwnerAsync(), await app.MainPropertyIdAsync());

    private static string Suffix() => Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private static async Task<AccommodationTypeDto> CreateTypeAsync(TestClient c, Guid pid, string mode = "UNIT", short baseOcc = 2, short maxOcc = 2)
    {
        var res = await c.PostAsync($"/api/v1/properties/{pid}/accommodation-types",
            new CreateAccommodationTypeRequest("T_" + Suffix(), mode == "BED" ? "Dorm bed" : "Garden room", null, mode, baseOcc, maxOcc, null, 0, null));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<AccommodationTypeDto>())!;
    }

    private static async Task<RoomDto> CreateRoomAsync(TestClient c, Guid pid, Guid typeId)
    {
        var code = "R" + Suffix();
        var res = await c.PostAsync($"/api/v1/properties/{pid}/rooms", new CreateRoomRequest(code, "Room " + code, typeId, null, null, 0));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<RoomDto>())!;
    }

    private static async Task<RatePlanDto> CreatePlanAsync(TestClient c, Guid pid, bool isDefault = false)
    {
        var res = await c.PostAsync($"/api/v1/properties/{pid}/rate-plans",
            new CreateRatePlanRequest("P_" + Suffix(), "Plan " + Suffix(), null, "EP", isDefault, 0));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<RatePlanDto>())!;
    }

    private static UpdateRatePlanRequest UpdateOf(RatePlanDto p) =>
        new(p.Name, p.Description, p.MealPlan, p.IsDefault, p.SortOrder, p.IsActive, p.Version);

    private static async Task<SetRatePricesResult> SetPricesAsync(TestClient c, Guid pid, Guid planId, Guid typeId, DateOnly from, DateOnly to,
        decimal price, decimal extraAdult = 0m, decimal extraChild = 0m, IReadOnlyList<DayOfWeek>? days = null)
    {
        var res = await c.PutAsync($"/api/v1/properties/{pid}/rate-plans/{planId}/prices",
            new SetRatePricesRequest([typeId], from, to, days, price, extraAdult, extraChild, 1));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<SetRatePricesResult>())!;
    }

    private static CreatePackageRequest NewPackage(Guid typeId, decimal price, IReadOnlyList<PackageComponentInput> parts) =>
        new("PK_" + Suffix(), "Package " + Suffix(), null, typeId, 1, 2, 0, price, 0, parts);

    /// <summary>The first validation reason for <paramref name="field"/> in a 400 VALIDATION_FAILED reply.</summary>
    private static async Task<string?> FieldErrorAsync(HttpResponseMessage res, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_FAILED", body.GetProperty("code").GetString());
        return body.GetProperty("details").GetProperty(field)[0].GetString();
    }

    private async Task SqlAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }

    private async Task<List<object?[]>> RowsAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await r.ReadAsync())
        {
            var row = new object?[r.FieldCount];
            for (var i = 0; i < r.FieldCount; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }
}
