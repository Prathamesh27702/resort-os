using System.Text;

namespace Resort.Application.Common;

/// <summary>
/// Field-level encryption for sensitive values (guest ID numbers, document details, ID scans).
/// AES-256-GCM with a key that lives outside the database (configuration / secret store). Each ciphertext names
/// the key that made it, so keys can be rotated: new data uses the active key, old data stays readable with the
/// previous keys until it is re-encrypted. Design and rotation steps: docs/SECURITY.md → Field encryption.
/// </summary>
public interface IFieldEncryptor
{
    /// <summary>Id of the key used for new ciphertexts (stored next to the data, e.g. guest_documents.key_id).</summary>
    string ActiveKeyId { get; }

    /// <summary>Encrypts <paramref name="plaintext"/>. <paramref name="context"/> is bound into the ciphertext as
    /// associated data (e.g. "guest_document:{id}:number"), so a value copied to another row won't decrypt.</summary>
    byte[] Encrypt(ReadOnlySpan<byte> plaintext, string context);

    /// <summary>Decrypts an envelope made by <see cref="Encrypt"/>; throws if it was tampered with or the context differs.</summary>
    byte[] Decrypt(byte[] envelope, string context);

    /// <summary>Keyed hash (HMAC-SHA256) for equality lookups on encrypted data, e.g. "is this passport on another profile?".</summary>
    byte[] Hash(string value, string purpose);
}

public static class FieldEncryptorExtensions
{
    public static byte[] EncryptString(this IFieldEncryptor e, string value, string context) =>
        e.Encrypt(Encoding.UTF8.GetBytes(value), context);

    public static string DecryptString(this IFieldEncryptor e, byte[] envelope, string context) =>
        Encoding.UTF8.GetString(e.Decrypt(envelope, context));
}

/// <summary>Blob storage for files (ID scans now; photos and PDFs later). Local disk in development; cloud blob
/// storage in production (Sprint 29). Keys are opaque paths chosen by the caller, never user input.</summary>
public interface IFileStore
{
    Task SaveAsync(string key, byte[] content, CancellationToken ct);
    /// <summary>Null if the file doesn't exist.</summary>
    Task<byte[]?> ReadAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
}
