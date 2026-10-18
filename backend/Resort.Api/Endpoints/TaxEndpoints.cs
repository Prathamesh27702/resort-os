using Resort.Api.Infrastructure;
using Resort.Application.Common;
using Resort.Application.Tax;
using Resort.Contracts;

namespace Resort.Api.Endpoints;

/// <summary>Sprint 9 — GST: a property's registrations, the organisation's tax categories and rules (data with the dates
/// they apply, so a new rate needs no deployment), and a preview of the tax on a charge. Nothing is deleted.
/// Categories and rules belong to the organisation: reading them needs tax.read anywhere, changing them tax.manage
/// organisation-wide.</summary>
public static class TaxEndpoints
{
    public static void MapTaxEndpoints(this IEndpointRouteBuilder app)
    {
        var p = app.MapGroup("/api/v1/properties/{propertyId:guid}/gst-registrations").WithTags("Tax");
        p.MapGet("", (Guid propertyId, TaxService s, CancellationToken ct) => s.ListRegistrationsAsync(propertyId, ct))
            .RequirePermission(Perm.TaxRead);
        p.MapPost("", async (Guid propertyId, CreateGstRegistrationRequest body, TaxService s, CancellationToken ct) =>
            {
                var reg = await s.CreateRegistrationAsync(propertyId, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/gst-registrations/{reg.Id}", reg);
            })
            .Validate<CreateGstRegistrationRequest>().RequirePermission(Perm.TaxManage);

        var t = app.MapGroup("/api/v1/tax").WithTags("Tax");
        t.MapGet("/categories", (TaxService s, CancellationToken ct) => s.ListCategoriesAsync(ct))
            .RequirePermission(Perm.TaxRead, PermissionScope.Any);
        t.MapPost("/categories", async (CreateTaxCategoryRequest body, TaxService s, CancellationToken ct) =>
            {
                var category = await s.CreateCategoryAsync(body, ct);
                return Results.Created($"/api/v1/tax/categories/{category.Id}", category);
            })
            .Validate<CreateTaxCategoryRequest>().RequirePermission(Perm.TaxManage);
        t.MapGet("/rules", (Guid? categoryId, TaxService s, CancellationToken ct) => s.ListRulesAsync(categoryId, ct))
            .RequirePermission(Perm.TaxRead, PermissionScope.Any);
        t.MapPost("/rules", async (CreateTaxRuleRequest body, TaxService s, CancellationToken ct) =>
            {
                var result = await s.CreateRuleAsync(body, ct);
                return Results.Created($"/api/v1/tax/rules/{result.Rule.Id}", result);
            })
            .Validate<CreateTaxRuleRequest>().RequirePermission(Perm.TaxManage);
        t.MapPost("/rules/{id:guid}/verify", (Guid id, TaxService s, CancellationToken ct) => s.VerifyRuleAsync(id, ct))
            .RequirePermission(Perm.TaxManage);
        t.MapPost("/calculate", (TaxCalculationRequest body, TaxService s, CancellationToken ct) => s.CalculateAsync(body, ct))
            .Validate<TaxCalculationRequest>().RequirePermission(Perm.TaxRead, PermissionScope.Any);
    }
}
