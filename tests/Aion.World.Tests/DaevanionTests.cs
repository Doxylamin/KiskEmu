using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class DaevanionTests
{
    [Fact]
    public void Export_covers_global_forty_boards()
    {
        var tables = DaevanionTables.Embedded;
        Assert.Equal(40, tables.BoardCount);
        Assert.True(tables.NodeCount > 4000);
    }

    [Fact]
    public void Board_ids_follow_class_slot_and_god_order()
    {
        var tables = DaevanionTables.Embedded;
        Assert.NotNull(tables.BoardOf(11));
        Assert.Equal(1, tables.BoardOf(11)!.Slot);
        Assert.Equal(12, tables.BoardOf(11)!.NeedLevel);
        Assert.Equal(20, tables.BoardOf(12)!.NeedLevel);
        Assert.Equal(45, tables.BoardOf(16)!.NeedLevel);
    }

    [Fact]
    public void Start_node_id_matches_row_major_formula()
    {
        var tables = DaevanionTables.Embedded;
        var start = tables.StartNodeId(11);
        Assert.Equal(110113, start);
        Assert.NotNull(tables.NodeOf(start));
    }

    [Fact]
    public async Task Open_node_spends_bonus_points_and_persists()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 12, WorldId = 1204 }, new Random(3))
        {
            Store = store,
        };
        const long now = 1_760_000_400_000;
        await session.HandleAsync(GameSessionTestHelpers.Login(), now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), now);
        var created = store.States[0];
        store.States[0] = created with { Progress = created.Progress with { Level = 12 } };
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);

        store.Daevanion = new DaevanionProgress([], new Dictionary<int, int>(), new Dictionary<int, int> { [1] = 5 });
        session.ReloadDaevanionForTests(store.Daevanion);

        var tables = DaevanionTables.Embedded;
        var boardId = tables.BoardsForCreation(1).First();
        var start = tables.StartNodeId(boardId);
        var startNode = tables.NodeOf(start)!;
        var neighborId = tables.NodesOfBoard(boardId)
            .First(id => tables.NodeOf(id) is { } n && n.Cost == 1 && tables.IsNeighbor(n, startNode));

        var plain = PacketWriter.Opcode(GameSession.DaevanionOpenNodeRq).I32(neighborId).ToArray();
        var replies = await session.HandleAsync(plain, now + 1);

        Assert.Contains(replies, p => BitConverter.ToUInt16(p, 0) == GameSession.DaevanionOpenNodeRs && BitConverter.ToUInt16(p, 2) == 0);
        Assert.NotNull(store.Daevanion);
        Assert.Contains(neighborId, store.Daevanion!.NodeIds);
        Assert.Equal(1, store.Daevanion.UsedPoints.GetValueOrDefault(1));

        // Refunding the node costs 500 Kina (OBSERVED 2026-11-15): refused without it; with it the points come back and the
        // replies end with 0xe22b `0000 <node>` after the Kina change, with no 0xe226 board list.
        store.Items.RemoveAll(i => i.TemplateId == 930100001);
        await session.InventoryChangesAsync(default);
        var close = PacketWriter.Opcode(GameSession.DaevanionCloseNodeRq).I32(neighborId).ToArray();
        var refused = await session.HandleAsync(close, now + 2);
        Assert.Equal(GameSession.DaevanionCloseNodeRs, GameSessionTestHelpers.Op(refused.Single()));
        Assert.NotEqual(0, BitConverter.ToUInt16(refused[0], 2));
        Assert.Contains(neighborId, store.Daevanion!.NodeIds);

        store.Items.Add(FakeCharacterStore.BagItem(930100001, 800, 7700));
        await session.InventoryChangesAsync(default);
        var refund = await session.HandleAsync(close, now + 3);
        Assert.Equal(GameSession.DaevanionPointUpdateNt, GameSessionTestHelpers.Op(refund[0]));
        Assert.Equal(PacketWriter.Opcode(GameSession.DaevanionCloseNodeRs).U16(0).I32(neighborId).ToArray(), refund[^1]);
        Assert.DoesNotContain(refund, p => GameSessionTestHelpers.Op(p) == GameSession.DaevanionBoardInfoNt);
        Assert.Equal(300, FakeCharacterStore.KinaIn(store));
        Assert.DoesNotContain(neighborId, store.Daevanion!.NodeIds);
        Assert.Equal(0, store.Daevanion.UsedPoints.GetValueOrDefault(1));
    }

    [Fact]
    public async Task Open_route_opens_nodes_in_order()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 12, WorldId = 1204 }, new Random(3))
        {
            Store = store,
        };
        const long now = 1_760_000_500_000;
        await session.HandleAsync(GameSessionTestHelpers.Login(), now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), now);
        var created = store.States[0];
        store.States[0] = created with { Progress = created.Progress with { Level = 12 } };
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);

        store.Daevanion = new DaevanionProgress([], new Dictionary<int, int>(), new Dictionary<int, int> { [1] = 10 });
        session.ReloadDaevanionForTests(store.Daevanion);

        var tables = DaevanionTables.Embedded;
        var boardId = tables.BoardsForCreation(1).First();
        var start = tables.StartNodeId(boardId);
        var startNode = tables.NodeOf(start)!;
        var step1 = tables.NodesOfBoard(boardId).First(id => tables.NodeOf(id) is { } n && n.Cost == 1 && tables.IsNeighbor(n, startNode));
        var step1Node = tables.NodeOf(step1)!;
        var step2 = tables.NodesOfBoard(boardId).First(id => id != step1 && tables.NodeOf(id) is { } n && n.Cost == 1 && tables.IsNeighbor(n, step1Node));

        var plain = PacketWriter.Opcode(GameSession.DaevanionOpenRouteNodeRq).I32(boardId).Varint(2).I32(step1).I32(step2).ToArray();
        var replies = await session.HandleAsync(plain, now + 1);

        Assert.Contains(replies, p => BitConverter.ToUInt16(p, 0) == GameSession.DaevanionOpenRouteNodeRs && BitConverter.ToInt32(p, 2) == 0);
        Assert.Contains(step1, store.Daevanion!.NodeIds);
        Assert.Contains(step2, store.Daevanion.NodeIds);
        Assert.Equal(2, store.Daevanion.UsedPoints.GetValueOrDefault(1));
    }

    [Fact]
    public async Task Open_all_on_the_live_opcode_opens_the_whole_board_or_nothing()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 12, WorldId = 1204 }, new Random(3))
        {
            Store = store,
        };
        const long now = 1_760_000_600_000;
        await session.HandleAsync(GameSessionTestHelpers.Login(), now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), now);
        var created = store.States[0];
        store.States[0] = created with { Progress = created.Progress with { Level = 12 } };
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);

        store.Daevanion = new DaevanionProgress([], new Dictionary<int, int>(), new Dictionary<int, int> { [1] = 3 });
        session.ReloadDaevanionForTests(store.Daevanion);
        var boardId = DaevanionTables.Embedded.BoardsForCreation(1).First();
        var available = session.DaevanionAvailableForTests(1);

        // Live 2026-11-12: "acquire all nodes" sent 0xe500 with the board id (6 bytes) and got no answer. Live 2026-11-15
        // it opened what the points paid for; the button asks for the whole board's crystals, so too few opens nothing.
        var short_ = await session.HandleAsync(PacketWriter.Opcode(GameSession.DaevanionOpenAllNodeRqLive).I32(boardId).ToArray(), now + 1);
        var refused = Assert.Single(short_, p => BitConverter.ToUInt16(p, 0) == GameSession.DaevanionOpenAllNodeRsLive);
        Assert.Equal(1, BitConverter.ToInt32(refused, 2));
        Assert.Equal(available, session.DaevanionAvailableForTests(1));

        // Enough crystals for the whole board: every node opens in one reply.
        store.Daevanion = new DaevanionProgress([], new Dictionary<int, int>(), new Dictionary<int, int> { [1] = 100_000 });
        session.ReloadDaevanionForTests(store.Daevanion);
        var replies = await session.HandleAsync(PacketWriter.Opcode(GameSession.DaevanionOpenAllNodeRqLive).I32(boardId).ToArray(), now + 2);
        var reply = Assert.Single(replies, p => BitConverter.ToUInt16(p, 0) == GameSession.DaevanionOpenAllNodeRsLive);
        var reader = new PacketReader(reply) { Offset = 2 };
        Assert.Equal(0, reader.I32());
        Assert.Equal(boardId, reader.I32());
        var count = (int)reader.Varint();
        var opened = Enumerable.Range(0, count).Select(_ => reader.I32()).ToList();
        Assert.Equal(reply.Length, reader.Offset);
        Assert.All(DaevanionTables.Embedded.NodesOfBoard(boardId), id => Assert.Contains(id, store.Daevanion!.NodeIds));
        Assert.Contains(replies, p => BitConverter.ToUInt16(p, 0) == GameSession.DaevanionPointUpdateNt);
    }

    [Fact]
    public void Wire_stats_include_percent_combat_speed_from_nodes()
    {
        var tables = DaevanionTables.Embedded;
        var nodeId = tables.NodesOfBoard(11).First(id => tables.NodeOf(id) is { } n && n.Stats.Any(s => s.Wire == 282));
        var expected = tables.NodeOf(nodeId)!.Stats.Where(s => s.Wire == 282).Sum(s => s.Value);
        var session = new GameSession(GameData.Embedded, new GameSessionOptions(), Random.Shared);
        session.ReloadDaevanionForTests(new DaevanionProgress([tables.StartNodeId(11), nodeId], new Dictionary<int, int>(), new Dictionary<int, int>()));
        Assert.Equal(expected, session.DaevanionWireStat(282));
    }
}
