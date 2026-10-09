using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Resort.Api.Infrastructure;
using Resort.Application.Common;
using Resort.Application.Guests;
using Resort.Contracts;

namespace Resort.Api.Endpoints;

/// <summary>
/// Sprint 3 — guest profiles and ID documents. Guests belong to the organisation and are shared by all its
/// properties, so a grant for any property is enough (PermissionScope.Any). Anonymising and the retention report
/// need guest.delete organisation-wide. Documents need guest_document.read for every call.
/// </summary>
public static class GuestEndpoints
{
    public static void MapGuestEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/guests").WithTags("Guests");

        g.MapGet("", (string? q, bool? includeInactive, int? page, int? pageSize, GuestService s, CancellationToken ct) =>
                s.ListAsync(q, includeInactive ?? false, page ?? 1, pageSize ?? 50, ct))
            .RequirePermission(Perm.GuestRead, PermissionScope.Any);

        g.MapGet("/possible-duplicates", (string? firstName, string? lastName, string? phone, string? email, Guid? excludeId,
                GuestService s, CancellationToken ct) => s.PossibleDuplicatesAsync(firstName, lastName, phone, email, excludeId, ct))
            .RequirePermission(Perm.GuestRead, PermissionScope.Any);

        g.MapPost("", async (CreateGuestRequest r, GuestService s, CancellationToken ct) =>
            {
                var guest = await s.CreateAsync(r, ct);
                return Results.Created($"/api/v1/guests/{guest.Id}", guest);
            })
            .Validate<CreateGuestRequest>().RequirePermission(Perm.GuestCreate, PermissionScope.Any);

        g.MapPost("/merge", (MergeGuestsRequest r, GuestService s, CancellationToken ct) => s.MergeAsync(r, ct))
            .Validate<MergeGuestsRequest>().RequirePermission(Perm.GuestUpdate, PermissionScope.Any);

        g.MapGet("/retention/dry-run", (GuestDocumentService s, CancellationToken ct) => s.RetentionDryRunAsync(ct))
            .RequirePermission(Perm.GuestDelete);

        g.MapGet("/{id:guid}", (Guid id, GuestService s, CancellationToken ct) => s.GetAsync(id, ct))
            .RequirePermission(Perm.GuestRead, PermissionScope.Any);

        g.MapPut("/{id:guid}", (Guid id, UpdateGuestRequest r, GuestService s, CancellationToken ct) => s.UpdateAsync(id, r, ct))
            .Validate<UpdateGuestRequest>().RequirePermission(Perm.GuestUpdate, PermissionScope.Any);

        g.MapGet("/{id:guid}/stays", (Guid id, GuestService s, CancellationToken ct) => s.StaysAsync(id, ct))
            .RequirePermission(Perm.GuestRead, PermissionScope.Any);

        g.MapPost("/{id:guid}/consents", (Guid id, RecordConsentRequest r, GuestService s, CancellationToken ct) => s.RecordConsentAsync(id, r, ct))
            .Validate<RecordConsentRequest>().RequirePermission(Perm.GuestUpdate, PermissionScope.Any);

        g.MapPost("/{id:guid}/anonymise", (Guid id, AnonymiseGuestRequest r, GuestService s, CancellationToken ct) => s.AnonymiseAsync(id, r, ct))
            .Validate<AnonymiseGuestRequest>().RequirePermission(Perm.GuestDelete);

        // ---------------------------------------------------------------- ID documents (sensitive)
        var docs = g.MapGroup("/{id:guid}/documents").WithTags("Guest documents");

        docs.MapGet("", (Guid id, GuestDocumentService s, CancellationToken ct) => s.ListAsync(id, ct))
            .RequirePermission(Perm.GuestDocumentRead, PermissionScope.Any);

        // multipart/form-data: documentType, number?, issuingCountry?, expiresOn? (YYYY-MM-DD), details? (JSON object), file?
        docs.MapPost("", async (Guid id, HttpRequest http, GuestDocumentService s, IValidator<UploadGuestDocumentCommand> validator, CancellationToken ct) =>
            {
                if (!http.HasFormContentType)
                    throw new ValidationException([new FluentValidation.Results.ValidationFailure("body", "Send the document as multipart/form-data.")]);
                var form = await http.ReadFormAsync(ct);
                var cmd = await ToCommandAsync(form, ct);
                await validator.ValidateAndThrowAsync(cmd, ct);
                var doc = await s.UploadAsync(id, cmd, ct);
                return Results.Created($"/api/v1/guests/{id}/documents/{doc.Id}", doc);
            })
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(6 * 1024 * 1024))
            .Accepts<IFormFile>("multipart/form-data")
            .RequirePermission(Perm.GuestDocumentRead, PermissionScope.Any);

        docs.MapGet("/{docId:guid}", (Guid id, Guid docId, GuestDocumentService s, CancellationToken ct) => s.GetAsync(id, docId, ct))
            .RequirePermission(Perm.GuestDocumentRead, PermissionScope.Any);

        docs.MapGet("/{docId:guid}/file", async (Guid id, Guid docId, GuestDocumentService s, CancellationToken ct) =>
            {
                var (content, contentType, fileName) = await s.GetFileAsync(id, docId, ct);
                return Results.File(content, contentType, fileName);
            })
            .RequirePermission(Perm.GuestDocumentRead, PermissionScope.Any);
    }

    private static async Task<UploadGuestDocumentCommand> ToCommandAsync(IFormCollection form, CancellationToken ct)
    {
        static string? Field(IFormCollection f, string name) => f.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.ToString() : null;

        DateOnly? expires = null;
        if (Field(form, "expiresOn") is { } e)
        {
            if (!DateOnly.TryParseExact(e, "yyyy-MM-dd", out var d))
                throw new ValidationException([new FluentValidation.Results.ValidationFailure("ExpiresOn", "Expiry date must be YYYY-MM-DD.")]);
            expires = d;
        }

        Dictionary<string, string>? details = null;
        if (Field(form, "details") is { } json)
        {
            try { details = JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
            catch (JsonException)
            {
                throw new ValidationException([new FluentValidation.Results.ValidationFailure("Details", "Details must be a JSON object of text values.")]);
            }
        }

        UploadedFile? file = null;
        if (form.Files.GetFile("file") is { } f)
        {
            if (f.Length > GuestDocumentService.MaxFileBytes)
                throw new BusinessRuleAppException("FILE_SIZE", "Scans must be at most 5 MB.");
            using var ms = new MemoryStream((int)f.Length);
            await f.CopyToAsync(ms, ct);
            file = new UploadedFile(f.FileName, ms.ToArray());
        }

        return new UploadGuestDocumentCommand(Field(form, "documentType") ?? "", Field(form, "number"), Field(form, "issuingCountry"),
            expires, details, file);
    }
}
