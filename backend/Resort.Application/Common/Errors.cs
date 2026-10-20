namespace Resort.Application.Common;

/// <summary>Expected business failures. The API maps each to an RFC 7807 problem response with a stable <c>code</c>.</summary>
public abstract class AppException(string code, string message, object? details = null) : Exception(message)
{
    public string Code { get; } = code;
    /// <summary>Optional structured details returned in the error body (e.g. the possible duplicates of a guest).</summary>
    public object? Details { get; } = details;
}

public sealed class NotFoundAppException : AppException
{
    public NotFoundAppException(string entity, object id) : base("NOT_FOUND", $"{entity} '{id}' was not found.") { }

    /// <summary>A not-found whose message is written for the person reading it (e.g. a guest with a wrong link).</summary>
    public NotFoundAppException(string message) : base("NOT_FOUND", message) { }
}

public sealed class ConflictAppException(string code, string message, object? details = null) : AppException(code, message, details);

/// <summary>Optimistic concurrency failure — someone else saved the record first.</summary>
public sealed class ConcurrencyAppException()
    : AppException("CONCURRENCY_CONFLICT", "The record was changed by someone else. Reload and try again.");

public sealed class ForbiddenAppException(string message = "You do not have permission to do this.")
    : AppException("FORBIDDEN", message);

public sealed class UnauthorizedAppException(string code = "UNAUTHORIZED", string message = "Authentication required.")
    : AppException(code, message);

public sealed class BusinessRuleAppException(string code, string message, object? details = null) : AppException(code, message, details);
