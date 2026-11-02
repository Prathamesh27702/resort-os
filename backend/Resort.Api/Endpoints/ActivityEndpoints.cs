using Microsoft.AspNetCore.Mvc;
using Resort.Api.Infrastructure;
using Resort.Application.Activities;
using Resort.Application.Common;
using Resort.Contracts;

namespace Resort.Api.Endpoints;

/// <summary>Sprint 20 — activities and equipment: activities and their sessions, the day's board, bookings within the seats
/// (Idempotency-Key), cancelling a booking or a whole session for the weather (refunds on the bill, or moves), and
/// equipment kinds, items, reserving, issuing and taking back.</summary>
public static class ActivityEndpoints
{
    public static void MapActivityEndpoints(this IEndpointRouteBuilder app)
    {
        var p = app.MapGroup("/api/v1/properties/{propertyId:guid}");

        var a = p.MapGroup("/activities").WithTags("Activities");
        a.MapGet("", (Guid propertyId, ActivityService s, CancellationToken ct) => s.ActivitiesAsync(propertyId, ct))
            .RequirePermission(Perm.ActivityRead);
        a.MapPost("", async (Guid propertyId, SaveActivityRequest body, ActivityService s, CancellationToken ct) =>
            {
                var x = await s.SaveActivityAsync(propertyId, null, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/activities/{x.Id}", x);
            })
            .Validate<SaveActivityRequest>().RequirePermission(Perm.ActivityManage);
        a.MapPut("/{id:guid}", (Guid propertyId, Guid id, SaveActivityRequest body, ActivityService s, CancellationToken ct) =>
                s.SaveActivityAsync(propertyId, id, body, ct))
            .Validate<SaveActivityRequest>().RequirePermission(Perm.ActivityManage);

        a.MapGet("/sessions", (Guid propertyId, DateOnly? date, ActivityService s, CancellationToken ct) => s.DayAsync(propertyId, date, ct))
            .RequirePermission(Perm.ActivityRead);
        a.MapPost("/sessions", (Guid propertyId, CreateSessionsRequest body, ActivityService s, CancellationToken ct) => s.CreateSessionsAsync(propertyId, body, ct))
            .Validate<CreateSessionsRequest>().RequirePermission(Perm.ActivityManage);
        a.MapGet("/sessions/{id:guid}", (Guid propertyId, Guid id, ActivityService s, ActivityBookingService b, CancellationToken ct) =>
                s.SessionAsync(propertyId, id, b, ct))
            .RequirePermission(Perm.ActivityRead);
        a.MapPut("/sessions/{id:guid}", (Guid propertyId, Guid id, UpdateSessionRequest body, ActivityService s, CancellationToken ct) =>
                s.UpdateSessionAsync(propertyId, id, body, ct))
            .Validate<UpdateSessionRequest>().RequirePermission(Perm.ActivityManage);
        a.MapPost("/sessions/{id:guid}/weather-cancel", (Guid propertyId, Guid id, WeatherCancelRequest body, ActivityBookingService s, CancellationToken ct) =>
                s.WeatherCancelAsync(propertyId, id, body, ct))
            .Validate<WeatherCancelRequest>().RequirePermission(Perm.ActivityManage);

        a.MapGet("/stays", (Guid propertyId, DateOnly? date, ActivityService s, CancellationToken ct) => s.StaysAsync(propertyId, date, ct))
            .RequirePermission(Perm.ActivityBookingCreate);
        a.MapPost("/bookings", async (Guid propertyId, [FromHeader(Name = Idempotency.Header)] string? idempotencyKey, BookActivityRequest body,
                ActivityBookingService s, HttpContext http, CancellationToken ct) =>
            {
                var (booking, replayed) = await s.BookAsync(propertyId, idempotencyKey, body, ct);
                if (replayed) http.Response.Headers[Idempotency.ReplayedHeader] = "true";
                return Results.Created($"/api/v1/properties/{propertyId}/activities/bookings/{booking.Id}", booking);
            })
            .Validate<BookActivityRequest>().RequirePermission(Perm.ActivityBookingCreate);
        a.MapGet("/bookings", (Guid propertyId, Guid reservationId, ActivityBookingService s, CancellationToken ct) => s.ForStayAsync(propertyId, reservationId, ct))
            .RequirePermission(Perm.ActivityRead);
        a.MapGet("/bookings/{id:guid}", (Guid propertyId, Guid id, ActivityBookingService s, CancellationToken ct) => s.GetAsync(propertyId, id, ct))
            .RequirePermission(Perm.ActivityRead);
        a.MapPost("/bookings/{id:guid}/cancel", (Guid propertyId, Guid id, CancelActivityBookingRequest body, ActivityBookingService s, CancellationToken ct) =>
                s.CancelAsync(propertyId, id, body, ct))
            .Validate<CancelActivityBookingRequest>().RequirePermission(Perm.ActivityBookingCreate);

        var e = p.MapGroup("/equipment").WithTags("Equipment");
        e.MapGet("", (Guid propertyId, EquipmentService s, CancellationToken ct) => s.BoardAsync(propertyId, ct))
            .RequirePermission(Perm.EquipmentRead);
        e.MapPost("/types", async (Guid propertyId, SaveEquipmentTypeRequest body, EquipmentService s, CancellationToken ct) =>
            {
                var x = await s.SaveTypeAsync(propertyId, null, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/equipment/types/{x.Id}", x);
            })
            .Validate<SaveEquipmentTypeRequest>().RequirePermission(Perm.EquipmentManage);
        e.MapPut("/types/{id:guid}", (Guid propertyId, Guid id, SaveEquipmentTypeRequest body, EquipmentService s, CancellationToken ct) =>
                s.SaveTypeAsync(propertyId, id, body, ct))
            .Validate<SaveEquipmentTypeRequest>().RequirePermission(Perm.EquipmentManage);
        e.MapPost("/items", async (Guid propertyId, AddEquipmentRequest body, EquipmentService s, CancellationToken ct) =>
                Results.Created($"/api/v1/properties/{propertyId}/equipment", await s.AddItemsAsync(propertyId, body, ct)))
            .Validate<AddEquipmentRequest>().RequirePermission(Perm.EquipmentManage);
        e.MapGet("/items/{id:guid}", (Guid propertyId, Guid id, EquipmentService s, CancellationToken ct) => s.ItemAsync(propertyId, id, ct))
            .RequirePermission(Perm.EquipmentRead);
        e.MapPost("/items/{id:guid}/status", (Guid propertyId, Guid id, ChangeEquipmentStatusRequest body, EquipmentService s, CancellationToken ct) =>
                s.ChangeStatusAsync(propertyId, id, body, ct))
            .Validate<ChangeEquipmentStatusRequest>().RequirePermission(Perm.EquipmentManage);

        e.MapPost("/issue", async (Guid propertyId, IssueEquipmentRequest body, EquipmentService s, CancellationToken ct) =>
                Results.Created($"/api/v1/properties/{propertyId}/equipment", await s.IssueAsync(propertyId, body, ct)))
            .Validate<IssueEquipmentRequest>().RequirePermission(Perm.EquipmentIssue);
        e.MapPost("/assignments/{id:guid}/issue", (Guid propertyId, Guid id, AssignmentStepRequest? body, EquipmentService s, CancellationToken ct) =>
                s.HandOverAsync(propertyId, id, body ?? new AssignmentStepRequest(), ct))
            .RequirePermission(Perm.EquipmentIssue);
        e.MapPost("/assignments/{id:guid}/release", (Guid propertyId, Guid id, AssignmentStepRequest? body, EquipmentService s, CancellationToken ct) =>
                s.ReleaseAsync(propertyId, id, body ?? new AssignmentStepRequest(), ct))
            .RequirePermission(Perm.EquipmentIssue);
        e.MapPost("/assignments/{id:guid}/return", (Guid propertyId, Guid id, ReturnEquipmentRequest body, EquipmentService s, CancellationToken ct) =>
                s.ReturnAsync(propertyId, id, body, ct))
            .Validate<ReturnEquipmentRequest>().RequirePermission(Perm.EquipmentIssue);
    }
}
