using Microsoft.AspNetCore.Mvc;
using Resort.Api.Infrastructure;
using Resort.Application.Common;
using Resort.Application.FrontDesk;
using Resort.Contracts;

namespace Resort.Api.Endpoints;

/// <summary>Sprint 7 — the front desk: arrivals, departures, in-house, the reception dashboard, assigning rooms, check-in,
/// check-out, walk-ins and a room's cleaning state. Dates default to the property's business date (its timezone), never
/// the server's. A walk-in needs an Idempotency-Key header.</summary>
public static class FrontDeskEndpoints
{
    public static void MapFrontDeskEndpoints(this IEndpointRouteBuilder app)
    {
        var p = app.MapGroup("/api/v1/properties/{propertyId:guid}").WithTags("Front desk");

        var fd = p.MapGroup("/front-desk");
        fd.MapGet("/arrivals", (Guid propertyId, DateOnly? date, FrontDeskService s, CancellationToken ct) => s.ArrivalsAsync(propertyId, date, ct))
            .RequirePermission(Perm.ReservationRead);
        fd.MapGet("/departures", (Guid propertyId, DateOnly? date, FrontDeskService s, CancellationToken ct) => s.DeparturesAsync(propertyId, date, ct))
            .RequirePermission(Perm.ReservationRead);
        fd.MapGet("/in-house", (Guid propertyId, FrontDeskService s, CancellationToken ct) => s.InHouseAsync(propertyId, ct))
            .RequirePermission(Perm.ReservationRead);
        fd.MapGet("/dashboard", (Guid propertyId, FrontDeskService s, CancellationToken ct) => s.DashboardAsync(propertyId, ct))
            .RequirePermission(Perm.ReservationRead);

        var r = p.MapGroup("/reservations/{id:guid}");
        r.MapGet("/room-options", (Guid propertyId, Guid id, Guid reservationUnitId, FrontDeskService s, CancellationToken ct) =>
                s.RoomOptionsAsync(propertyId, id, reservationUnitId, ct))
            .RequirePermission(Perm.ReservationRead);
        r.MapPost("/assign-room", (Guid propertyId, Guid id, AssignRoomRequest body, FrontDeskService s, CancellationToken ct) =>
                s.AssignRoomAsync(propertyId, id, body, ct))
            .Validate<AssignRoomRequest>().RequirePermission(Perm.FrontDeskCheckIn);
        r.MapPost("/check-in", (Guid propertyId, Guid id, CheckInRequest? body, FrontDeskService s, CancellationToken ct) =>
                s.CheckInAsync(propertyId, id, body ?? new CheckInRequest(null), ct))
            .RequirePermission(Perm.FrontDeskCheckIn);
        r.MapPost("/check-out", (Guid propertyId, Guid id, CheckOutRequest? body, FrontDeskService s, CancellationToken ct) =>
                s.CheckOutAsync(propertyId, id, body ?? new CheckOutRequest(null), ct))
            .RequirePermission(Perm.FrontDeskCheckOut);

        p.MapPost("/walk-ins", async (Guid propertyId, [FromHeader(Name = Idempotency.Header)] string? idempotencyKey,
                WalkInRequest body, FrontDeskService s, HttpContext http, CancellationToken ct) =>
            {
                var (reservation, replayed) = await s.WalkInAsync(propertyId, idempotencyKey, body, ct);
                if (replayed) http.Response.Headers[Idempotency.ReplayedHeader] = "true";
                return Results.Created($"/api/v1/properties/{propertyId}/reservations/{reservation.Id}", reservation);
            })
            .Validate<WalkInRequest>().RequirePermission(Perm.FrontDeskCheckIn);

        p.MapPut("/rooms/{unitId:guid}/housekeeping", (Guid propertyId, Guid unitId, SetHousekeepingRequest body, FrontDeskService s,
                CancellationToken ct) => s.SetHousekeepingAsync(propertyId, unitId, body, ct))
            .WithTags("Front desk").Validate<SetHousekeepingRequest>().RequirePermission(Perm.HousekeepingUpdate);
    }
}
