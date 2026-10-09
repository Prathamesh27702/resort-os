namespace Resort.Application.Common;

/// <summary>
/// Per-request audit details set by application services. When a business action carries its own reason
/// (a block's reason, a refund reason…) the service sets <see cref="Reason"/> so every audit row written by
/// that request records it, even if the client didn't send the X-Change-Reason header.
/// </summary>
public sealed class AuditContext
{
    public string? Reason { get; set; }
}
