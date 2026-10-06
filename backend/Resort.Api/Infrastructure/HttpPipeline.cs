using System.Threading.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Resort.Api.Infrastructure;

public static class RateLimits
{
    public const string Auth = "auth";
}

public sealed class RateLimitOptions
{
    public const string Section = "RateLimits";
    public int AuthPerMinutePerIp { get; set; } = 10;
    public int GlobalPerMinutePerClient { get; set; } = 600;
}

public static class SecurityHeaders
{
    /// <summary>Defence-in-depth headers. The API only serves JSON, so its CSP denies everything.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        ctx.Response.OnStarting(() =>
        {
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            h["Cross-Origin-Resource-Policy"] = "same-origin";
            if (!ctx.Request.Path.StartsWithSegments("/swagger"))
                h["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            if (ctx.Request.Path.StartsWithSegments("/api"))
                h.CacheControl = "no-store";
            return Task.CompletedTask;
        });
        await next();
    });
}

public sealed class DatabaseHealthCheck(NpgsqlDataSource ds) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var cmd = ds.CreateCommand("SELECT 1");
            await cmd.ExecuteScalarAsync(ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database unreachable", ex);
        }
    }
}

public static class RateLimiting
{
    public static IServiceCollection AddResortRateLimiting(this IServiceCollection services, IConfiguration config)
    {
        var opt = config.GetSection(RateLimitOptions.Section).Get<RateLimitOptions>() ?? new RateLimitOptions();
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (ctx, _) => new ValueTask(Problems.Write(ctx.HttpContext, 429, "RATE_LIMITED", "Too many requests. Wait a minute and try again."));

            // Sign-in endpoints: per IP, to slow password guessing.
            o.AddPolicy(RateLimits.Auth, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = opt.AuthPerMinutePerIp, Window = TimeSpan.FromMinutes(1) }));

            // Everything: per signed-in user, else per IP.
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.User.FindFirst("sub")?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = opt.GlobalPerMinutePerClient, Window = TimeSpan.FromMinutes(1) }));
        });
        return services;
    }
}
