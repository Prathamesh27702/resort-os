using System.Net;

namespace Resort.Domain.Audit;

/// <summary>Append-only. The database rejects UPDATE/DELETE on this table.</summary>
public class AuditLog
{
    public long Id { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid? PropertyId { get; set; }
    public Guid? UserId { get; set; }
    public string Action { get; set; } = default!;
    public string EntityType { get; set; } = default!;
    public string? EntityId { get; set; }
    /// <summary>jsonb: {"Column": {"old": .., "new": ..}}</summary>
    public string? Changes { get; set; }
    public string? CorrelationId { get; set; }
    public string? RequestId { get; set; }
    /// <summary>User-Agent of the client (device).</summary>
    public string? UserAgent { get; set; }
    public string? Reason { get; set; }
    public IPAddress? IpAddress { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public static class AuditActions
{
    public const string Insert = "INSERT";
    public const string Update = "UPDATE";
    public const string Delete = "DELETE";
    public const string SoftDelete = "SOFT_DELETE";
    public const string RefreshTokenReuse = "REFRESH_TOKEN_REUSE";
    public const string PasswordChanged = "PASSWORD_CHANGED";
    public const string SessionsRevoked = "SESSIONS_REVOKED";
    /// <summary>Someone opened sensitive data (a guest's ID number or scan).</summary>
    public const string SensitiveRead = "SENSITIVE_READ";
}
