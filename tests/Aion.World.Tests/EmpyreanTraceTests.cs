using Aion.Protocol.Native;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Empyrean Traces and Monoliths (ported from zaraza87/A2Core, #72), on our live opcodes (#74).</summary>
public sealed class EmpyreanTraceTests
{
    const long Now = 1_760_000_400_000;
    const int AltgardToken = 631900007, Altgard = 2001;

    static bool Is(byte[] packet, ushort opcode) => packet.Length >= 2 && BitConverter.ToUInt16(packet, 0) == opcode;
    static bool Is(byte[] packet, ushort opcode, int key) => Is(packet, opcode) && new PacketReader(packet) { Offset = 2 }.Varint() == key;
    static byte[] Use(int key) => new PacketWriter().U16(0x3636).U8(1).Varint(key).I64(0).I32(0).ToArray();
    static byte[] Resonate(int monolith, int count, int key) => new PacketWriter().U16(GamePackets.MonolithResonateRq).I32(monolith).I32(count).I32(key).ToArray();

    [Fact]
    public void Trace_spawn_matches_the_official_bytes()
    {
        // OBSERVED (fork, official rec 20261106-105448 #637): E_D1_GodFragment_01_Ground_129, achievement 125010210.
        var official = Convert.FromHexString("3436e7f5010c0081d9130021d01dc8c3ce97c60076c84600000000008077430101" +
            "01da7967a5a10100000000000000000000a5232d2d09002281730700");
        var r = new PacketReader(official) { Offset = 11 };
        var at = new Vector3d(r.F32(), r.F32(), r.F32());
        var built = GamePackets.TraceSpawn(31463, 1300865, at, 247.5, BitConverter.ToInt64(official, 34), BitConverter.ToInt32(official, 50), 9, 125010210);
        Assert.Equal(official, built);
    }

    [Fact]
    public void Object_reward_carries_key_position_and_items()
    {
        var reward = GamePackets.EnvObjReward(910001, new Vector3d(1, 2, 3), [(AltgardToken, 3)]);
        Assert.Equal(Convert.FromHexString("3ae3" + "b1e20d00" + "0000803f0000004000004040" + "01" + "6707aa25" + "03000000"), reward);
    }

    [Fact]
    public void Monolith_ids_are_the_observed_list_ids()
    {
        var tables = MonolithTables.Embedded;
        // OBSERVED 2026-11-12: a Light character's MonolithList_NT carries 1001, 1002, 3001.
        Assert.Equal([1001, 1002, 3001], tables.OfRace(1).Select(m => m.Id));
        Assert.Equal([2001, 2002, 4001], tables.OfRace(2).Select(m => m.Id));
        Assert.Equal(631900001, tables.Of(1001)!.Token);   // OBSERVED token item of the 1001 resonance
        Assert.Equal(Altgard, tables.OfObject(1300862, 2)!.Id);
        Assert.Equal(4001, tables.OfObject(1100226, 2)!.Id);   // Reshanta: the race's Monolith
        Assert.Equal(3001, tables.OfObject(1100226, 1)!.Id);
        Assert.Equal(560, tables.Of(Altgard)!.MaxCount);
        Assert.Equal(21, tables.Of(1001)!.LevelAt(270));   // OBSERVED 264 -> 270 passed no level: 255 reaches 21, 285 reaches 22
    }

    [Fact]
    public void Traces_are_placed_with_their_spawn_achievement()
    {
        var traces = WorldObjects.ObjectsOn(1110).Where(o => o.Achievement != 0).ToList();
        Assert.Equal(559, traces.Count);
        Assert.All(traces, t => Assert.Equal("UseEnvObjSpawnerName", AchievementTables.Embedded.Of(t.Achievement)!.Type));
        Assert.Equal(2, WorldObjects.ObjectsOn(1110).Count(o => o.Monolith));
        // The hand-placed Reshanta "Monoliths" on 1100 (really trace env ids) are gone.
        Assert.DoesNotContain(WorldObjects.ObjectsOn(1100), o => o.Monolith);
    }

    static async Task<(GameSession Session, FakeCharacterStore Store, int Key, WorldObject Trace)> AtAltgardTraceAsync(FakeCharacterStore? store = null)
    {
        store ??= new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedDarkWorldAsync(store, Now, level: 20);
        var objects = WorldObjects.ObjectsOn(1110);
        var index = objects.ToList().FindIndex(o => o.Achievement == 125010210);
        Assert.True(index >= 0);
        var trace = objects[index];
        var spawned = session.Teleport(1110, trace.At, Now + 100);
        spawned.AddRange(await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 110));
        spawned.AddRange(await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(trace.At), Now + 120));
        spawned.AddRange(await session.TickAsync(Now + 130));
        var key = WorldObjects.FirstKey + index;
        var spawn = Assert.Single(spawned, p => Is(p, 0x3634, key));
        var reader = new PacketReader(spawn) { Offset = 2 };
        reader.Varint();
        Assert.Equal(0x0c, spawn[reader.Offset]);
        Assert.Equal(125010210, BitConverter.ToInt32(spawn, spawn.Length - 5));
        return (session, store, key, trace);
    }

    [Fact]
    public async Task Collecting_a_trace_gives_the_tokens_once_in_the_observed_order()
    {
        var (session, store, key, _) = await AtAltgardTraceAsync();
        var start = await session.HandleAsync(Use(key), Now + 1000);
        Assert.True(Is(start[0], 0x3637) && BitConverter.ToUInt16(start[0], 2) == 0);   // the reply first, with the start (OBSERVED)
        Assert.Contains(start, p => Is(p, 0x3638, key));
        Assert.DoesNotContain(start, p => Is(p, 0x363a, key));

        var done = await session.TickAsync(Now + 1000 + 2500);
        var order = new[] { 0x363a, 0x8d6d, 0x363b, 0xe33a, 0x561b }.Select(op => done.FindIndex(p => Is(p, (ushort)op))).ToList();
        Assert.All(order, i => Assert.True(i >= 0));
        Assert.Equal(order.OrderBy(i => i), order);
        Assert.DoesNotContain(done, p => Is(p, 0x3637));
        Assert.Equal(3, store.Items.Where(i => i.TemplateId == AltgardToken).Sum(i => i.Quantity));

        // Collected for good: the spawn achievement counted, a second use is refused and gives nothing.
        var again = await session.HandleAsync(Use(key), Now + 10_000);
        Assert.Contains(again, p => Is(p, 0x3637) && BitConverter.ToUInt16(p, 2) == 1);
        Assert.DoesNotContain(await session.TickAsync(Now + 13_000), p => Is(p, 0xe33a));
        Assert.Equal(3, store.Items.Where(i => i.TemplateId == AltgardToken).Sum(i => i.Quantity));
    }

    [Fact]
    public async Task Resonating_stores_the_count_and_grants_the_levels_passed()
    {
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.BagItem(AltgardToken, 6, 7000));
        var (session, _, _, _) = await AtAltgardTraceAsync(store);

        var replies = await session.HandleAsync(Resonate(Altgard, 6, 0), Now + 5000);
        var reply = Assert.Single(replies, p => Is(p, GamePackets.MonolithResonateRs));
        // 0 -> 6 passes levels 2 (need 1) and 3 (need 5): two Amulet Enhance Scrolls and two of the other item.
        Assert.Equal(GamePackets.MonolithResonateReply(0, Altgard, AltgardToken, 6, 0, 6, [(531110002, 2), (630900003, 2)]), reply);
        Assert.True(replies.FindIndex(p => Is(p, GamePackets.MonolithResonateRs)) < replies.FindIndex(p => Is(p, 0x561b)));
        Assert.Equal(6, store.Monoliths[Altgard]);
        Assert.Equal(6, session.MonolithCount(Altgard));
        Assert.DoesNotContain(store.Items, i => i.TemplateId == AltgardToken);
        Assert.Equal(2, store.Items.Where(i => i.TemplateId == 531110002).Sum(i => i.Quantity));

        // Without tokens: refused, nothing moves.
        var refused = Assert.Single(await session.HandleAsync(Resonate(Altgard, 1, 0), Now + 6000));
        Assert.Equal(15954, BitConverter.ToUInt16(refused, 2));
        Assert.Equal(6, store.Monoliths[Altgard]);
        // Another race's Monolith: refused.
        Assert.Equal(15960, BitConverter.ToUInt16(Assert.Single(await session.HandleAsync(Resonate(1001, 1, 0), Now + 7000)), 2));
    }

    [Fact]
    public async Task Stored_counts_are_listed_at_entry()
    {
        var store = new FakeCharacterStore();
        store.Monoliths[Altgard] = 42;
        var entry = await GameSessionTestHelpers.EnterSharedDarkWorldAsync(store, Now, level: 20);
        Assert.Equal(42, entry.MonolithCount(Altgard));
        Assert.Equal(GamePackets.MonolithList([(2001, 42), (2002, 0), (4001, 0)]), entry.MonolithListForTests());
    }

    [Fact]
    public async Task Monolith_levels_add_their_pvp_stats()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now, level: 20);
        Assert.Equal(0, session.MonolithStatForTests("PvPDamageDefense"));
        session.SetMonolithForTests(3001, 5);   // levels 2 and 3: PvPDamageDefense 15 + 15
        Assert.Equal(30, session.MonolithStatForTests("PvPDamageDefense"));
    }
}
