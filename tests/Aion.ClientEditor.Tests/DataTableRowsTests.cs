using System.Buffers.Binary;
using System.Text;
using AionClientEditor.Editing;
using AionClientEditor.Serialization;
using Blake3;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.GameTypes.Aion2.Encryption.Aes;
using CUE4Parse.GameTypes.Aion2.Objects;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using Xunit;

namespace Aion.ClientEditor.Tests;

public class DataTableRowsTests
{
    static readonly VersionContainer Versions = new(EGame.GAME_Aion2);

    [Fact]
    public void AppendRow_clones_row_sets_id_and_applies_edits()
    {
        var table = Table(Row(100, "N_Source", "STR_Source", 5));

        var added = DataTableRows.AppendRow(table, 2999001, 100, new Dictionary<string, string>
        {
            ["Name"] = "N_KISK_Test",
            ["Desc.Key"] = "STR_KISK_NPC_TEST_NAME",
        });

        Assert.Equal(ColumnIds, table.Ids); // one hash per column, not per row: unchanged
        Assert.Equal(2999001, DataTableRows.RowId(added));
        Assert.Equal("N_KISK_Test", DataTableRows.GetPath(added, "Name"));
        Assert.Equal("STR_KISK_NPC_TEST_NAME", DataTableRows.GetPath(added, "Desc.Key"));
        Assert.Equal("5", DataTableRows.GetPath(added, "Level"));
        // The source row, including its nested structs, is untouched.
        var source = DataTableRows.FindRow(table, 100)!;
        Assert.Equal("N_Source", DataTableRows.GetPath(source, "Name"));
        Assert.Equal("STR_Source", DataTableRows.GetPath(source, "Desc.Key"));
        Assert.Equal("100", DataTableRows.GetPath(source, "ID.Value"));

        // Serialized bytes equal a table that was built with both rows from the start.
        var expected = Table(Row(100, "N_Source", "STR_Source", 5), Row(2999001, "N_KISK_Test", "STR_KISK_NPC_TEST_NAME", 5));
        Assert.Equal(Serialize(expected), Serialize(table));
    }

    [Fact]
    public void AppendRow_refuses_duplicate_or_missing_ids()
    {
        var table = Table(Row(100, "N_Source", "STR_Source", 5));
        Assert.Throws<InvalidOperationException>(() => DataTableRows.AppendRow(table, 100, 100));
        Assert.Throws<KeyNotFoundException>(() => DataTableRows.AppendRow(table, 200, 999));
        Assert.Throws<KeyNotFoundException>(() =>
            DataTableRows.AppendRow(table, 200, 100, new Dictionary<string, string> { ["Missing"] = "1" }));
        Assert.Throws<FormatException>(() =>
            DataTableRows.AppendRow(table, 200, 100, new Dictionary<string, string> { ["Name.Key"] = "x" }));
        Assert.Single(DataTableRows.Rows(table));
    }

    [Fact]
    public void Encrypted_l10n_round_trips_through_reader()
    {
        const string path = "AION2/Content/Data/L10N/en-US/L10NString.dat";
        Aion2DatFileEncryption.AesKeys[Seed("L10NString_en-US")] =
            new FAesKey("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

        // Build an L10N document from the unencrypted (XORed) format, then write it as an encrypted container.
        using var ms = new MemoryStream();
        using (var w = new FAion2DatFileWriter(ms, Versions))
        {
            w.WriteL10NString("Ns");
            w.WriteMap(new Dictionary<string, string>
            {
                ["STR_KISK_NPC_TEST_NAME"] = "Kisk Tester",
                ["STR_KISK_NPC_TEST_LINE"] = "Grüße, Daeva! 안녕하세요",
                ["STR_EMPTY"] = "",
            }, w.WriteL10NString, w.WriteL10NString);
        }
        var provider = new DefaultFileProvider(Path.GetTempPath(), SearchOption.TopDirectoryOnly, Versions);
        var original = ClientDatSerializer.ReadL10N(ms.ToArray(), path, provider);
        Assert.Equal("Ns", original.Namespace); // 3 bytes with the null: stored without XOR
        Assert.Equal("Grüße, Daeva! 안녕하세요", original.Entries["STR_KISK_NPC_TEST_LINE"]);

        var encryptedHeader = new byte[8];
        encryptedHeader[0] = 2;
        var onDisk = ClientDatSerializer.SerializeOnDiskL10N(original, path, provider, encryptedHeader);
        Assert.Equal(2u, BitConverter.ToUInt32(onDisk, 0));

        var plain = Aion2DatFileEncryption.DecryptL10N(onDisk, path);
        Assert.Equal(ClientDatSerializer.SerializeEncryptedL10NPayload(original, Versions), plain);
        var round = ClientDatSerializer.ReadL10N(onDisk, path, provider);
        Assert.Equal("Ns", round.Namespace);
        Assert.Equal(original.Entries, round.Entries);
    }

    static readonly string[] ColumnIds = ["1858135510", "1575265074", "654954401", "1524168231"];

    static FAion2DataTableFile Table(params FStructFallback[] rows)
    {
        // A path outside Data/Table makes the reader skip parsing, leaving an empty table to fill in.
        var table = new FAion2DataTableFile(new MemoryGameFile("Synthetic/Npc.dat", []), null!);
        table.Version = 13;
        table.Ids = [.. ColumnIds];
        var array = new UScriptArray(
            rows.Select(r => (FPropertyTagType)new StructProperty(new FScriptStruct(r))).ToList(),
            "StructProperty", StructData("NpcTableItem"));
        table.Properties.Add(new FPropertyTag
        {
            Name = new FName("Data"),
            PropertyType = new FName("ArrayProperty"),
            TagData = new FPropertyTagData { Type = "ArrayProperty", InnerType = "StructProperty", InnerTypeData = StructData("NpcTableItem") },
            Tag = new ArrayProperty(array),
        });
        return table;
    }

    static FPropertyTagData StructData(string name) => new() { Type = "StructProperty", StructType = name };

    static FPropertyTag Struct(string name, string structType, params FPropertyTag[] fields) => new()
    {
        Name = new FName(name),
        PropertyType = new FName("StructProperty"),
        TagData = StructData(structType),
        Tag = new StructProperty(new FScriptStruct(new FStructFallback([.. fields]))),
    };

    static FStructFallback Row(int id, string name, string descKey, int level) => new(
    [
        Struct("ID", "TableItemId", new FPropertyTag { Name = new FName("Value"), PropertyType = new FName("IntProperty"), Tag = new IntProperty(id) }),
        new FPropertyTag { Name = new FName("Name"), PropertyType = new FName("NameProperty"), Tag = new NameProperty(new FName(name)) },
        Struct("Desc", "TableString", new FPropertyTag { Name = new FName("Key"), PropertyType = new FName("NameProperty"), Tag = new NameProperty(new FName(descKey)) }),
        new FPropertyTag { Name = new FName("Level"), PropertyType = new FName("IntProperty"), Tag = new IntProperty(level) },
    ]);

    static byte[] Serialize(FAion2DataTableFile table) =>
        ClientDatSerializer.SerializePlaintextTable(table, new TypeMappings(), Versions);

    static ulong Seed(string value)
    {
        using var hasher = Hasher.New();
        hasher.Update(Encoding.UTF8.GetBytes(value));
        return BinaryPrimitives.ReadUInt64LittleEndian(hasher.Finalize().AsSpan());
    }

    sealed class MemoryGameFile(string path, byte[] data) : GameFile(path, data.Length)
    {
        public override bool IsEncrypted => false;
        public override CompressionMethod CompressionMethod => CompressionMethod.None;
        public override byte[] Read(FByteBulkDataHeader? header = null) => data;
        public override FArchive CreateReader(FByteBulkDataHeader? header = null) => new FByteArchive(Path, data, null);
    }
}
