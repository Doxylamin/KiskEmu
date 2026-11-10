using AionClientEditor.CustomNpcs;
using AionClientEditor.Editing;
using AionClientEditor.Serialization;
using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.GameTypes.Aion2.Objects;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using Xunit;

namespace Aion.ClientEditor.Tests;

public class CustomNpcPatchTests
{
    static readonly VersionContainer Versions = new(EGame.GAME_Aion2);
    const string EnPath = "AION2/Content/Data/Table/L10N/en-US/L10NString.dat";
    const string DePath = "AION2/Content/L10N/Text/de-DE/L10NString.dat";

    static CustomNpcDefinitions Definitions() => CustomNpcDefinitions.Parse("""
        {
          "npcs": [{
            "slug": "Tester", "templateId": 2999001, "baseTemplateId": 2931044,
            "name": { "en-US": "Kisk Tester", "de-DE": "Kisk-Prüfer" },
            "talks": [{ "key": "Hello", "lines": [ { "en-US": "Line one" }, { "en-US": "Line two", "de-DE": "Zeile zwei" } ] }]
          }]
        }
        """);

    [Fact]
    public void Apply_adds_npc_string_talk_rows_and_texts()
    {
        var (npcData, npcTalk, strings, l10n) = Tables();

        var summary = CustomNpcPatch.Apply(Definitions(), npcData, npcTalk, strings, l10n);

        var npc = DataTableRows.FindRow(npcData, 2999001)!;
        Assert.Equal("N_KISK_Tester", DataTableRows.GetPath(npc, "Name"));
        Assert.Equal("STR_KISK_NPC_Tester", DataTableRows.GetPath(npc, "Desc.Key"));
        Assert.Equal("NPC_M_Mercenary_01", DataTableRows.GetPath(npc, "ResourceKey")); // look copied from the base

        var name = DataTableRows.FindRowByName(strings, "STR_KISK_NPC_Tester")!;
        Assert.Equal("String_STR_KISK_NPC_Tester_body", DataTableRows.GetPath(name, "Body.Key"));

        var lines = DataTableRows.Rows(npcTalk).Where(r => DataTableRows.GetPath(r, "Name") == "KISK_Npc_Tester_Hello").ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(["1", "2"], lines.Select(r => DataTableRows.GetPath(r, "Order")));
        Assert.All(lines, r => Assert.Equal("N_KISK_Tester", DataTableRows.GetPath(r, "SpeakerValue")));
        Assert.All(lines, r => Assert.Equal("ENpcTalkSpeakerType::Npc", DataTableRows.GetPath(r, "SpeakerType")));
        Assert.All(lines, r => Assert.Equal("None", DataTableRows.GetPath(r, "CvRes")));
        Assert.Equal("NpcTalk_KISK_Tester_Hello_2_cv_text", DataTableRows.GetPath(lines[1], "ChatDescription.Key"));
        Assert.Equal(CustomNpcPatch.TalkRowIdBase + 1, DataTableRows.RowId(lines[1]));

        Assert.Equal("Kisk Tester", l10n[EnPath].Entries["String_STR_KISK_NPC_Tester_body"]);
        Assert.Equal("Kisk-Prüfer", l10n[DePath].Entries["String_STR_KISK_NPC_Tester_body"]);
        Assert.Equal("Line one", l10n[DePath].Entries["NpcTalk_KISK_Tester_Hello_1_cv_text"]); // falls back to en-US
        Assert.Equal("Zeile zwei", l10n[DePath].Entries["NpcTalk_KISK_Tester_Hello_2_cv_text"]);

        var only = Assert.Single(summary.Npcs);
        Assert.Equal("KISK_Npc_Tester_Hello", Assert.Single(only.Talks).TalkName);
        // The official template rows are unchanged.
        Assert.Equal("HQ1101010_00_Accept", DataTableRows.GetPath(DataTableRows.FindRow(npcTalk, 110101001)!, "Name"));
        Assert.Equal("N_L0_Q_GuardSylvar_01", DataTableRows.GetPath(DataTableRows.FindRow(npcData, 2931044)!, "Name"));
    }

    [Fact]
    public void Validate_rejects_bad_definitions()
    {
        var defs = Definitions();
        defs.Npcs[0].TemplateId = 2990928; // official range
        defs.Npcs[0].Slug = "bad slug";
        defs.Npcs[0].Name.Remove("en-US");
        defs.Npcs[0].Talks[0].Lines.Clear();
        defs.Npcs.Add(new CustomNpcDefinition { Slug = "Other", TemplateId = 2999002, BaseTemplateId = 2999003, Name = { ["en-US"] = "x" } });

        var errors = CustomNpcPatch.Validate(defs);
        Assert.Contains(errors, e => e.Contains("templateId must be"));
        Assert.Contains(errors, e => e.Contains("slug must be"));
        Assert.Contains(errors, e => e.Contains("en-US text is required"));
        Assert.Contains(errors, e => e.Contains("needs 1-"));
        Assert.Contains(errors, e => e.Contains("baseTemplateId must be an official"));

        var (npcData, npcTalk, strings, l10n) = Tables();
        Assert.Throws<InvalidOperationException>(() => CustomNpcPatch.Apply(defs, npcData, npcTalk, strings, l10n));
        Assert.Null(DataTableRows.FindRow(npcData, 2999002));
    }

    [Fact]
    public void Apply_refuses_existing_l10n_keys()
    {
        var (npcData, npcTalk, strings, l10n) = Tables();
        l10n[EnPath].Entries["NpcTalk_KISK_Tester_Hello_1_cv_text"] = "taken";
        Assert.Throws<InvalidOperationException>(() => CustomNpcPatch.Apply(Definitions(), npcData, npcTalk, strings, l10n));
    }

    static (FAion2DataTableFile, FAion2DataTableFile, FAion2DataTableFile, Dictionary<string, FAion2L10NFile>) Tables()
    {
        var npcData = Table("NpcTableItem", Row(
            Id(2931044), Name("Name", "N_L0_Q_GuardSylvar_01"), Struct("Desc", "TableString", Name("Key", "STR_N_GuardSylvar_01")),
            Name("ResourceKey", "NPC_M_Mercenary_01")));
        var npcTalk = Table("NpcTalkTableItem", Row(
            Id(110101001), Name("Name", "HQ1101010_00_Accept"), Int("Order", 1), Enum("Type", "ENpcTalkType::FullScreen"),
            Struct("ChatTitle", "TableL10NText", Name("Key", "None"), Name("StringKey", "None")),
            Struct("ChatDescription", "TableL10NText", Name("Key", "NpcTalk_2D4B_cv_text"), Name("StringKey", "None")),
            Enum("SpeakerType", "ENpcTalkSpeakerType::Npc"), Name("SpeakerValue", "N_L0_Q_GuardSylvar_01"),
            Name("Camera", "3rd_Right"), Name("CameraTarget", "N_L0_Q_GuardSylvar_01"), Name("Act", "AD_Defence_L_01"),
            Name("FinishTalk", "None"), Name("FinishAct", "None"), Name("CvRes", "CV_L_H0_110101001")));
        var strings = Table("AionStringTableItem", Row(
            Name("Name", "STR_N_GuardSylvar_01"), Struct("Body", "TableL10NText", Name("Key", "String_STR_N_GuardSylvar_01_body"), Name("StringKey", "None"))));
        var l10n = new Dictionary<string, FAion2L10NFile> { [EnPath] = L10N(EnPath), [DePath] = L10N(DePath) };
        return (npcData, npcTalk, strings, l10n);
    }

    static FAion2L10NFile L10N(string path)
    {
        using var ms = new MemoryStream();
        using (var w = new FAion2DatFileWriter(ms, Versions))
        {
            w.WriteL10NString("AION2");
            w.WriteMap(new Dictionary<string, string> { ["String_STR_N_GuardSylvar_01_body"] = "Guard Sylvar" }, w.WriteL10NString, w.WriteL10NString);
        }
        return ClientDatSerializer.ReadL10N(ms.ToArray(), path, new DefaultFileProvider(Path.GetTempPath(), SearchOption.TopDirectoryOnly, Versions));
    }

    static FAion2DataTableFile Table(string structType, params FStructFallback[] rows)
    {
        var table = new FAion2DataTableFile(new MemoryGameFile("Synthetic/Table.dat", []), null!) { Version = 13, Ids = ["1", "2"] };
        var inner = new FPropertyTagData { Type = "StructProperty", StructType = structType };
        table.Properties.Add(new FPropertyTag
        {
            Name = new FName("Data"),
            PropertyType = new FName("ArrayProperty"),
            TagData = new FPropertyTagData { Type = "ArrayProperty", InnerType = "StructProperty", InnerTypeData = inner },
            Tag = new ArrayProperty(new UScriptArray(rows.Select(r => (FPropertyTagType)new StructProperty(new FScriptStruct(r))).ToList(), "StructProperty", inner)),
        });
        return table;
    }

    static FStructFallback Row(params FPropertyTag[] tags) => new([.. tags]);
    static FPropertyTag Id(int id) => Struct("ID", "TableItemId", Int("Value", id));
    static FPropertyTag Int(string name, int value) => new() { Name = new FName(name), PropertyType = new FName("IntProperty"), Tag = new IntProperty(value) };
    static FPropertyTag Name(string name, string value) => new() { Name = new FName(name), PropertyType = new FName("NameProperty"), Tag = new NameProperty(new FName(value)) };
    static FPropertyTag Enum(string name, string value) => new() { Name = new FName(name), PropertyType = new FName("EnumProperty"), Tag = new EnumProperty(new FName(value)) };
    static FPropertyTag Struct(string name, string type, params FPropertyTag[] fields) => new()
    {
        Name = new FName(name),
        PropertyType = new FName("StructProperty"),
        TagData = new FPropertyTagData { Type = "StructProperty", StructType = type },
        Tag = new StructProperty(new FScriptStruct(new FStructFallback([.. fields]))),
    };

    sealed class MemoryGameFile(string path, byte[] data) : GameFile(path, data.Length)
    {
        public override bool IsEncrypted => false;
        public override CompressionMethod CompressionMethod => CompressionMethod.None;
        public override byte[] Read(FByteBulkDataHeader? header = null) => data;
        public override FArchive CreateReader(FByteBulkDataHeader? header = null) => new FByteArchive(Path, data, null);
    }
}
