using System.Text.Json;
using Aion.Application.Chat;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>"/bug &lt;text&gt;" (docs/BUG_REPORTS.md): a report with the player's context, from chat, for every player.</summary>
public sealed class BugReportTests
{
    const long Now = 1_793_000_000_000;

    sealed class MemoryBugReports : IBugReports
    {
        public List<NewBugReport> Reports { get; } = [];

        public Task<long> CreateAsync(NewBugReport report, CancellationToken ct = default)
        {
            Reports.Add(report);
            return Task.FromResult((long)Reports.Count);
        }

        public Task<IReadOnlyList<BugReportSummary>> ListAsync(VerifiedAccount actor, BugReportFilter filter, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BugReport?> GetAsync(VerifiedAccount actor, long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> UpdateAsync(VerifiedAccount actor, long id, string status, string note, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long?> AttachScreenshotAsync(Guid account, Guid character, BugScreenshot screenshot, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BugScreenshot?> ScreenshotAsync(VerifiedAccount actor, long id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    static async Task<string> Chat(GmCommands commands, GameSession session, string user, string text, long now)
    {
        var reply = commands.HandleAsync(new ChatIdentity("1", "1204", "en_US"), user, text, default);
        await session.TickAsync(now);   // the capture runs on the session's loop
        return await reply;
    }

    [Fact]
    public async Task A_bug_report_stores_the_character_place_quests_nearby_spawns_and_recent_packets()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1);
        var mapId = session.MapId!.Value;
        // Stand next to the nearest MapData NPC of the map, facing somewhere, so the nearby list has a known row.
        var spawns = GameData.Embedded.Spawns(mapId);
        var start = session.Position!.Value;
        var index = Enumerable.Range(0, spawns.Count)
            .OrderBy(i => Math.Pow(spawns[i].Position[0] - start.X, 2) + Math.Pow(spawns[i].Position[1] - start.Y, 2)).First();
        var row = spawns[index];
        var at = new Vector3d(row.Position[0] + 300, row.Position[1], row.Position[2]);
        session.Teleport(mapId, at, Now + 10);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20);
        // A few messages through the connection's path: the ring keeps them with their bodies.
        await session.HandleClientPacketSafeAsync(new PacketWriter().U16(0x3602).U64(77).ToArray(), Now + 30, default);
        session.RecordOutboundPackets([new byte[] { 0x10, 0x36, 1, 2, 3 }], Now + 40);
        await session.HandleClientPacketSafeAsync(GameSessionTestHelpers.Login(), Now + 50, default);   // a login body is never kept

        var presence = new WorldPresence();
        presence.Enter(session.CharacterName!, session, store.AccountId);
        var reports = new MemoryBugReports();
        var commands = new GmCommands(presence, new BuilderCommands(presence, new GmPolicy(), (_, _, _) => { }))
        {
            Bugs = new BugReporter(reports, "0123abcd", op => op == 0x3602 ? "Ping_RQ" : null) { Clock = () => DateTimeOffset.FromUnixTimeMilliseconds(Now) },
        };

        var reply = await Chat(commands, session, session.CharacterName!, "/bug the guard stands outside the dialog camera", Now + 1000);
        Assert.StartsWith("Thanks, bug report #1 was saved at ", reply);
        var report = Assert.Single(reports.Reports);
        Assert.Equal("the guard stands outside the dialog camera", report.Text);
        Assert.Equal((store.AccountId, session.CharacterId, session.CharacterName, store.WorldId, mapId, "0123abcd"),
            (report.AccountId!.Value, report.CharacterId, report.CharacterName, report.WorldId, report.MapId!.Value, report.Commit));

        using var json = JsonDocument.Parse(report.ContextJson);
        var context = json.RootElement;
        Assert.Equal("0123abcd", context.GetProperty("server").GetProperty("commit").GetString());
        var character = context.GetProperty("character");
        Assert.Equal(session.CharacterName, character.GetProperty("name").GetString());
        Assert.Equal("Light", character.GetProperty("raceName").GetString());
        Assert.Equal(session.ClassName, character.GetProperty("className").GetString());
        Assert.Equal(session.Level, character.GetProperty("level").GetInt32());
        var location = context.GetProperty("location");
        Assert.Equal(mapId, location.GetProperty("mapId").GetInt32());
        Assert.Equal(MapNames.Of(mapId, GameData.Embedded), location.GetProperty("mapName").GetString());
        Assert.Equal(Math.Round(at.X, 1), location.GetProperty("x").GetDouble());
        Assert.True(location.GetProperty("subzoneId").GetInt32() > 0);

        // The MapData row the NPC came from: file, map, row index and its placed position.
        var npc = context.GetProperty("nearby").EnumerateArray().First(n => n.GetProperty("source").TryGetProperty("index", out var i) && i.GetInt32() == index);
        Assert.Equal(row.NpcId, npc.GetProperty("npcId").GetInt32());
        Assert.Equal(300, npc.GetProperty("distance").GetDouble(), 0);
        var source = npc.GetProperty("source");
        Assert.Equal(("map_data", "tools/data/world-npcs.json", mapId), (source.GetProperty("kind").GetString(), source.GetProperty("file").GetString(), source.GetProperty("map").GetInt32()));
        Assert.Equal(Math.Round(row.Position[0], 1), source.GetProperty("home")[0].GetDouble());
        Assert.All(context.GetProperty("nearby").EnumerateArray(), n => Assert.True(n.GetProperty("distance").GetDouble() <= GameSession.BugNearbyRadius));

        // Quests: what the new character has (the starter quest) with its step, also in the report's quest codes.
        var active = context.GetProperty("quests").GetProperty("active").EnumerateArray().ToList();
        Assert.NotEmpty(active);
        Assert.Contains(active[0].GetProperty("code").GetString(), report.Quests);
        Assert.True(active[0].TryGetProperty("step", out _) && active[0].TryGetProperty("goals", out _));

        // The packet ring: oldest first, times before the report, names where known, bodies as hex, the login redacted.
        var packets = context.GetProperty("packets").GetProperty("recent").EnumerateArray().ToList();
        var ping = packets.Single(p => p.GetProperty("op").GetString() == "0x3602");
        Assert.Equal(("C", "Ping_RQ", 10, 970L), (ping.GetProperty("dir").GetString(), ping.GetProperty("name").GetString(), ping.GetProperty("length").GetInt32(),
            ping.GetProperty("agoMs").GetInt64()));
        Assert.Equal("4d00000000000000", ping.GetProperty("hex").GetString());
        Assert.Equal("010203", packets.Single(p => p.GetProperty("op").GetString() == "0x3610").GetProperty("hex").GetString());
        Assert.Equal("(redacted)", packets.Single(p => p.GetProperty("op").GetString() == "0x3613").GetProperty("hex").GetString());

        // One report per character a minute; "//bug" works too; text is needed.
        Assert.StartsWith("Please wait 60 s", await commands.HandleAsync(new ChatIdentity("1", "1204", "en_US"), session.CharacterName!, "/bug again", default));
        Assert.StartsWith("Describe the bug", await commands.HandleAsync(new ChatIdentity("1", "1204", "en_US"), session.CharacterName!, "//bug", default));
        Assert.Single(reports.Reports);
    }

    [Fact]
    public async Task Bug_reports_need_a_character_in_the_world_and_a_configured_store()
    {
        var presence = new WorldPresence();
        var builder = new BuilderCommands(presence, new GmPolicy(), (_, _, _) => { });
        var chatter = new ChatIdentity("1", "1204", "en_US");
        Assert.Equal("Bug reports are not available on this server.", await new GmCommands(presence, builder).HandleAsync(chatter, "Nobody", "/bug broken", default));
        var commands = new GmCommands(presence, builder) { Bugs = new BugReporter(new MemoryBugReports()) };
        Assert.Equal("Enter the world first.", await commands.HandleAsync(chatter, "Nobody", "/bug broken", default));
        Assert.Equal("Unknown command. Use //help.", await commands.HandleAsync(chatter, "Nobody", "/bugger", default));
    }

    [Fact]
    public void The_packet_ring_is_bounded_and_keeps_the_newest()
    {
        var trace = new ConnectionPacketTrace();
        var body = new byte[200];
        for (var i = 0; i < ConnectionPacketTrace.Capacity + 10; i++)
        {
            body[0] = (byte)i;
            body[1] = 0x37;
            body[2] = (byte)i;
            trace.RecordServer(body, i);
        }
        var kept = trace.Snapshot();
        Assert.Equal(ConnectionPacketTrace.Capacity, kept.Count);
        Assert.Equal(10, kept[0].Ms);
        Assert.Equal(ConnectionPacketTrace.PreviewBytes, kept[^1].Preview.Length);
        Assert.Equal(unchecked((byte)(ConnectionPacketTrace.Capacity + 9)), kept[^1].Preview[0]);
        Assert.Equal(200, kept[^1].Length);
    }
}
