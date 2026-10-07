using Resort.Api.Infrastructure;
using Resort.Application.Accommodation;
using Resort.Application.Common;
using Resort.Contracts;

namespace Resort.Api.Endpoints;

/// <summary>Sprint 2 — room types, rooms/dormitories, beds, amenities, blocks and the room status board.
/// All routes are under a property; the permission must be granted org-wide or for that property.</summary>
public static class AccommodationEndpoints
{
    public static void MapAccommodationEndpoints(this IEndpointRouteBuilder app)
    {
        var p = app.MapGroup("/api/v1/properties/{propertyId:guid}");

        // ---------------------------------------------------------------- amenities
        p.MapGet("/amenities", (Guid propertyId, AccommodationService s, CancellationToken ct) => s.ListAmenitiesAsync(propertyId, ct))
            .WithTags("Rooms").RequirePermission(Perm.AccommodationRead);

        // ---------------------------------------------------------------- room types
        var types = p.MapGroup("/accommodation-types").WithTags("Room types");
        types.MapGet("", (Guid propertyId, AccommodationService s, CancellationToken ct) => s.ListTypesAsync(propertyId, ct))
            .RequirePermission(Perm.AccommodationRead);
        types.MapGet("/{id:guid}", (Guid propertyId, Guid id, AccommodationService s, CancellationToken ct) => s.GetTypeAsync(propertyId, id, ct))
            .RequirePermission(Perm.AccommodationRead);
        types.MapPost("", async (Guid propertyId, CreateAccommodationTypeRequest r, AccommodationService s, CancellationToken ct) =>
            {
                var t = await s.CreateTypeAsync(propertyId, r, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/accommodation-types/{t.Id}", t);
            })
            .Validate<CreateAccommodationTypeRequest>().RequirePermission(Perm.AccommodationManage);
        types.MapPut("/{id:guid}", (Guid propertyId, Guid id, UpdateAccommodationTypeRequest r, AccommodationService s, CancellationToken ct) =>
                s.UpdateTypeAsync(propertyId, id, r, ct))
            .Validate<UpdateAccommodationTypeRequest>().RequirePermission(Perm.AccommodationManage);

        // ---------------------------------------------------------------- rooms (accommodation units)
        var rooms = p.MapGroup("/rooms").WithTags("Rooms");
        rooms.MapGet("", (Guid propertyId, AccommodationService s, CancellationToken ct) => s.ListRoomsAsync(propertyId, ct))
            .RequirePermission(Perm.AccommodationRead);
        rooms.MapGet("/status", (Guid propertyId, DateOnly? date, AccommodationService s, CancellationToken ct) => s.StatusAsync(propertyId, date, ct))
            .RequirePermission(Perm.AccommodationRead);
        rooms.MapGet("/{id:guid}", (Guid propertyId, Guid id, AccommodationService s, CancellationToken ct) => s.GetRoomAsync(propertyId, id, ct))
            .RequirePermission(Perm.AccommodationRead);
        rooms.MapPost("", async (Guid propertyId, CreateRoomRequest r, AccommodationService s, CancellationToken ct) =>
            {
                var room = await s.CreateRoomAsync(propertyId, r, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/rooms/{room.Id}", room);
            })
            .Validate<CreateRoomRequest>().RequirePermission(Perm.AccommodationManage);
        rooms.MapPut("/{id:guid}", (Guid propertyId, Guid id, UpdateRoomRequest r, AccommodationService s, CancellationToken ct) =>
                s.UpdateRoomAsync(propertyId, id, r, ct))
            .Validate<UpdateRoomRequest>().RequirePermission(Perm.AccommodationManage);

        // ---------------------------------------------------------------- beds
        rooms.MapGet("/{id:guid}/beds", (Guid propertyId, Guid id, AccommodationService s, CancellationToken ct) => s.ListBedsAsync(propertyId, id, ct))
            .RequirePermission(Perm.AccommodationRead);
        rooms.MapPost("/{id:guid}/beds", async (Guid propertyId, Guid id, CreateBedRequest r, AccommodationService s, CancellationToken ct) =>
            {
                var bed = await s.CreateBedAsync(propertyId, id, r, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/beds/{bed.Id}", bed);
            })
            .Validate<CreateBedRequest>().RequirePermission(Perm.AccommodationManage);
        p.MapPut("/beds/{id:guid}", (Guid propertyId, Guid id, UpdateBedRequest r, AccommodationService s, CancellationToken ct) =>
                s.UpdateBedAsync(propertyId, id, r, ct))
            .WithTags("Rooms").Validate<UpdateBedRequest>().RequirePermission(Perm.AccommodationManage);

        // ---------------------------------------------------------------- blocks
        var blocks = p.MapGroup("/blocks").WithTags("Blocks");
        blocks.MapGet("", (Guid propertyId, DateOnly? from, DateOnly? to, Guid? unitId, bool? includeReleased, BlockService s, CancellationToken ct) =>
                s.ListAsync(propertyId, new BlockService.BlockQuery(from, to, unitId, includeReleased ?? false), ct))
            .RequirePermission(Perm.AccommodationRead);
        blocks.MapPost("", async (Guid propertyId, CreateBlockRequest r, BlockService s, CancellationToken ct) =>
            {
                var b = await s.CreateAsync(propertyId, r, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/blocks/{b.Id}", b);
            })
            .Validate<CreateBlockRequest>().RequirePermission(Perm.UnitBlockManage);
        blocks.MapPost("/{id:guid}/release", (Guid propertyId, Guid id, ReleaseBlockRequest r, BlockService s, CancellationToken ct) =>
                s.ReleaseAsync(propertyId, id, r, ct))
            .Validate<ReleaseBlockRequest>().RequirePermission(Perm.UnitBlockManage);
    }
}
