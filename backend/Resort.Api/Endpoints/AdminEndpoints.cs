using FluentValidation;
using Resort.Api.Infrastructure;
using Resort.Application.Audit;
using Resort.Application.Common;
using Resort.Application.Identity;
using Resort.Application.Properties;
using Resort.Application.Validation;
using Resort.Contracts;

namespace Resort.Api.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        // ---------------------------------------------------------------- organisation
        var org = app.MapGroup("/api/v1/organization").WithTags("Organization");
        org.MapGet("", (OrganizationService s, CancellationToken ct) => s.GetAsync(ct))
            .RequirePermission(Perm.OrganizationRead);
        org.MapPut("", (UpdateOrganizationRequest r, OrganizationService s, CancellationToken ct) => s.UpdateAsync(r, ct))
            .Validate<UpdateOrganizationRequest>().RequirePermission(Perm.OrganizationUpdate);

        // ---------------------------------------------------------------- properties
        var props = app.MapGroup("/api/v1/properties").WithTags("Properties");
        props.MapGet("", (PropertyService s, CancellationToken ct) => s.ListAsync(ct))
            .RequirePermission(Perm.PropertyRead, PermissionScope.Any);
        props.MapPost("", async (CreatePropertyRequest r, PropertyService s, CancellationToken ct) =>
            {
                var p = await s.CreateAsync(r, ct);
                return Results.Created($"/api/v1/properties/{p.Id}", p);
            })
            .Validate<CreatePropertyRequest>().RequirePermission(Perm.PropertyCreate);
        props.MapGet("/{propertyId:guid}", (Guid propertyId, PropertyService s, CancellationToken ct) => s.GetAsync(propertyId, ct))
            .RequirePermission(Perm.PropertyRead);
        props.MapPut("/{propertyId:guid}", (Guid propertyId, UpdatePropertyRequest r, PropertyService s, CancellationToken ct) =>
                s.UpdateAsync(propertyId, r, ct))
            .Validate<UpdatePropertyRequest>().RequirePermission(Perm.PropertyUpdate);
        props.MapGet("/{propertyId:guid}/settings", (Guid propertyId, PropertyService s, CancellationToken ct) =>
                s.GetSettingsAsync(propertyId, ct))
            .RequirePermission(Perm.PropertyRead);
        props.MapPut("/{propertyId:guid}/settings/{key}", async (Guid propertyId, string key, PutPropertySettingRequest r,
                PropertyService s, CancellationToken ct) =>
            {
                await new PropertySettingKeyValidator().ValidateAndThrowAsync(key, ct);
                return await s.PutSettingAsync(propertyId, key, r.Value, ct);
            })
            .RequirePermission(Perm.PropertySettingsUpdate);

        // ---------------------------------------------------------------- users
        var users = app.MapGroup("/api/v1/users").WithTags("Users");
        users.MapGet("", (UserService s, CancellationToken ct) => s.ListAsync(ct)).RequirePermission(Perm.UserRead);
        users.MapGet("/{id:guid}", (Guid id, UserService s, CancellationToken ct) => s.GetAsync(id, ct)).RequirePermission(Perm.UserRead);
        users.MapPost("", async (CreateUserRequest r, UserService s, CancellationToken ct) =>
            {
                var u = await s.CreateAsync(r, ct);
                return Results.Created($"/api/v1/users/{u.Id}", u);
            })
            .Validate<CreateUserRequest>().RequirePermission(Perm.UserCreate);
        users.MapPut("/{id:guid}", (Guid id, UpdateUserRequest r, UserService s, CancellationToken ct) => s.UpdateAsync(id, r, ct))
            .Validate<UpdateUserRequest>().RequirePermission(Perm.UserUpdate);
        users.MapPut("/{id:guid}/roles", (Guid id, SetUserRolesRequest r, UserService s, CancellationToken ct) => s.SetRolesAsync(id, r, ct))
            .Validate<SetUserRolesRequest>().RequirePermission(Perm.UserUpdate);
        users.MapPost("/{id:guid}/deactivate", (Guid id, UserService s, CancellationToken ct) => s.SetActiveAsync(id, false, ct))
            .RequirePermission(Perm.UserDeactivate);
        users.MapPost("/{id:guid}/activate", (Guid id, UserService s, CancellationToken ct) => s.SetActiveAsync(id, true, ct))
            .RequirePermission(Perm.UserDeactivate);
        users.MapPost("/{id:guid}/reset-password", async (Guid id, ResetPasswordRequest r, UserService s, CancellationToken ct) =>
            {
                await s.ResetPasswordAsync(id, r, ct);
                return Results.NoContent();
            })
            .Validate<ResetPasswordRequest>().RequirePermission(Perm.UserUpdate);

        // ---------------------------------------------------------------- roles & permissions
        app.MapGet("/api/v1/roles", (RoleService s, CancellationToken ct) => s.ListAsync(ct))
            .WithTags("Roles").RequirePermission(Perm.RoleRead, PermissionScope.Any);
        app.MapGet("/api/v1/permissions", (RoleService s, CancellationToken ct) => s.ListPermissionsAsync(ct))
            .WithTags("Roles").RequirePermission(Perm.RoleRead, PermissionScope.Any);

        // ---------------------------------------------------------------- audit
        app.MapGet("/api/v1/audit-logs", ([AsParameters] AuditQuery q, AuditQueryService s, CancellationToken ct) => s.QueryAsync(q, ct))
            .WithTags("Audit").RequirePermission(Perm.AuditRead);
    }
}
