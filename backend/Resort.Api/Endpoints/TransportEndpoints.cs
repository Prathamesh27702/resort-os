using Microsoft.AspNetCore.Mvc;
using Resort.Api.Infrastructure;
using Resort.Application.Common;
using Resort.Application.Transport;
using Resort.Contracts;

namespace Resort.Api.Endpoints;

/// <summary>Sprint 21 — beach transport: vehicles, routes, trips with a vehicle and a driver, the day's board, seats booked
/// within the capacity (Idempotency-Key) with the price on the guest's bill, the manifest (JSON and PDF), the driver's own
/// trips, boarding, departing, arriving, and cancelling a passenger or a whole trip (charges reversed).</summary>
public static class TransportEndpoints
{
    public static void MapTransportEndpoints(this IEndpointRouteBuilder app)
    {
        var t = app.MapGroup("/api/v1/properties/{propertyId:guid}/transport").WithTags("Transport");

        t.MapGet("/vehicles", (Guid propertyId, TransportService s, CancellationToken ct) => s.VehiclesAsync(propertyId, ct))
            .RequirePermission(Perm.TransportRead);
        t.MapPost("/vehicles", async (Guid propertyId, SaveVehicleRequest body, TransportService s, CancellationToken ct) =>
            {
                var x = await s.SaveVehicleAsync(propertyId, null, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/transport/vehicles/{x.Id}", x);
            })
            .Validate<SaveVehicleRequest>().RequirePermission(Perm.TransportManage);
        t.MapPut("/vehicles/{id:guid}", (Guid propertyId, Guid id, SaveVehicleRequest body, TransportService s, CancellationToken ct) =>
                s.SaveVehicleAsync(propertyId, id, body, ct))
            .Validate<SaveVehicleRequest>().RequirePermission(Perm.TransportManage);

        t.MapGet("/routes", (Guid propertyId, TransportService s, CancellationToken ct) => s.RoutesAsync(propertyId, ct))
            .RequirePermission(Perm.TransportRead);
        t.MapPost("/routes", async (Guid propertyId, SaveTransportRouteRequest body, TransportService s, CancellationToken ct) =>
            {
                var x = await s.SaveRouteAsync(propertyId, null, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/transport/routes/{x.Id}", x);
            })
            .Validate<SaveTransportRouteRequest>().RequirePermission(Perm.TransportManage);
        t.MapPut("/routes/{id:guid}", (Guid propertyId, Guid id, SaveTransportRouteRequest body, TransportService s, CancellationToken ct) =>
                s.SaveRouteAsync(propertyId, id, body, ct))
            .Validate<SaveTransportRouteRequest>().RequirePermission(Perm.TransportManage);

        t.MapGet("/trips", (Guid propertyId, DateOnly? date, TransportService s, CancellationToken ct) => s.DayAsync(propertyId, date, ct))
            .RequirePermission(Perm.TransportRead);
        t.MapPost("/trips", (Guid propertyId, CreateTripsRequest body, TransportService s, CancellationToken ct) => s.CreateTripsAsync(propertyId, body, ct))
            .Validate<CreateTripsRequest>().RequirePermission(Perm.TransportManage);
        t.MapGet("/my-trips", (Guid propertyId, DateOnly? date, TransportService s, TransportBookingService b, CancellationToken ct) =>
                s.MyTripsAsync(propertyId, date, b, ct))
            .RequirePermission(Perm.TransportTripOperate);
        t.MapGet("/trips/{id:guid}", (Guid propertyId, Guid id, TransportService s, TransportBookingService b, CancellationToken ct) =>
                s.TripAsync(propertyId, id, b, ct))
            .RequirePermission(Perm.TransportRead);
        t.MapGet("/trips/{id:guid}/manifest.pdf", async (Guid propertyId, Guid id, TransportService s, TransportBookingService b, CancellationToken ct) =>
            {
                var (bytes, name) = await s.ManifestPdfAsync(propertyId, id, b, ct);
                return Results.File(bytes, "application/pdf", name);
            })
            .RequirePermission(Perm.TransportRead);
        t.MapPut("/trips/{id:guid}", (Guid propertyId, Guid id, UpdateTripRequest body, TransportService s, CancellationToken ct) =>
                s.UpdateTripAsync(propertyId, id, body, ct))
            .Validate<UpdateTripRequest>().RequirePermission(Perm.TransportManage);
        t.MapPost("/trips/{id:guid}/cancel", (Guid propertyId, Guid id, CancelTripRequest body, TransportBookingService s, CancellationToken ct) =>
                s.CancelTripAsync(propertyId, id, body, ct))
            .Validate<CancelTripRequest>().RequirePermission(Perm.TransportManage);
        t.MapPost("/trips/{id:guid}/depart", (Guid propertyId, Guid id, TripStepRequest? body, TransportBookingService s, CancellationToken ct) =>
                s.DepartAsync(propertyId, id, body ?? new TripStepRequest(), ct))
            .RequirePermission(Perm.TransportTripOperate);
        t.MapPost("/trips/{id:guid}/complete", (Guid propertyId, Guid id, TripStepRequest? body, TransportBookingService s, CancellationToken ct) =>
                s.CompleteAsync(propertyId, id, body ?? new TripStepRequest(), ct))
            .RequirePermission(Perm.TransportTripOperate);

        t.MapGet("/stays", (Guid propertyId, DateOnly? date, TransportBookingService s, CancellationToken ct) => s.StaysAsync(propertyId, date, ct))
            .RequirePermission(Perm.TransportBookingCreate);
        t.MapPost("/bookings", async (Guid propertyId, [FromHeader(Name = Idempotency.Header)] string? idempotencyKey, BookTransportRequest body,
                TransportBookingService s, HttpContext http, CancellationToken ct) =>
            {
                var (passenger, replayed) = await s.BookAsync(propertyId, idempotencyKey, body, ct);
                if (replayed) http.Response.Headers[Idempotency.ReplayedHeader] = "true";
                return Results.Created($"/api/v1/properties/{propertyId}/transport/bookings/{passenger.Id}", passenger);
            })
            .Validate<BookTransportRequest>().RequirePermission(Perm.TransportBookingCreate);
        t.MapGet("/bookings", (Guid propertyId, Guid reservationId, TransportBookingService s, CancellationToken ct) => s.ForStayAsync(propertyId, reservationId, ct))
            .RequirePermission(Perm.TransportRead);
        t.MapGet("/bookings/{id:guid}", (Guid propertyId, Guid id, TransportBookingService s, CancellationToken ct) => s.GetAsync(propertyId, id, ct))
            .RequirePermission(Perm.TransportRead);
        t.MapPost("/bookings/{id:guid}/cancel", (Guid propertyId, Guid id, CancelPassengerRequest body, TransportBookingService s, CancellationToken ct) =>
                s.CancelAsync(propertyId, id, body, ct))
            .Validate<CancelPassengerRequest>().RequirePermission(Perm.TransportBookingCreate);
        t.MapPost("/bookings/{id:guid}/board", (Guid propertyId, Guid id, TripStepRequest? body, TransportBookingService s, CancellationToken ct) =>
                s.BoardAsync(propertyId, id, true, body ?? new TripStepRequest(), ct))
            .RequirePermission(Perm.TransportTripOperate);
        t.MapPost("/bookings/{id:guid}/unboard", (Guid propertyId, Guid id, TripStepRequest? body, TransportBookingService s, CancellationToken ct) =>
                s.BoardAsync(propertyId, id, false, body ?? new TripStepRequest(), ct))
            .RequirePermission(Perm.TransportTripOperate);
    }
}
