using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Resort.Api.Endpoints;
using Resort.Api.Infrastructure;
using Resort.Application;
using Resort.Application.Common;
using Resort.Infrastructure;
using Resort.Infrastructure.Identity;
using Resort.Infrastructure.Persistence;
using Serilog;

// Local development reads secrets from the gitignored repo-root .env (see .env.example and docs/SETUP.md).
if (string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase))
    DotEnv.Load();

var builder = WebApplication.CreateBuilder(args);

// ------------------------------------------------------------------ logging & telemetry
builder.Host.UseSerilog((ctx, sp, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("app", "resort-api"));

var otlp = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
if (!string.IsNullOrWhiteSpace(otlp))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("resort-api"))
        .WithTracing(t => t
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSource("Npgsql")
            .AddOtlpExporter());
}

// ------------------------------------------------------------------ core services
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AppExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// ------------------------------------------------------------------ authentication
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((o, jwt) =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Value.Issuer,
            ValidAudience = jwt.Value.Audience,
            IssuerSigningKey = jwt.Value.Key(),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
        };
        o.Events = new JwtBearerEvents
        {
            // A valid signature isn't enough: the user must still be active and the token's security stamp
            // must match (rotated on password change / deactivation / admin reset => instant sign-out).
            OnTokenValidated = async ctx =>
            {
                var access = ctx.HttpContext.RequestServices.GetRequiredService<IUserAccessService>();
                var sub = ctx.Principal?.FindFirst("sub")?.Value;
                var stamp = ctx.Principal?.FindFirst(ResortClaims.SecurityStamp)?.Value;
                var user = Guid.TryParse(sub, out var id) ? await access.GetAsync(id, ctx.HttpContext.RequestAborted) : null;
                if (user is null || !user.IsActive || user.SecurityStamp.ToString() != stamp)
                    ctx.Fail("Session is no longer valid.");
            },
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, ProblemAuthorizationResultHandler>();

// ------------------------------------------------------------------ HTTP hardening
builder.Services.AddResortRateLimiting(builder.Configuration);
var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(corsOrigins)
    .WithMethods("GET", "POST", "PUT", "DELETE")
    .WithHeaders("Authorization", "Content-Type", AuthEndpoints.ClientHeader, RequestContext.CorrelationHeader, RequestContext.ChangeReasonHeader)
    .WithExposedHeaders(RequestContext.CorrelationHeader, RequestContext.RequestIdHeader)
    .AllowCredentials()));
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Behind a reverse proxy (nginx/Caddy/cloud LB): trust X-Forwarded-For/Proto from configured proxies only.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var ip in builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [])
        o.KnownProxies.Add(System.Net.IPAddress.Parse(ip));
});
builder.Services.AddHsts(o => { o.MaxAge = TimeSpan.FromDays(365); o.IncludeSubDomains = true; });

builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);
builder.Services.AddOpenApi(o => o.AddDocumentTransformer<BearerSecurityTransformer>());

var app = builder.Build();

// Fail at start-up, not on the first guest document, if the field-encryption key is missing or malformed.
_ = app.Services.GetRequiredService<IFieldEncryptor>();

// ------------------------------------------------------------------ database
if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    await app.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken.None);
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<Bootstrapper>().RunAsync(CancellationToken.None);

    // Demo rooms etc. for local development only (database/seed/dev). Never runs outside Development.
    if (app.Environment.IsDevelopment() && app.Configuration.GetValue("DevSeed:Enabled", false))
        await app.Services.GetRequiredService<DevSeeder>().RunAsync(CancellationToken.None);
}

// ------------------------------------------------------------------ pipeline
app.UseForwardedHeaders();
app.UseRequestContext();
// Request logging sits outside the exception handler so it records the final status code
// (a handled 404/409 is logged as such, not as the exception that produced it).
app.UseSerilogRequestLogging(o => o.GetLevel = (ctx, _, ex) =>
    ex is not null || ctx.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error
    : ctx.Request.Path.StartsWithSegments("/health") ? Serilog.Events.LogEventLevel.Verbose
    : Serilog.Events.LogEventLevel.Information);
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseSecurityHeaders();
app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint("/openapi/v1.json", "Resort OS API v1");
        o.RoutePrefix = "swagger";
    });
}

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).DisableRateLimiting();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).DisableRateLimiting();

app.MapAuthEndpoints();
app.MapAdminEndpoints();
app.MapAccommodationEndpoints();
app.MapGuestEndpoints();

app.Run();

public partial class Program;
