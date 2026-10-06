namespace Resort.Contracts;

// ---------------------------------------------------------------- common
/// <summary>Every error response (brief §66). <c>Code</c> is stable and machine-readable, <c>Message</c> is safe to show
/// to a user, <c>Details</c> carries field errors for validation failures, <c>CorrelationId</c> ties the response to server logs.
/// Stack traces, SQL and infrastructure details are never included.</summary>
public sealed record ApiError(string Code, string Message, object? Details, string CorrelationId);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total);

// ---------------------------------------------------------------- auth
public sealed record LoginRequest(string Email, string Password);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record AuthResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, MeResponse Me);

public sealed record MeResponse(
    Guid UserId,
    string Email,
    string FullName,
    bool MustChangePassword,
    OrganizationSummary Organization,
    IReadOnlyList<AccessGrantDto> Grants);

public sealed record OrganizationSummary(Guid Id, string Code, string Name);

/// <summary>Effective permissions for one scope. PropertyId null = organisation-wide.</summary>
public sealed record AccessGrantDto(Guid? PropertyId, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions);

// ---------------------------------------------------------------- organisation
public sealed record OrganizationDto(Guid Id, string Code, string Name, string? LegalName, string BaseCurrency, string CountryCode, uint Version);
public sealed record UpdateOrganizationRequest(string Name, string? LegalName, uint Version);

// ---------------------------------------------------------------- properties
public sealed record PropertyDto(
    Guid Id, string Code, string Name, string? LegalName, string PropertyType, string Timezone, string Currency,
    string? AddressLine1, string? AddressLine2, string? City, string? District, string? StateName, string? GstStateCode,
    string? PostalCode, string CountryCode, string? Phone, string? Email,
    TimeOnly CheckInTime, TimeOnly CheckOutTime, decimal? Latitude, decimal? Longitude,
    bool IsActive, DateOnly LocalDate, uint Version);

public sealed record CreatePropertyRequest(
    string Code, string Name, string? LegalName, string PropertyType, string Timezone, string Currency,
    string? AddressLine1, string? AddressLine2, string? City, string? District, string? StateName, string? GstStateCode,
    string? PostalCode, string CountryCode, string? Phone, string? Email,
    TimeOnly CheckInTime, TimeOnly CheckOutTime, decimal? Latitude, decimal? Longitude);

public sealed record UpdatePropertyRequest(
    string Name, string? LegalName, string PropertyType, string Timezone, string Currency,
    string? AddressLine1, string? AddressLine2, string? City, string? District, string? StateName, string? GstStateCode,
    string? PostalCode, string CountryCode, string? Phone, string? Email,
    TimeOnly CheckInTime, TimeOnly CheckOutTime, decimal? Latitude, decimal? Longitude, bool IsActive, uint Version);

public sealed record PropertySettingDto(string Key, System.Text.Json.JsonElement Value, DateTimeOffset UpdatedAt);
public sealed record PutPropertySettingRequest(System.Text.Json.JsonElement Value);

// ---------------------------------------------------------------- users & roles
public sealed record RoleGrantDto(string RoleCode, Guid? PropertyId);

public sealed record UserDto(
    Guid Id, string Email, string FullName, string? Phone, bool IsActive, bool MustChangePassword,
    DateTimeOffset? LastLoginAt, IReadOnlyList<RoleGrantDto> Roles, uint Version);

public sealed record CreateUserRequest(string Email, string FullName, string? Phone, string TemporaryPassword, IReadOnlyList<RoleGrantDto> Roles);
public sealed record UpdateUserRequest(string FullName, string? Phone, uint Version);
public sealed record SetUserRolesRequest(IReadOnlyList<RoleGrantDto> Roles);
public sealed record ResetPasswordRequest(string TemporaryPassword);

public sealed record RoleDto(Guid Id, string Code, string Name, string? Description, bool IsSystem, IReadOnlyList<string> Permissions);
public sealed record PermissionDto(string Code, string Module, string Description, bool IsSensitive);

// ---------------------------------------------------------------- audit
public sealed record AuditLogDto(
    long Id, DateTimeOffset OccurredAt, Guid? UserId, string? UserName, Guid? PropertyId,
    string Action, string EntityType, string? EntityId, System.Text.Json.JsonElement? Changes, string? Reason,
    string? Device, string? IpAddress, string? CorrelationId, string? RequestId);
