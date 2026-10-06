using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Contracts;
using Resort.Domain.Identity;

namespace Resort.Application.Identity;

public sealed class UserService(
    IAppDbContext db,
    ICurrentUser caller,
    CallerAccess access,
    IPasswordHasherService hasher,
    IUserAccessService accessCache,
    TimeProvider clock)
{
    public const string OwnerRole = "OWNER";

    public async Task<IReadOnlyList<UserDto>> ListAsync(CancellationToken ct)
    {
        var users = await db.Users.Include(u => u.Roles).ThenInclude(r => r.Role)
            .OrderBy(u => u.FullName).ToListAsync(ct);
        return users.Select(ToDto).ToList();
    }

    public async Task<UserDto> GetAsync(Guid id, CancellationToken ct) => ToDto(await LoadAsync(id, ct));

    public async Task<UserDto> CreateAsync(CreateUserRequest r, CancellationToken ct)
    {
        var orgId = caller.RequireOrganizationId();
        var normalized = r.Email.Trim().ToLowerInvariant();

        // Email is unique platform-wide, so check across tenants (without revealing which tenant).
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.NormalizedEmail == normalized && u.DeletedAt == null, ct))
            throw new ConflictAppException("EMAIL_TAKEN", "A user with this email already exists.");

        var grants = await ResolveGrantsAsync(r.Roles, ct);
        await EnsureCanGrantAsync(grants, ct);

        var user = new User
        {
            OrganizationId = orgId,
            FullName = r.FullName.Trim(),
            Phone = string.IsNullOrWhiteSpace(r.Phone) ? null : r.Phone.Trim(),
            PasswordHash = hasher.Hash(r.TemporaryPassword),
            MustChangePassword = true,
        };
        user.SetEmail(r.Email);
        var now = clock.GetUtcNow();
        foreach (var g in grants)
            user.Roles.Add(new UserRole { UserId = user.Id, RoleId = g.Role.Id, Role = g.Role, PropertyId = g.PropertyId, CreatedAt = now, CreatedBy = caller.UserId });

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return ToDto(user);
    }

    public async Task<UserDto> UpdateAsync(Guid id, UpdateUserRequest r, CancellationToken ct)
    {
        var user = await LoadAsync(id, ct);
        ConcurrencyGuard.ExpectVersion(db, user, r.Version);
        user.FullName = r.FullName.Trim();
        user.Phone = string.IsNullOrWhiteSpace(r.Phone) ? null : r.Phone.Trim();
        await db.SaveChangesAsync(ct);
        return ToDto(user);
    }

    /// <summary>Replaces the user's role grants. The caller must be able to grant every role added AND
    /// every role removed (so a manager can't strip an owner), can't edit their own grants, and the
    /// organisation must keep at least one active org-wide OWNER.</summary>
    public async Task<UserDto> SetRolesAsync(Guid id, SetUserRolesRequest r, CancellationToken ct)
    {
        if (id == caller.UserId)
            throw new BusinessRuleAppException("SELF_ROLE_CHANGE", "You can't change your own roles.");

        var user = await LoadAsync(id, ct);
        var desired = await ResolveGrantsAsync(r.Roles, ct);

        var current = user.Roles.Select(x => new ResolvedGrant(x.Role, x.PropertyId)).ToList();
        var added = desired.Where(d => !current.Any(c => c.Same(d))).ToList();
        var removed = current.Where(c => !desired.Any(d => d.Same(c))).ToList();

        await EnsureCanGrantAsync(added.Concat(removed), ct);

        if (removed.Any(g => g.Role.Code == OwnerRole && g.PropertyId is null)
            && !desired.Any(g => g.Role.Code == OwnerRole && g.PropertyId is null))
            await EnsureAnotherOwnerAsync(user.Id, ct);

        var now = clock.GetUtcNow();
        foreach (var g in removed)
        {
            var row = user.Roles.First(x => x.RoleId == g.Role.Id && x.PropertyId == g.PropertyId);
            user.Roles.Remove(row);
            db.UserRoles.Remove(row);
        }
        foreach (var g in added)
            user.Roles.Add(new UserRole { UserId = user.Id, RoleId = g.Role.Id, Role = g.Role, PropertyId = g.PropertyId, CreatedAt = now, CreatedBy = caller.UserId });

        await db.SaveChangesAsync(ct);
        accessCache.Invalidate(user.Id);
        return ToDto(user);
    }

    public async Task<UserDto> SetActiveAsync(Guid id, bool active, CancellationToken ct)
    {
        if (id == caller.UserId)
            throw new BusinessRuleAppException("SELF_DEACTIVATE", "You can't deactivate your own account.");

        var user = await LoadAsync(id, ct);
        if (user.IsActive == active) return ToDto(user);

        await EnsureCanGrantAsync(user.Roles.Select(x => new ResolvedGrant(x.Role, x.PropertyId)), ct);

        if (!active)
        {
            if (user.Roles.Any(x => x.Role.Code == OwnerRole && x.PropertyId is null))
                await EnsureAnotherOwnerAsync(user.Id, ct);
            user.RotateSecurityStamp();
            await RevokeSessionsAsync(user.Id, RevokeReasons.UserDeactivated, ct);
        }
        user.IsActive = active;
        await db.SaveChangesAsync(ct);
        accessCache.Invalidate(user.Id);
        return ToDto(user);
    }

    public async Task ResetPasswordAsync(Guid id, ResetPasswordRequest r, CancellationToken ct)
    {
        var user = await LoadAsync(id, ct);
        await EnsureCanGrantAsync(user.Roles.Select(x => new ResolvedGrant(x.Role, x.PropertyId)), ct);

        user.PasswordHash = hasher.Hash(r.TemporaryPassword);
        user.MustChangePassword = true;
        user.FailedLoginCount = 0;
        user.LockoutUntil = null;
        user.RotateSecurityStamp();
        await RevokeSessionsAsync(user.Id, RevokeReasons.Admin, ct);
        await db.SaveChangesAsync(ct);
        accessCache.Invalidate(user.Id);
    }

    // ------------------------------------------------------------------ helpers

    private sealed record ResolvedGrant(Role Role, Guid? PropertyId)
    {
        public bool Same(ResolvedGrant o) => o.Role.Id == Role.Id && o.PropertyId == PropertyId;
    }

    private async Task<List<ResolvedGrant>> ResolveGrantsAsync(IReadOnlyList<RoleGrantDto> requested, CancellationToken ct)
    {
        var orgId = caller.RequireOrganizationId();
        var codes = requested.Select(x => x.RoleCode).Distinct().ToList();
        var roles = await db.Roles.Include(r => r.Permissions)
            .Where(r => codes.Contains(r.Code) && (r.OrganizationId == null || r.OrganizationId == orgId))
            .ToListAsync(ct);

        var propertyIds = requested.Where(x => x.PropertyId is not null).Select(x => x.PropertyId!.Value).Distinct().ToList();
        var known = await db.Properties.Where(p => propertyIds.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct);

        var result = new List<ResolvedGrant>();
        foreach (var g in requested.DistinctBy(x => (x.RoleCode, x.PropertyId)))
        {
            // Custom org role with the same code wins over the system template.
            var role = roles.Where(x => x.Code == g.RoleCode).OrderByDescending(x => x.OrganizationId is not null).FirstOrDefault()
                ?? throw new BusinessRuleAppException("UNKNOWN_ROLE", $"Role '{g.RoleCode}' does not exist.");
            if (g.PropertyId is { } pid && !known.Contains(pid))
                throw new BusinessRuleAppException("UNKNOWN_PROPERTY", $"Property '{pid}' does not exist.");
            result.Add(new ResolvedGrant(role, g.PropertyId));
        }
        return result;
    }

    /// <summary>Privilege-escalation guard: you can only grant/revoke a role whose permissions you hold yourself in that scope.</summary>
    private async Task EnsureCanGrantAsync(IEnumerable<ResolvedGrant> grants, CancellationToken ct)
    {
        var mine = await access.GetAsync(ct);
        foreach (var g in grants)
        {
            var perms = g.Role.Permissions.Count > 0
                ? g.Role.Permissions.Select(p => p.PermissionCode)
                : await db.RolePermissions.Where(rp => rp.RoleId == g.Role.Id).Select(rp => rp.PermissionCode).ToListAsync(ct);

            var missing = perms.Where(p => g.PropertyId is null ? !mine.HasOrgWide(p) : !mine.Has(p, g.PropertyId)).ToList();
            if (missing.Count > 0)
                throw new ForbiddenAppException($"You can't grant or remove the {g.Role.Name} role: it includes permissions you don't have.");
        }
    }

    private async Task EnsureAnotherOwnerAsync(Guid excludingUserId, CancellationToken ct)
    {
        var others = await db.UserRoles
            .Where(ur => ur.UserId != excludingUserId && ur.PropertyId == null && ur.Role.Code == OwnerRole)
            .Join(db.Users.Where(u => u.IsActive), ur => ur.UserId, u => u.Id, (ur, u) => u.Id)
            .AnyAsync(ct);
        if (!others)
            throw new BusinessRuleAppException("LAST_OWNER", "The organisation must keep at least one active owner.");
    }

    private async Task RevokeSessionsAsync(Guid userId, string reason, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var tokens = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in tokens) t.Revoke(now, reason);
    }

    private async Task<User> LoadAsync(Guid id, CancellationToken ct) =>
        await db.Users.Include(u => u.Roles).ThenInclude(r => r.Role).ThenInclude(r => r.Permissions)
            .SingleOrDefaultAsync(u => u.Id == id, ct)
        ?? throw new NotFoundAppException("User", id);

    private static UserDto ToDto(User u) => new(
        u.Id, u.Email, u.FullName, u.Phone, u.IsActive, u.MustChangePassword, u.LastLoginAt,
        u.Roles.Select(r => new RoleGrantDto(r.Role.Code, r.PropertyId)).OrderBy(r => r.RoleCode).ToList(),
        u.Version);
}

public sealed class RoleService(IAppDbContext db, ICurrentUser caller)
{
    public async Task<IReadOnlyList<RoleDto>> ListAsync(CancellationToken ct)
    {
        var orgId = caller.RequireOrganizationId();
        var roles = await db.Roles.Include(r => r.Permissions)
            .Where(r => r.OrganizationId == null || r.OrganizationId == orgId)
            .OrderBy(r => r.Name).ToListAsync(ct);
        return roles.Select(r => new RoleDto(r.Id, r.Code, r.Name, r.Description, r.IsSystem,
            r.Permissions.Select(p => p.PermissionCode).Order().ToList())).ToList();
    }

    public async Task<IReadOnlyList<PermissionDto>> ListPermissionsAsync(CancellationToken ct) =>
        await db.Permissions.OrderBy(p => p.Module).ThenBy(p => p.Code)
            .Select(p => new PermissionDto(p.Code, p.Module, p.Description, p.IsSensitive)).ToListAsync(ct);
}
