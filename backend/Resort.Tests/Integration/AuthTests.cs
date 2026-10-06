using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Resort.Contracts;
using Resort.Tests.Support;

namespace Resort.Tests.Integration;

public class AuthTests(ResortApiFactory app) : IClassFixture<ResortApiFactory>
{
    [Fact]
    public async Task Login_returns_access_token_and_secure_httponly_refresh_cookie()
    {
        var c = app.NewClient();
        var res = await c.LoginAsync(ResortApiFactory.OwnerEmail, ResortApiFactory.OwnerPassword);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.NotNull(c.AccessToken);
        var cookie = c.RefreshCookieHeader(res);
        Assert.NotNull(cookie);
        Assert.Contains("httponly", cookie!.ToLowerInvariant());
        Assert.Contains("secure", cookie.ToLowerInvariant());
        Assert.Contains("samesite=strict", cookie.ToLowerInvariant());
        Assert.Contains("path=/api/v1/auth", cookie.ToLowerInvariant());
        Assert.Contains("OWNER", c.Me!.Grants.Single().Roles);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_give_the_same_error()
    {
        var wrong = await app.NewClient().LoginAsync(ResortApiFactory.OwnerEmail, "NotThePassword1");
        var unknown = await app.NewClient().LoginAsync($"nobody-{Guid.NewGuid():N}@test.local", "NotThePassword1");

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal("INVALID_CREDENTIALS", await wrong.ProblemCodeAsync());
        Assert.Equal("INVALID_CREDENTIALS", await unknown.ProblemCodeAsync());
    }

    [Fact]
    public async Task Account_locks_after_five_failures_even_for_the_right_password()
    {
        var (email, password) = await app.CreateUserAsync("EMPLOYEE");
        for (var i = 0; i < 5; i++)
            await app.NewClient().LoginAsync(email, "WrongPassword99");

        var res = await app.NewClient().LoginAsync(email, password);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal("ACCOUNT_LOCKED", await res.ProblemCodeAsync());

        var events = await app.WithDbAsync(db => db.LoginEvents.Where(e => e.EmailAttempted == email).ToListAsync());
        Assert.Equal(5, events.Count(e => e.FailureReason == "BAD_PASSWORD"));
        var lockedOut = events.Count(e => e.FailureReason == "LOCKED_OUT");
        Assert.True(lockedOut == 1, $"expected 1 LOCKED_OUT event, got {lockedOut}");
    }

    [Fact]
    public async Task Refresh_rotates_the_token()
    {
        var c = await app.SignInOwnerAsync();
        var firstAccess = c.AccessToken;

        var res = await c.RefreshAsync();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.NotEqual(firstAccess, c.AccessToken);
        Assert.NotNull(c.RefreshCookieHeader(res));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/properties")).StatusCode);
    }

    [Fact]
    public async Task Reusing_a_rotated_refresh_token_revokes_the_whole_session_family()
    {
        var victim = app.NewClient();
        var login = await victim.LoginAsync(ResortApiFactory.OwnerEmail, ResortApiFactory.OwnerPassword);
        var stolen = victim.RefreshCookieHeader(login)!.Split(';')[0]; // "__Secure-resort_rt=<value>"

        // Victim refreshes normally: the stolen token is now rotated.
        Assert.Equal(HttpStatusCode.OK, (await victim.RefreshAsync()).StatusCode);

        // Attacker replays the old token.
        var attacker = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        req.Headers.Add("Cookie", stolen);
        req.Headers.Add("X-Resort-Client", "web");
        var replay = await attacker.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // ...and the victim's current token was revoked too, forcing a fresh sign-in.
        Assert.Equal(HttpStatusCode.Unauthorized, (await victim.RefreshAsync()).StatusCode);

        var reasons = await app.WithDbAsync(db => db.AuditLogs.CountAsync(a => a.Action == "REFRESH_TOKEN_REUSE"));
        Assert.True(reasons >= 1, "reuse should be audited");
    }

    [Fact]
    public async Task Refresh_without_client_header_is_rejected_as_csrf_defence()
    {
        var c = await app.SignInOwnerAsync();
        var res = await c.RefreshAsync(withClientHeader: false);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("MISSING_CLIENT_HEADER", await res.ProblemCodeAsync());
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token()
    {
        var c = await app.SignInOwnerAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await c.LogoutAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.RefreshAsync()).StatusCode);
    }

    [Fact]
    public async Task New_user_must_change_password_before_using_the_system()
    {
        var owner = await app.SignInOwnerAsync();
        var email = $"new-{Guid.NewGuid():N}@test.local";
        var created = await owner.PostAsync("/api/v1/users", new CreateUserRequest(email, "New Person", null, "TempPass12345",
            [new RoleGrantDto("EMPLOYEE", null)]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var user = await app.SignInAsync(email, "TempPass12345");
        Assert.True(user.Me!.MustChangePassword);

        var blocked = await user.GetAsync("/api/v1/properties");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("PASSWORD_CHANGE_REQUIRED", await blocked.ProblemCodeAsync());

        var changed = await user.PostAsync("/api/v1/auth/change-password", new ChangePasswordRequest("TempPass12345", "MyOwnPass12345"));
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        // Password change rotates the security stamp: the old access token is dead, sign in again.
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/v1/properties")).StatusCode);
        var again = await app.SignInAsync(email, "MyOwnPass12345");
        Assert.False(again.Me!.MustChangePassword);
        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/api/v1/properties")).StatusCode);
    }

    [Fact]
    public async Task Deactivated_user_is_signed_out_immediately()
    {
        var (email, password) = await app.CreateUserAsync("RECEPTIONIST", await app.MainPropertyIdAsync());
        var user = await app.SignInAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/v1/properties")).StatusCode);

        var owner = await app.SignInOwnerAsync();
        var userId = user.Me!.UserId;
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/v1/users/{userId}/deactivate")).StatusCode);

        // Same, still-unexpired access token: rejected because the security stamp changed.
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/v1/properties")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.RefreshAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.NewClient().LoginAsync(email, password)).StatusCode);
    }
}

public class RateLimitedApiFactory : ResortApiFactory
{
    protected override int AuthRateLimit => 3;
}

public class RateLimitTests(RateLimitedApiFactory app) : IClassFixture<RateLimitedApiFactory>
{
    [Fact]
    public async Task Sign_in_is_rate_limited_per_ip()
    {
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
            statuses.Add((await app.NewClient().LoginAsync("someone@test.local", "Whatever12345")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, statuses[0]);
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[4]);
    }
}
