using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Resort.Api.Infrastructure;
using Resort.Application.Common;
using Resort.Application.Guests;
using Resort.Application.Housekeeping;
using Resort.Contracts;
using Resort.Domain.Housekeeping;

namespace Resort.Api.Endpoints;

/// <summary>Sprint 13 — housekeeping (the board and its cleaning tasks, checklists) and maintenance (tickets, their
/// timeline and photos, equipment). Built for phones: every step is one small POST. A ticket may carry an
/// Idempotency-Key (a report sent twice on a weak signal is saved once).</summary>
public static class HousekeepingEndpoints
{
    public static void MapHousekeepingEndpoints(this IEndpointRouteBuilder app)
    {
        var p = app.MapGroup("/api/v1/properties/{propertyId:guid}");

        // ---------------------------------------------------------------- housekeeping
        var hk = p.MapGroup("/housekeeping").WithTags("Housekeeping");
        hk.MapGet("/board", (Guid propertyId, HousekeepingService s, CancellationToken ct) => s.BoardAsync(propertyId, ct))
            .RequirePermission(Perm.HousekeepingRead);
        hk.MapGet("/staff", (Guid propertyId, HousekeepingService s, CancellationToken ct) => s.StaffAsync(propertyId, ct))
            .RequirePermission(Perm.HousekeepingAssign);
        hk.MapPost("/tasks", async (Guid propertyId, CreateTaskRequest body, HousekeepingService s, CancellationToken ct) =>
            {
                var task = await s.CreateTaskAsync(propertyId, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/housekeeping/tasks/{task.Id}", task);
            })
            .Validate<CreateTaskRequest>().RequirePermission(Perm.HousekeepingAssign);
        hk.MapGet("/tasks/{id:guid}", (Guid propertyId, Guid id, HousekeepingService s, CancellationToken ct) => s.GetTaskAsync(propertyId, id, ct))
            .RequirePermission(Perm.HousekeepingRead);
        hk.MapPost("/tasks/{id:guid}/assign", (Guid propertyId, Guid id, AssignTaskRequest body, HousekeepingService s, CancellationToken ct) =>
                s.AssignAsync(propertyId, id, body, ct))
            .RequirePermission(Perm.HousekeepingAssign);
        hk.MapPost("/tasks/{id:guid}/start", (Guid propertyId, Guid id, StartTaskRequest? body, HousekeepingService s, CancellationToken ct) =>
                s.StartAsync(propertyId, id, body ?? new StartTaskRequest(null), ct))
            .RequirePermission(Perm.HousekeepingUpdate);
        hk.MapPost("/tasks/{id:guid}/finish", (Guid propertyId, Guid id, FinishTaskRequest? body, HousekeepingService s, CancellationToken ct) =>
                s.FinishAsync(propertyId, id, body ?? new FinishTaskRequest(null, null, null), ct))
            .Validate<FinishTaskRequest>().RequirePermission(Perm.HousekeepingUpdate);
        hk.MapPost("/tasks/{id:guid}/inspect", (Guid propertyId, Guid id, InspectTaskRequest body, HousekeepingService s, CancellationToken ct) =>
                s.InspectAsync(propertyId, id, body, ct))
            .Validate<InspectTaskRequest>().RequirePermission(Perm.HousekeepingInspect);

        hk.MapGet("/checklists", (Guid propertyId, HousekeepingService s, CancellationToken ct) => s.TemplatesAsync(propertyId, ct))
            .RequirePermission(Perm.HousekeepingRead);
        hk.MapPost("/checklists", async (Guid propertyId, SaveChecklistTemplateRequest body, HousekeepingService s, CancellationToken ct) =>
            {
                var t = await s.SaveTemplateAsync(propertyId, null, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/housekeeping/checklists/{t.Id}", t);
            })
            .Validate<SaveChecklistTemplateRequest>().RequirePermission(Perm.HousekeepingAssign);
        hk.MapPut("/checklists/{id:guid}", (Guid propertyId, Guid id, SaveChecklistTemplateRequest body, HousekeepingService s, CancellationToken ct) =>
                s.SaveTemplateAsync(propertyId, id, body, ct))
            .Validate<SaveChecklistTemplateRequest>().RequirePermission(Perm.HousekeepingAssign);

        // ---------------------------------------------------------------- maintenance
        var m = p.MapGroup("/maintenance").WithTags("Maintenance");
        // Reporters (maintenance.create) see the tickets they reported; maintenance.read sees all.
        m.MapGet("/tickets", (Guid propertyId, string? status, bool? mine, Guid? unitId, MaintenanceService s, CancellationToken ct) =>
                s.ListAsync(propertyId, status, mine ?? false, unitId, ct))
            .RequirePermission(Perm.MaintenanceCreate);
        m.MapPost("/tickets", async (Guid propertyId, [FromHeader(Name = Idempotency.Header)] string? idempotencyKey, CreateTicketRequest body,
                MaintenanceService s, HttpContext http, CancellationToken ct) =>
            {
                var (ticket, replayed) = await s.CreateAsync(propertyId, idempotencyKey, body, ct);
                if (replayed) http.Response.Headers[Idempotency.ReplayedHeader] = "true";
                return Results.Created($"/api/v1/properties/{propertyId}/maintenance/tickets/{ticket.Id}", ticket);
            })
            .Validate<CreateTicketRequest>().RequirePermission(Perm.MaintenanceCreate);
        m.MapGet("/tickets/{id:guid}", (Guid propertyId, Guid id, MaintenanceService s, CancellationToken ct) => s.GetAsync(propertyId, id, ct))
            .RequirePermission(Perm.MaintenanceCreate);
        m.MapPost("/tickets/{id:guid}/assign", (Guid propertyId, Guid id, AssignTicketRequest body, MaintenanceService s, CancellationToken ct) =>
                s.AssignAsync(propertyId, id, body, ct))
            .Validate<AssignTicketRequest>().RequirePermission(Perm.MaintenanceAssign);
        m.MapPost("/tickets/{id:guid}/progress", (Guid propertyId, Guid id, TicketProgressRequest body, MaintenanceService s, CancellationToken ct) =>
                s.ProgressAsync(propertyId, id, body, ct))
            .Validate<TicketProgressRequest>().RequirePermission(Perm.MaintenanceUpdate);
        m.MapPost("/tickets/{id:guid}/resolve", (Guid propertyId, Guid id, ResolveTicketRequest body, MaintenanceService s, CancellationToken ct) =>
                s.ResolveAsync(propertyId, id, body, ct))
            .Validate<ResolveTicketRequest>().RequirePermission(Perm.MaintenanceUpdate);
        m.MapPost("/tickets/{id:guid}/close", (Guid propertyId, Guid id, CloseTicketRequest? body, MaintenanceService s, CancellationToken ct) =>
                s.CloseAsync(propertyId, id, body ?? new CloseTicketRequest(null, null), ct))
            .Validate<CloseTicketRequest>().RequirePermission(Perm.MaintenanceAssign);
        m.MapPost("/tickets/{id:guid}/comments", (Guid propertyId, Guid id, AddTicketCommentRequest body, MaintenanceService s, CancellationToken ct) =>
                s.CommentAsync(propertyId, id, body, ct))
            .Validate<AddTicketCommentRequest>().RequirePermission(Perm.MaintenanceCreate);

        // multipart/form-data with one "file": a photo already shrunk on the phone (JPEG, PNG or WebP, at most 2 MB).
        m.MapPost("/tickets/{id:guid}/photos", async (Guid propertyId, Guid id, HttpRequest http, MaintenanceService s, CancellationToken ct) =>
            {
                if (!http.HasFormContentType)
                    throw new ValidationException([new FluentValidation.Results.ValidationFailure("File", "Send the photo as multipart/form-data with a field named file.")]);
                var form = await http.ReadFormAsync(ct);
                var f = form.Files.GetFile("file")
                        ?? throw new ValidationException([new FluentValidation.Results.ValidationFailure("File", "Choose a photo to add.")]);
                if (f.Length > MaintenanceFlow.MaxPhotoBytes)
                    throw new ValidationException([new FluentValidation.Results.ValidationFailure("File",
                        $"The photo is {f.Length / 1024 / 1024.0:0.0} MB; at most 2 MB. Take it again or choose a smaller one.")]);
                using var ms = new MemoryStream((int)f.Length);
                await f.CopyToAsync(ms, ct);
                var photo = await s.AddPhotoAsync(propertyId, id, new UploadedFile(f.FileName, ms.ToArray()), ct);
                return Results.Created($"/api/v1/properties/{propertyId}/maintenance/tickets/{id}/photos/{photo.Id}", photo);
            })
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(3 * 1024 * 1024))
            .Accepts<IFormFile>("multipart/form-data")
            .RequirePermission(Perm.MaintenanceCreate);
        m.MapGet("/tickets/{id:guid}/photos/{photoId:guid}", async (Guid propertyId, Guid id, Guid photoId, MaintenanceService s, CancellationToken ct) =>
            {
                var (content, contentType, fileName) = await s.GetPhotoAsync(propertyId, id, photoId, ct);
                return Results.File(content, contentType, fileName);
            })
            .RequirePermission(Perm.MaintenanceCreate);

        m.MapGet("/staff", (Guid propertyId, MaintenanceService s, CancellationToken ct) => s.StaffAsync(propertyId, ct))
            .RequirePermission(Perm.MaintenanceAssign);
        m.MapGet("/assets", (Guid propertyId, MaintenanceService s, CancellationToken ct) => s.AssetsAsync(propertyId, ct))
            .RequirePermission(Perm.MaintenanceCreate);
        m.MapPost("/assets", async (Guid propertyId, SaveAssetRequest body, MaintenanceService s, CancellationToken ct) =>
            {
                var a = await s.SaveAssetAsync(propertyId, null, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/maintenance/assets/{a.Id}", a);
            })
            .Validate<SaveAssetRequest>().RequirePermission(Perm.MaintenanceAssign);
        m.MapPut("/assets/{id:guid}", (Guid propertyId, Guid id, SaveAssetRequest body, MaintenanceService s, CancellationToken ct) =>
                s.SaveAssetAsync(propertyId, id, body, ct))
            .Validate<SaveAssetRequest>().RequirePermission(Perm.MaintenanceAssign);
    }
}
