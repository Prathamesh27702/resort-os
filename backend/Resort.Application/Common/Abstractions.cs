using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Resort.Domain.Accommodation;
using Resort.Domain.Audit;
using Resort.Domain.Guests;
using Resort.Domain.Identity;
using Resort.Domain.Organizations;
using Resort.Domain.Rates;

namespace Resort.Application.Common;

/// <summary>The EF Core unit of work as seen by application services. Kept as an interface only so the
/// Application project doesn't reference Npgsql; there is exactly one implementation.</summary>
public interface IAppDbContext
{
    DbSet<Organization> Organizations { get; }
    DbSet<Property> Properties { get; }
    DbSet<PropertySetting> PropertySettings { get; }
    DbSet<SystemSetting> SystemSettings { get; }
    DbSet<User> Users { get; }
    DbSet<Role> Roles { get; }
    DbSet<Permission> Permissions { get; }
    DbSet<RolePermission> RolePermissions { get; }
    DbSet<UserRole> UserRoles { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<LoginEvent> LoginEvents { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<Amenity> Amenities { get; }
    DbSet<AccommodationType> AccommodationTypes { get; }
    DbSet<AccommodationTypeAmenity> AccommodationTypeAmenities { get; }
    DbSet<AccommodationUnit> AccommodationUnits { get; }
    DbSet<Bed> Beds { get; }
    DbSet<UnitImage> UnitImages { get; }
    DbSet<UnitBlock> UnitBlocks { get; }
    DbSet<Guest> Guests { get; }
    DbSet<GuestAddress> GuestAddresses { get; }
    DbSet<GuestDocument> GuestDocuments { get; }
    DbSet<GuestPreference> GuestPreferences { get; }
    DbSet<GuestConsent> GuestConsents { get; }
    DbSet<ReservationSource> ReservationSources { get; }
    DbSet<RatePlan> RatePlans { get; }
    DbSet<RatePlanPrice> RatePlanPrices { get; }
    DbSet<Package> Packages { get; }
    DbSet<PackageComponent> PackageComponents { get; }

    DatabaseFacade Database { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Who is calling. Populated from the validated JWT; empty for anonymous requests.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid? UserId { get; }
    Guid? OrganizationId { get; }
    IPAddress? IpAddress { get; }
    string? UserAgent { get; }
    /// <summary>Ties together every request in one user flow (X-Correlation-Id, generated if absent).</summary>
    string? CorrelationId { get; }
    /// <summary>Unique per HTTP request (X-Request-Id response header).</summary>
    string? RequestId { get; }
    /// <summary>Optional free-text reason sent by the UI in the X-Change-Reason header. Stored on every audit row
    /// written by the request. Services that require a reason (refunds, cancellations…) validate it themselves.</summary>
    string? ChangeReason { get; }

    Guid RequireUserId() => UserId ?? throw new UnauthorizedAppException();
    Guid RequireOrganizationId() => OrganizationId ?? throw new UnauthorizedAppException();
}

public interface IPasswordHasherService
{
    string Hash(string password);
    PasswordCheck Verify(string hash, string password);
    /// <summary>Runs a verification against a dummy hash (timing equaliser for unknown emails).</summary>
    void SimulateVerify(string password);
}

public enum PasswordCheck { Failed, Success, SuccessRehashNeeded }

public interface ITokenService
{
    (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(User user);
    /// <summary>Returns the raw token (sent to the client once) and its SHA-256 hash (stored).</summary>
    (string RawToken, byte[] Hash) CreateRefreshToken();
    byte[] HashRefreshToken(string rawToken);
}

/// <summary>Resolved access for a user: active flag, security stamp and role grants. Cached briefly.</summary>
public sealed record UserAccess(
    Guid UserId,
    Guid OrganizationId,
    bool IsActive,
    Guid SecurityStamp,
    bool MustChangePassword,
    IReadOnlyList<ScopedGrant> Grants)
{
    /// <summary>True if the user holds <paramref name="permission"/> org-wide, or for <paramref name="propertyId"/>.</summary>
    public bool Has(string permission, Guid? propertyId) =>
        Grants.Any(g => (g.PropertyId is null || (propertyId is not null && g.PropertyId == propertyId))
                        && g.Permissions.Contains(permission));

    /// <summary>True if the user holds the permission organisation-wide (property_id NULL grant).</summary>
    public bool HasOrgWide(string permission) =>
        Grants.Any(g => g.PropertyId is null && g.Permissions.Contains(permission));

    /// <summary>True if the user holds the permission for at least one scope.</summary>
    public bool HasAnywhere(string permission) => Grants.Any(g => g.Permissions.Contains(permission));
}

public sealed record ScopedGrant(Guid? PropertyId, string RoleCode, IReadOnlySet<string> Permissions);

public interface IUserAccessService
{
    Task<UserAccess?> GetAsync(Guid userId, CancellationToken ct);
    void Invalidate(Guid userId);
}
