using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Resort.Api.Infrastructure;
using Resort.Application.Common;
using Resort.Application.Employees;
using Resort.Application.Guests;
using Resort.Contracts;

namespace Resort.Api.Endpoints;

/// <summary>Sprint 17 — employees and roster: departments and job titles, employee profiles (optionally linked to a staff
/// account), their pay (employee_salary.read only, its own endpoints) and documents (employee_document.read only), shift
/// templates, and the weekly roster with clash detection and the shortage view.</summary>
public static class EmployeesEndpoints
{
    public static void MapEmployeesEndpoints(this IEndpointRouteBuilder app)
    {
        var p = app.MapGroup("/api/v1/properties/{propertyId:guid}").WithTags("Employees");

        p.MapGet("/departments", (Guid propertyId, EmployeeService s, CancellationToken ct) => s.DepartmentsAsync(propertyId, ct))
            .RequirePermission(Perm.EmployeeRead);
        p.MapPost("/departments", async (Guid propertyId, SaveDepartmentRequest body, EmployeeService s, CancellationToken ct) =>
            {
                var x = await s.SaveDepartmentAsync(propertyId, null, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/departments/{x.Id}", x);
            })
            .Validate<SaveDepartmentRequest>().RequirePermission(Perm.EmployeeUpdate);
        p.MapPut("/departments/{id:guid}", (Guid propertyId, Guid id, SaveDepartmentRequest body, EmployeeService s, CancellationToken ct) =>
                s.SaveDepartmentAsync(propertyId, id, body, ct))
            .Validate<SaveDepartmentRequest>().RequirePermission(Perm.EmployeeUpdate);
        p.MapPost("/designations", async (Guid propertyId, SaveDesignationRequest body, EmployeeService s, CancellationToken ct) =>
                Results.Created($"/api/v1/properties/{propertyId}/departments/{body.DepartmentId}", await s.SaveDesignationAsync(propertyId, null, body, ct)))
            .Validate<SaveDesignationRequest>().RequirePermission(Perm.EmployeeUpdate);
        p.MapPut("/designations/{id:guid}", (Guid propertyId, Guid id, SaveDesignationRequest body, EmployeeService s, CancellationToken ct) =>
                s.SaveDesignationAsync(propertyId, id, body, ct))
            .Validate<SaveDesignationRequest>().RequirePermission(Perm.EmployeeUpdate);

        var e = p.MapGroup("/employees");
        e.MapGet("", (Guid propertyId, string? q, Guid? departmentId, string? status, EmployeeService s, CancellationToken ct) =>
                s.ListAsync(propertyId, q, departmentId, status, ct))
            .RequirePermission(Perm.EmployeeRead);
        e.MapGet("/accounts", (Guid propertyId, EmployeeService s, CancellationToken ct) => s.AccountsAsync(propertyId, ct))
            .RequirePermission(Perm.EmployeeUpdate);
        e.MapGet("/{id:guid}", (Guid propertyId, Guid id, EmployeeService s, CancellationToken ct) => s.GetAsync(propertyId, id, ct))
            .RequirePermission(Perm.EmployeeRead);
        e.MapPost("", async (Guid propertyId, SaveEmployeeRequest body, EmployeeService s, CancellationToken ct) =>
            {
                var x = await s.SaveAsync(propertyId, null, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/employees/{x.Id}", x);
            })
            .Validate<SaveEmployeeRequest>().RequirePermission(Perm.EmployeeCreate);
        e.MapPut("/{id:guid}", (Guid propertyId, Guid id, SaveEmployeeRequest body, EmployeeService s, CancellationToken ct) =>
                s.SaveAsync(propertyId, id, body, ct))
            .Validate<SaveEmployeeRequest>().RequirePermission(Perm.EmployeeUpdate);
        e.MapPost("/{id:guid}/leave", (Guid propertyId, Guid id, EmployeeLeftRequest body, EmployeeService s, CancellationToken ct) =>
                s.LeaveAsync(propertyId, id, body, ct))
            .Validate<EmployeeLeftRequest>().RequirePermission(Perm.EmployeeUpdate);
        e.MapGet("/{id:guid}/shifts", (Guid propertyId, Guid id, RosterService s, CancellationToken ct) => s.UpcomingAsync(propertyId, id, ct))
            .RequirePermission(Perm.RosterRead);

        // Pay: its own endpoints, readable only with employee_salary.read; recording also needs employee.update.
        e.MapGet("/{id:guid}/compensation", (Guid propertyId, Guid id, CompensationService s, CancellationToken ct) => s.GetAsync(propertyId, id, ct))
            .RequirePermission(Perm.EmployeeSalaryRead);
        e.MapPost("/{id:guid}/compensation", async (Guid propertyId, Guid id, RecordCompensationRequest body, CompensationService s, CancellationToken ct) =>
                Results.Created($"/api/v1/properties/{propertyId}/employees/{id}/compensation", await s.RecordAsync(propertyId, id, body, ct)))
            .Validate<RecordCompensationRequest>().RequirePermission(Perm.EmployeeSalaryRead).RequirePermission(Perm.EmployeeUpdate);

        // Documents: sensitive, employee_document.read for every call; adding or removing one also needs employee.update.
        var docs = e.MapGroup("/{id:guid}/documents").WithTags("Employee documents");
        docs.MapGet("", (Guid propertyId, Guid id, EmployeeDocumentService s, CancellationToken ct) => s.ListAsync(propertyId, id, ct))
            .RequirePermission(Perm.EmployeeDocumentRead);
        // multipart/form-data: documentType, number?, expiresOn? (YYYY-MM-DD), note?, file?
        docs.MapPost("", async (Guid propertyId, Guid id, HttpRequest http, EmployeeDocumentService s, IValidator<UploadEmployeeDocumentCommand> validator,
                CancellationToken ct) =>
            {
                if (!http.HasFormContentType)
                    throw new ValidationException([new FluentValidation.Results.ValidationFailure("body", "Send the document as multipart/form-data.")]);
                var cmd = await ToCommandAsync(await http.ReadFormAsync(ct), ct);
                await validator.ValidateAndThrowAsync(cmd, ct);
                var doc = await s.UploadAsync(propertyId, id, cmd, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/employees/{id}/documents/{doc.Id}", doc);
            })
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(6 * 1024 * 1024))
            .Accepts<IFormFile>("multipart/form-data")
            .RequirePermission(Perm.EmployeeDocumentRead).RequirePermission(Perm.EmployeeUpdate);
        docs.MapGet("/{docId:guid}", (Guid propertyId, Guid id, Guid docId, EmployeeDocumentService s, CancellationToken ct) => s.GetAsync(propertyId, id, docId, ct))
            .RequirePermission(Perm.EmployeeDocumentRead);
        docs.MapGet("/{docId:guid}/file", async (Guid propertyId, Guid id, Guid docId, EmployeeDocumentService s, CancellationToken ct) =>
            {
                var (content, contentType, fileName) = await s.GetFileAsync(propertyId, id, docId, ct);
                return Results.File(content, contentType, fileName);
            })
            .RequirePermission(Perm.EmployeeDocumentRead);
        docs.MapPost("/{docId:guid}/remove", (Guid propertyId, Guid id, Guid docId, RemoveEmployeeDocumentRequest body, EmployeeDocumentService s,
                CancellationToken ct) => s.RemoveAsync(propertyId, id, docId, body, ct))
            .Validate<RemoveEmployeeDocumentRequest>().RequirePermission(Perm.EmployeeDocumentRead).RequirePermission(Perm.EmployeeUpdate);

        var r = p.MapGroup("/roster").WithTags("Roster");
        r.MapGet("", (Guid propertyId, DateOnly? week, RosterService s, CancellationToken ct) => s.WeekAsync(propertyId, week, ct))
            .RequirePermission(Perm.RosterRead);
        r.MapGet("/mine", (Guid propertyId, DateOnly? from, RosterService s, CancellationToken ct) => s.MineAsync(propertyId, from, ct))
            .RequirePermission(Perm.RosterSelf);
        r.MapGet("/templates", (Guid propertyId, RosterService s, CancellationToken ct) => s.TemplatesAsync(propertyId, ct))
            .RequirePermission(Perm.RosterRead);
        r.MapPost("/templates", async (Guid propertyId, SaveShiftTemplateRequest body, RosterService s, CancellationToken ct) =>
            {
                var x = await s.SaveTemplateAsync(propertyId, null, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/roster/templates/{x.Id}", x);
            })
            .Validate<SaveShiftTemplateRequest>().RequirePermission(Perm.RosterManage);
        r.MapPut("/templates/{id:guid}", (Guid propertyId, Guid id, SaveShiftTemplateRequest body, RosterService s, CancellationToken ct) =>
                s.SaveTemplateAsync(propertyId, id, body, ct))
            .Validate<SaveShiftTemplateRequest>().RequirePermission(Perm.RosterManage);
        r.MapPost("/shifts", async (Guid propertyId, SaveRosterShiftRequest body, RosterService s, CancellationToken ct) =>
            {
                var x = await s.SaveShiftAsync(propertyId, null, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/roster/shifts/{x.Id}", x);
            })
            .Validate<SaveRosterShiftRequest>().RequirePermission(Perm.RosterManage);
        r.MapPut("/shifts/{id:guid}", (Guid propertyId, Guid id, SaveRosterShiftRequest body, RosterService s, CancellationToken ct) =>
                s.SaveShiftAsync(propertyId, id, body, ct))
            .Validate<SaveRosterShiftRequest>().RequirePermission(Perm.RosterManage);
        r.MapPost("/shifts/{id:guid}/cancel", async (Guid propertyId, Guid id, CancelRosterShiftRequest body, RosterService s, CancellationToken ct) =>
            {
                await s.CancelShiftAsync(propertyId, id, body, ct);
                return Results.NoContent();
            })
            .Validate<CancelRosterShiftRequest>().RequirePermission(Perm.RosterManage);
        r.MapPost("/copy-week", (Guid propertyId, CopyRosterWeekRequest body, RosterService s, CancellationToken ct) => s.CopyWeekAsync(propertyId, body, ct))
            .Validate<CopyRosterWeekRequest>().RequirePermission(Perm.RosterManage);
    }

    private static async Task<UploadEmployeeDocumentCommand> ToCommandAsync(IFormCollection form, CancellationToken ct)
    {
        static string? Field(IFormCollection f, string name) => f.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.ToString() : null;

        DateOnly? expires = null;
        if (Field(form, "expiresOn") is { } x)
        {
            if (!DateOnly.TryParseExact(x, "yyyy-MM-dd", out var d))
                throw new ValidationException([new FluentValidation.Results.ValidationFailure("ExpiresOn", "Enter the expiry date as a date (YYYY-MM-DD), or leave it empty.")]);
            expires = d;
        }
        UploadedFile? file = null;
        if (form.Files.GetFile("file") is { } f)
        {
            if (f.Length > GuestDocumentService.MaxFileBytes)
                throw new ValidationException([new FluentValidation.Results.ValidationFailure("File", "Scans must be at most 5 MB.")]);
            using var ms = new MemoryStream((int)f.Length);
            await f.CopyToAsync(ms, ct);
            file = new UploadedFile(f.FileName, ms.ToArray());
        }
        return new UploadEmployeeDocumentCommand(Field(form, "documentType") ?? "", Field(form, "number"), expires, Field(form, "note"), file);
    }
}
