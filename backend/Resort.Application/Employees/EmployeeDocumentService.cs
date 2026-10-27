using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Resort.Application.Common;
using Resort.Application.Guests;
using Resort.Contracts;
using Resort.Domain.Audit;
using Resort.Domain.Employees;
using Resort.Domain.Guests;

namespace Resort.Application.Employees;

public sealed record UploadEmployeeDocumentCommand(string DocumentType, string? Number, DateOnly? ExpiresOn, string? Note, UploadedFile? File);

/// <summary>
/// Employee documents (Sprint 17): Aadhaar, PAN, bank account, passport, driving licence, offer letter. Every call needs
/// employee_document.read. Numbers are encrypted (AES-256-GCM) before they reach the database and only the last 4
/// characters stay readable; Aadhaar keeps the last 4 digits only, never the full number. Scans are encrypted before they
/// reach file storage. Opening a number or a scan writes a SENSITIVE_READ audit entry. Removing a document wipes the
/// number and the scan; the entry stays with the reason.
/// </summary>
public sealed class EmployeeDocumentService(IAppDbContext db, ICurrentUser caller, IFieldEncryptor crypto, IFileStore files, TimeProvider clock,
    EmployeeService employees)
{
    public async Task<IReadOnlyList<EmployeeDocumentDto>> ListAsync(Guid propertyId, Guid employeeId, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var emp = await employees.LoadAsync(propertyId, employeeId, ct);
        var today = property.LocalDate(clock.GetUtcNow());
        var docs = await db.EmployeeDocuments.AsNoTracking().Where(d => d.EmployeeId == emp.Id).ToListAsync(ct);
        var names = await NamesAsync(docs.Select(d => d.CreatedBy), ct);
        return docs.OrderBy(d => d.Status).ThenByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id)
            .Select(d => ToDto(d, today, names)).ToList();
    }

    public async Task<EmployeeDocumentDto> UploadAsync(Guid propertyId, Guid employeeId, UploadEmployeeDocumentCommand c, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var emp = await employees.LoadAsync(propertyId, employeeId, ct);
        var type = c.DocumentType.Trim().ToUpperInvariant();
        var number = string.IsNullOrWhiteSpace(c.Number) ? null : GuestRules.NormalizeIdNumber(c.Number);
        if (number is null && c.File is null)
            throw Field("Number", "Enter the document number, attach a scan, or both.");
        if (type == EmployeeDocumentTypes.Pan && number is not null && !System.Text.RegularExpressions.Regex.IsMatch(number, "^[A-Z]{5}[0-9]{4}[A-Z]$"))
            throw Field("Number", "A PAN is 10 characters: 5 letters, 4 digits, 1 letter, e.g. ABCPK1234F.");
        if (type == EmployeeDocumentTypes.Aadhaar && number is not null && !System.Text.RegularExpressions.Regex.IsMatch(number, "^([0-9]{4}|[0-9]{12})$"))
            throw Field("Number", "Enter the 12-digit Aadhaar number or just its last 4 digits. Only the last 4 are kept.");

        var doc = new EmployeeDocument
        {
            OrganizationId = emp.OrganizationId,
            EmployeeId = emp.Id,
            DocumentType = type,
            KeyId = crypto.ActiveKeyId,
            ExpiresOn = c.ExpiresOn,
            Note = string.IsNullOrWhiteSpace(c.Note) ? null : c.Note.Trim(),
        };
        if (number is not null)
        {
            doc.NumberLast4 = GuestRules.Last4(number);
            // Masked Aadhaar only (UIDAI guidance): the last 4 digits, never the full number.
            if (type != EmployeeDocumentTypes.Aadhaar) doc.NumberEncrypted = crypto.EncryptString(number, Context(doc, "number"));
        }

        string? storageKey = null;
        if (c.File is { } f)
        {
            var contentType = GuestDocumentService.Sniff(f.Content)
                ?? throw Field("File", "Scans must be a JPEG or PNG photo, or a PDF.");
            if (f.Content.Length is 0 or > GuestDocumentService.MaxFileBytes)
                throw Field("File", "Scans must be at most 5 MB.");
            storageKey = $"employee-documents/{emp.OrganizationId:N}/{doc.Id:N}";
            await files.SaveAsync(storageKey, crypto.Encrypt(f.Content, Context(doc, "file")), ct);
            doc.StorageKey = storageKey;
            doc.ContentType = contentType;
            doc.SizeBytes = f.Content.Length;
            doc.FileName = GuestDocumentService.SafeFileName(f.FileName, contentType);
        }

        db.EmployeeDocuments.Add(doc);
        try { await db.SaveChangesAsync(ct); }
        catch
        {
            if (storageKey is not null) await files.DeleteAsync(storageKey, CancellationToken.None);
            throw;
        }
        return ToDto(doc, property.LocalDate(clock.GetUtcNow()), []);
    }

    /// <summary>Opens one document: the number decrypted. Audited as a sensitive read.</summary>
    public async Task<EmployeeDocumentDetailDto> GetAsync(Guid propertyId, Guid employeeId, Guid docId, CancellationToken ct)
    {
        var doc = await LoadAsync(propertyId, employeeId, docId, ct);
        var number = doc.NumberEncrypted is null ? null : crypto.DecryptString(doc.NumberEncrypted, Context(doc, "number"));
        await AuditReadAsync(doc, "number", ct);
        var names = await NamesAsync([doc.CreatedBy], ct);
        return new EmployeeDocumentDetailDto(doc.Id, doc.DocumentType, number, Mask(doc), doc.ExpiresOn, doc.Note, doc.StorageKey is not null, doc.FileName,
            doc.ContentType, doc.SizeBytes, doc.Status, doc.CreatedAt, doc.CreatedBy is { } u ? names.GetValueOrDefault(u) : null);
    }

    /// <summary>The decrypted scan. Audited as a sensitive read.</summary>
    public async Task<(byte[] Content, string ContentType, string FileName)> GetFileAsync(Guid propertyId, Guid employeeId, Guid docId, CancellationToken ct)
    {
        var doc = await LoadAsync(propertyId, employeeId, docId, ct);
        if (doc.StorageKey is null) throw new NotFoundAppException("This document has no scan.");
        var encrypted = await files.ReadAsync(doc.StorageKey, ct) ?? throw new NotFoundAppException("The scan's file is missing from storage.");
        var content = crypto.Decrypt(encrypted, Context(doc, "file"));
        await AuditReadAsync(doc, "file", ct);
        return (content, doc.ContentType!, doc.FileName ?? "document");
    }

    /// <summary>Wipes the number and the scan; the entry stays as REMOVED with the reason (employee.update; audited).</summary>
    public async Task<EmployeeDocumentDto> RemoveAsync(Guid propertyId, Guid employeeId, Guid docId, RemoveEmployeeDocumentRequest r, CancellationToken ct)
    {
        var property = await employees.RequirePropertyAsync(propertyId, ct);
        var doc = await LoadAsync(propertyId, employeeId, docId, ct, track: true);
        if (doc.Status == EmployeeDocumentStatuses.Removed)
            throw new BusinessRuleAppException("INVALID_STATUS_TRANSITION", "This document was already removed.");
        var key = doc.StorageKey;
        doc.NumberEncrypted = null;
        doc.StorageKey = null;
        doc.FileName = null;
        doc.ContentType = null;
        doc.SizeBytes = null;
        doc.Status = EmployeeDocumentStatuses.Removed;
        doc.RemovedAt = clock.GetUtcNow();
        doc.RemovedReason = r.Reason.Trim();
        await db.SaveChangesAsync(ct);
        if (key is not null) await files.DeleteAsync(key, CancellationToken.None);
        var names = await NamesAsync([doc.CreatedBy], ct);
        return ToDto(doc, property.LocalDate(clock.GetUtcNow()), names);
    }

    // ------------------------------------------------------------------ helpers

    private static FluentValidation.ValidationException Field(string field, string message) => Reservations.ReservationService.Field(field, message);

    private async Task<EmployeeDocument> LoadAsync(Guid propertyId, Guid employeeId, Guid docId, CancellationToken ct, bool track = false)
    {
        var emp = await employees.LoadAsync(propertyId, employeeId, ct);
        var q = track ? db.EmployeeDocuments : db.EmployeeDocuments.AsNoTracking();
        var doc = await q.SingleOrDefaultAsync(d => d.Id == docId && d.EmployeeId == emp.Id, ct)
                  ?? throw new NotFoundAppException($"That document isn't one of {emp.FullName}'s. Reload the page.");
        return doc;
    }

    private static string Context(EmployeeDocument d, string field) => $"employee_document:{d.Id:N}:{field}";

    private static string? Mask(EmployeeDocument d) => d.NumberLast4 is null ? null : "•••• " + d.NumberLast4;

    private static EmployeeDocumentDto ToDto(EmployeeDocument d, DateOnly today, Dictionary<Guid, string> names) =>
        new(d.Id, d.DocumentType, Mask(d), d.ExpiresOn, d.ExpiresOn is { } e && e < today, d.Note, d.StorageKey is not null, d.FileName, d.ContentType,
            d.SizeBytes, d.Status, d.RemovedReason, d.CreatedAt, d.CreatedBy is { } u ? names.GetValueOrDefault(u) : null);

    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.OfType<Guid>().Distinct().ToList();
        return await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
    }

    private async Task AuditReadAsync(EmployeeDocument doc, string what, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = doc.OrganizationId,
            UserId = caller.UserId,
            Action = AuditActions.SensitiveRead,
            EntityType = nameof(EmployeeDocument),
            EntityId = doc.Id.ToString(),
            Changes = JsonSerializer.Serialize(new Dictionary<string, object?> { ["viewed"] = what, ["employeeId"] = doc.EmployeeId }),
            CorrelationId = caller.CorrelationId,
            RequestId = caller.RequestId,
            UserAgent = caller.UserAgent is { Length: > 500 } ua ? ua[..500] : caller.UserAgent,
            Reason = caller.ChangeReason is { Length: > 500 } r ? r[..500] : caller.ChangeReason,
            IpAddress = caller.IpAddress,
            OccurredAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
    }
}
