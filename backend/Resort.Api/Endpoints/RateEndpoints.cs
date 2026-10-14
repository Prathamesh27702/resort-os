using Resort.Api.Infrastructure;
using Resort.Application.Common;
using Resort.Application.Rates;
using Resort.Application.Validation;
using Resort.Contracts;

namespace Resort.Api.Endpoints;

/// <summary>Sprint 4 — rate plans, the rate grid, quotes, packages and reservation sources.
/// All routes are under a property; the permission must be granted org-wide or for that property.</summary>
public static class RateEndpoints
{
    public static void MapRateEndpoints(this IEndpointRouteBuilder app)
    {
        var p = app.MapGroup("/api/v1/properties/{propertyId:guid}");

        // ---------------------------------------------------------------- reservation sources (used from Sprint 5)
        p.MapGet("/reservation-sources", (Guid propertyId, RateService s, CancellationToken ct) => s.ListSourcesAsync(propertyId, ct))
            .WithTags("Rates").RequirePermission(Perm.RateRead);

        // ---------------------------------------------------------------- rate plans
        var plans = p.MapGroup("/rate-plans").WithTags("Rates");
        plans.MapGet("", (Guid propertyId, RateService s, CancellationToken ct) => s.ListPlansAsync(propertyId, ct))
            .RequirePermission(Perm.RateRead);
        plans.MapGet("/{id:guid}", (Guid propertyId, Guid id, RateService s, CancellationToken ct) => s.GetPlanAsync(propertyId, id, ct))
            .RequirePermission(Perm.RateRead);
        plans.MapPost("", async (Guid propertyId, CreateRatePlanRequest r, RateService s, CancellationToken ct) =>
            {
                var plan = await s.CreatePlanAsync(propertyId, r, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/rate-plans/{plan.Id}", plan);
            })
            .Validate<CreateRatePlanRequest>().RequirePermission(Perm.RateManage);
        plans.MapPut("/{id:guid}", (Guid propertyId, Guid id, UpdateRatePlanRequest r, RateService s, CancellationToken ct) =>
                s.UpdatePlanAsync(propertyId, id, r, ct))
            .Validate<UpdateRatePlanRequest>().RequirePermission(Perm.RateManage);

        // ---------------------------------------------------------------- rate grid
        plans.MapGet("/{id:guid}/prices", (Guid propertyId, Guid id, DateOnly? from, DateOnly? to, RateService s, CancellationToken ct) =>
                s.GetPricesAsync(propertyId, id, from, to, ct))
            .RequirePermission(Perm.RateRead);
        plans.MapPut("/{id:guid}/prices", (Guid propertyId, Guid id, SetRatePricesRequest r, RateService s, CancellationToken ct) =>
                s.SetPricesAsync(propertyId, id, r, ct))
            .Validate<SetRatePricesRequest>().RequirePermission(Perm.RateManage);

        // ---------------------------------------------------------------- quote
        p.MapGet("/rates/quote", (Guid propertyId, string? type, string? room, string? ratePlan, DateOnly? from, DateOnly? to,
                    int? adults, int? children, RateService s, CancellationToken ct) =>
                s.QuoteAsync(propertyId, new RateQuoteQuery(type, room, ratePlan, from, to, adults, children), ct))
            .WithTags("Rates").RequirePermission(Perm.RateRead);

        // ---------------------------------------------------------------- packages
        var packages = p.MapGroup("/packages").WithTags("Packages");
        packages.MapGet("", (Guid propertyId, PackageService s, CancellationToken ct) => s.ListAsync(propertyId, ct))
            .RequirePermission(Perm.RateRead);
        packages.MapGet("/{id:guid}", (Guid propertyId, Guid id, PackageService s, CancellationToken ct) => s.GetAsync(propertyId, id, ct))
            .RequirePermission(Perm.RateRead);
        packages.MapPost("", async (Guid propertyId, CreatePackageRequest r, PackageService s, CancellationToken ct) =>
            {
                var pkg = await s.CreateAsync(propertyId, r, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/packages/{pkg.Id}", pkg);
            })
            .Validate<CreatePackageRequest>().RequirePermission(Perm.RateManage);
        packages.MapPut("/{id:guid}", (Guid propertyId, Guid id, UpdatePackageRequest r, PackageService s, CancellationToken ct) =>
                s.UpdateAsync(propertyId, id, r, ct))
            .Validate<UpdatePackageRequest>().RequirePermission(Perm.RateManage);
    }
}
