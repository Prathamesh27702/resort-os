using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Resort.Application.Common;
using Resort.Contracts;

namespace Resort.Api.Infrastructure;

public static class Problems
{
    public static Task Write(HttpContext ctx, int status, string code, string message, object? details = null)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new ApiError(code, message, details, ctx.TraceIdentifier));
    }

    public static IResult Result(HttpContext ctx, int status, string code, string message) =>
        Results.Json(new ApiError(code, message, null, ctx.TraceIdentifier), statusCode: status);
}

/// <summary>Maps exceptions to problem responses. Unexpected errors return a generic 500 (details only in logs).</summary>
public sealed class AppExceptionHandler(ILogger<AppExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        switch (ex)
        {
            case ValidationException v:
                var errors = v.Errors.GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
                await Problems.Write(ctx, 400, "VALIDATION_FAILED", "Some fields need fixing.", errors);
                return true;
            case NotFoundAppException e: await Problems.Write(ctx, 404, e.Code, e.Message); return true;
            case ConflictAppException e: await Problems.Write(ctx, 409, e.Code, e.Message); return true;
            case ConcurrencyAppException e: await Problems.Write(ctx, 409, e.Code, e.Message); return true;
            case ForbiddenAppException e: await Problems.Write(ctx, 403, e.Code, e.Message); return true;
            case UnauthorizedAppException e: await Problems.Write(ctx, 401, e.Code, e.Message); return true;
            case BusinessRuleAppException e: await Problems.Write(ctx, 422, e.Code, e.Message); return true;
            case DbUpdateConcurrencyException:
                await Problems.Write(ctx, 409, "CONCURRENCY_CONFLICT", "The record was changed by someone else. Reload and try again.");
                return true;
            case DbUpdateException { InnerException: PostgresException pg }:
                var (status, code, msg) = pg.SqlState switch
                {
                    PostgresErrorCodes.UniqueViolation => (409, "DUPLICATE", "A record with the same unique value already exists."),
                    PostgresErrorCodes.ExclusionViolation => (409, "OVERLAP", "This overlaps an existing record."),
                    PostgresErrorCodes.ForeignKeyViolation => (409, "REFERENCE_CONFLICT", "A referenced record is missing or still in use."),
                    PostgresErrorCodes.CheckViolation => (422, "CONSTRAINT_VIOLATION", "A value broke a data rule."),
                    _ => (500, "DATABASE_ERROR", "The request could not be completed."),
                };
                log.LogWarning(ex, "Database constraint {Constraint} ({SqlState})", pg.ConstraintName, pg.SqlState);
                await Problems.Write(ctx, status, code, msg);
                return true;
            case BadHttpRequestException b:
                await Problems.Write(ctx, b.StatusCode, "BAD_REQUEST", "The request was malformed.");
                return true;
            case OperationCanceledException when ctx.RequestAborted.IsCancellationRequested:
                ctx.Response.StatusCode = 499;
                return true;
            default:
                log.LogError(ex, "Unhandled exception");
                await Problems.Write(ctx, 500, "INTERNAL_ERROR", "Something went wrong. Quote the trace id when reporting it.");
                return true;
        }
    }
}

/// <summary>Validates the first argument of type T with its FluentValidation validator before the handler runs.</summary>
public sealed class ValidationFilter<T>(IValidator<T> validator) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var arg = ctx.Arguments.OfType<T>().FirstOrDefault();
        if (arg is null)
            throw new ValidationException([new FluentValidation.Results.ValidationFailure("body", "Request body is required.")]);
        await validator.ValidateAndThrowAsync(arg, ctx.HttpContext.RequestAborted);
        return await next(ctx);
    }
}

public static class ValidationExtensions
{
    public static RouteHandlerBuilder Validate<T>(this RouteHandlerBuilder b) => b.AddEndpointFilter<ValidationFilter<T>>();
}
