namespace Resort.Application.Common;

/// <summary>Resource-level authorisation helpers for services (endpoint policies do the coarse check).</summary>
public sealed class CallerAccess(ICurrentUser caller, IUserAccessService accessService)
{
    private UserAccess? _cached;

    public async Task<UserAccess> GetAsync(CancellationToken ct) =>
        _cached ??= await accessService.GetAsync(caller.RequireUserId(), ct) ?? throw new UnauthorizedAppException();

    public async Task RequireAsync(string permission, Guid? propertyId, CancellationToken ct)
    {
        var access = await GetAsync(ct);
        var ok = propertyId is null ? access.HasOrgWide(permission) : access.Has(permission, propertyId);
        if (!ok) throw new ForbiddenAppException();
    }
}
