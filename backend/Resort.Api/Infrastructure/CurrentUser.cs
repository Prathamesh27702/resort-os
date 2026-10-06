using System.Net;
using Resort.Application.Common;
using Resort.Infrastructure.Identity;

namespace Resort.Api.Infrastructure;

public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private HttpContext? Ctx => accessor.HttpContext;

    public bool IsAuthenticated => Ctx?.User.Identity?.IsAuthenticated == true;

    public Guid? UserId => IsAuthenticated && Guid.TryParse(Ctx!.User.FindFirst("sub")?.Value, out var id) ? id : null;

    public Guid? OrganizationId =>
        IsAuthenticated && Guid.TryParse(Ctx!.User.FindFirst(ResortClaims.Organization)?.Value, out var id) ? id : null;

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
