using Resort.Api.Infrastructure;
using Resort.Application.Identity;
using Resort.Contracts;

namespace Resort.Api.Endpoints;

public static class AuthEndpoints
{
    /// <summary>__Secure- prefix: browsers only accept it over HTTPS (localhost is treated as secure).</summary>
    public const string RefreshCookie = "__Secure-resort_rt";
    public const string ClientHeader = "X-Resort-Client";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Only password-guessing surfaces get the strict per-IP limit. Refresh/me are cheap and frequent, and staff
        // share one resort IP, so they fall under the global per-user limit instead.
        var g = app.MapGroup("/api/v1/auth").WithTags("Auth");

        g.MapPost("/login", async (LoginRequest req, AuthService auth, HttpContext http, CancellationToken ct) =>
        {
            var result = await auth.LoginAsync(req, ct);
            SetRefreshCookie(http, result);
            return Results.Ok(result.Response);
        }).Validate<LoginRequest>().AllowAnonymous().RequireRateLimiting(RateLimits.Auth);

        g.MapPost("/refresh", async (AuthService auth, HttpContext http, CancellationToken ct) =>
        {
            var raw = http.Request.Cookies[RefreshCookie];
            if (string.IsNullOrEmpty(raw))
                return Problems.Result(http, 401, "INVALID_REFRESH_TOKEN", "Session expired. Please sign in again.");
            try
            {
                var result = await auth.RefreshAsync(raw, ct);
                SetRefreshCookie(http, result);
                return Results.Ok(result.Response);
            }
            catch (Application.Common.UnauthorizedAppException)
            {
                ClearRefreshCookie(http);
                throw;
            }
        }).AddEndpointFilter<RequireClientHeaderFilter>().AllowAnonymous();

        g.MapPost("/logout", async (AuthService auth, HttpContext http, CancellationToken ct) =>
        {
            await auth.LogoutAsync(http.Request.Cookies[RefreshCookie], ct);
            ClearRefreshCookie(http);
            return Results.NoContent();
        }).AddEndpointFilter<RequireClientHeaderFilter>().AllowAnonymous();

        g.MapGet("/me", (AuthService auth, CancellationToken ct) => auth.MeAsync(ct)).RequireAuthorization();

        g.MapPost("/change-password", async (ChangePasswordRequest req, AuthService auth, HttpContext http, CancellationToken ct) =>
        {
            await auth.ChangePasswordAsync(req, ct);
            ClearRefreshCookie(http);
            return Results.NoContent();
        }).Validate<ChangePasswordRequest>().RequireAuthorization().RequireRateLimiting(RateLimits.Auth);
    }

    private static void SetRefreshCookie(HttpContext http, AuthResult r) =>
        http.Response.Cookies.Append(RefreshCookie, r.RefreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/api/v1/auth",
            Expires = r.RefreshExpiresAt,
            IsEssential = true,
        });

    private static void ClearRefreshCookie(HttpContext http) =>
        http.Response.Cookies.Delete(RefreshCookie, new CookieOptions { Path = "/api/v1/auth", Secure = true, SameSite = SameSiteMode.Strict, HttpOnly = true });

    /// <summary>CSRF defence for the cookie-authenticated endpoints: a custom header can't be sent
    /// cross-site without a CORS preflight, which our CORS policy refuses (on top of SameSite=Strict).</summary>
    private sealed class RequireClientHeaderFilter : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
        {
            if (!ctx.HttpContext.Request.Headers.ContainsKey(ClientHeader))
                return Problems.Result(ctx.HttpContext, 400, "MISSING_CLIENT_HEADER", $"Header {ClientHeader} is required.");
            return await next(ctx);
        }
    }
}
