using System.Text.RegularExpressions;
using Serilog.Context;

namespace Resort.Api.Infrastructure;

/// <summary>
/// Correlation and request IDs (brief §58, §61).
///  * X-Correlation-Id: accepted from the client if it is well-formed (lets the UI tie several calls of one user action
///    together), otherwise generated. Becomes HttpContext.TraceIdentifier, so it appears in every error body and log line.
///  * X-Request-Id: always generated, unique per HTTP request.
/// Both are echoed in response headers and pushed into the Serilog log context.
/// </summary>
public static partial class RequestContext
{
    public const string CorrelationHeader = "X-Correlation-Id";
    public const string RequestIdHeader = "X-Request-Id";
    public const string ChangeReasonHeader = "X-Change-Reason";
    public const string RequestIdKey = "resort.request-id";

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex SafeId();

    public static IApplicationBuilder UseRequestContext(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var incoming = ctx.Request.Headers[CorrelationHeader].ToString();
        var correlationId = SafeId().IsMatch(incoming) ? incoming : Guid.NewGuid().ToString("N");
        var requestId = Guid.NewGuid().ToString("N");

        ctx.TraceIdentifier = correlationId;
        ctx.Items[RequestIdKey] = requestId;
        ctx.Response.Headers[CorrelationHeader] = correlationId;
        ctx.Response.Headers[RequestIdHeader] = requestId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        using (LogContext.PushProperty("RequestId", requestId))
            await next();
    });
}
