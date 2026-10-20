using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Resort.Contracts;
using Resort.Tests.Support;
using static Resort.Tests.Support.BookingKit;

namespace Resort.Tests.Integration;

/// <summary>Sprint 9 — GST configuration and the tax engine: registrations, categories, rules with effective dates and
/// slabs, the preview, and GST on bill lines (rule id and rate stored, so a rule change never alters a posted charge).
/// Real API, real PostgreSQL. Rules belong to the organisation, so each test that adds rules uses a charge category no
/// other test here taxes.</summary>
public class TaxTests(FolioFactory app) : IClassFixture<FolioFactory>
{
    private const string DemoGstin = "30AABCT1234C1Z9";   // Goa; check digit valid

    // ------------------------------------------------------------------ seed data

    [Fact]
    public async Task The_current_accommodation_and_restaurant_rules_are_seeded_as_data_with_their_source_and_unverified()
    {
        var (owner, _) = await OwnerAsync();
        var categories = await owner.GetJsonAsync<List<TaxCategoryDto>>("/api/v1/tax/categories");
        var rooms = categories.Single(c => c.Code == "ACCOMMODATION");
        Assert.Equal(("Accommodation", "996311", "ROOM", 2), (rooms.Name, rooms.HsnSac, rooms.ChargeCategory, rooms.Rules));
        var now = rooms.CurrentRule!;
        Assert.Equal((new DateOnly(2025, 9, 22), (DateOnly?)null, "SLAB", "CURRENT"), (now.EffectiveFrom, now.EffectiveTo, now.RuleType, now.State));
        Assert.Equal(new[] { new TaxSlabDto(7500m, 5m, false), new TaxSlabDto(null, 18m, true) }, now.Slabs);
        Assert.Contains("05/2025-Central Tax (Rate)", now.Source);
        Assert.Null(now.VerifiedAt);

        var food = categories.Single(c => c.Code == "RESTAURANT");
        Assert.Equal(("996331", "FOOD", "FLAT"), (food.HsnSac, food.ChargeCategory, food.CurrentRule!.RuleType));
        Assert.Equal(new[] { new TaxSlabDto(null, 5m, false) }, food.CurrentRule.Slabs);

        var rules = await owner.GetJsonAsync<List<TaxRuleDto>>($"/api/v1/tax/rules?categoryId={rooms.Id}");
        Assert.Equal(new[] { ("CURRENT", 5m), ("PAST", 12m) }, rules.Select(r => (r.State, r.Slabs[0].Rate)));
        Assert.Equal(new DateOnly(2025, 9, 21), rules[1].EffectiveTo);
    }

    // ------------------------------------------------------------------ the demo: same charge before and after a rule change date

    [Fact]
    public async Task The_same_room_night_is_taxed_12_percent_before_the_rule_change_date_and_5_percent_from_it()
    {
        var (owner, pid) = await OwnerAsync();
        await RegisterMainAsync(owner, pid);

        var before = await CalculateAsync(owner, new TaxCalculationRequest(pid, null, "ROOM", new DateOnly(2025, 9, 21), 3500m));
        var after = await CalculateAsync(owner, new TaxCalculationRequest(pid, null, "ROOM", new DateOnly(2025, 9, 22), 3500m));

        Assert.Equal((true, 12m, 420m, 3920m), (before.Taxed, before.Slab!.Rate, before.Tax, before.Total));
        Assert.Equal(new[] { ("CGST", 6m, 210m), ("SGST", 6m, 210m) }, before.Components.Select(c => (c.Code, c.Rate, c.Amount)));
        Assert.Equal((true, 5m, 175m, 3675m), (after.Taxed, after.Slab!.Rate, after.Tax, after.Total));
        Assert.Equal(new[] { ("CGST", 2.5m, 87.5m), ("SGST", 2.5m, 87.5m) }, after.Components.Select(c => (c.Code, c.Rate, c.Amount)));
        Assert.NotEqual(before.Rule!.Id, after.Rule!.Id);
        Assert.Equal((DemoGstin, "Goa (30)", "Goa (30)", true), (after.Gstin, after.SupplierState, after.PlaceOfSupply, after.IntraState));

        // the slab goes by the value per unit per night: ₹7,500 is still 5%, ₹7,500.01 is 18%
        var at = await CalculateAsync(owner, new TaxCalculationRequest(pid, null, "ROOM", new DateOnly(2026, 1, 10), 15000m, 7500m));
        var above = await CalculateAsync(owner, new TaxCalculationRequest(pid, null, "ROOM", new DateOnly(2026, 1, 10), 7500.01m));
        Assert.Equal((5m, 750m), (at.Slab!.Rate, at.Tax));
        Assert.Equal((18m, true, 1350m), (above.Slab!.Rate, above.Slab.InputTaxCredit, above.Tax));   // 9% of 7,500.01 = 675.0009 → 675.00, twice
    }

    [Fact]
    public async Task A_supply_taxed_in_another_state_is_IGST_and_an_unregistered_or_composition_property_charges_none()
    {
        var (owner, pid) = await OwnerAsync();
        await RegisterMainAsync(owner, pid);
        var other = await CalculateAsync(owner, new TaxCalculationRequest(pid, null, "FOOD", null, 1450m, PlaceOfSupply: "27"));
        Assert.Equal((false, "Maharashtra (27)"), (other.IntraState, other.PlaceOfSupply));
        Assert.Equal(new[] { ("IGST", 5m, 72.5m) }, other.Components.Select(c => (c.Code, c.Rate, c.Amount)));

        // nothing taxes transport (no category): the preview says so
        var transport = await CalculateAsync(owner, new TaxCalculationRequest(pid, null, "TRANSPORT", null, 1200m));
        Assert.Equal((false, 0m, 1200m), (transport.Taxed, transport.Tax, transport.Total));
        Assert.StartsWith("Transport charges carry no GST: no tax category covers them.", transport.NoTaxReason);

        // a second property with no registration, then under the composition scheme
        var second = await NewPropertyAsync(owner, "30");
        var none = await CalculateAsync(owner, new TaxCalculationRequest(second, null, "FOOD", null, 1450m));
        Assert.False(none.Taxed);
        Assert.Matches("^Beach Annex .* has no GST registration on .*, so it charges no GST. Add its GSTIN in Settings → GST.$", none.NoTaxReason);
        Assert.Equal(5m, none.Rule!.Slabs[0].Rate);   // the rule is still shown

        await RegisterAsync(owner, second, new CreateGstRegistrationRequest(DemoGstin, "Test Resorts Pvt Ltd", null, "COMPOSITION", new DateOnly(2024, 4, 1)));
        var composition = await CalculateAsync(owner, new TaxCalculationRequest(second, null, "FOOD", null, 1450m));
        Assert.Equal((false, 0m), (composition.Taxed, composition.Tax));
        Assert.Contains($"is under the composition scheme on {Day(Today)} ({DemoGstin}): it charges guests no GST.", composition.NoTaxReason);
    }

    // ------------------------------------------------------------------ GST on bills

    [Fact]
    public async Task Check_in_puts_room_nights_on_the_bill_with_GST_from_the_slab_of_each_rooms_nightly_value()
    {
        var (owner, pid) = await OwnerAsync();
        await RegisterMainAsync(owner, pid);
        var current = (await owner.GetJsonAsync<List<TaxCategoryDto>>("/api/v1/tax/categories")).Single(c => c.Code == "ACCOMMODATION").CurrentRule!;

        var cheap = await InHouseFolioAsync(owner, pid, 3500m);
        Assert.Equal((7000m, 350m, 7350m), (cheap.Charges - cheap.Tax, cheap.Tax, cheap.Total));
        Assert.All(cheap.Items, i =>
        {
            Assert.Equal((3500m, 175m, 3675m), (i.Amount, i.TaxAmount, i.Total));
            Assert.Equal(new[] { ("CGST", 2.5m, 3500m, 87.5m, (Guid?)current.Id), ("SGST", 2.5m, 3500m, 87.5m, current.Id) },
                i.Taxes.Select(t => (t.Code, t.Rate, t.TaxableValue, t.Amount, t.TaxRuleId)));
        });

        var dear = await InHouseFolioAsync(owner, pid, 8000m);
        Assert.All(dear.Items, i => Assert.Equal((8000m, 1440m, 9440m), (i.Amount, i.TaxAmount, i.Total)));
        Assert.Equal(new[] { 9m, 9m }, dear.Items[0].Taxes.Select(t => t.Rate));

        // the database holds the same: one row per tax, the rule and rate copied
        Assert.Equal(4L, await ScalarAsync($"SELECT count(*) FROM folio_item_taxes t JOIN folio_items i ON i.id = t.folio_item_id WHERE i.folio_id = '{dear.Id}'"));
        Assert.Equal(2880m, await ScalarAsync($"SELECT sum(t.amount) FROM folio_item_taxes t JOIN folio_items i ON i.id = t.folio_item_id WHERE i.folio_id = '{dear.Id}'"));
    }

    [Fact]
    public async Task Food_is_taxed_transport_is_not_and_a_reversal_or_a_moved_line_carries_the_same_tax()
    {
        var (owner, pid) = await OwnerAsync();
        await RegisterMainAsync(owner, pid);
        var folio = await InHouseFolioAsync(owner, pid, 3500m);
        folio = await PostAsync(owner, pid, folio.Id, new PostChargeRequest("FOOD", "Dinner at the beach shack", 1, 1450m));
        folio = await PostAsync(owner, pid, folio.Id, new PostChargeRequest("TRANSPORT", "Airport transfer", 1, 1200m));
        var dinner = folio.Items.Single(i => i.Description == "Dinner at the beach shack");
        var transfer = folio.Items.Single(i => i.Description == "Airport transfer");
        Assert.Equal((72.5m, 1522.5m), (dinner.TaxAmount, dinner.Total));
        Assert.Equal(new[] { ("CGST", 36.25m), ("SGST", 36.25m) }, dinner.Taxes.Select(t => (t.Code, t.Amount)));
        Assert.Equal((0m, 1200m), (transfer.TaxAmount, transfer.Total));
        Assert.Empty(transfer.Taxes);
        Assert.Equal(7350m + 1522.5m + 1200m, folio.Total);
        Assert.Equal(350m + 72.5m, folio.Tax);

        // a reversal negates the tax exactly
        folio = await OkJsonAsync<FolioDto>(await owner.PostAsync(
            $"/api/v1/properties/{pid}/folios/{folio.Id}/charges/{dinner.Id}/reverse", new ReverseChargeRequest("Ordered by another room")));
        var reversal = folio.Items.Single(i => i.ReversesItemId == dinner.Id);
        Assert.Equal((-72.5m, -1522.5m), (reversal.TaxAmount, reversal.Total));
        Assert.Equal(new[] { ("CGST", 2.5m, -1450m, -36.25m), ("SGST", 2.5m, -1450m, -36.25m) },
            reversal.Taxes.Select(t => (t.Code, t.Rate, t.TaxableValue, t.Amount)));
        Assert.Equal(350m, folio.Tax);

        // moving a night to another folio keeps its tax rows (same rule, same rate), and the stay's total stays the same
        var night = folio.Items.First(i => i.Source == "ROOM_NIGHT");
        var split = await OkJsonAsync<SplitFolioResultDto>(await owner.PostAsync(
            $"/api/v1/properties/{pid}/folios/{folio.Id}/split", new SplitFolioRequest([night.Id], null, "Company")));
        var moved = split.To.Items.Single();
        Assert.Equal((175m, 3675m), (moved.TaxAmount, moved.Total));
        Assert.Equal(night.Taxes, moved.Taxes);
        Assert.Equal(folio.Total, split.From.Total + split.To.Total);
    }

    [Fact]
    public async Task A_package_is_taxed_per_component_the_room_part_by_its_value_per_night()
    {
        var (owner, pid) = await OwnerAsync();
        await RegisterMainAsync(owner, pid);
        var folio = await InHouseFolioAsync(owner, pid, 3500m);
        var stay = await SetupAsync(owner, pid, rooms: 0);
        // 2 nights for ₹20,000: room 80% (₹16,000 = ₹8,000 a night → 18%), food 15% (5%), transport 5% (no category → none)
        var res = await owner.PostAsync($"/api/v1/properties/{pid}/packages", new CreatePackageRequest("PK_" + Suffix(), "Honeymoon", null,
            stay.Type.Id, 2, 2, 0, 20000m, 0,
            [new PackageComponentInput("ROOM", "Sea-view room", 80m), new PackageComponentInput("FOOD", "Candle-light dinners", 15m),
             new PackageComponentInput("TRANSPORT", "Airport transfers", 5m)]));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var package = (await res.Content.ReadFromJsonAsync<PackageDto>())!;

        folio = await PostAsync(owner, pid, folio.Id, new PostChargeRequest(null, null, 1, 0, null, package.Id));
        var lines = folio.Items.Where(i => i.Source == "PACKAGE").ToDictionary(i => i.Category);
        Assert.Equal((16000m, 2880m), (lines["ROOM"].Amount, lines["ROOM"].TaxAmount));
        Assert.Equal((3000m, 150m), (lines["FOOD"].Amount, lines["FOOD"].TaxAmount));
        Assert.Equal((1000m, 0m), (lines["TRANSPORT"].Amount, lines["TRANSPORT"].TaxAmount));
    }

    // ------------------------------------------------------------------ history never changes (required test)

    [Fact]
    public async Task Changing_a_rule_does_not_change_the_tax_of_a_charge_already_posted()
    {
        var (owner, pid) = await OwnerAsync();
        await RegisterMainAsync(owner, pid);
        var laundry = await CreateCategoryAsync(owner, new CreateTaxCategoryRequest("LAUNDRY_" + Suffix()[..4], "Laundry service", "999712", "LAUNDRY", null));
        var first = await CreateRuleAsync(owner, new CreateTaxRuleRequest(laundry.Id, Today, null, "FLAT", [new TaxSlabDto(null, 18m, true)], "Test rule A"));
        Assert.Null(first.ClosedRule);

        var folio = await InHouseFolioAsync(owner, pid, 3500m);
        folio = await PostAsync(owner, pid, folio.Id, new PostChargeRequest("LAUNDRY", "Shirts", 1, 400m));
        var shirts = folio.Items.Single(i => i.Description == "Shirts");
        Assert.Equal((72m, 472m), (shirts.TaxAmount, shirts.Total));

        // the accountant adds a 12% rule from tomorrow: today's rule now ends today
        var second = await CreateRuleAsync(owner, new CreateTaxRuleRequest(laundry.Id, Today.AddDays(1), null, "FLAT", [new TaxSlabDto(null, 12m, true)], "Test rule B"));
        Assert.Equal((first.Rule.Id, (DateOnly?)Today), (second.ClosedRule!.Id, second.ClosedRule.EffectiveTo));

        try
        {
            app.Clock.At = DateTimeOffset.UtcNow.AddDays(1);   // tomorrow
            folio = await PostAsync(owner, pid, folio.Id, new PostChargeRequest("LAUNDRY", "Trousers", 1, 400m));
            folio = await PostAsync(owner, pid, folio.Id, new PostChargeRequest("LAUNDRY", "Late shirts", 1, 400m, Today));   // dated yesterday

            var lines = folio.Items.Where(i => i.Category == "LAUNDRY").ToDictionary(i => i.Description);
            Assert.Equal((72m, first.Rule.Id), (lines["Shirts"].TaxAmount, lines["Shirts"].Taxes[0].TaxRuleId));          // unchanged
            Assert.Equal((48m, second.Rule.Id), (lines["Trousers"].TaxAmount, lines["Trousers"].Taxes[0].TaxRuleId));      // the new rule
            Assert.Equal((72m, first.Rule.Id), (lines["Late shirts"].TaxAmount, lines["Late shirts"].Taxes[0].TaxRuleId)); // the rule of its day
            Assert.Equal(9m, lines["Shirts"].Taxes[0].Rate);
        }
        finally { app.Clock.At = null; }

        // and the database refuses to change a rule's rate or a posted tax row
        foreach (var sql in new[]
                 {
                     $"UPDATE tax_rule_slabs SET rate = 5 WHERE tax_rule_id = '{first.Rule.Id}'",
                     $"UPDATE tax_rules SET effective_from = effective_from - 1 WHERE id = '{first.Rule.Id}'",
                     $"DELETE FROM tax_rules WHERE id = '{first.Rule.Id}'",
                     $"UPDATE folio_item_taxes SET rate = 6 WHERE folio_item_id = '{shirts.Id}'",
                 })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
    }

    // ------------------------------------------------------------------ acceptance: the accountant adds next month's rule

    [Fact]
    public async Task The_accountant_adds_a_rule_effective_next_month_without_a_deployment()
    {
        var (owner, pid) = await OwnerAsync();
        await RegisterMainAsync(owner, pid);
        var (email, pw) = await app.CreateUserAsync("ACCOUNTANT");
        var accountant = await app.SignInAsync(email, pw);
        var (remail, rpw) = await app.CreateUserAsync("RECEPTIONIST", pid);
        var desk = await app.SignInAsync(remail, rpw);

        var food = (await accountant.GetJsonAsync<List<TaxCategoryDto>>("/api/v1/tax/categories")).Single(c => c.Code == "RESTAURANT");
        var nextMonth = new DateOnly(Today.Year, Today.Month, 1).AddMonths(1);
        var rule = new CreateTaxRuleRequest(food.Id, nextMonth, null, "FLAT", [new TaxSlabDto(null, 18m, true)],
            "Test: restaurant in specified premises from next month", "The property now charges more than ₹7,500 a night.");

        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PostAsync("/api/v1/tax/rules", rule)).StatusCode);
        var added = await CreateRuleAsync(accountant, rule);
        Assert.Equal(("FUTURE", "ACCOUNTANT user"), (added.Rule.State, added.Rule.CreatedByName));
        Assert.Equal(nextMonth.AddDays(-1), added.ClosedRule!.EffectiveTo);

        // today's dinner is still 5%; dinner on the 1st of next month is 18%
        var today = await CalculateAsync(accountant, new TaxCalculationRequest(pid, null, "FOOD", Today, 1000m));
        var then = await CalculateAsync(accountant, new TaxCalculationRequest(pid, null, "FOOD", nextMonth, 1000m));
        Assert.Equal((50m, 180m), (today.Tax, then.Tax));
        Assert.Equal(added.Rule.Id, then.Rule!.Id);

        // a rule that would start before it, or in the past, is refused with the reason under the date
        var early = await accountant.PostAsync("/api/v1/tax/rules", rule with { EffectiveFrom = Today.AddDays(1) });
        var message = await FieldErrorAsync(early, "EffectiveFrom");
        Assert.StartsWith($"Restaurant service already has a rule from {Day(nextMonth)} with no end date, which overlaps.", message);
        var past = await accountant.PostAsync("/api/v1/tax/rules", rule with { EffectiveFrom = Today.AddDays(-1) });
        Assert.StartsWith($"A new rule can start today ({Day(Today)}) or later.", await FieldErrorAsync(past, "EffectiveFrom"));

        // the accountant verifies the seeded rule against its notification
        var verified = await OkJsonAsync<TaxRuleDto>(await accountant.PostAsync($"/api/v1/tax/rules/{food.CurrentRule!.Id}/verify"));
        Assert.Equal("ACCOUNTANT user", verified.VerifiedByName);
        Assert.NotNull(verified.VerifiedAt);
    }

    // ------------------------------------------------------------------ errors say why, under the field

    [Fact]
    public async Task Bad_rules_categories_and_registrations_are_refused_with_the_reason_under_the_field()
    {
        var (owner, pid) = await OwnerAsync();
        await RegisterMainAsync(owner, pid);
        var cat = await CreateCategoryAsync(owner, new CreateTaxCategoryRequest("SPA_" + Suffix()[..4], "Spa", "999722", null, null));

        async Task<string?> RuleError(CreateTaxRuleRequest r, string field) => await FieldErrorAsync(await owner.PostAsync("/api/v1/tax/rules", r), field);
        Assert.Equal("A slab rule needs at least two rates, e.g. up to ₹7,500 at 5% and above it at 18%.",
            await RuleError(new(cat.Id, Today, null, "SLAB", [new TaxSlabDto(null, 5m, false)], "x source"), "Slabs"));
        Assert.Equal("A flat rule has one rate for any value: remove the value limits, or choose SLAB.",
            await RuleError(new(cat.Id, Today, null, "FLAT", [new TaxSlabDto(5000m, 5m, false), new TaxSlabDto(null, 18m, false)], "x source"), "Slabs"));
        Assert.Equal("The rate is split evenly into CGST and SGST, so it must be a multiple of 0.02% (e.g. 5, 18 or 0.1).",
            await RuleError(new(cat.Id, Today, null, "FLAT", [new TaxSlabDto(null, 0.25m, false)], "x source"), "Slabs[0].Rate"));
        Assert.StartsWith("Say where the rule comes from", await RuleError(new(cat.Id, Today, null, "FLAT", [new TaxSlabDto(null, 5m, false)], ""), "Source"));

        Assert.StartsWith($"The code {cat.Code} is already used by Spa.",
            await FieldErrorAsync(await owner.PostAsync("/api/v1/tax/categories", new CreateTaxCategoryRequest(cat.Code, "Spa 2", "999722", null, null)), "Code"));
        Assert.Equal("Food charges are already taxed under Restaurant service. A charge category can belong to one tax category only.",
            await FieldErrorAsync(await owner.PostAsync("/api/v1/tax/categories", new CreateTaxCategoryRequest("FOOD2", "Food 2", "996331", "FOOD", null)), "ChargeCategory"));
        Assert.StartsWith("Enter the SAC or HSN code",
            await FieldErrorAsync(await owner.PostAsync("/api/v1/tax/categories", new CreateTaxCategoryRequest("X1", "X", "99631", null, null)), "HsnSac"));

        var url = $"/api/v1/properties/{pid}/gst-registrations";
        Assert.StartsWith("The last character (check digit) should be 9",
            await FieldErrorAsync(await owner.PostAsync(url, new CreateGstRegistrationRequest("30AABCT1234C1Z8", "X", null, null, Today)), "Gstin"));
        Assert.Equal("This GSTIN is registered in Maharashtra (27), but Main Resort is in Goa (30). A property bills under the GSTIN of its own state.",
            await FieldErrorAsync(await owner.PostAsync(url, new CreateGstRegistrationRequest("27AAPFU0939F1ZV", "X", null, null, Today)), "Gstin"));
        Assert.StartsWith($"Main Resort is already registered under {DemoGstin} from {Day(new DateOnly(2017, 7, 1))} with no end date, which overlaps.",
            await FieldErrorAsync(await owner.PostAsync(url, new CreateGstRegistrationRequest(DemoGstin, "X", null, null, new DateOnly(2017, 7, 1))), "EffectiveFrom"));
    }

    [Fact]
    public async Task A_taxed_charge_with_no_rule_for_its_day_is_refused_rather_than_posted_without_GST()
    {
        var (owner, pid) = await OwnerAsync();
        await RegisterMainAsync(owner, pid);
        var equipment = await CreateCategoryAsync(owner, new CreateTaxCategoryRequest("EQUIP_" + Suffix()[..4], "Equipment rental", "997319", "EQUIPMENT", null));
        await CreateRuleAsync(owner, new CreateTaxRuleRequest(equipment.Id, Today.AddDays(1), null, "FLAT", [new TaxSlabDto(null, 18m, true)], "Test rule"));
        var folio = await InHouseFolioAsync(owner, pid, 3500m);

        var res = await owner.PostAsync($"/api/v1/properties/{pid}/folios/{folio.Id}/charges", new PostChargeRequest("EQUIPMENT", "Kayak hire", 1, 500m),
            new Dictionary<string, string> { ["Idempotency-Key"] = "charge-" + Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var (code, message) = await FrontDeskTests.ProblemAsync(res);
        Assert.Equal("TAX_RULE_MISSING", code);
        Assert.Equal($"There is no GST rule for Equipment rental on {Day(Today)}. Ask your accountant to add one in Settings → GST. " +
                     $"Until then equipment charges dated {Day(Today)} can't be posted.", message);
        Assert.Equal(folio.Total, (await GetAsync(owner, pid, folio.Id)).Total);
    }

    // ------------------------------------------------------------------ access and tenancy

    [Fact]
    public async Task Tax_settings_need_tax_permissions_and_stay_inside_the_organisation()
    {
        var (owner, pid) = await OwnerAsync();
        var (remail, rpw) = await app.CreateUserAsync("RECEPTIONIST", pid);
        var desk = await app.SignInAsync(remail, rpw);
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.GetAsync("/api/v1/tax/categories")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.GetAsync($"/api/v1/properties/{pid}/gst-registrations")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PostAsync("/api/v1/tax/calculate", new TaxCalculationRequest(pid, null, "FOOD", null, 100m))).StatusCode);

        var (orgId, otherPid, email, pw) = await app.CreateSecondTenantAsync();
        var other = await app.SignInAsync(email, pw);
        var theirs = await other.GetJsonAsync<List<TaxCategoryDto>>("/api/v1/tax/categories");
        var ours = await owner.GetJsonAsync<List<TaxCategoryDto>>("/api/v1/tax/categories");
        Assert.Contains(theirs, c => c.Code == "ACCOMMODATION");            // every new organisation gets the seeded rules
        Assert.Empty(theirs.Select(c => c.Id).Intersect(ours.Select(c => c.Id)));
        Assert.Equal("That property doesn't exist. Reload the page and choose again.",
            await FieldErrorAsync(await other.PostAsync("/api/v1/tax/calculate", new TaxCalculationRequest(pid, null, "FOOD", null, 100m)), "PropertyId"));
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/properties/{pid}/gst-registrations")).StatusCode);
        Assert.NotEqual(Guid.Empty, otherPid);
        Assert.NotEqual(Guid.Empty, orgId);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(TestClient Owner, Guid PropertyId)> OwnerAsync() => (await app.SignInOwnerAsync(), await app.MainPropertyIdAsync());

    /// <summary>Registers the main property (Goa) from 1 Jul 2017, once per test class.</summary>
    private static async Task RegisterMainAsync(TestClient owner, Guid pid)
    {
        var list = await owner.GetJsonAsync<List<GstRegistrationDto>>($"/api/v1/properties/{pid}/gst-registrations");
        if (list.Count > 0) return;
        var reg = await RegisterAsync(owner, pid, new CreateGstRegistrationRequest(DemoGstin, "Test Resorts Pvt Ltd", "Main Resort", "REGULAR", new DateOnly(2017, 7, 1)));
        Assert.Equal(("30", "Goa", true), (reg.StateCode, reg.StateName, reg.Current));
    }

    private static async Task<GstRegistrationDto> RegisterAsync(TestClient c, Guid pid, CreateGstRegistrationRequest body)
    {
        var res = await c.PostAsync($"/api/v1/properties/{pid}/gst-registrations", body);
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<GstRegistrationDto>())!;
    }

    private static async Task<Guid> NewPropertyAsync(TestClient c, string state)
    {
        var code = "P" + Suffix();
        var res = await c.PostAsync("/api/v1/properties", new CreatePropertyRequest(code, $"Beach Annex {code}", null, "RESORT", "Asia/Kolkata", "INR",
            null, null, null, null, null, state, null, "IN", null, null, new TimeOnly(14, 0), new TimeOnly(11, 0), null, null));
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<PropertyDto>())!.Id;
    }

    private static async Task<TaxCalculationDto> CalculateAsync(TestClient c, TaxCalculationRequest r) =>
        await OkJsonAsync<TaxCalculationDto>(await c.PostAsync("/api/v1/tax/calculate", r));

    private static async Task<TaxCategoryDto> CreateCategoryAsync(TestClient c, CreateTaxCategoryRequest r)
    {
        var res = await c.PostAsync("/api/v1/tax/categories", r);
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<TaxCategoryDto>())!;
    }

    private static async Task<CreateTaxRuleResultDto> CreateRuleAsync(TestClient c, CreateTaxRuleRequest r)
    {
        var res = await c.PostAsync("/api/v1/tax/rules", r);
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<CreateTaxRuleResultDto>())!;
    }

    /// <summary>A guest checked in today for two nights at <paramref name="price"/>, and their main folio.</summary>
    private static async Task<FolioDto> InHouseFolioAsync(TestClient owner, Guid pid, decimal price)
    {
        var stay = await SetupAsync(owner, pid, rooms: 1, price: price);
        var guest = await CreateGuestAsync(owner);
        var booked = await CreatedAsync(await BookAsync(owner, pid, NewBooking(stay, guest.Id, await WalkInAsync(owner, pid), Today, Today.AddDays(2))));
        await FrontDeskTests.OkAsync(await owner.PostAsync(FrontDeskTests.CheckInUrl(pid, booked), new CheckInRequest(null)));
        var list = await owner.GetJsonAsync<List<FolioListItemDto>>($"/api/v1/properties/{pid}/reservations/{booked.Id}/folios");
        return await GetAsync(owner, pid, list.Single(f => f.IsPrimary).Id);
    }

    private static Task<FolioDto> GetAsync(TestClient c, Guid pid, Guid folioId) => c.GetJsonAsync<FolioDto>($"/api/v1/properties/{pid}/folios/{folioId}");

    private static async Task<FolioDto> PostAsync(TestClient c, Guid pid, Guid folioId, PostChargeRequest body)
    {
        var res = await c.PostAsync($"/api/v1/properties/{pid}/folios/{folioId}/charges", body,
            new Dictionary<string, string> { ["Idempotency-Key"] = "charge-" + Guid.NewGuid() });
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<FolioDto>())!;
    }

    private static string Day(DateOnly d) => FrontDeskTests.Day(d);

    private static async Task<T> OkJsonAsync<T>(HttpResponseMessage res)
    {
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

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
