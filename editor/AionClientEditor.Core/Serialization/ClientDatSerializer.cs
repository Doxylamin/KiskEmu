using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.GameTypes.Aion2.Encryption.Aes;
using CUE4Parse.GameTypes.Aion2.Objects;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;

namespace AionClientEditor.Serialization;

public static class ClientDatSerializer
{
    public static byte[] SerializePlaintextTable(FAion2DataTableFile table, IFileProvider provider, bool unicodeNames = false,
        bool? unicodeIds = null)
    {
        var mappings = provider.MappingsContainer?.MappingsForGame
            ?? throw new InvalidOperationException("Usmap mappings are required to serialize tables");
        return SerializePlaintextTable(table, mappings, provider.Versions, unicodeNames, unicodeIds);
    }

    public static byte[] SerializePlaintextTable(FAion2DataTableFile table, TypeMappings mappings, VersionContainer versions,
        bool unicodeNames = false, bool? unicodeIds = null)
    {
        using var stream = new MemoryStream();
        using var writer = new FAion2DatFileWriter(stream, versions) { UnicodeNames = unicodeNames };
        writer.Write(table.Version);
        var ids = string.Join(",", table.Ids);
        if ((unicodeIds ?? unicodeNames) && ids.Length > 1) writer.WriteUnicodeAion2String(ids);
        else writer.WriteAion2String(ids);
        if (table.Properties.Count != 1) throw new InvalidOperationException("Expected a single Data property");
        // FAion2DataTableFile reads the top-level row array without a struct tag (readtag: false).
        var data = table.Properties[0];
        FAion2PropertyWriter.WritePropertyTagType(writer, data.Tag!, mappings, data.PropertyType.Text, data.TagData,
            writeStructTag: false, ReadType.ARRAY);
        return stream.ToArray();
    }

    public static byte[] SerializeOnDiskTable(FAion2DataTableFile table, string virtualPath, IFileProvider provider, byte[]? originalOnDisk = null)
    {
        if (originalOnDisk is null || originalOnDisk.Length < 8 || BitConverter.ToUInt32(originalOnDisk, 0) != 13)
            return SerializePlaintextTable(table, provider);
        Aion2DatFileEncryption.Initialize(provider);
        var original = Aion2DatFileEncryption.DecryptDataTable(originalOnDisk, virtualPath);
        var plaintext = SerializePlaintextTable(table, provider, UsesUnicodeNames(original), UsesUnicodeIds(original));
        return Aion2DatFileEncryption.EncryptDataTable(plaintext, virtualPath);
    }

    /// <summary>True when a plaintext table stores its id string as UTF-16 (negative length).</summary>
    public static bool UsesUnicodeIds(byte[] plaintextTable) =>
        plaintextTable.Length >= 8 && BitConverter.ToInt32(plaintextTable, 4) < 0;

    /// <summary>
    /// True when a plaintext table stores its names as UTF-16 (negative length). Clients before 2.0.11 stored the id string
    /// the same way as the names; 2.0.11 keeps the id string single-byte while its names are UTF-16 (OBSERVED in NpcData
    /// and String). After the id string come the row count and the first row: its name (tables keyed by name, String) or
    /// its numeric id and then its name (NpcData), so either of the next two ints is a negative length.
    /// </summary>
    public static bool UsesUnicodeNames(byte[] plaintextTable)
    {
        if (plaintextTable.Length < 8) return false;
        var idLength = BitConverter.ToInt32(plaintextTable, 4);
        if (idLength < 0) return true;
        var rows = 8L + idLength + 4;
        bool Negative(long at) => at + 4 <= plaintextTable.Length && BitConverter.ToInt32(plaintextTable, (int) at) < 0;
        return Negative(rows) || Negative(rows + 4);
    }

    public static byte[] SerializePlaintextL10N(FAion2L10NFile file, VersionContainer versions)
    {
        using var stream = new MemoryStream();
        using var writer = new FAion2DatFileWriter(stream, versions);
        writer.WriteL10NString(file.Namespace);
        writer.WriteMap(file.Entries, writer.WriteL10NString, writer.WriteL10NString);
        return stream.ToArray();
    }

    public static byte[] SerializeOnDiskL10N(FAion2L10NFile file, string virtualPath, IFileProvider provider, byte[]? originalOnDisk = null)
    {
        if (originalOnDisk is null || originalOnDisk.Length < 8 || BitConverter.ToUInt32(originalOnDisk, 0) != 2)
            return SerializePlaintextL10N(file, provider.Versions);
        Aion2DatFileEncryption.Initialize(provider);
        return Aion2DatFileEncryption.EncryptL10N(SerializeEncryptedL10NPayload(file, provider.Versions), virtualPath);
    }

    /// <summary>
    /// Plaintext inside an AES-encrypted (header version 2) L10N file: int32 1, then plain Unreal FStrings. Unlike the
    /// unencrypted format these strings carry no XOR (<c>FAion2L10NFile</c> reads them with <c>ReadFString</c>).
    /// </summary>
    public static byte[] SerializeEncryptedL10NPayload(FAion2L10NFile file, VersionContainer versions)
    {
        using var inner = new MemoryStream();
        using (var writer = new FAion2DatFileWriter(inner, versions))
        {
            writer.Write(1);
            writer.WriteUnrealFString(file.Namespace);
            writer.WriteMap(file.Entries, writer.WriteUnrealFString, writer.WriteUnrealFString);
            // OBSERVED: client files end with one more int32 0 (an empty trailing collection the reader ignores).
            writer.Write(0);
        }
        return inner.ToArray();
    }

    public static byte[] SerializeMapHierarchy(FAion2MapHierarchyFile file)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(file.Version);
        writer.Write(file.Worlds.Length);
        foreach (var world in file.Worlds) // ReadArray layout: count then elements
        {
            writer.Write(world.Id);
            writer.Write((byte)FAion2MapHierarchyFile.EAionMapDataHierarchy.Map);
            writer.Write(world.Id);
            writer.Write(world.SubZones.Length);
            foreach (var sub in world.SubZones)
            {
                writer.Write((byte)FAion2MapHierarchyFile.EAionMapDataHierarchy.Subzone);
                writer.Write(sub.Id);
                writer.Write(sub.NPCTypes.Length);
                foreach (var npcType in sub.NPCTypes)
                {
                    writer.Write((byte)FAion2MapHierarchyFile.EAionMapDataHierarchy.NPCType);
                    writer.Write((int)npcType.Id);
                    writer.Write(npcType.NPCs.Length);
                    foreach (var npc in npcType.NPCs)
                    {
                        writer.Write((byte)FAion2MapHierarchyFile.EAionMapDataHierarchy.NPC);
                        writer.Write(npc.Id);
                        writer.Write(npc.Items.Length);
                        foreach (var item in npc.Items)
                        {
                            writer.Write((byte)FAion2MapHierarchyFile.EAionMapDataHierarchy.Item);
                            writer.Write(item.Id);
                            writer.Write(0);
                        }
                    }
                }
            }
        }
        return stream.ToArray();
    }

    public static FAion2DataTableFile ReadPlaintextTable(byte[] data, string virtualPath, IFileProvider provider) =>
        ReadTable(data, virtualPath, provider, encrypted: false);

    /// <summary>
    /// Reads a table. On-disk (<paramref name="encrypted"/>) bytes go straight to <c>FAion2DataTableFile</c>, which
    /// decrypts them itself: client tables are version 13, the same value as the encrypted-container magic, so
    /// decrypting here first would make the constructor try to decrypt the plaintext again.
    /// Plaintext input is only safe for tables whose version is not 13.
    /// </summary>
    public static FAion2DataTableFile ReadTable(byte[] data, string virtualPath, IFileProvider provider, bool encrypted)
    {
        if (!encrypted && data.Length >= 4 && BitConverter.ToUInt32(data, 0) == 13)
            throw new NotSupportedException("Plaintext tables with version 13 cannot be told apart from encrypted ones");
        return new FAion2DataTableFile(new FakeGameFile(virtualPath, data), provider);
    }

    public static FAion2L10NFile ReadL10N(byte[] data, string virtualPath, IFileProvider provider) =>
        new(new FakeGameFile(virtualPath, data), provider);

    sealed class FakeGameFile : GameFile
    {
        readonly byte[] _data;

        public FakeGameFile(string path, byte[] data) : base(path.Replace('\\', '/'), data.Length) => _data = data;

        public override bool IsEncrypted => false;
        public override CompressionMethod CompressionMethod => CompressionMethod.None;
        public override byte[] Read(FByteBulkDataHeader? header = null) => _data;
        public override FArchive CreateReader(FByteBulkDataHeader? header = null) => new FByteArchive(Path, _data, null);
    }
}
