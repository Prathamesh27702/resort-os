using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Resort.Application.Common;
using Resort.Contracts;
using Resort.Domain.Identity;
using Resort.Domain.Organizations;
using Resort.Infrastructure.Persistence;

namespace Resort.Tests.Support;

/// <summary>
/// Boots the real API against a real, freshly created PostgreSQL database (no mocks, no in-memory provider —
/// constraint, locking and transaction behaviour must be the real thing).
/// Connection: env RESORT_TEST_DB (a login that can CREATE/DROP databases). scripts/setup-dev writes it to .env.
/// </summary>
public class ResortApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string OwnerEmail = "owner@test.local";
    public const string OwnerPassword = "OwnerPass12345";

    private static string BaseConnectionString =>
        Environment.GetEnvironmentVariable("RESORT_TEST_DB")
        ?? throw new InvalidOperationException(
            "Set RESORT_TEST_DB to a PostgreSQL connection string that can create databases (run scripts/setup-dev, then scripts/test.sh).");

    /// <summary>Each factory (test class) gets its own database, so classes can run in parallel.</summary>
    public string ConnectionString { get; } = new NpgsqlConnectionStringBuilder(BaseConnectionString)
    {
        Database = new NpgsqlConnectionStringBuilder(BaseConnectionString).Database + "_" + Guid.NewGuid().ToString("N")[..8],
    }.ConnectionString;

    protected virtual int AuthRateLimit => 100_000;

    /// <summary>Where this factory's API stores files (encrypted ID scans). Deleted with the database.</summary>
    public string FileRoot { get; } = Path.Combine(Path.GetTempPath(), "resort-tests", Guid.NewGuid().ToString("N")[..8]);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Resort", ConnectionString);
        builder.UseSetting("Jwt:SigningKey", "integration-test-signing-key-0123456789abcdef");
        builder.UseSetting("RateLimits:AuthPerMinutePerIp", AuthRateLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("RateLimits:GlobalPerMinutePerClient", "100000");
        builder.UseSetting("Bootstrap:Enabled", "true");
        builder.UseSetting("Bootstrap:OrganizationCode", "TESTORG");
        builder.UseSetting("Bootstrap:OrganizationName", "Test Resorts");
        builder.UseSetting("Bootstrap:PropertyCode", "MAIN");
        builder.UseSetting("Bootstrap:PropertyName", "Main Resort");
        builder.UseSetting("Bootstrap:Timezone", "Asia/Kolkata");
        builder.UseSetting("Bootstrap:OwnerEmail", OwnerEmail);
        builder.UseSetting("Bootstrap:OwnerName", "Test Owner");
        builder.UseSetting("Bootstrap:OwnerPassword", OwnerPassword);
        builder.UseSetting("Bootstrap:OwnerMustChangePassword", "false");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        // Throwaway test key (32 zero-to-31 bytes) and a private folder for ID scans.
        builder.UseSetting("Encryption:Key", Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()));
        builder.UseSetting("FileStore:Root", FileRoot);
        // Tests run the nightly room charge themselves (RoomChargeRun), at a time they choose.
        builder.UseSetting("Folio:RoomChargeJob:Enabled", "false");
        builder.UseSetting("Leave:AccrualJob:Enabled", "false");
    }

    public async Task InitializeAsync()
    {
        var csb = new NpgsqlConnectionStringBuilder(ConnectionString);
        var dbName = csb.Database!;
        csb.Database = "postgres";
        await using (var c = new NpgsqlConnection(csb.ConnectionString))
        {
            await c.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)", c);
            await drop.ExecuteNonQueryAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", c);
            await create.ExecuteNonQueryAsync();
        }
        _ = Server; // starts the host: runs migrations + bootstrap
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        var csb = new NpgsqlConnectionStringBuilder(ConnectionString);
        var dbName = csb.Database!;
        csb.Database = "postgres";
        NpgsqlConnection.ClearAllPools();
        await using var c = new NpgsqlConnection(csb.ConnectionString);
        await c.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)", c);
        await drop.ExecuteNonQueryAsync();
        if (Directory.Exists(FileRoot)) Directory.Delete(FileRoot, recursive: true);
    }

    public TestClient NewClient() => new(CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        HandleCookies = true,
        AllowAutoRedirect = false,
    }));

    public async Task<TestClient> SignInAsync(string email, string password)
    {
        var c = NewClient();
        var res = await c.LoginAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return c;
    }

    public Task<TestClient> SignInOwnerAsync() => SignInAsync(OwnerEmail, OwnerPassword);

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        await using var scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async Task<Guid> MainPropertyIdAsync() =>
        await WithDbAsync(db => db.Properties.IgnoreQueryFilters().Where(p => p.Code == "MAIN").Select(p => p.Id).SingleAsync());

    /// <summary>Creates a user directly (bypassing the API), already past first-login password change.</summary>
    public async Task<(string Email, string Password)> CreateUserAsync(string roleCode, Guid? propertyId = null, Guid? organizationId = null)
    {
        var email = $"{roleCode.ToLowerInvariant()}-{Guid.NewGuid():N}@test.local";
        const string password = "UserPass12345";
        await WithDbAsync(async db =>
        {
            await using var scope = Services.CreateAsyncScope();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();
            var orgId = organizationId ?? await db.Organizations.Where(o => o.Code == "TESTORG").Select(o => o.Id).SingleAsync();
            var role = await db.Roles.SingleAsync(r => r.Code == roleCode && r.OrganizationId == null);
            var user = new User { OrganizationId = orgId, FullName = roleCode + " user", PasswordHash = hasher.Hash(password) };
            user.SetEmail(email);
            user.Roles.Add(new UserRole { UserId = user.Id, RoleId = role.Id, Role = role, PropertyId = propertyId });
            db.Users.Add(user);
            return await db.SaveChangesAsync();
        });
        return (email, password);
    }

    /// <summary>A second tenant with its own property and owner, for isolation tests.</summary>
    public async Task<(Guid OrgId, Guid PropertyId, string OwnerEmail, string Password)> CreateSecondTenantAsync()
    {
        var code = "ORG" + Random.Shared.Next(10000, 99999);
        var (orgId, propId) = await WithDbAsync(async db =>
        {
            var org = new Organization { Code = code, Name = code + " Pvt Ltd" };
            var prop = new Property { OrganizationId = org.Id, Code = "OTHER", Name = "Other Resort" };
            db.AddRange(org, prop);
            await db.SaveChangesAsync();
            return (org.Id, prop.Id);
        });
        var (email, pw) = await CreateUserAsync("OWNER", null, orgId);
        return (orgId, propId, email, pw);
    }
}

/// <summary>A browser-like client: keeps the refresh cookie and the current access token.</summary>
public sealed class TestClient(HttpClient http)
{
    public HttpClient Http { get; } = http;
    public string? AccessToken { get; private set; }
    public MeResponse? Me { get; private set; }

    public async Task<HttpResponseMessage> LoginAsync(string email, string password)
    {
        var res = await Http.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password));
        await CaptureAsync(res);
        return res;
    }

    public async Task<HttpResponseMessage> RefreshAsync(bool withClientHeader = true)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        if (withClientHeader) req.Headers.Add("X-Resort-Client", "web");
        var res = await Http.SendAsync(req);
        await CaptureAsync(res);
        return res;
    }

    public async Task<HttpResponseMessage> LogoutAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        req.Headers.Add("X-Resort-Client", "web");
        return await Http.SendAsync(req);
    }

    public Task<HttpResponseMessage> GetAsync(string url) => SendAsync(HttpMethod.Get, url, null);
    public Task<HttpResponseMessage> PostAsync(string url, object? body = null) => SendAsync(HttpMethod.Post, url, body);
    public Task<HttpResponseMessage> PutAsync(string url, object body) => SendAsync(HttpMethod.Put, url, body);
    public Task<HttpResponseMessage> DeleteAsync(string url) => SendAsync(HttpMethod.Delete, url, null);

    /// <summary>POST with extra headers (e.g. Idempotency-Key).</summary>
    public Task<HttpResponseMessage> PostAsync(string url, object? body, IReadOnlyDictionary<string, string> headers) =>
        SendAsync(HttpMethod.Post, url, body, headers);

    /// <summary>Sends any body (e.g. multipart/form-data for document uploads) with the access token.</summary>
    public async Task<HttpResponseMessage> PostContentAsync(string url, HttpContent content)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (AccessToken is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        return await Http.SendAsync(req);
    }

    public async Task<T> GetJsonAsync<T>(string url)
    {
        var res = await GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        using var req = new HttpRequestMessage(method, url);
        foreach (var (k, v) in headers ?? new Dictionary<string, string>()) req.Headers.Add(k, v);
        if (AccessToken is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        if (body is not null) req.Content = JsonContent.Create(body);
        return await Http.SendAsync(req);
    }

    private async Task CaptureAsync(HttpResponseMessage res)
    {
        if (res.StatusCode != HttpStatusCode.OK) return;
        var auth = await res.Content.ReadFromJsonAsync<AuthResponse>();
        AccessToken = auth!.AccessToken;
        Me = auth.Me;
    }

    /// <summary>Copies the refresh cookie into another client (simulates an attacker replaying a stolen token).</summary>
    public string? RefreshCookieHeader(HttpResponseMessage loginOrRefresh) =>
        loginOrRefresh.Headers.TryGetValues("Set-Cookie", out var v) ? v.FirstOrDefault(c => c.StartsWith("__Secure-resort_rt=", StringComparison.Ordinal)) : null;
}

public static class ProblemExtensions
{
    public static async Task<string?> ProblemCodeAsync(this HttpResponseMessage res)
    {
        var doc = await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return doc.TryGetProperty("code", out var c) ? c.GetString() : null;
    }
}
