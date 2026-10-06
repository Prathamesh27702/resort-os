using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Resort.Application.Common;

namespace Resort.Api.Infrastructure;

/// <summary>How a permission check is scoped.</summary>
public enum PermissionScope
{
    /// <summary>Route has {propertyId}: need the permission org-wide or for that property.
    /// Otherwise: need it org-wide.</summary>
    Auto,
    /// <summary>Need the permission for at least one scope (the service then filters results).</summary>
    Any,
}

public sealed record PermissionRequirement(string Permission, PermissionScope Scope) : IAuthorizationRequirement;

public static class AuthFailures
{
    public const string PasswordChangeRequired = "PASSWORD_CHANGE_REQUIRED";
}

/// <summary>Permission-based authorisation: roles are never checked in code, only permission codes.</summary>
public sealed class PermissionHandler(IUserAccessService accessService) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement req)
    {
        if (!Guid.TryParse(context.User.FindFirst("sub")?.Value, out var userId)) return;

        var access = await accessService.GetAsync(userId, CancellationToken.None);
        if (access is null || !access.IsActive) return;

        if (access.MustChangePassword)
        {
            context.Fail(new AuthorizationFailureReason(this, AuthFailures.PasswordChangeRequired));
            return;
        }

        Guid? propertyId = null;
        if (context.Resource is HttpContext http
            && http.Request.RouteValues.TryGetValue("propertyId", out var raw)
            && Guid.TryParse(raw?.ToString(), out var pid))
            propertyId = pid;

        var ok = req.Scope switch
        {
            PermissionScope.Any => access.HasAnywhere(req.Permission),
            _ when propertyId is not null => access.Has(req.Permission, propertyId),
            _ => access.HasOrgWide(req.Permission),
        };
        if (ok) context.Succeed(req);
    }
}

public static class AuthorizationExtensions
{
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission, PermissionScope scope = PermissionScope.Auto)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(permission, scope))
            .Build());
}

/// <summary>Writes 401/403 as problem+json with a stable code the UI can act on.</summary>
public sealed class ProblemAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (result.Challenged)
        {
            await Problems.Write(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Authentication required.");
            return;
        }
        if (result.Forbidden)
        {
            var mustChange = result.AuthorizationFailure?.FailureReasons.Any(r => r.Message == AuthFailures.PasswordChangeRequired) == true;
            if (mustChange)
                await Problems.Write(context, StatusCodes.Status403Forbidden, AuthFailures.PasswordChangeRequired, "You must change your password before continuing.");
            else
                await Problems.Write(context, StatusCodes.Status403Forbidden, "FORBIDDEN", "You do not have permission to do this.");
            return;
        }
        await _default.HandleAsync(next, context, policy, result);
    }
}
