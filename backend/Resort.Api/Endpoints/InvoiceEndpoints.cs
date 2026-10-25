using Microsoft.AspNetCore.Mvc;
using Resort.Api.Infrastructure;
using Resort.Application.Common;
using Resort.Application.Invoicing;
using Resort.Contracts;

namespace Resort.Api.Endpoints;

/// <summary>Sprint 12 — GST invoices, credit and debit notes, receipts. Issue a folio's invoice (invoice.create and an
/// Idempotency-Key), list and read invoices with their PDFs (invoice.read), correct an invoice only with a credit note
/// (credit_note.create, a reason and an Idempotency-Key; cancelling the whole invoice also needs invoice.cancel), bill late
/// charges with a debit note, and print a payment's receipt. Nothing issued is ever edited or deleted.</summary>
public static class InvoiceEndpoints
{
    public static void MapInvoiceEndpoints(this IEndpointRouteBuilder app)
    {
        var p = app.MapGroup("/api/v1/properties/{propertyId:guid}").WithTags("Invoices");

        p.MapGet("/folios/{id:guid}/invoice-preview", (Guid propertyId, Guid id, InvoiceService s, CancellationToken ct) =>
                s.PreviewAsync(propertyId, id, ct))
            .RequirePermission(Perm.InvoiceRead);
        p.MapPost("/folios/{id:guid}/invoice", async (Guid propertyId, Guid id, [FromHeader(Name = Idempotency.Header)] string? idempotencyKey,
                IssueInvoiceRequest body, InvoiceService s, HttpContext http, CancellationToken ct) =>
            {
                var (invoice, replayed) = await s.IssueAsync(propertyId, id, idempotencyKey, body, ct);
                if (replayed) http.Response.Headers[Idempotency.ReplayedHeader] = "true";
                return Results.Created($"/api/v1/properties/{propertyId}/invoices/{invoice.Id}", invoice);
            })
            .Validate<IssueInvoiceRequest>().RequirePermission(Perm.InvoiceCreate);

        p.MapGet("/invoices", (Guid propertyId, string? q, DateOnly? from, DateOnly? to, Guid? reservationId, string? status, int? page, int? pageSize,
                InvoiceService s, CancellationToken ct) =>
                s.ListAsync(propertyId, new InvoiceService.ListQuery(q, from, to, reservationId, status, page ?? 1, pageSize ?? 50), ct))
            .RequirePermission(Perm.InvoiceRead);
        var i = p.MapGroup("/invoices/{id:guid}");
        i.MapGet("", (Guid propertyId, Guid id, InvoiceService s, CancellationToken ct) => s.GetAsync(propertyId, id, ct))
            .RequirePermission(Perm.InvoiceRead);
        i.MapGet("/pdf", async (Guid propertyId, Guid id, InvoiceService s, CancellationToken ct) =>
                Pdf(s, await s.InvoicePdfAsync(propertyId, id, ct)))
            .RequirePermission(Perm.InvoiceRead);
        i.MapPost("/credit-notes", async (Guid propertyId, Guid id, [FromHeader(Name = Idempotency.Header)] string? idempotencyKey,
                CreateCreditNoteRequest body, InvoiceService s, HttpContext http, CancellationToken ct) =>
            {
                var (invoice, replayed) = await s.CreditAsync(propertyId, id, idempotencyKey, body, ct);
                if (replayed) http.Response.Headers[Idempotency.ReplayedHeader] = "true";
                return Results.Created($"/api/v1/properties/{propertyId}/invoices/{invoice.Id}", invoice);
            })
            .Validate<CreateCreditNoteRequest>().RequirePermission(Perm.CreditNoteCreate);
        i.MapPost("/debit-notes", async (Guid propertyId, Guid id, [FromHeader(Name = Idempotency.Header)] string? idempotencyKey,
                CreateDebitNoteRequest body, InvoiceService s, HttpContext http, CancellationToken ct) =>
            {
                var (invoice, replayed) = await s.DebitAsync(propertyId, id, idempotencyKey, body, ct);
                if (replayed) http.Response.Headers[Idempotency.ReplayedHeader] = "true";
                return Results.Created($"/api/v1/properties/{propertyId}/invoices/{invoice.Id}", invoice);
            })
            .Validate<CreateDebitNoteRequest>().RequirePermission(Perm.CreditNoteCreate);

        p.MapGet("/credit-notes/{id:guid}/pdf", async (Guid propertyId, Guid id, InvoiceService s, CancellationToken ct) =>
                Pdf(s, await s.CreditNotePdfAsync(propertyId, id, ct)))
            .RequirePermission(Perm.InvoiceRead);
        p.MapGet("/debit-notes/{id:guid}/pdf", async (Guid propertyId, Guid id, InvoiceService s, CancellationToken ct) =>
                Pdf(s, await s.DebitNotePdfAsync(propertyId, id, ct)))
            .RequirePermission(Perm.InvoiceRead);

        p.MapPost("/payments/{id:guid}/receipt", (Guid propertyId, Guid id, ReceiptService s, CancellationToken ct) =>
                s.IssueForPaymentAsync(propertyId, id, ct))
            .RequirePermission(Perm.InvoiceCreate);
        p.MapGet("/receipts/{id:guid}/pdf", async (Guid propertyId, Guid id, ReceiptService r, InvoiceService s, CancellationToken ct) =>
                Pdf(s, await r.PdfAsync(propertyId, id, ct)))
            .RequirePermission(Perm.InvoiceRead);
    }

    /// <summary>The PDF inline (the browser shows it; the file name is the document number).</summary>
    private static IResult Pdf(InvoiceService s, PrintDocument document) =>
        Results.File(s.Render(document), "application/pdf", document.FileName);
}
