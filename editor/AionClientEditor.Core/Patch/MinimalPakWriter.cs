using System.Security.Cryptography;
using System.Text;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.Versions;

namespace AionClientEditor.Patch;

/// <summary>
/// Writes an Unreal pak in the layout the AION2 client's own paks use (OBSERVED from the client's trailers):
/// pak version 11 (Fnv64BugFix), encrypted index, encryption key GUID all zeros. Entries are stored uncompressed and
/// AES-256-ECB encrypted (zero-padded to 16 bytes). Every entry is written as a non-encoded FPakEntry, and both the
/// path-hash index and the full directory index are present because CUE4Parse and Unreal expect them for v10+.
/// </summary>
public static class MinimalPakWriter
{
    public sealed record Entry(string Path, byte[] Bytes);

    public const int PakVersion = 11;
    const uint PakMagic = 0x5A6F12E1;
    const int CompressionMethodNameLength = 32;
    const int CompressionMethodSlots = 5;
    const byte EntryFlagEncrypted = 0x01;

    /// <param name="mountPoint">Mount point, for example "../../../Aion2/"; entry paths are relative to it.</param>
    public static void Write(string outputPath, string mountPoint, IReadOnlyList<Entry> entries, FAesKey aesKey, EGame game)
    {
        _ = game;
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) throw new ArgumentException("A pak needs at least one entry", nameof(entries));
        if (!mountPoint.EndsWith('/')) mountPoint += "/";

        var ordered = entries
            .Select(e => e with { Path = NormalizePath(e.Path) })
            .ToList();
        if (ordered.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ordered.Count)
            throw new ArgumentException("Duplicate entry paths", nameof(entries));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using var fs = File.Create(outputPath);
        using var w = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: true);

        var records = new List<EntryRecord>(ordered.Count);
        foreach (var entry in ordered)
        {
            var padded = PadTo16(entry.Bytes);
            var hash = SHA1.HashData(padded);
            var record = new EntryRecord(entry.Path, fs.Position, entry.Bytes.LongLength, hash);
            // Unreal writes a copy of the entry (with Offset 0) in front of each file's data.
            WriteEntry(w, record with { Offset = 0 });
            Encrypt(padded, aesKey);
            w.Write(padded);
            records.Add(record);
        }

        var directoryIndex = BuildDirectoryIndex(records);
        var pathHashSeed = PathHashSeedFor(Path.GetFileName(outputPath));
        var pathHashIndex = BuildPathHashIndex(records, pathHashSeed, directoryIndex);

        var indexOffset = fs.Position;
        var primary = BuildPrimaryIndex(mountPoint, records, pathHashSeed,
            pathHashIndexOffset: 0, pathHashIndexSize: 0, pathHashIndexHash: [],
            directoryIndexOffset: 0, directoryIndexSize: 0, directoryIndexHash: []);
        // The primary index holds the offsets of the two secondary blocks that follow it, so its size must be known
        // first; the layout is fixed-size, so a dry run with zero offsets gives the exact length.
        var primaryPaddedLength = Align16(primary.Length);

        var pathHashPadded = PadTo16(pathHashIndex);
        var dirPadded = PadTo16(directoryIndex);
        var pathHashOffset = indexOffset + primaryPaddedLength;
        var dirOffset = pathHashOffset + pathHashPadded.Length;

        primary = BuildPrimaryIndex(mountPoint, records, pathHashSeed,
            pathHashOffset, pathHashPadded.Length, SHA1.HashData(pathHashPadded),
            dirOffset, dirPadded.Length, SHA1.HashData(dirPadded));
        var primaryPadded = PadTo16(primary);
        if (primaryPadded.Length != primaryPaddedLength)
            throw new InvalidOperationException("Primary pak index changed size between passes");
        var primaryHash = SHA1.HashData(primaryPadded);

        Encrypt(primaryPadded, aesKey);
        Encrypt(pathHashPadded, aesKey);
        Encrypt(dirPadded, aesKey);
        w.Write(primaryPadded);
        w.Write(pathHashPadded);
        w.Write(dirPadded);

        // FPakInfo (v11): key GUID, encrypted-index flag, magic, version, index offset/size/hash, compression names.
        w.Write(new byte[16]);
        w.Write((byte)1);
        w.Write(PakMagic);
        w.Write(PakVersion);
        w.Write(indexOffset);
        w.Write((long)primaryPadded.Length);
        w.Write(primaryHash);
        w.Write(new byte[CompressionMethodNameLength * CompressionMethodSlots]);
    }

    sealed record EntryRecord(string Path, long Offset, long Size, byte[] Hash);

    static void WriteEntry(BinaryWriter w, EntryRecord e)
    {
        w.Write(e.Offset);
        w.Write(e.Size); // compressed size
        w.Write(e.Size); // uncompressed size
        w.Write(0); // compression method index: none
        w.Write(e.Hash);
        w.Write(EntryFlagEncrypted);
        w.Write(0u); // compression block size
    }

    static byte[] BuildPrimaryIndex(string mountPoint, IReadOnlyList<EntryRecord> records, ulong pathHashSeed,
        long pathHashIndexOffset, long pathHashIndexSize, byte[] pathHashIndexHash,
        long directoryIndexOffset, long directoryIndexSize, byte[] directoryIndexHash)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII);
        WriteFString(w, mountPoint);
        w.Write(records.Count);
        w.Write(pathHashSeed);

        w.Write(1); // bHasPathHashIndex
        w.Write(pathHashIndexOffset);
        w.Write(pathHashIndexSize);
        w.Write(PadHash(pathHashIndexHash));

        w.Write(1); // bHasFullDirectoryIndex
        w.Write(directoryIndexOffset);
        w.Write(directoryIndexSize);
        w.Write(PadHash(directoryIndexHash));

        w.Write(0); // encoded pak entries: none, all entries are stored non-encoded below
        w.Write(records.Count);
        foreach (var record in records) WriteEntry(w, record);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>TMap&lt;FString dir, TMap&lt;FString file, int32 location&gt;&gt; with parent directories, as Unreal writes it.</summary>
    static byte[] BuildDirectoryIndex(IReadOnlyList<EntryRecord> records)
    {
        var dirs = new SortedDictionary<string, List<(string Name, int Location)>>(StringComparer.Ordinal) { ["/"] = [] };
        for (var i = 0; i < records.Count; i++)
        {
            var path = records[i].Path;
            var slash = path.LastIndexOf('/');
            var dir = slash < 0 ? "/" : path[..(slash + 1)];
            var name = slash < 0 ? path : path[(slash + 1)..];
            for (var parent = dir; parent != "/"; parent = ParentDirectory(parent))
                if (!dirs.ContainsKey(parent)) dirs[parent] = [];
            dirs[dir].Add((name, -i - 1));
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII);
        w.Write(dirs.Count);
        foreach (var (dir, files) in dirs)
        {
            WriteFString(w, dir);
            w.Write(files.Count);
            foreach (var (name, location) in files)
            {
                WriteFString(w, name);
                w.Write(location);
            }
        }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>TMap&lt;uint64 pathHash, int32 location&gt; followed by the pruned directory index (here: the full one).</summary>
    static byte[] BuildPathHashIndex(IReadOnlyList<EntryRecord> records, ulong seed, byte[] directoryIndex)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII);
        w.Write(records.Count);
        for (var i = 0; i < records.Count; i++)
        {
            w.Write(PathHash(records[i].Path, seed));
            w.Write(-i - 1);
        }
        w.Write(directoryIndex);
        w.Flush();
        return ms.ToArray();
    }

    static string ParentDirectory(string dir)
    {
        var trimmed = dir.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? "/" : trimmed[..(slash + 1)];
    }

    /// <summary>FNV-1a 64 over the lower-cased UTF-16 path relative to the mount point (pak version 11 and later).</summary>
    internal static ulong PathHash(string relativePath, ulong seed)
    {
        var hash = 0xcbf29ce484222325UL + seed;
        foreach (var b in Encoding.Unicode.GetBytes(relativePath.ToLowerInvariant()))
        {
            hash ^= b;
            hash *= 0x00000100000001b3UL;
        }
        return hash;
    }

    static ulong PathHashSeedFor(string fileName)
    {
        // Unreal derives the seed from the pak file name; any value works because readers take it from the index.
        var hash = PathHash(fileName, 0);
        return (uint)(hash ^ (hash >> 32));
    }

    static void WriteFString(BinaryWriter w, string value)
    {
        foreach (var c in value)
            if (c > 0x7f) throw new ArgumentException($"Pak paths must be ASCII: {value}");
        w.Write(value.Length + 1);
        w.Write(Encoding.ASCII.GetBytes(value));
        w.Write((byte)0);
    }

    static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0 || normalized.EndsWith('/'))
            throw new ArgumentException($"Invalid pak entry path: '{path}'");
        return normalized;
    }

    static byte[] PadHash(byte[] hash) => hash.Length == 20 ? hash : new byte[20];

    static int Align16(int length) => (length + 15) & ~15;

    static byte[] PadTo16(byte[] data)
    {
        var padded = new byte[Align16(data.Length)];
        Buffer.BlockCopy(data, 0, padded, 0, data.Length);
        return padded;
    }

    static void Encrypt(byte[] data, FAesKey key)
    {
        if (data.Length % 16 != 0) throw new ArgumentException("AES data must be 16-byte aligned");
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key.Key;
        aes.EncryptEcb(data, data, PaddingMode.None);
    }
}
