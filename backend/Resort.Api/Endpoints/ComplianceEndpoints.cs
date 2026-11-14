using Resort.Api.Infrastructure;
using Resort.Application.Common;
using Resort.Application.Compliance;
using Resort.Application.Restaurant;
using Resort.Contracts;

namespace Resort.Api.Endpoints;

/// <summary>Sprint 30 — India readiness: the UPI QR for an amount, the printable restaurant bill (FSSAI number, GSTIN, UPI
/// QR), and Form C for foreign guests (the sheets to file with the FRRO within 24 hours, and the foreign guest register).
/// A Form C sheet shows passport and visa numbers, so reading one needs guest_document.read and is audited; changing one
/// needs frontdesk.checkin.</summary>
public static class ComplianceEndpoints
{
    public static void MapComplianceEndpoints(this IEndpointRouteBuilder app)
    {
        var p = app.MapGroup("/api/v1/properties/{propertyId:guid}");

        p.MapGet("/upi-qr", (Guid propertyId, decimal? amount, string? note, UpiService s, CancellationToken ct) => s.GetAsync(propertyId, amount, note, ct))
            .WithTags("UPI").RequirePermission(Perm.PropertyRead);

        p.MapGet("/orders/{id:guid}/bill", (Guid propertyId, Guid id, OrderService s, CancellationToken ct) => s.BillAsync(propertyId, id, ct))
            .WithTags("Restaurant orders").RequirePermission(Perm.PosOrderRead);

        var f = p.MapGroup("/form-c").WithTags("Form C");
        f.MapGet("", (Guid propertyId, string? status, FormCService s, CancellationToken ct) => s.ListAsync(propertyId, status, ct))
            .RequirePermission(Perm.GuestDocumentRead);
        f.MapGet("/register.csv", async (Guid propertyId, DateOnly from, DateOnly to, FormCService s, CancellationToken ct) =>
            {
                var (name, content) = await s.RegisterCsvAsync(propertyId, from, to, ct);
                return Results.File(content, "text/csv; charset=utf-8", name);
            })
            .RequirePermission(Perm.GuestDocumentRead);
        f.MapGet("/{id:guid}", (Guid propertyId, Guid id, FormCService s, CancellationToken ct) => s.GetAsync(propertyId, id, ct))
            .RequirePermission(Perm.GuestDocumentRead);
        f.MapPut("/{id:guid}", (Guid propertyId, Guid id, SaveFormCRequest body, FormCService s, CancellationToken ct) => s.SaveAsync(propertyId, id, body, ct))
            .RequirePermission(Perm.FrontDeskCheckIn);
        f.MapPost("/{id:guid}/submit", (Guid propertyId, Guid id, SubmitFormCRequest body, FormCService s, CancellationToken ct) =>
                s.SubmitAsync(propertyId, id, body, ct))
            .RequirePermission(Perm.FrontDeskCheckIn);
        f.MapPost("/{id:guid}/not-required", (Guid propertyId, Guid id, FormCNotRequiredRequest body, FormCService s, CancellationToken ct) =>
                s.NotRequiredAsync(propertyId, id, body, ct))
            .RequirePermission(Perm.FrontDeskCheckIn);
        f.MapPost("/{id:guid}/reopen", (Guid propertyId, Guid id, ReopenFormCRequest body, FormCService s, CancellationToken ct) =>
                s.ReopenAsync(propertyId, id, body, ct))
            .RequirePermission(Perm.FrontDeskCheckIn);
    }
}
