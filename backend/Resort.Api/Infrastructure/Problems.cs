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

    /// <summary>
    /// The top-level message for a validation failure: the reasons themselves, never a generic "fix these fields",
    /// so a screen that only shows the message still says why. Duplicates are dropped; at most 3 are listed.
    /// </summary>
    public static string Reasons(IEnumerable<string> messages)
    {
        var list = messages.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim().TrimEnd('.') + ".").Distinct().ToList();
        if (list.Count == 0) return "The request has a value that isn't valid.";
        return string.Join(" ", list.Take(3)) + (list.Count > 3 ? $" (and {list.Count - 3} more)" : "");
    }

    public static IResult Result(HttpContext ctx, int status, string code, string message) =>
        Results.Json(new ApiError(code, message, null, ctx.TraceIdentifier), statusCode: status);
}

/// <summary>Maps exceptions to problem responses. Unexpected errors return a generic 500 (details only in logs).</summary>
public sealed class AppExceptionHandler(ILogger<AppExceptionHandler> log) : IExceptionHandler
{
    /// <summary>Plain-words reasons for constraints a user can hit when two people save at the same moment
    /// (the services check these first, so only a race reaches the database).</summary>
    private static readonly Dictionary<string, (string? Code, string Message)> KnownConstraints = new(StringComparer.Ordinal)
    {
        ["ux_rate_plan_prices_cell"] = (null, "Someone else set prices for the same nights at the same moment. Reload the rate grid and try again."),
        ["ux_rate_plans_default"] = (null, "Another rate plan was made the default at the same moment. Reload and try again."),
        ["ux_rate_plans_code"] = (null, "A rate plan with this code already exists at this property."),
        ["ux_packages_code"] = (null, "A package with this code already exists at this property."),
        // The final guards against double booking (V007). The service checks first under row locks, so these only
        // fire if something bypasses it.
        ["ex_roster_shifts_clash"] = ("SHIFT_CLASH", "Someone else just put this person on another shift at the same time. Reload the roster to see it, then choose another time or person."),
        ["ux_geofences_name"] = (null, "There is already an area with this name at this property."),
        ["ex_leave_requests_overlap"] = ("LEAVE_OVERLAP", "This person already has leave on some of these days (asked for a moment ago). Reload to see it, then choose other dates."),
        ["ck_leave_balances_not_negative"] = ("INSUFFICIENT_LEAVE_BALANCE", "The leave balance was used by another approval a moment ago and there aren't enough days left. Reload to see the balance."),
        ["ux_holidays_date"] = (null, "That date was just made a holiday by someone else. Reload the holidays."),
        ["ux_leave_types_name"] = (null, "There is already a leave type with this name at this property."),
        ["ux_leave_ledger_accrual"] = (null, "This month's leave was just added by someone else (or the accrual job). Reload the balances."),
        ["ux_employees_user"] = (null, "That staff account was just linked to another employee. Reload and choose a different account."),
        ["ex_reservation_units_unit"] = ("ROOM_UNAVAILABLE", "This room was booked by someone else for some of these nights. Check availability again and choose another."),
        ["ex_reservation_units_bed"] = ("ROOM_UNAVAILABLE", "This bed was booked by someone else for some of these nights. Check availability again and choose another."),
        ["ex_reservation_units_blocked"] = ("ROOM_UNAVAILABLE", "This room is blocked for some of these nights. Check availability again and choose another."),
        ["ex_unit_blocks_reserved"] = ("BLOCK_OVERLAPS_RESERVATION", "This room is booked for some of these nights, so it can't be blocked. Move or cancel the booking first."),
        ["ck_reservations_status_transition"] = ("INVALID_STATUS_TRANSITION", "That status change isn't allowed for this reservation. Reload it to see its current status."),
    };

    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        switch (ex)
        {
            case ValidationException v:
                var errors = v.Errors.GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
                await Problems.Write(ctx, 400, "VALIDATION_FAILED", Problems.Reasons(v.Errors.Select(e => e.ErrorMessage)), errors);
                return true;
            case NotFoundAppException e: await Problems.Write(ctx, 404, e.Code, e.Message); return true;
            case ConflictAppException e: await Problems.Write(ctx, 409, e.Code, e.Message, e.Details); return true;
            case ConcurrencyAppException e: await Problems.Write(ctx, 409, e.Code, e.Message); return true;
            case ForbiddenAppException e: await Problems.Write(ctx, 403, e.Code, e.Message); return true;
            case UnauthorizedAppException e: await Problems.Write(ctx, 401, e.Code, e.Message); return true;
            case BusinessRuleAppException e: await Problems.Write(ctx, 422, e.Code, e.Message, e.Details); return true;
            case DbUpdateConcurrencyException:
                await Problems.Write(ctx, 409, "CONCURRENCY_CONFLICT", "The record was changed by someone else. Reload and try again.");
                return true;
            case DbUpdateException { InnerException: PostgresException pg }:
                var (status, code, msg) = pg.SqlState switch
                {
                    PostgresErrorCodes.UniqueViolation => (409, "DUPLICATE", $"A record with the same value already exists (rule {pg.ConstraintName})."),
                    PostgresErrorCodes.ExclusionViolation => (409, "OVERLAP", $"This overlaps an existing record (rule {pg.ConstraintName})."),
                    PostgresErrorCodes.ForeignKeyViolation => (409, "REFERENCE_CONFLICT", $"A referenced record is missing or still in use (rule {pg.ConstraintName})."),
                    PostgresErrorCodes.CheckViolation => (422, "CONSTRAINT_VIOLATION", $"A value broke the database rule {pg.ConstraintName}."),
                    _ => (500, "DATABASE_ERROR", $"The database refused the request (code {pg.SqlState}). Quote the trace id when reporting it."),
                };
                if (pg.ConstraintName is { } name && KnownConstraints.TryGetValue(name, out var known))
                    (code, msg) = (known.Code ?? code, known.Message);
                log.LogWarning(ex, "Database constraint {Constraint} ({SqlState})", pg.ConstraintName, pg.SqlState);
                await Problems.Write(ctx, status, code, msg);
                return true;
            case BadHttpRequestException b:
                await Problems.Write(ctx, b.StatusCode, "BAD_REQUEST", $"The request could not be read: {b.Message}");
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
