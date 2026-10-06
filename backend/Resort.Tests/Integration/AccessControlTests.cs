using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Resort.Application.Common;
using Resort.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Resort.Infrastructure.Persistence;
using Resort.Tests.Support;

namespace Resort.Tests.Integration;

public class AccessControlTests(ResortApiFactory app) : IClassFixture<ResortApiFactory>
{
    [Fact]
    public async Task Receptionist_scoped_to_one_property_cannot_reach_admin_or_other_properties()
    {
        var owner = await app.SignInOwnerAsync();
        var created = await owner.PostAsync("/api/v1/properties", NewProperty("SECOND"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var second = (await created.Content.ReadFromJsonAsync<PropertyDto>())!;

        var mainId = await app.MainPropertyIdAsync();
        var (email, pw) = await app.CreateUserAsync("RECEPTIONIST", mainId);
        var desk = await app.SignInAsync(email, pw);

        // sees only the property they are granted
        var list = await desk.GetJsonAsync<List<PropertyDto>>("/api/v1/properties");
        Assert.Equal(new[] { mainId }, list.Select(p => p.Id).ToArray());

        Assert.Equal(HttpStatusCode.OK, (await desk.GetAsync($"/api/v1/properties/{mainId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.GetAsync($"/api/v1/properties/{second.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.GetAsync("/api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.GetAsync("/api/v1/audit-logs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PostAsync("/api/v1/properties", NewProperty("THIRD"))).StatusCode);
    }

    [Fact]
    public async Task Tenants_are_isolated()
    {
        var other = await app.CreateSecondTenantAsync();
        var otherOwner = await app.SignInAsync(other.OwnerEmail, other.Password);
        var mainId = await app.MainPropertyIdAsync();

        // Other tenant's owner has org-wide property.read, but the row is outside their tenant: 404, not 403,
        // so the response doesn't even confirm the id exists.
        Assert.Equal(HttpStatusCode.NotFound, (await otherOwner.GetAsync($"/api/v1/properties/{mainId}")).StatusCode);

        var props = await otherOwner.GetJsonAsync<List<PropertyDto>>("/api/v1/properties");
        Assert.Equal(new[] { other.PropertyId }, props.Select(p => p.Id).ToArray());

        var users = await otherOwner.GetJsonAsync<List<UserDto>>("/api/v1/users");
        Assert.DoesNotContain(ResortApiFactory.OwnerEmail, users.Select(u => u.Email));

        var audit = await otherOwner.GetJsonAsync<PagedResult<AuditLogDto>>("/api/v1/audit-logs?pageSize=200");
        Assert.All(audit.Items, a => Assert.NotEqual(mainId, a.PropertyId));
    }

    [Fact]
    public async Task General_manager_cannot_grant_the_owner_role()
    {
        var (email, pw) = await app.CreateUserAsync("GENERAL_MANAGER");
        var gm = await app.SignInAsync(email, pw);

        var res = await gm.PostAsync("/api/v1/users", new CreateUserRequest($"x-{Guid.NewGuid():N}@test.local", "Sneaky", null,
            "TempPass12345", [new RoleGrantDto("OWNER", null)]));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        // but can create a receptionist
        var ok = await gm.PostAsync("/api/v1/users", new CreateUserRequest($"r-{Guid.NewGuid():N}@test.local", "Desk", null,
            "TempPass12345", [new RoleGrantDto("RECEPTIONIST", await app.MainPropertyIdAsync())]));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    [Fact]
    public async Task Users_cannot_change_their_own_roles_or_deactivate_themselves()
    {
        var owner = await app.SignInOwnerAsync();
        var me = owner.Me!.UserId;
        var roles = await owner.PutAsync($"/api/v1/users/{me}/roles", new SetUserRolesRequest([new RoleGrantDto("EMPLOYEE", null)]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, roles.StatusCode);
        Assert.Equal("SELF_ROLE_CHANGE", await roles.ProblemCodeAsync());

        var deact = await owner.PostAsync($"/api/v1/users/{me}/deactivate");
        Assert.Equal("SELF_DEACTIVATE", await deact.ProblemCodeAsync());
    }

    [Fact]
    public async Task Role_change_takes_effect_immediately()
    {
        var (email, pw) = await app.CreateUserAsync("EMPLOYEE");
        var user = await app.SignInAsync(email, pw);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/v1/users")).StatusCode);

        var owner = await app.SignInOwnerAsync();
        var set = await owner.PutAsync($"/api/v1/users/{user.Me!.UserId}/roles", new SetUserRolesRequest([new RoleGrantDto("HR", null)]));
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/v1/users")).StatusCode);
    }

    [Fact]
    public async Task Every_permission_constant_used_in_code_exists_in_the_database()
    {
        var codes = await app.WithDbAsync(db => db.Permissions.Select(p => p.Code).ToListAsync());
        Assert.All(Perm.All, p => Assert.Contains(p, codes));
    }

    [Fact]
    public async Task Default_matrix_keeps_salary_and_audit_away_from_ordinary_staff()
    {
        var matrix = await app.WithDbAsync(db => db.Roles.Where(r => r.OrganizationId == null)
            .Select(r => new { r.Code, Perms = r.Permissions.Select(p => p.PermissionCode).ToList() }).ToListAsync());

        Assert.Equal(17, matrix.Count);
        var withSalary = matrix.Where(r => r.Perms.Contains("employee_salary.read")).Select(r => r.Code).Order().ToList();
        Assert.Equal(new[] { "HR", "OWNER" }, withSalary.ToArray());
        var employee = matrix.Single(r => r.Code == "EMPLOYEE").Perms.Order().ToList();
        Assert.Equal(new[] { "attendance.self", "leave.create", "property.read", "roster.self", "timesheet.self" }, employee.ToArray());
    }

    private static CreatePropertyRequest NewProperty(string code) => new(
        code, code + " Resort", null, "RESORT", "Asia/Kolkata", "INR", null, null, "Panaji", null, "Goa", "30",
        "403001", "IN", null, null, new TimeOnly(14, 0), new TimeOnly(11, 0), null, null);
}

public class DataIntegrityTests(ResortApiFactory app) : IClassFixture<ResortApiFactory>
{
    [Fact]
    public async Task Stale_update_is_rejected_with_409()
    {
        var owner = await app.SignInOwnerAsync();
        var id = await app.MainPropertyIdAsync();
        var p = await owner.GetJsonAsync<PropertyDto>($"/api/v1/properties/{id}");

        var first = await owner.PutAsync($"/api/v1/properties/{id}", Update(p, "Renamed once", p.Version));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var stale = await owner.PutAsync($"/api/v1/properties/{id}", Update(p, "Renamed from a stale screen", p.Version));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("CONCURRENCY_CONFLICT", await stale.ProblemCodeAsync());
    }

    [Fact]
    public async Task Changes_are_audited_with_old_and_new_values_and_secrets_masked()
    {
        var owner = await app.SignInOwnerAsync();
        var id = await app.MainPropertyIdAsync();
        var p = await owner.GetJsonAsync<PropertyDto>($"/api/v1/properties/{id}");
        var res = await owner.PutAsync($"/api/v1/properties/{id}", Update(p, "Audited Name", p.Version) with { City = "Margao" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var log = await owner.GetJsonAsync<PagedResult<AuditLogDto>>($"/api/v1/audit-logs?entityType=Property&entityId={id}");
        var entry = log.Items.First();
        Assert.Equal("UPDATE", entry.Action);
        Assert.Equal(owner.Me!.UserId, entry.UserId);
        Assert.Equal("Audited Name", entry.Changes!.Value.GetProperty("Name").GetProperty("new").GetString());
        Assert.Equal("Margao", entry.Changes!.Value.GetProperty("City").GetProperty("new").GetString());

        var userInsert = await owner.GetJsonAsync<PagedResult<AuditLogDto>>("/api/v1/audit-logs?entityType=User&pageSize=200");
        Assert.All(userInsert.Items.Where(i => i.Action == "INSERT"),
            i => Assert.Equal("***", i.Changes!.Value.GetProperty("PasswordHash").GetProperty("new").GetString()));
    }

    [Fact]
    public async Task Audit_log_is_append_only_at_database_level()
    {
        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE audit_logs SET action = 'TAMPERED'", conn);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Contains("append-only", ex.MessageText);

        await using var del = new NpgsqlCommand("DELETE FROM login_events", conn);
        await Assert.ThrowsAsync<PostgresException>(() => del.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Audit_records_reason_device_and_request_ids()
    {
        var owner = await app.SignInOwnerAsync();
        var (email, pw) = await app.CreateUserAsync("EMPLOYEE");
        var target = await app.SignInAsync(email, pw);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/users/{target.Me!.UserId}/deactivate");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", owner.AccessToken);
        req.Headers.Add("X-Change-Reason", Uri.EscapeDataString("Left the company — last day 5 Oct"));
        req.Headers.Add("X-Correlation-Id", "flow-12345678");
        req.Headers.UserAgent.ParseAdd("ReceptionTablet/1.0");
        var res = await owner.Http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("flow-12345678", res.Headers.GetValues("X-Correlation-Id").Single());
        var requestId = res.Headers.GetValues("X-Request-Id").Single();

        var log = await owner.GetJsonAsync<PagedResult<AuditLogDto>>($"/api/v1/audit-logs?entityType=User&entityId={target.Me.UserId}");
        var entry = log.Items.First(i => i.Action == "UPDATE");
        Assert.Equal("Left the company — last day 5 Oct", entry.Reason);
        Assert.Equal("ReceptionTablet/1.0", entry.Device);
        Assert.Equal("flow-12345678", entry.CorrelationId);
        Assert.Equal(requestId, entry.RequestId);
    }

    [Fact]
    public async Task Errors_use_the_standard_shape_and_never_leak_internals()
    {
        var owner = await app.SignInOwnerAsync();
        var res = await owner.GetAsync($"/api/v1/properties/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("NOT_FOUND", body.GetProperty("code").GetString());
        Assert.True(body.TryGetProperty("message", out _));
        Assert.True(body.TryGetProperty("correlationId", out _));
        var raw = body.GetRawText();
        var leaksExceptionName = raw.Contains("Exception", StringComparison.Ordinal);
        var leaksStackTrace = raw.Contains("   at ", StringComparison.Ordinal);
        Assert.False(leaksExceptionName, "error body must not leak exception names");
        Assert.False(leaksStackTrace, "error body must not leak stack traces");
    }

    [Fact]
    public async Task Migrator_is_idempotent_and_refuses_an_edited_migration()
    {
        var migrator = app.Services.GetRequiredService<DatabaseMigrator>();
        await migrator.MigrateAsync(CancellationToken.None); // second run: nothing to do, no error

        await using var conn = new NpgsqlConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using (var t = new NpgsqlCommand("UPDATE schema_migrations SET checksum = 'tampered' WHERE version = 1", conn))
            await t.ExecuteNonQueryAsync();
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => migrator.MigrateAsync(CancellationToken.None));
            Assert.Contains("was modified after it was applied", ex.Message);
        }
        finally
        {
            var real = DatabaseMigrator.Discover().Single(m => m.Version == 1).Checksum;
            await using var fix = new NpgsqlCommand($"UPDATE schema_migrations SET checksum = '{real}' WHERE version = 1", conn);
            await fix.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Validation_errors_name_each_field()
    {
        var owner = await app.SignInOwnerAsync();
        var res = await owner.PostAsync("/api/v1/users", new CreateUserRequest("not-an-email", "", null, "short", []));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("correlationId").GetString()));
        Assert.Equal("Some fields need fixing.", body.GetProperty("message").GetString());
        var errors = body.GetProperty("details");
        Assert.True(errors.TryGetProperty("Email", out _));
        Assert.True(errors.TryGetProperty("FullName", out _));
        Assert.True(errors.TryGetProperty("TemporaryPassword", out _));
    }

    [Fact]
    public async Task Invalid_timezone_is_rejected()
    {
        var owner = await app.SignInOwnerAsync();
        var id = await app.MainPropertyIdAsync();
        var p = await owner.GetJsonAsync<PropertyDto>($"/api/v1/properties/{id}");
        var res = await owner.PutAsync($"/api/v1/properties/{id}", Update(p, p.Name, p.Version) with { Timezone = "IST" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Property_settings_round_trip_as_json()
    {
        var owner = await app.SignInOwnerAsync();
        var id = await app.MainPropertyIdAsync();
        var put = await owner.PutAsync($"/api/v1/properties/{id}/settings/frontdesk.allow_checkout_with_balance",
            new { value = false });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var all = await owner.GetJsonAsync<List<PropertySettingDto>>($"/api/v1/properties/{id}/settings");
        var s = all.Single(x => x.Key == "frontdesk.allow_checkout_with_balance");
        Assert.Equal(System.Text.Json.JsonValueKind.False, s.Value.ValueKind);

        var bad = await owner.PutAsync($"/api/v1/properties/{id}/settings/Bad Key!", new { value = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Security_headers_are_present()
    {
        var res = await app.NewClient().Http.GetAsync("/health/live");
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", res.Headers.GetValues("Content-Security-Policy").Single());
    }

    private static UpdatePropertyRequest Update(PropertyDto p, string name, uint version) => new(
        name, p.LegalName, p.PropertyType, p.Timezone, p.Currency, p.AddressLine1, p.AddressLine2, p.City, p.District,
        p.StateName, p.GstStateCode, p.PostalCode, p.CountryCode, p.Phone, p.Email, p.CheckInTime, p.CheckOutTime,
        p.Latitude, p.Longitude, p.IsActive, version);
}
