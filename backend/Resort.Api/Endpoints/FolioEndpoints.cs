using Microsoft.AspNetCore.Mvc;
using Resort.Api.Infrastructure;
using Resort.Application.Common;
using Resort.Application.Folios;
using Resort.Contracts;

namespace Resort.Api.Endpoints;

/// <summary>Sprint 8 — folios: the bill of a stay. Read, post a charge (Idempotency-Key), reverse a charge (folio.adjust and a
/// reason) and split lines onto another folio. Lines are never edited or deleted.</summary>
public static class FolioEndpoints
{
    public static void MapFolioEndpoints(this IEndpointRouteBuilder app)
    {
        var p = app.MapGroup("/api/v1/properties/{propertyId:guid}").WithTags("Folios");

        p.MapGet("/folios", (Guid propertyId, string? q, string? show, int? page, int? pageSize, FolioService s, CancellationToken ct) =>
                s.ListAsync(propertyId, new FolioService.ListQuery(q, show, page ?? 1, pageSize ?? 50), ct))
            .RequirePermission(Perm.FolioRead);
        p.MapGet("/reservations/{reservationId:guid}/folios", (Guid propertyId, Guid reservationId, FolioService s, CancellationToken ct) =>
                s.ForReservationAsync(propertyId, reservationId, ct))
            .RequirePermission(Perm.FolioRead);

        var f = p.MapGroup("/folios/{id:guid}");
        f.MapGet("", (Guid propertyId, Guid id, FolioService s, CancellationToken ct) => s.GetAsync(propertyId, id, ct))
            .RequirePermission(Perm.FolioRead);
        f.MapPost("/charges", async (Guid propertyId, Guid id, [FromHeader(Name = Idempotency.Header)] string? idempotencyKey,
                PostChargeRequest body, FolioService s, HttpContext http, CancellationToken ct) =>
            {
                var (folio, replayed) = await s.PostChargeAsync(propertyId, id, idempotencyKey, body, ct);
                if (replayed) http.Response.Headers[Idempotency.ReplayedHeader] = "true";
                return Results.Created($"/api/v1/properties/{propertyId}/folios/{folio.Id}", folio);
            })
            .Validate<PostChargeRequest>().RequirePermission(Perm.FolioPostCharge);
        f.MapPost("/charges/{itemId:guid}/reverse", (Guid propertyId, Guid id, Guid itemId, ReverseChargeRequest body, FolioService s,
                CancellationToken ct) => s.ReverseAsync(propertyId, id, itemId, body, ct))
            .Validate<ReverseChargeRequest>().RequirePermission(Perm.FolioAdjust);
        f.MapPost("/split", (Guid propertyId, Guid id, SplitFolioRequest body, FolioService s, CancellationToken ct) =>
                s.SplitAsync(propertyId, id, body, ct))
            .Validate<SplitFolioRequest>().RequirePermission(Perm.FolioPostCharge);
    }
}
