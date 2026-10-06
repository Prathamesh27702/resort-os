using System.Net;
using Resort.Domain.Common;

namespace Resort.Domain.Identity;

public class User : AuditableEntity, IOrganizationScoped, ISoftDelete, IVersioned, IAudited
{
    public Guid OrganizationId { get; set; }
    public string Email { get; set; } = default!;
    public string NormalizedEmail { get; set; } = default!;
    public string? Phone { get; set; }
    public string FullName { get; set; } = default!;
    public string PasswordHash { get; set; } = default!;
    public Guid SecurityStamp { get; set; } = Guid.NewGuid();
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public bool MfaEnabled { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockoutUntil { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public uint Version { get; set; }

    public List<UserRole> Roles { get; set; } = [];

    public void SetEmail(string email)
    {
        Email = email.Trim();
        NormalizedEmail = Email.ToLowerInvariant();
    }

    /// <summary>Invalidates every issued access token and forces re-authentication.</summary>
    public void RotateSecurityStamp() => SecurityStamp = Guid.NewGuid();

    public bool IsLockedOut(DateTimeOffset now) => LockoutUntil is { } until && until > now;
}

public class Permission
{
    public string Code { get; set; } = default!;
    public string Module { get; set; } = default!;
    public string Description { get; set; } = default!;
    public bool IsSensitive { get; set; }
}

public class Role : AuditableEntity, IAudited
{
    /// <summary>NULL for built-in system roles.</summary>
    public Guid? OrganizationId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public List<RolePermission> Permissions { get; set; } = [];
}

public class RolePermission
{
    public Guid RoleId { get; set; }
    public string PermissionCode { get; set; } = default!;
}

/// <summary>A role grant. PropertyId NULL = every property in the organisation.</summary>
public class UserRole : Entity, IAudited
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public Guid? PropertyId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Role Role { get; set; } = default!;
}

public static class RevokeReasons
{
    public const string Rotated = "ROTATED";
    public const string Logout = "LOGOUT";
    public const string ReuseDetected = "REUSE_DETECTED";
    public const string UserDeactivated = "USER_DEACTIVATED";
    public const string PasswordChanged = "PASSWORD_CHANGED";
    public const string Admin = "ADMIN";
}

public class RefreshToken : Entity
{
    public Guid UserId { get; set; }
    public Guid FamilyId { get; set; }
    public byte[] TokenHash { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
    public Guid? ReplacedById { get; set; }
    public IPAddress? CreatedIp { get; set; }
    public string? UserAgent { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    public void Revoke(DateTimeOffset now, string reason)
    {
        if (RevokedAt is not null) return;
        RevokedAt = now;
        RevokedReason = reason;
    }
}

public class LoginEvent
{
    public long Id { get; set; }
    public Guid? UserId { get; set; }
    public string EmailAttempted { get; set; } = default!;
    public bool Success { get; set; }
    public string? FailureReason { get; set; }
    public IPAddress? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
