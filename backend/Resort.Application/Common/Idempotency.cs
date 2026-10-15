using System.Security.Cryptography;
using System.Text.Json;

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
}
