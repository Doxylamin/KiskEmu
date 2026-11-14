using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Aion.Core.Security;

/// <summary>
/// Two-step sign-in recovery codes: 8 random codes of 10 upper-case hex digits (40 bits each), stored as the upper-case hex
/// SHA-256 of the code. A typed code is accepted in any letter case and with spaces, tabs or dashes anywhere in it (a phone
/// keyboard's en dash, a copied non-breaking space). After addiboii/A2Core (e5e8d2b); the stored form is unchanged, so
/// codes issued earlier keep working.
/// </summary>
public static class RecoveryCodes
{
    public const int Length = 10;
    public const int Count = 8;
    /// <summary>Longest input looked at (a code with generous spacing); longer input is refused unread.</summary>
    public const int MaxInput = 64;

    public static string Generate() => Convert.ToHexString(RandomNumberGenerator.GetBytes(Length / 2));

    /// <summary>New codes: the plain codes shown once, and their stored hashes.</summary>
    public static (string[] Plain, string[] Hashed) GenerateSet()
    {
        var plain = Enumerable.Range(0, Count).Select(_ => Generate()).ToArray();
        return (plain, plain.Select(c => Hash(c)!).ToArray());
    }

    /// <summary>The code's 10 upper-case hex digits, or null when the input is not a recovery code.</summary>
    public static string? Normalize(string? input)
    {
        if (input is null || input.Length > MaxInput) return null;
        var digits = new StringBuilder(Length);
        foreach (var c in input)
        {
            if (char.IsWhiteSpace(c) || IsDash(c)) continue;
            if (!char.IsAsciiHexDigit(c) || digits.Length == Length) return null;
            digits.Append(char.ToUpperInvariant(c));
        }
        return digits.Length == Length ? digits.ToString() : null;
    }

    /// <summary>The stored form: upper-case hex SHA-256 of the normalized code, or null when the input is not a code.</summary>
    public static string? Hash(string? input) =>
        Normalize(input) is { } code ? Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(code))) : null;

    /// <summary>
    /// The index of the stored hash <paramref name="input"/> matches, or -1. Every stored hash is compared in constant time,
    /// with no early exit on a match.
    /// </summary>
    public static int Find(IReadOnlyList<string> storedHashes, string? input)
    {
        if (Hash(input) is not { } digest) return -1;
        var given = Encoding.ASCII.GetBytes(digest);
        var found = -1;
        for (var i = 0; i < storedHashes.Count; i++)
        {
            var equal = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(storedHashes[i]), given);
            if (equal && found < 0) found = i;
        }
        return found;
    }

    static bool IsDash(char c) => c == '-' || c == '−' || char.GetUnicodeCategory(c) == UnicodeCategory.DashPunctuation;
}
