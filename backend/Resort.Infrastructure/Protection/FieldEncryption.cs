using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Resort.Application.Common;

namespace Resort.Infrastructure.Protection;

/// <summary>
/// Encryption:Key (base64, 32 bytes) is the active key, id Encryption:ActiveKeyId (default "k1").
/// Encryption:PreviousKeys:{id} (base64) keeps older keys readable after a rotation.
/// Set via environment variables (Encryption__Key) or a secret store, never in a committed file.
/// </summary>
public sealed class EncryptionOptions
{
    public const string Section = "Encryption";
    public string Key { get; set; } = "";
    public string ActiveKeyId { get; set; } = "k1";
    public Dictionary<string, string> PreviousKeys { get; set; } = [];
}

/// <summary>
/// AES-256-GCM. Envelope layout: [1: version=1][1: key-id length][key id ASCII][12: nonce][ciphertext][16: tag].
/// The data key and the hashing key are derived from the configured key with HKDF-SHA256 (separate purposes never
/// share a key). Associated data = the caller's context string, binding each ciphertext to its row and field.
/// </summary>
public sealed class AesGcmFieldEncryptor : IFieldEncryptor
{
    private const byte EnvelopeVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly Dictionary<string, byte[]> _dataKeys = new(StringComparer.Ordinal);
    private readonly byte[] _hashKey;

    public string ActiveKeyId { get; }

    public AesGcmFieldEncryptor(IOptions<EncryptionOptions> options)
    {
        var o = options.Value;
        ActiveKeyId = string.IsNullOrWhiteSpace(o.ActiveKeyId) ? "k1" : o.ActiveKeyId.Trim();
        if (ActiveKeyId.Length > 32 || !ActiveKeyId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new InvalidOperationException("Encryption:ActiveKeyId must be 1–32 letters, digits, '-' or '_'.");

        var active = ParseKey(o.Key, "Encryption:Key (Encryption__Key)");
        _dataKeys[ActiveKeyId] = Derive(active, "resort-os/field-encryption/v1");
        // The hash key comes from the active key, so lookups keep working for data hashed after a rotation;
        // the rotation job recomputes stored hashes (docs/SECURITY.md).
        _hashKey = Derive(active, "resort-os/lookup-hash/v1");
        foreach (var (id, b64) in o.PreviousKeys)
            if (!string.Equals(id, ActiveKeyId, StringComparison.Ordinal))
                _dataKeys[id] = Derive(ParseKey(b64, $"Encryption:PreviousKeys:{id}"), "resort-os/field-encryption/v1");
    }

    private static byte[] ParseKey(string? b64, string name)
    {
        if (string.IsNullOrWhiteSpace(b64))
            throw new InvalidOperationException($"{name} is not set. Run scripts/setup-dev (writes it to .env) or set it in the host's secret store.");
        byte[] key;
        try { key = Convert.FromBase64String(b64.Trim()); }
        catch (FormatException) { throw new InvalidOperationException($"{name} must be base64."); }
        if (key.Length != 32) throw new InvalidOperationException($"{name} must be 32 bytes (base64 of 32 random bytes).");
        return key;
    }

    private static byte[] Derive(byte[] ikm, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt: [], info: Encoding.ASCII.GetBytes(info));

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, string context)
    {
        var id = Encoding.ASCII.GetBytes(ActiveKeyId);
        var header = 2 + id.Length;
        var envelope = new byte[header + NonceSize + plaintext.Length + TagSize];
        envelope[0] = EnvelopeVersion;
        envelope[1] = (byte)id.Length;
        id.CopyTo(envelope, 2);
        var nonce = envelope.AsSpan(header, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_dataKeys[ActiveKeyId], TagSize);
        aes.Encrypt(nonce, plaintext,
            envelope.AsSpan(header + NonceSize, plaintext.Length),
            envelope.AsSpan(header + NonceSize + plaintext.Length, TagSize),
            Encoding.UTF8.GetBytes(context));
        return envelope;
    }

    public byte[] Decrypt(byte[] envelope, string context)
    {
        if (envelope.Length < 2 || envelope[0] != EnvelopeVersion)
            throw new CryptographicException("Unknown encryption envelope.");
        var idLen = envelope[1];
        var header = 2 + idLen;
        if (envelope.Length < header + NonceSize + TagSize)
            throw new CryptographicException("Encryption envelope is truncated.");
        var id = Encoding.ASCII.GetString(envelope, 2, idLen);
        if (!_dataKeys.TryGetValue(id, out var key))
            throw new CryptographicException($"Encryption key '{id}' is not configured (Encryption:PreviousKeys:{id}).");
        var ctLen = envelope.Length - header - NonceSize - TagSize;
        var plaintext = new byte[ctLen];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(envelope.AsSpan(header, NonceSize),
            envelope.AsSpan(header + NonceSize, ctLen),
            envelope.AsSpan(header + NonceSize + ctLen, TagSize),
            plaintext,
            Encoding.UTF8.GetBytes(context));
        return plaintext;
    }

    public byte[] Hash(string value, string purpose) =>
        HMACSHA256.HashData(_hashKey, Encoding.UTF8.GetBytes(purpose + "\n" + value));
}
