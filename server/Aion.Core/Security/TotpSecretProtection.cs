using System.Security.Cryptography;
using System.Text;

namespace Aion.Core.Security;

/// <summary>
/// AES-256-GCM for stored TOTP secrets (the database holds ciphertext + tag and a 12-byte nonce). The key is
/// Aion:Totp:EncryptionKey (base64 of 32 random bytes), a key of its own as in addiboii/A2Core (26306c5).
/// Older servers derived the key from Aion:BridgeSecret, which world servers and the client bridge also hold: that
/// derived key is kept as a decrypt-only <em>legacy</em> key, so secrets stored under it still work and are encrypted
/// again under the dedicated key the next time they are used (<see cref="Decrypt(byte[], byte[], out bool)"/>).
/// </summary>
public sealed class TotpSecretProtection
{
    const int KeyBytes = 32, TagBytes = 16, NonceBytes = 12;
    readonly byte[] key;
    readonly byte[]? legacyKey;

    public TotpSecretProtection(byte[] keyBytes, byte[]? legacyKeyBytes = null)
    {
        if (keyBytes.Length != KeyBytes) throw new ArgumentException("Expected a 256-bit encryption key.");
        if (legacyKeyBytes is not null && legacyKeyBytes.Length != KeyBytes) throw new ArgumentException("Expected a 256-bit legacy key.");
        key = keyBytes.ToArray();
        legacyKey = legacyKeyBytes?.ToArray();
    }

    public TotpSecretProtection(string keyBase64) : this(Convert.FromBase64String(keyBase64)) { }

    /// <summary>True while secrets are encrypted under the key derived from Aion:BridgeSecret (no dedicated key set).</summary>
    public bool UsesBridgeDerivedKey { get; private init; }

    /// <summary>Parses a base64 key; null when the text is not base64 of exactly 32 bytes.</summary>
    public static byte[]? ParseKey(string? keyBase64)
    {
        if (string.IsNullOrWhiteSpace(keyBase64)) return null;
        var bytes = new byte[64];
        return Convert.TryFromBase64String(keyBase64.Trim(), bytes, out var written) && written == KeyBytes ? bytes[..KeyBytes] : null;
    }

    /// <summary>The key older servers used: SHA-256 of Aion:BridgeSecret (when it has at least 32 characters).</summary>
    public static byte[]? BridgeDerivedKey(string? bridgeSecret) =>
        bridgeSecret is { Length: >= 32 } ? SHA256.HashData(Encoding.UTF8.GetBytes(bridgeSecret)) : null;

    /// <summary>
    /// The protection for this configuration: the dedicated key, with the bridge-derived key as a decrypt-only fallback;
    /// without a dedicated key, the bridge-derived key alone (and <paramref name="warning"/> asks for a dedicated one);
    /// null when neither is set (two-step sign-in is unavailable). A dedicated key that is not base64 of 32 bytes throws.
    /// </summary>
    public static TotpSecretProtection? FromConfiguration(string? encryptionKeyBase64, string? bridgeSecret, out string? warning)
    {
        warning = null;
        var bridgeKey = BridgeDerivedKey(bridgeSecret);
        if (!string.IsNullOrWhiteSpace(encryptionKeyBase64))
        {
            var dedicated = ParseKey(encryptionKeyBase64)
                ?? throw new InvalidOperationException("Aion:Totp:EncryptionKey must be base64 of 32 random bytes (openssl rand -base64 32).");
            return new TotpSecretProtection(dedicated, bridgeKey is not null && !bridgeKey.AsSpan().SequenceEqual(dedicated) ? bridgeKey : null);
        }
        if (bridgeKey is null) return null;
        warning = "Aion:Totp:EncryptionKey is not set: two-step sign-in secrets are encrypted with a key derived from "
            + "Aion:BridgeSecret. Set a dedicated key (openssl rand -base64 32); stored secrets move to it as they are used.";
        return new TotpSecretProtection(bridgeKey) { UsesBridgeDerivedKey = true };
    }

    public (byte[] Ciphertext, byte[] Nonce) Encrypt(string secret)
    {
        var plain = Encoding.UTF8.GetBytes(secret);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var packed = new byte[plain.Length + TagBytes];
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plain, packed.AsSpan(0, plain.Length), packed.AsSpan(plain.Length));
        return (packed, nonce);
    }

    public string? Decrypt(byte[] ciphertext, byte[] nonce) => Decrypt(ciphertext, nonce, out _);

    /// <summary>The secret, or null; <paramref name="legacy"/> is true when only the legacy key opened it (store it again).</summary>
    public string? Decrypt(byte[] ciphertext, byte[] nonce, out bool legacy)
    {
        legacy = false;
        if (TryDecrypt(key, ciphertext, nonce) is { } secret) return secret;
        if (legacyKey is null || TryDecrypt(legacyKey, ciphertext, nonce) is not { } old) return null;
        legacy = true;
        return old;
    }

    static string? TryDecrypt(byte[] key, byte[] ciphertext, byte[] nonce)
    {
        if (ciphertext.Length <= TagBytes || nonce.Length != NonceBytes) return null;
        var plain = new byte[ciphertext.Length - TagBytes];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, ciphertext.AsSpan(0, plain.Length), ciphertext.AsSpan(plain.Length), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException) { return null; }
    }
}
