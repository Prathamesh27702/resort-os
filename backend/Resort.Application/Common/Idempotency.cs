using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Resort.Application.Common;

/// <summary>Helpers for the Idempotency-Key header (create endpoints that book or take money).</summary>
public static class Idempotency
{
    public const string Header = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";
    public const int MinLength = 8;
    public const int MaxLength = 100;

    /// <summary>The same options the HTTP layer uses, so a stored response replays byte-for-byte the same.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>SHA-256 of the request as parsed (not as sent), so whitespace or property order don't make a retry look
    /// like a different request.</summary>
    public static byte[] Hash<T>(T request) => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, Json));

    /// <summary>The trimmed key, or a field error that says what is wrong with it.</summary>
    public static string RequireKey(string? key)
    {
        var k = key?.Trim();
        if (string.IsNullOrEmpty(k))
            throw Fail($"Send an {Header} header (any unique text, {MinLength}–{MaxLength} characters) so a retried request can't book twice.");
        if (k.Length < MinLength || k.Length > MaxLength)
            throw Fail($"The {Header} header must be {MinLength}–{MaxLength} characters long; this one is {k.Length}.");
        return k;
    }

    private static FluentValidation.ValidationException Fail(string message) =>
        new([new FluentValidation.Results.ValidationFailure(Header, message)]);

    /// <summary>
    /// Claims the key inside the caller's transaction (INSERT … ON CONFLICT DO NOTHING). Returns null when this request
    /// owns the key and should do the work; otherwise the stored response of the earlier request with the same key and
    /// body (a concurrent request with the same key waits on the first one's row lock until it commits or rolls back).
    /// A key reused for a different body, or one still being worked on, is refused with a message naming
    /// <paramref name="what"/> ("booking", "walk-in").
    /// </summary>
    public static async Task<string?> ClaimAsync(IAppDbContext db, Guid orgId, Guid? userId, string scope, string key, byte[] hash,
        string what, CancellationToken ct)
    {
        var claimed = await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO idempotency_keys (id, organization_id, scope, key, request_hash, user_id)
             VALUES ({Guid.CreateVersion7()}, {orgId}, {scope}, {key}, {hash}, {userId})
             ON CONFLICT (organization_id, scope, key) DO NOTHING
             """, ct);
        if (claimed == 1) return null;
        var stored = await db.IdempotencyKeys.AsNoTracking().SingleAsync(k => k.Scope == scope && k.Key == key, ct);
        if (!stored.RequestHash.AsSpan().SequenceEqual(hash))
            throw new BusinessRuleAppException("IDEMPOTENCY_KEY_REUSED",
                $"This {Header} was already used for a different {what} ({ReferenceOf(stored.Response)}). " +
                $"Each new {what} needs a new key; reload the form and try again.");
        return stored.Response
               ?? throw new ConflictAppException("IDEMPOTENCY_IN_PROGRESS", $"The same {what} is still being saved. Wait a moment and reload.");
    }

    /// <summary>Stores the response for a claimed key, in the same transaction as the work it describes.</summary>
    public static async Task CompleteAsync<T>(IAppDbContext db, Guid orgId, string scope, string key, int statusCode, T response,
        Guid resourceId, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(response, Json);
        await db.Database.ExecuteSqlAsync(
            $"""
             UPDATE idempotency_keys SET status_code = {(short)statusCode}, response = {json}::jsonb, resource_id = {resourceId}, completed_at = now()
              WHERE organization_id = {orgId} AND scope = {scope} AND key = {key}
             """, ct);
    }

    private static string ReferenceOf(string? response)
    {
        if (response is null) return "still being saved";
        using var doc = JsonDocument.Parse(response);
        return doc.RootElement.TryGetProperty("reference", out var r) ? r.GetString() ?? "another booking" : "another booking";
    }
}
