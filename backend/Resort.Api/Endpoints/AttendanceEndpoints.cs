using Resort.Api.Infrastructure;
using Resort.Application.Attendance;
using Resort.Application.Common;
using Resort.Contracts;

namespace Resort.Api.Endpoints;

/// <summary>Sprint 18 — attendance and timesheets: geofences, clocking in and out from a phone (checked against the
/// geofence here), a manager recording an entry with a reason, the day's attendance board, and weekly timesheets that
/// are submitted, approved or sent back, corrected with a reason and locked for payroll.</summary>
public static class AttendanceEndpoints
{
    public static void MapAttendanceEndpoints(this IEndpointRouteBuilder app)
    {
        var p = app.MapGroup("/api/v1/properties/{propertyId:guid}");

        var a = p.MapGroup("/attendance").WithTags("Attendance");
        a.MapGet("", (Guid propertyId, DateOnly? date, AttendanceService s, CancellationToken ct) => s.DayAsync(propertyId, date, ct))
            .RequirePermission(Perm.AttendanceRead);
        a.MapGet("/me", (Guid propertyId, AttendanceService s, CancellationToken ct) => s.MeAsync(propertyId, ct))
            .RequirePermission(Perm.AttendanceSelf);
        a.MapPost("/me/clock", (Guid propertyId, ClockRequest body, HttpRequest http, AttendanceService s, CancellationToken ct) =>
                s.ClockAsync(propertyId, body, http.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null, ct))
            .Validate<ClockRequest>().RequirePermission(Perm.AttendanceSelf);
        a.MapPost("/events", (Guid propertyId, ManagerClockRequest body, AttendanceService s, CancellationToken ct) => s.RecordAsync(propertyId, body, ct))
            .Validate<ManagerClockRequest>().RequirePermission(Perm.AttendanceUpdate);
        a.MapGet("/geofences", (Guid propertyId, AttendanceService s, CancellationToken ct) => s.GeofencesAsync(propertyId, ct))
            .RequirePermission(Perm.AttendanceRead);
        a.MapPost("/geofences", async (Guid propertyId, SaveGeofenceRequest body, AttendanceService s, CancellationToken ct) =>
            {
                var x = await s.SaveGeofenceAsync(propertyId, null, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/attendance/geofences/{x.Id}", x);
            })
            .Validate<SaveGeofenceRequest>().RequirePermission(Perm.AttendanceUpdate);
        a.MapPut("/geofences/{id:guid}", (Guid propertyId, Guid id, SaveGeofenceRequest body, AttendanceService s, CancellationToken ct) =>
                s.SaveGeofenceAsync(propertyId, id, body, ct))
            .Validate<SaveGeofenceRequest>().RequirePermission(Perm.AttendanceUpdate);

        var t = p.MapGroup("/timesheets").WithTags("Timesheets");
        t.MapGet("", (Guid propertyId, DateOnly? week, TimesheetService s, CancellationToken ct) => s.WeekAsync(propertyId, week, ct))
            .RequirePermission(Perm.AttendanceRead);
        t.MapGet("/mine", (Guid propertyId, DateOnly? week, TimesheetService s, CancellationToken ct) => s.MineAsync(propertyId, week, ct))
            .RequirePermission(Perm.TimesheetSelf);
        t.MapGet("/{id:guid}", (Guid propertyId, Guid id, TimesheetService s, CancellationToken ct) => s.GetAsync(propertyId, id, ct))
            .RequirePermission(Perm.AttendanceRead);
        t.MapPost("/{id:guid}/submit", (Guid propertyId, Guid id, SubmitTimesheetRequest body, TimesheetService s, CancellationToken ct) =>
                s.SubmitAsync(propertyId, id, body, ct))
            .Validate<SubmitTimesheetRequest>().RequirePermission(Perm.TimesheetSelf);
        t.MapPost("/{id:guid}/approve", (Guid propertyId, Guid id, TimesheetVersionRequest body, TimesheetService s, CancellationToken ct) =>
                s.ApproveAsync(propertyId, id, body, ct))
            .RequirePermission(Perm.TimesheetApprove);
        t.MapPost("/{id:guid}/reject", (Guid propertyId, Guid id, RejectTimesheetRequest body, TimesheetService s, CancellationToken ct) =>
                s.RejectAsync(propertyId, id, body, ct))
            .Validate<RejectTimesheetRequest>().RequirePermission(Perm.TimesheetApprove);
        t.MapPost("/{id:guid}/lock", (Guid propertyId, Guid id, TimesheetVersionRequest body, TimesheetService s, CancellationToken ct) =>
                s.LockAsync(propertyId, id, body, ct))
            .RequirePermission(Perm.TimesheetApprove);
        t.MapPost("/{id:guid}/corrections", (Guid propertyId, Guid id, CorrectTimesheetLineRequest body, TimesheetService s, CancellationToken ct) =>
                s.CorrectAsync(propertyId, id, body, ct))
            .Validate<CorrectTimesheetLineRequest>().RequirePermission(Perm.AttendanceUpdate);
        t.MapPost("/approve-week", (Guid propertyId, ApproveTimesheetWeekRequest body, TimesheetService s, CancellationToken ct) =>
                s.ApproveWeekAsync(propertyId, body, ct))
            .Validate<ApproveTimesheetWeekRequest>().RequirePermission(Perm.TimesheetApprove);
    }
}
