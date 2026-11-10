using System.Text;
using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Custom NPCs: placement from the directory, the NpcId each session sees, the dialog and its actions.</summary>
public sealed class CustomNpcTests
{
    static readonly long Now = 1_700_000_000_000;
    const int TemplateId = 2_999_001, BaseTemplateId = 2_931_044; // Guard Sylvar's NpcData row: a friendly citizen
    static readonly int NpcKey = CustomNpcDirectory.KeyOf(TemplateId, 0);

    static CustomNpc Npc(int mapId, Vector3d at, bool enabled = true) => new(TemplateId, "Tester", BaseTemplateId,
        new Dictionary<string, string> { ["en-US"] = "Kisk Tester", ["de-DE"] = "Kisk-Prüfer" },
        [new CustomNpcPlacement(mapId, at.X + 200, at.Y, at.Z, 90)],
        [
            new CustomNpcNode("Hello", [
                    new Dictionary<string, string> { ["en-US"] = "Line one" },
                    new Dictionary<string, string> { ["en-US"] = "Line two", ["de-DE"] = "Zeile zwei" },
                ], new CustomNpcCondition(NoFlag: "met"),
                [new CustomNpcAction("give_kina", Count: 1234), new CustomNpcAction("set_flag", Flag: "met")]),
            new CustomNpcNode("Again", [new Dictionary<string, string> { ["en-US"] = "Back again?" }], null, []),
        ], enabled, DateTimeOffset.UnixEpoch);

    static async Task<(GameSession Session, SharedWorld World, FakeCharacterStore Store)> EnterAsync(bool patched)
    {
        var store = new FakeCharacterStore();
        var world = new SharedWorld(GameData.Embedded);
        var session = new GameSession(GameData.Embedded,
            new GameSessionOptions { World = world, CharacterLevel = 55, WorldId = store.WorldId, TrainingDummy = true }, new Random(19)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        world.CustomNpcs.AssumePatchedClients = patched;
        world.CustomNpcs.Publish([Npc(session.WorldPlayer!.Map!.MapId, session.Position!.Value)]);
        await session.TickAsync(Now + 1_000);   // the map places it
        return (session, world, store);
    }

    static byte[] Talk(ushort op) => new PacketWriter().U16(op).Varint(NpcKey).ToArray();

    /// <summary>The NpcId of an NpcAppear (0x3641) for the custom NPC, or null.</summary>
    static int? AppearedNpcId(IEnumerable<byte[]> packets)
    {
        foreach (var p in packets.Where(p => GameSessionTestHelpers.Op(p) == 0x3641))
        {
            var r = new PacketReader(p) { Offset = 2 };
            if ((int)r.Varint() != NpcKey) continue;
            return BitConverter.ToInt32(p, r.Offset + 3);
        }
        return null;
    }

    static bool HasText(IEnumerable<byte[]> packets, ushort op, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return packets.Any(p => GameSessionTestHelpers.Op(p) == op && p.AsSpan().IndexOf(bytes) >= 0);
    }

    static async Task<List<byte[]>> SeeAsync(GameSession session)
    {
        var seen = new List<byte[]>();
        for (var t = 2_000; t <= 4_000 && AppearedNpcId(seen) is null; t += 1_000) seen.AddRange(await session.TickAsync(Now + t));
        return seen;
    }

    [Fact]
    public async Task Without_the_pak_the_base_template_appears_and_lines_come_one_per_talk()
    {
        var (session, _, store) = await EnterAsync(patched: false);
        Assert.True(session.Targets.ContainsKey(NpcKey));
        Assert.Equal(BaseTemplateId, AppearedNpcId(await SeeAsync(session)));

        var first = await session.HandleAsync(Talk(0xe240), Now + 5_000);
        Assert.Contains(first, p => GameSessionTestHelpers.Op(p) == 0xe241);
        Assert.True(HasText(first, GameSession.NoticeNt, "Kisk Tester: Line one …"));
        Assert.True(HasText(first, GameSession.NoticeNt, "Kisk-Prüfer: Line one …")); // German client: the name's translation, the line's English
        Assert.DoesNotContain(first, p => GameSessionTestHelpers.Op(p) == 0x8d15);
        await session.HandleAsync(Talk(0xe242), Now + 5_100);
        Assert.False(session.CustomNpcFlag("Tester", "met"));

        var kinaBefore = store.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity);
        var second = await session.HandleAsync(Talk(0xe240), Now + 6_000);
        Assert.True(HasText(second, GameSession.NoticeNt, "Kisk-Prüfer: Zeile zwei"));
        await session.HandleAsync(Talk(0xe242), Now + 6_100);
        Assert.True(session.CustomNpcFlag("Tester", "met"));
        Assert.Equal(kinaBefore + 1234, store.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity));
        Assert.Equal("1", store.Settings["custom_npc_flags"].GetProperty("Tester").GetProperty("met").GetString());

        // The flag moves the dialog on, and the reward is not paid again.
        var third = await session.HandleAsync(Talk(0xe240), Now + 7_000);
        Assert.True(HasText(third, GameSession.NoticeNt, "Kisk Tester: Back again?"));
        await session.HandleAsync(Talk(0xe242), Now + 7_100);
        Assert.Equal(kinaBefore + 1234, store.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity));
    }

    [Fact]
    public async Task With_the_pak_the_custom_template_appears_and_the_talk_plays_by_name()
    {
        var (session, _, store) = await EnterAsync(patched: true);
        Assert.Equal(TemplateId, AppearedNpcId(await SeeAsync(session)));

        var opened = await session.HandleAsync(Talk(0xe240), Now + 5_000);
        Assert.Contains(opened, p => GameSessionTestHelpers.Op(p) == 0xe241);
        Assert.True(HasText(opened, 0x8d15, "KISK_Npc_Tester_Hello"));
        Assert.DoesNotContain(opened, p => GameSessionTestHelpers.Op(p) == GameSession.NoticeNt);
        await session.HandleAsync(Talk(0xe242), Now + 5_100);
        Assert.True(session.CustomNpcFlag("Tester", "met"));
        Assert.True(store.Settings.ContainsKey("custom_npc_flags"));
    }

    [Fact]
    public async Task A_shop_request_talks_instead_of_selling()
    {
        var (session, _, store) = await EnterAsync(patched: false);
        await SeeAsync(session);
        var kinaBefore = store.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity);

        // A merchant base: [F] asks for the shop (0x8c00); it is refused and each request shows the next line.
        var first = await session.HandleAsync(Talk(GameSession.ShopOpenRq), Now + 5_000);
        var refusal = Assert.Single(first, p => GameSessionTestHelpers.Op(p) == GameSession.ShopOpenRs);
        Assert.Equal(1, BitConverter.ToUInt16(refusal, 2));
        Assert.True(HasText(first, GameSession.NoticeNt, "Kisk Tester: Line one …"));
        Assert.False(session.CustomNpcFlag("Tester", "met"));

        var second = await session.HandleAsync(Talk(GameSession.ShopOpenRq), Now + 6_000);
        Assert.True(HasText(second, GameSession.NoticeNt, "Kisk Tester: Line two"));
        Assert.True(session.CustomNpcFlag("Tester", "met"));
        Assert.Equal(kinaBefore + 1234, store.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity));

        // When the client opened the talk first (0xe240), the shop request only gets its refusal.
        await session.HandleAsync(Talk(0xe240), Now + 7_000);
        var during = await session.HandleAsync(Talk(GameSession.ShopOpenRq), Now + 7_050);
        Assert.Single(during);

        // Nothing can be bought from it.
        var buy = await session.HandleAsync(new PacketWriter().U16(GameSession.ShopBuyRq).Varint(NpcKey).Varint(1).I32(1).I32(1).ToArray(), Now + 8_000);
        Assert.All(buy.Where(p => GameSessionTestHelpers.Op(p) == GameSession.ShopBuyRs), p => Assert.NotEqual(0, BitConverter.ToUInt16(p, 2)));
    }

    [Fact]
    public async Task A_changed_or_disabled_npc_leaves_the_view()
    {
        var (session, world, _) = await EnterAsync(patched: false);
        await SeeAsync(session);
        Assert.Contains(NpcKey, session.SeenNpcs);

        var moved = Npc(session.WorldPlayer!.Map!.MapId, session.Position!.Value with { X = session.Position.Value.X + 300 });
        Assert.True(world.CustomNpcs.Publish([moved]));
        Assert.False(world.CustomNpcs.Publish([moved]));   // unchanged: same version
        var after = await session.TickAsync(Now + 10_000);
        Assert.Contains(after, p => GameSessionTestHelpers.Op(p) == 0x3642 && new PacketReader(p) { Offset = 2 }.Varint() == (uint)NpcKey);
        Assert.Equal(session.Position.Value.X + 500, session.Targets[NpcKey].Position.X);
        Assert.Equal(BaseTemplateId, AppearedNpcId([.. after, .. await SeeAsync(session)]));

        world.CustomNpcs.Publish([moved with { Enabled = false }]);
        await session.TickAsync(Now + 20_000);
        Assert.True(session.Targets[NpcKey].CorpseRemoved);
        Assert.DoesNotContain(NpcKey, session.SeenNpcs);
    }

    [Fact]
    public void Staff_tools_leave_custom_npcs_alone()
    {
        var data = GameData.Embedded;
        var world = new SharedWorld(data);
        world.CustomNpcs.Publish([Npc(1100, new Vector3d(0, 0, 0))]);
        var map = world.Map(1100);
        Assert.True(map.Targets.ContainsKey(NpcKey));
        Assert.Equal(0, map.RemoveStaffSpawns());
        Assert.False(map.Targets[NpcKey].Dead);
        Assert.DoesNotContain(map.Monsters(200, 0, 500, 50), m => m.Key == NpcKey && m.Staff);
    }

    [Fact]
    public void Rules_refuse_bad_npcs_and_export_the_tool_input()
    {
        var npc = Npc(1100, new Vector3d(0, 0, 0));
        Assert.Null(CustomNpcRules.Validate(npc));
        Assert.Equal("invalid_template_id", CustomNpcRules.Validate(npc with { TemplateId = 2_990_928 }));
        Assert.Equal("invalid_slug", CustomNpcRules.Validate(npc with { Slug = "bad slug" }));
        Assert.Equal("invalid_base_template", CustomNpcRules.Validate(npc with { BaseTemplateId = 2_999_002 }));
        Assert.Equal("invalid_name", CustomNpcRules.Validate(npc with { Name = new Dictionary<string, string> { ["de-DE"] = "x" } }));
        Assert.Equal("invalid_node_key", CustomNpcRules.Validate(npc with { Nodes = [npc.Nodes[0], npc.Nodes[0]] }));
        Assert.Equal("invalid_action", CustomNpcRules.Validate(npc with { Nodes = [npc.Nodes[0] with { Actions = [new CustomNpcAction("give_item", ItemId: QunaWalletRules.ItemId, Count: 1)] }] }));
        Assert.Equal("invalid_condition", CustomNpcRules.Validate(npc with { Nodes = [npc.Nodes[0] with { When = new CustomNpcCondition(MinLevel: 50, MaxLevel: 10) }] }));

        var json = JsonSerializer.SerializeToElement(CustomNpcRules.Export([npc]));
        var exported = json.GetProperty("npcs")[0];
        Assert.Equal("Tester", exported.GetProperty("slug").GetString());
        Assert.Equal(TemplateId, exported.GetProperty("templateId").GetInt32());
        Assert.Equal(BaseTemplateId, exported.GetProperty("baseTemplateId").GetInt32());
        Assert.Equal("Zeile zwei", exported.GetProperty("talks")[0].GetProperty("lines")[1].GetProperty("de-DE").GetString());
        Assert.Equal("KISK_Npc_Tester_Hello", CustomNpcRules.TalkName("Tester", "Hello"));

        // The realm link and the store carry NPCs as JSON (web defaults).
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var back = JsonSerializer.Deserialize<List<CustomNpc>>(JsonSerializer.Serialize(new[] { npc }, web), web)!.Single();
        Assert.Equal("Zeile zwei", back.Nodes[0].Lines[1]["de-DE"]);
        Assert.Equal("met", back.Nodes[0].When!.NoFlag);
        Assert.Equal(1234, back.Nodes[0].Actions[0].Count);
        Assert.Equal(90, back.Placements[0].Yaw);
    }
}
