using System.Text;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;

namespace AionClientEditor.Serialization;

/// <summary>Binary writer mirroring <see cref="CUE4Parse.GameTypes.Aion2.Objects.FAion2DatFileArchive"/> string rules.</summary>
public sealed class FAion2DatFileWriter(Stream stream, VersionContainer versions) : BinaryWriter(stream, Encoding.UTF8, leaveOpen: true)
{
    private static readonly byte[] XorKeyLong = [0x25, 0x00, 0xa8, 0x00, 0x7e, 0x00, 0x91, 0x00];
    private static readonly byte[] XorKeyShort = [0x25, 0xa8, 0x7e, 0x91];

    public VersionContainer Versions { get; } = versions;

    /// <summary>
    /// Write names (table ids, NameProperty, EnumProperty) as XORed UTF-16 like the client's own tables do
    /// (OBSERVED: NpcData's id string has a negative length). Off writes UTF-8, which the reader also accepts.
    /// </summary>
    public bool UnicodeNames { get; init; }

    /// <summary>A name-like string as <c>FAion2DatFileArchive.ReadFName</c> reads it.</summary>
    public void WriteName(string value)
    {
        // OBSERVED in NpcTalk: a one-character name ("A") is stored as ANSI, longer ones as XORed UTF-16.
        if (UnicodeNames && value.Length > 1) WriteUnicodeAion2String(value);
        else WriteAion2String(value);
    }

    public void WriteAion2String(string value, bool l10n = false)
    {
        if (value.Length == 0)
        {
            Write(0);
            return;
        }

        var xorKey = l10n ? XorKeyShort : null;
        var utf8 = Encoding.UTF8.GetBytes(value + "\0");
        var buffer = utf8.AsSpan().ToArray();
        // FAion2DatFileArchive.DecryptData leaves buffers of 4 bytes or fewer (null included) un-XORed.
        if (xorKey is not null && buffer.Length > 4) TensorUtils.Xor(buffer, xorKey);
        Write(buffer.Length);
        Write(buffer);
    }

    public void WriteUnencryptedFString(string value)
    {
        if (value.Length == 0)
        {
            Write(0);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value + "\0");
        Write(bytes.Length);
        Write(bytes);
    }

    public void WriteUnicodeAion2String(string value)
    {
        if (value.Length == 0)
        {
            Write(0);
            return;
        }

        var bytes = Encoding.Unicode.GetBytes(value + "\0");
        // The client XORs the characters only; the UTF-16 null terminator stays 00 00 (OBSERVED in NpcData).
        TensorUtils.Xor(bytes.AsSpan(0, bytes.Length - 2), XorKeyLong);
        Write(-bytes.Length / 2);
        Write(bytes);
    }

    public void WriteL10NString(string value) => WriteAion2String(value, l10n: true);

    /// <summary>
    /// Standard Unreal FString (no AION2 XOR): ANSI with a positive length when every char is 7-bit, otherwise UTF-16
    /// with a negative length. Used inside AES-encrypted L10N files, which CUE4Parse reads with <c>ReadFString</c>.
    /// </summary>
    public void WriteUnrealFString(string value)
    {
        if (value.Length == 0)
        {
            Write(0);
            return;
        }

        if (value.All(c => c < 0x80))
        {
            Write(value.Length + 1);
            Write(Encoding.ASCII.GetBytes(value));
            Write((byte)0);
            return;
        }

        Write(-(value.Length + 1));
        Write(Encoding.Unicode.GetBytes(value));
        Write((ushort)0);
    }

    public void WriteMap<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> map, Action<TKey> writeKey, Action<TValue> writeValue)
        where TKey : notnull
    {
        Write(map.Count);
        foreach (var pair in map)
        {
            writeKey(pair.Key);
            writeValue(pair.Value);
        }
    }
}
