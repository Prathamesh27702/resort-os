using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Resort.Api.Infrastructure;
using Resort.Application.Common;
using Resort.Application.Finance;
using Resort.Application.Guests;
using Resort.Contracts;
using Resort.Domain.Finance;

namespace Resort.Api.Endpoints;

/// <summary>Sprint 22 — expenses and budgets: categories and departments, recording an expense (Idempotency-Key) with its
/// GST, receipts (photos or PDFs, encrypted), approving, rejecting and cancelling (audited), each financial year's budget
/// by department and month, and the budget against actual report with the variance and a forecast.</summary>
public static class FinanceEndpoints
{
    public static void MapFinanceEndpoints(this IEndpointRouteBuilder app)
    {
        var x = app.MapGroup("/api/v1/properties/{propertyId:guid}/expenses").WithTags("Expenses");

        x.MapGet("/setup", (Guid propertyId, ExpenseService s, CancellationToken ct) => s.SetupAsync(propertyId, ct))
            .RequirePermission(Perm.ExpenseRead);
        x.MapPost("/categories", async (Guid propertyId, SaveExpenseCategoryRequest body, ExpenseService s, CancellationToken ct) =>
            {
                var c = await s.SaveCategoryAsync(propertyId, null, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/expenses/categories/{c.Id}", c);
            })
            .Validate<SaveExpenseCategoryRequest>().RequirePermission(Perm.BudgetManage);
        x.MapPut("/categories/{id:guid}", (Guid propertyId, Guid id, SaveExpenseCategoryRequest body, ExpenseService s, CancellationToken ct) =>
                s.SaveCategoryAsync(propertyId, id, body, ct))
            .Validate<SaveExpenseCategoryRequest>().RequirePermission(Perm.BudgetManage);
        x.MapPost("/cost-centres", async (Guid propertyId, SaveCostCentreRequest body, ExpenseService s, CancellationToken ct) =>
            {
                var c = await s.SaveCostCentreAsync(propertyId, null, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/expenses/cost-centres/{c.Id}", c);
            })
            .Validate<SaveCostCentreRequest>().RequirePermission(Perm.BudgetManage);
        x.MapPut("/cost-centres/{id:guid}", (Guid propertyId, Guid id, SaveCostCentreRequest body, ExpenseService s, CancellationToken ct) =>
                s.SaveCostCentreAsync(propertyId, id, body, ct))
            .Validate<SaveCostCentreRequest>().RequirePermission(Perm.BudgetManage);

        x.MapGet("", (Guid propertyId, DateOnly? from, DateOnly? to, string? status, Guid? costCentreId, Guid? categoryId, bool? mine,
                ExpenseService s, CancellationToken ct) => s.ListAsync(propertyId, from, to, status, costCentreId, categoryId, mine ?? false, ct))
            .RequirePermission(Perm.ExpenseRead);
        x.MapPost("", async (Guid propertyId, [FromHeader(Name = Idempotency.Header)] string? idempotencyKey, SaveExpenseRequest body,
                ExpenseService s, HttpContext http, CancellationToken ct) =>
            {
                var (e, replayed) = await s.CreateAsync(propertyId, idempotencyKey, body, ct);
                if (replayed) http.Response.Headers[Idempotency.ReplayedHeader] = "true";
                return Results.Created($"/api/v1/properties/{propertyId}/expenses/{e.Id}", e);
            })
            .Validate<SaveExpenseRequest>().RequirePermission(Perm.ExpenseCreate);
        x.MapGet("/{id:guid}", (Guid propertyId, Guid id, ExpenseService s, CancellationToken ct) => s.GetAsync(propertyId, id, ct))
            .RequirePermission(Perm.ExpenseRead);
        x.MapPut("/{id:guid}", (Guid propertyId, Guid id, SaveExpenseRequest body, ExpenseService s, CancellationToken ct) =>
                s.UpdateAsync(propertyId, id, body, ct))
            .Validate<SaveExpenseRequest>().RequirePermission(Perm.ExpenseCreate);
        x.MapPost("/{id:guid}/approve", (Guid propertyId, Guid id, ExpenseDecisionRequest? body, ExpenseService s, CancellationToken ct) =>
                s.ApproveAsync(propertyId, id, body ?? new ExpenseDecisionRequest(), ct))
            .RequirePermission(Perm.ExpenseApprove);
        x.MapPost("/{id:guid}/reject", (Guid propertyId, Guid id, ExpenseDecisionRequest body, ExpenseService s, CancellationToken ct) =>
                s.RejectAsync(propertyId, id, body, ct))
            .Validate<ExpenseDecisionRequest>().RequirePermission(Perm.ExpenseApprove);
        x.MapPost("/{id:guid}/cancel", (Guid propertyId, Guid id, ExpenseDecisionRequest body, ExpenseService s, CancellationToken ct) =>
                s.CancelAsync(propertyId, id, body, ct))
            .Validate<ExpenseDecisionRequest>().RequirePermission(Perm.ExpenseCreate);

        // multipart/form-data with one "file": a receipt photo already shrunk on the phone, or a PDF bill (at most 2 MB).
        x.MapPost("/{id:guid}/receipts", async (Guid propertyId, Guid id, HttpRequest http, ExpenseService s, CancellationToken ct) =>
            {
                if (!http.HasFormContentType)
                    throw new ValidationException([new FluentValidation.Results.ValidationFailure("File", "Send the receipt as multipart/form-data with a field named file.")]);
                var form = await http.ReadFormAsync(ct);
                var f = form.Files.GetFile("file")
                        ?? throw new ValidationException([new FluentValidation.Results.ValidationFailure("File", "Choose a receipt photo or PDF to add.")]);
                if (f.Length > FinanceMath.MaxReceiptBytes)
                    throw new ValidationException([new FluentValidation.Results.ValidationFailure("File",
                        $"The receipt is {f.Length / 1024 / 1024.0:0.0} MB; at most 2 MB. Take the photo again or choose a smaller file.")]);
                using var ms = new MemoryStream((int)f.Length);
                await f.CopyToAsync(ms, ct);
                var receipt = await s.AddReceiptAsync(propertyId, id, new UploadedFile(f.FileName, ms.ToArray()), ct);
                return Results.Created($"/api/v1/properties/{propertyId}/expenses/{id}/receipts/{receipt.Id}", receipt);
            })
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(3 * 1024 * 1024))
            .Accepts<IFormFile>("multipart/form-data")
            .RequirePermission(Perm.ExpenseCreate);
        x.MapGet("/{id:guid}/receipts/{receiptId:guid}", async (Guid propertyId, Guid id, Guid receiptId, ExpenseService s, CancellationToken ct) =>
            {
                var (content, contentType, fileName) = await s.GetReceiptAsync(propertyId, id, receiptId, ct);
                return Results.File(content, contentType, fileName);
            })
            .RequirePermission(Perm.ExpenseRead);

        var b = app.MapGroup("/api/v1/properties/{propertyId:guid}/budgets").WithTags("Budgets");

        b.MapGet("", (Guid propertyId, BudgetService s, CancellationToken ct) => s.ListAsync(propertyId, ct))
            .RequirePermission(Perm.BudgetRead);
        b.MapPost("", async (Guid propertyId, CreateBudgetRequest body, BudgetService s, CancellationToken ct) =>
            {
                var budget = await s.CreateAsync(propertyId, body, ct);
                return Results.Created($"/api/v1/properties/{propertyId}/budgets/{budget.Id}", budget);
            })
            .Validate<CreateBudgetRequest>().RequirePermission(Perm.BudgetManage);
        b.MapGet("/report", (Guid propertyId, int? year, DateOnly? through, BudgetService s, CancellationToken ct) =>
                s.ReportAsync(propertyId, year, through, ct))
            .RequirePermission(Perm.BudgetRead);
        b.MapGet("/{id:guid}", (Guid propertyId, Guid id, BudgetService s, CancellationToken ct) => s.GetAsync(propertyId, id, ct))
            .RequirePermission(Perm.BudgetRead);
        b.MapPut("/{id:guid}/rows", (Guid propertyId, Guid id, SaveBudgetRowRequest body, BudgetService s, CancellationToken ct) =>
                s.SaveRowAsync(propertyId, id, body, ct))
            .Validate<SaveBudgetRowRequest>().RequirePermission(Perm.BudgetManage);
    }
}
