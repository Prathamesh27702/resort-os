using System.Net;
using Resort.Application.Common;
using Resort.Infrastructure.Identity;

namespace Resort.Api.Infrastructure;

public sealed class HttpCurrentUser(IHttpContextAccessor accessor, SystemActor actor) : ICurrentUser
{
    private HttpContext? Ctx => accessor.HttpContext;

    public bool IsAuthenticated => Ctx?.User.Identity?.IsAuthenticated == true;

    public Guid? UserId => IsAuthenticated && Guid.TryParse(Ctx!.User.FindFirst("sub")?.Value, out var id) ? id : null;

    /// <summary>The signed-in user's organisation; for an anonymous call (Razorpay's webhook, the guest's pay page) the one
    /// it acts for once it has matched a payment link (<see cref="SystemActor"/>).</summary>
    public Guid? OrganizationId =>
        IsAuthenticated ? Guid.TryParse(Ctx!.User.FindFirst(ResortClaims.Organization)?.Value, out var id) ? id : null : actor.OrganizationId;

    public IPAddress? IpAddress => Ctx?.Connection.RemoteIpAddress;

    public string? UserAgent => Ctx?.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;

    public string? CorrelationId => Ctx?.TraceIdentifier;

    public string? RequestId => Ctx?.Items[RequestContext.RequestIdKey] as string;

    public string? ChangeReason
    {
        get
        {
            var raw = Ctx?.Request.Headers[RequestContext.ChangeReasonHeader].ToString();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var text = Uri.UnescapeDataString(raw).Trim();
            return text.Length <= 500 ? text : text[..500];
        }
    }
}
