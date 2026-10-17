using Microsoft.AspNetCore.Mvc;
using Resort.Api.Infrastructure;
using Resort.Application.Common;
using Resort.Application.Reservations;
using Resort.Contracts;

namespace Resort.Api.Endpoints;

/// <summary>Sprint 5 — availability and reservations; Sprint 6 — the calendar and moving a booking to another room.
/// All routes are under a property; the permission must be granted org-wide or for that property. Creating a reservation needs an Idempotency-Key header.</summary>
public static class ReservationEndpoints
{
    public static void MapReservationEndpoints(this IEndpointRouteBuilder app)
    {
        var p = app.MapGroup("/api/v1/properties/{propertyId:guid}");

        p.MapGet("/availability", (Guid propertyId, DateOnly? from, DateOnly? to, int? adults, int? children,
                AvailabilityService s, CancellationToken ct) => s.GetAsync(propertyId, from, to, adults, children, ct))
            .WithTags("Reservations").RequirePermission(Perm.ReservationRead);

        // Sprint 6 — the reservation calendar
        p.MapGet("/calendar", (Guid propertyId, DateOnly? from, DateOnly? to, CalendarService s, CancellationToken ct) =>
                s.GetAsync(propertyId, from, to, ct))
            .WithTags("Reservations").RequirePermission(Perm.ReservationRead);

        var r = p.MapGroup("/reservations").WithTags("Reservations");

        r.MapGet("", (Guid propertyId, string? q, string? status, DateOnly? from, DateOnly? to, int? page, int? pageSize,
                ReservationService s, CancellationToken ct) =>
                s.ListAsync(propertyId, new ReservationService.ListQuery(q, status, from, to, page ?? 1, pageSize ?? 50), ct))
            .RequirePermission(Perm.ReservationRead);

        r.MapGet("/{id:guid}", (Guid propertyId, Guid id, ReservationService s, CancellationToken ct) => s.GetAsync(propertyId, id, ct))
            .RequirePermission(Perm.ReservationRead);

        r.MapPost("", async (Guid propertyId, [FromHeader(Name = Idempotency.Header)] string? idempotencyKey,
                CreateReservationRequest body, ReservationService s, HttpContext http, CancellationToken ct) =>
            {
                var (reservation, replayed) = await s.CreateAsync(propertyId, idempotencyKey, body, ct);
                if (replayed) http.Response.Headers[Idempotency.ReplayedHeader] = "true";
                return Results.Created($"/api/v1/properties/{propertyId}/reservations/{reservation.Id}", reservation);
            })
            .Validate<CreateReservationRequest>().RequirePermission(Perm.ReservationCreate);

        r.MapPut("/{id:guid}", (Guid propertyId, Guid id, UpdateReservationRequest body, ReservationService s, CancellationToken ct) =>
                s.UpdateAsync(propertyId, id, body, ct))
            .Validate<UpdateReservationRequest>().RequirePermission(Perm.ReservationUpdate);

        r.MapPost("/{id:guid}/confirm", (Guid propertyId, Guid id, ReservationStatusRequest? body, ReservationService s, CancellationToken ct) =>
                s.ConfirmAsync(propertyId, id, body ?? new ReservationStatusRequest(null), ct))
            .RequirePermission(Perm.ReservationUpdate);

        r.MapPost("/{id:guid}/cancel", (Guid propertyId, Guid id, ReservationStatusRequest body, ReservationService s, CancellationToken ct) =>
                s.CancelAsync(propertyId, id, body, ct))
            .Validate<ReservationStatusRequest>().RequirePermission(Perm.ReservationCancel);

        r.MapPost("/{id:guid}/no-show", (Guid propertyId, Guid id, ReservationStatusRequest body, ReservationService s, CancellationToken ct) =>
                s.NoShowAsync(propertyId, id, body, ct))
            .Validate<ReservationStatusRequest>().RequirePermission(Perm.ReservationCancel);

        r.MapPost("/{id:guid}/reassign", (Guid propertyId, Guid id, ReassignRoomRequest body, ReservationService s, CancellationToken ct) =>
                s.ReassignAsync(propertyId, id, body, ct))
            .Validate<ReassignRoomRequest>().RequirePermission(Perm.ReservationUpdate);

        r.MapPost("/{id:guid}/notes", async (Guid propertyId, Guid id, AddReservationNoteRequest body, ReservationService s, CancellationToken ct) =>
            {
                var reservation = await s.AddNoteAsync(propertyId, id, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/reservations/{id}", reservation);
            })
            .Validate<AddReservationNoteRequest>().RequirePermission(Perm.ReservationUpdate);
    }
}
