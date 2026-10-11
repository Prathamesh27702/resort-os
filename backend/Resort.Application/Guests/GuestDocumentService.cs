using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Resort.Application.Common;
using Resort.Contracts;
using Resort.Domain.Audit;
using Resort.Domain.Guests;

namespace Resort.Application.Guests;

/// <summary>An uploaded file as the API received it (bytes already read, at most <see cref="GuestDocumentService.MaxFileBytes"/>).</summary>
public sealed record UploadedFile(string FileName, byte[] Content);

public sealed record UploadGuestDocumentCommand(
    string DocumentType, string? Number, string? IssuingCountry, DateOnly? ExpiresOn,
    IReadOnlyDictionary<string, string>? Details, UploadedFile? File);

/// <summary>
/// Guest ID documents (brief §59). Sensitive: every endpoint needs guest_document.read. The ID number and details are
/// encrypted with AES-256-GCM before they reach the database, scans are encrypted before they reach file storage,
/// and opening a document (number or scan) writes a SENSITIVE_READ row to the audit trail.
/// Aadhaar: only the last 4 digits are kept (masked Aadhaar); the full number is never stored.
/// </summary>
public sealed class GuestDocumentService(
    IAppDbContext db, ICurrentUser caller, IFieldEncryptor crypto, IFileStore files, TimeProvider clock, IOptions<GuestOptions> options)
{
    public const int MaxFileBytes = 5 * 1024 * 1024;

    public async Task<IReadOnlyList<GuestDocumentDto>> ListAsync(Guid guestId, CancellationToken ct)
    {
        await RequireGuestAsync(guestId, ct);
        var docs = await db.GuestDocuments.AsNoTracking().Where(d => d.GuestId == guestId)
            .OrderByDescending(d => d.CreatedAt).ToListAsync(ct);
        var names = await UserNamesAsync(docs.Select(d => d.CreatedBy), ct);
        return docs.Select(d => new GuestDocumentDto(d.Id, d.DocumentType, Mask(d), d.IssuingCountry, d.ExpiresOn,
            d.StorageKey is not null, d.FileName, d.ContentType, d.SizeBytes, d.Status, d.CreatedAt, NameOf(names, d.CreatedBy))).ToList();
    }

    public async Task<GuestDocumentDto> UploadAsync(Guid guestId, UploadGuestDocumentCommand c, CancellationToken ct)
    {
        var guest = await RequireGuestAsync(guestId, ct);
        if (guest.Status != GuestStatuses.Active)
            throw new BusinessRuleAppException("GUEST_NOT_ACTIVE", "Documents can only be added to an active profile.");

        var type = c.DocumentType.Trim().ToUpperInvariant();
        var number = string.IsNullOrWhiteSpace(c.Number) ? null : GuestRules.NormalizeIdNumber(c.Number);
        if (number is null && c.File is null)
            throw new BusinessRuleAppException("DOCUMENT_EMPTY", "Enter the document number, attach a scan, or both.");

        var doc = new GuestDocument
        {
            OrganizationId = guest.OrganizationId,
            GuestId = guest.Id,
            DocumentType = type,
            KeyId = crypto.ActiveKeyId,
            IssuingCountry = string.IsNullOrWhiteSpace(c.IssuingCountry) ? null : c.IssuingCountry.Trim().ToUpperInvariant(),
            ExpiresOn = c.ExpiresOn,
        };
        if (number is not null)
        {
            doc.NumberLast4 = GuestRules.Last4(number);
            // Masked Aadhaar only (Aadhaar Act / UIDAI guidance): keep the last 4 digits, never the full number.
            if (type != DocumentTypes.Aadhaar)
            {
                doc.NumberEncrypted = crypto.EncryptString(number, Context(doc, "number"));
                doc.NumberHash = crypto.Hash(type + ":" + number, "guest-document-number");
            }
        }
        var details = c.Details?.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key.Trim(), kv => kv.Value.Trim());
        if (details is { Count: > 0 })
            doc.DetailsEncrypted = crypto.EncryptString(JsonSerializer.Serialize(details), Context(doc, "details"));

        string? storageKey = null;
        if (c.File is { } f)
        {
            var contentType = Sniff(f.Content)
                ?? throw new BusinessRuleAppException("FILE_TYPE", "Scans must be a JPEG or PNG photo, or a PDF.");
            if (f.Content.Length is 0 or > MaxFileBytes)
                throw new BusinessRuleAppException("FILE_SIZE", "Scans must be at most 5 MB.");
            storageKey = $"guest-documents/{guest.OrganizationId:N}/{doc.Id:N}";
            await files.SaveAsync(storageKey, crypto.Encrypt(f.Content, Context(doc, "file")), ct);
            doc.StorageKey = storageKey;
            doc.ContentType = contentType;
            doc.SizeBytes = f.Content.Length;
            doc.FileName = SafeFileName(f.FileName, contentType);
        }

        db.GuestDocuments.Add(doc);
        try { await db.SaveChangesAsync(ct); }
        catch
        {
            if (storageKey is not null) await files.DeleteAsync(storageKey, CancellationToken.None);
            throw;
        }
        return new GuestDocumentDto(doc.Id, doc.DocumentType, Mask(doc), doc.IssuingCountry, doc.ExpiresOn, storageKey is not null,
            doc.FileName, doc.ContentType, doc.SizeBytes, doc.Status, doc.CreatedAt, null);
    }

    /// <summary>Opens one document: decrypts the number and details. Audited as a sensitive read.</summary>
    public async Task<GuestDocumentDetailDto> GetAsync(Guid guestId, Guid docId, CancellationToken ct)
    {
        var doc = await LoadAsync(guestId, docId, ct);
        string? number = doc.NumberEncrypted is null ? null : crypto.DecryptString(doc.NumberEncrypted, Context(doc, "number"));
        var details = doc.DetailsEncrypted is null ? null
            : JsonSerializer.Deserialize<Dictionary<string, string>>(crypto.DecryptString(doc.DetailsEncrypted, Context(doc, "details")));
        await AuditReadAsync(doc, "number", ct);
        var names = await UserNamesAsync([doc.CreatedBy], ct);
        return new GuestDocumentDetailDto(doc.Id, doc.DocumentType, number, Mask(doc), doc.IssuingCountry, doc.ExpiresOn, details,
            doc.StorageKey is not null, doc.FileName, doc.ContentType, doc.SizeBytes, doc.Status, doc.CreatedAt, NameOf(names, doc.CreatedBy));
    }

    /// <summary>The decrypted scan. Audited as a sensitive read.</summary>
    public async Task<(byte[] Content, string ContentType, string FileName)> GetFileAsync(Guid guestId, Guid docId, CancellationToken ct)
    {
        var doc = await LoadAsync(guestId, docId, ct);
        if (doc.StorageKey is null) throw new NotFoundAppException("Document scan", docId);
        var encrypted = await files.ReadAsync(doc.StorageKey, ct) ?? throw new NotFoundAppException("Document scan", docId);
        var content = crypto.Decrypt(encrypted, Context(doc, "file"));
        await AuditReadAsync(doc, "file", ct);
        return (content, doc.ContentType!, doc.FileName ?? "document");
    }

    /// <summary>
    /// Dry run of the retention rule: lists the ID documents the purge job WOULD remove today. Nothing is deleted;
    /// the purge is switched on only after the owner approves the retention period (issue #54).
    /// </summary>
    public async Task<RetentionReportDto> RetentionDryRunAsync(CancellationToken ct)
    {
        var days = Math.Max(1, options.Value.DocumentRetentionDays);
        var now = clock.GetUtcNow();
        var cutoff = now.AddDays(-days);
        var rows = await db.GuestDocuments.AsNoTracking()
            .Where(d => d.Status == DocumentStatuses.Active && d.CreatedAt < cutoff)
            .Join(db.Guests, d => d.GuestId, g => g.Id, (d, g) => new { d, g })
            .OrderBy(x => x.d.CreatedAt)
            .ToListAsync(ct);
        var items = rows.Select(x => new RetentionItemDto(x.g.Id, x.g.FullName, x.d.Id, x.d.DocumentType, x.d.StorageKey is not null,
            x.d.CreatedAt, DateOnly.FromDateTime(x.d.CreatedAt.UtcDateTime.AddDays(days)))).ToList();
        return new RetentionReportDto(days, now, items.Count, items);
    }

    // ------------------------------------------------------------------ helpers

    private static string Context(GuestDocument d, string field) => $"guest_document:{d.Id:N}:{field}";

    internal static string? Mask(GuestDocument d) => d.NumberLast4 is null ? null : "•••• " + d.NumberLast4;

    /// <summary>Content type from the file's first bytes, never from the client's claim.</summary>
    internal static string? Sniff(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A) return "image/png";
        if (b.Length >= 5 && b[0] == 0x25 && b[1] == 0x50 && b[2] == 0x44 && b[3] == 0x46 && b[4] == 0x2D) return "application/pdf";
        return null;
    }

    internal static string SafeFileName(string? name, string contentType)
    {
        var ext = contentType switch { "image/jpeg" => ".jpg", "image/png" => ".png", _ => ".pdf" };
        var baseName = Path.GetFileNameWithoutExtension(Path.GetFileName(name ?? "")) ?? "";
        var clean = new string(baseName.Where(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_' or '.').ToArray()).Trim();
        if (clean.Length == 0) clean = "document";
        if (clean.Length > 80) clean = clean[..80];
        return clean + ext;
    }

    private async Task<Guest> RequireGuestAsync(Guid guestId, CancellationToken ct) =>
        await db.Guests.AsNoTracking().FirstOrDefaultAsync(g => g.Id == guestId, ct) ?? throw new NotFoundAppException("Guest", guestId);

    private async Task<GuestDocument> LoadAsync(Guid guestId, Guid docId, CancellationToken ct)
    {
        var doc = await db.GuestDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == docId && d.GuestId == guestId, ct)
                  ?? throw new NotFoundAppException("Document", docId);
        if (doc.Status == DocumentStatuses.Purged)
            throw new BusinessRuleAppException("DOCUMENT_PURGED", "This document was purged; only its type and dates are kept.");
        return doc;
    }

    private async Task AuditReadAsync(GuestDocument doc, string what, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = doc.OrganizationId,
            UserId = caller.UserId,
            Action = AuditActions.SensitiveRead,
            EntityType = nameof(GuestDocument),
            EntityId = doc.Id.ToString(),
            Changes = JsonSerializer.Serialize(new Dictionary<string, object?> { ["viewed"] = what, ["guestId"] = doc.GuestId }),
            CorrelationId = caller.CorrelationId,
            RequestId = caller.RequestId,
            UserAgent = caller.UserAgent is { Length: > 500 } ua ? ua[..500] : caller.UserAgent,
            Reason = caller.ChangeReason is { Length: > 500 } r ? r[..500] : caller.ChangeReason,
            IpAddress = caller.IpAddress,
            OccurredAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task<Dictionary<Guid, string>> UserNamesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        return list.Count == 0 ? [] : await db.Users.IgnoreQueryFilters().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
    }

    private static string? NameOf(Dictionary<Guid, string> names, Guid? id) => id is { } u && names.TryGetValue(u, out var n) ? n : null;
}
