using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Death, CharRespawn_RQ, CharDie layout, and auto-rez timing (build pin 25767555).</summary>
public class DeathResurrectTests
{
    const long Now = 1_700_000_000_000;

    static async Task<GameSession> EnterAscendedAsync(FakeCharacterStore store, long now, GameSessionOptions? options = null)
    {
        var shared = new SharedWorld(GameData.Embedded);
        var session = new GameSession(GameData.Embedded,
            options ?? new GameSessionOptions { World = shared, CharacterLevel = 10, WorldId = store.WorldId, TrainingDummy = true },
            new Random(31))
        { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        session.Teleport(1010, GameSessionTestHelpers.MapStartOrDefault(1010), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 1);
        return session;
    }

    [Fact]
    public void CharDie_player_PvE_matches_usmap_body_length()
    {
        var ours = GamePackets.Kill(1, 11010000, 2001, 0, "", dieType: 2, npcCasterDataId: 55_001, npcDieActId: 0, reason: 1);
        Assert.Equal(0x8d04, ours[0] | (ours[1] << 8));
        Assert.Equal(2, ours[^1]);
        Assert.Equal(0u, BitConverter.ToUInt32(ours, ours.Length - 6));   // die act u32
        Assert.Equal(1, ours[^2]);                                          // reason u8: a hit
    }

    [Fact]
    public async Task CharDie_on_player_death_carries_resurrect_dat_timers()
    {
        const long t = Now + 50_000;
        var session = await EnterAscendedAsync(new FakeCharacterStore(), t);
        var death = session.NpcHitsPlayer(session.Resource.HpMax * 10, t + 1000, killerKey: 2001, killerSkill: 11010000);
        var kill = death.First(p => GameSessionTestHelpers.Op(p) == 0x8d04);
        Assert.Equal(0u, BitConverter.ToUInt32(kill, kill.Length - 6));
        Assert.Equal(1, kill[^2]);
    }

    [Fact]
    public async Task Short_CharRespawn_RQ_revives_at_respawn_point()
    {
        const long t = Now + 150_000;
        var session = await EnterAscendedAsync(new FakeCharacterStore(), t);
        session.NpcHitsPlayer(session.Resource.HpMax * 10, t + 1000, killerKey: 2001);
        Assert.True(session.Dead);
        var rez = await session.HandleAsync(new PacketWriter().U16(RidePackets.RespawnRq).U8(RidePackets.RespawnAuto).U8(0).ToArray(), t + 2000);
        Assert.Contains(rez, p => p.SequenceEqual(RidePackets.RespawnReply(0)));
        Assert.False(session.Dead);
        // The own disappear and the map load carry reason 4 and teleport effect 0x4f (community recording 090856 #104116, #104118).
        Assert.Contains(rez, p => p.SequenceEqual(PlayerPackets.CharDisappear(session.PlayerKey, 4, 0x4f)));
        var load = rez.First(p => GameSessionTestHelpers.Op(p) == 0x3621);
        var expected = GamePackets.PrepareEnterMap(1, 0, Vector3d.Zero, 0, reason: 4, effect: 0x4f);
        var head = PacketWriter.Opcode(0x3621).I32(0).I32(0).I64(0).Vec3(Vector3d.Zero).F32(0).ToArray().Length;   // ticket .. direction
        Assert.Equal(expected[head..], load[head..]);
    }

    [Fact]
    public void Auto_resurrect_delay_follows_dat_and_development_override()
    {
        var data = GameData.Embedded;
        Assert.Equal(60_000, data.AutoResurrectMs(1));
        Assert.Equal(0, data.DefaultResurrectWaitMs(1));
        var dev = new GameSession(data, new GameSessionOptions { AutoResurrectMsOverride = 60_000 }, new Random(1));
        Assert.Equal(60_000, dev.ResurrectAutoDelayMs);
    }

    [Fact]
    public async Task Manual_default_respawn_replies_and_clears_dead()
    {
        const long t = Now + 100_000;
        var session = await EnterAscendedAsync(new FakeCharacterStore(), t);
        session.NpcHitsPlayer(session.Resource.HpMax * 10, t + 1000, killerKey: 2001);
        Assert.True(session.Dead);
        var rez = await session.HandleAsync(new PacketWriter().U16(RidePackets.RespawnRq).U8(RidePackets.RespawnAuto).I64(0).ToArray(), t + 2000);
        Assert.Contains(rez, p => p.SequenceEqual(RidePackets.RespawnReply(0)));
        Assert.False(session.Dead);
        Assert.Contains(rez, p => GameSessionTestHelpers.Op(p) == 0x3647);
        Assert.Contains(rez, p => GameSessionTestHelpers.Op(p) == 0x3621);
    }

    [Fact]
    public async Task Auto_resurrect_includes_respawn_reply()
    {
        const long t = Now + 200_000;
        var store = new FakeCharacterStore();
        var session = await EnterAscendedAsync(store, t,
            new GameSessionOptions
            {
                AutoResurrectMsOverride = 5_000,
                World = new SharedWorld(GameData.Embedded),
                CharacterLevel = 10,
                WorldId = store.WorldId,
                TrainingDummy = true,
            });
        session.NpcHitsPlayer(session.Resource.HpMax * 10, t + 1000, killerKey: 2001);
        var tick = await session.TickAsync(t + 1000 + 5_000);
        Assert.Contains(tick, p => p.SequenceEqual(RidePackets.RespawnReply(0)));
        Assert.False(session.Dead);
    }

    [Fact]
    public async Task Dungeon_revives_count_per_character_and_staff_revives_do_not()
    {
        const long t = Now + 400_000;
        var store = new FakeCharacterStore();
        FakeCharacterStore.SeedExpeditionItemLevel(store);
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, t, level: 55);
        await GameSessionTestHelpers.LoadInventoryAsync(session, t + 10);
        session.Store = null;   // past the entry gates (level 99, item level 2400)
        session.EnterDungeonById(600004, t + 50);   // KraoCave_Hard: Dungeon.dat RebirthLimitCount 3
        session.Store = store;
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t + 60);
        var run = Assert.IsType<DungeonCopy>(session.Copy);
        var key = session.CharacterId!.Value.ToString();

        session.NpcHitsPlayer(session.Resource.HpMax * 10, t + 1_000);
        Assert.True(session.Dead);
        session.StaffRestore(t + 2_000);
        Assert.False(session.Dead);
        Assert.False(run.Revives.ContainsKey(key));

        session.NpcHitsPlayer(session.Resource.HpMax * 10, t + 3_000);
        await session.HandleAsync(new PacketWriter().U16(RidePackets.RespawnRq).U8(RidePackets.RespawnAuto).I64(0).ToArray(), t + 300_000);
        Assert.False(session.Dead);
        Assert.Equal(1, run.Revives[key]);
    }

    [Fact]
    public async Task Buffs_end_at_death_with_expire_reason_0x13()
    {
        const long t = Now + 500_000;
        var session = await EnterAscendedAsync(new FakeCharacterStore(), t);
        session.ReceiveBuffs([(170900001, 8000, 8000, false)], 17090000, t);
        var death = session.NpcHitsPlayer(session.Resource.HpMax * 10, t + 1000, killerKey: 2001);
        var removed = death.First(p => GameSessionTestHelpers.Op(p) == 0x382c);
        Assert.Equal(0x13, removed[^1]);
        Assert.DoesNotContain(170900001, session.ActiveBuffs);
    }

    [Fact]
    public async Task An_npc_kill_names_the_player_outside_a_copy_and_nobody_inside()
    {
        const long t = Now + 600_000;
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, t, level: 55);
        var open = session.NpcKilled(2001, 11010000, session.PlayerKey);
        Assert.Equal(0, open[^1]);   // die type 0
        session.Store = null;
        session.EnterDungeonById(600004, t + 50);
        session.Store = store;
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t + 60);
        Assert.NotNull(session.Copy);
        var inside = session.NpcKilled(2001, 11010000, session.PlayerKey);
        Assert.Equal(GamePackets.Kill(2001, 11010000, 0, 0, "", dieType: 2, reason: 1), inside);
    }

    [Fact]
    public async Task Spiritstone_revive_is_in_place_without_a_map_load_in_the_recorded_order()
    {
        const long t = Now + 700_000;
        var store = new FakeCharacterStore();
        var session = await EnterAscendedAsync(store, t);
        store.Items.Add(FakeCharacterStore.BagItem(502760001, 2, 82));
        await GameSessionTestHelpers.LoadInventoryAsync(session, t);
        var fell = session.Position!.Value;
        session.NpcHitsPlayer(session.Resource.HpMax * 10, t + 1000, killerKey: 2001);
        var stone = await session.HandleAsync(new PacketWriter().U16(RidePackets.RespawnRq).U8(RidePackets.RespawnItem).I64(82).ToArray(), t + 2000);
        Assert.False(session.Dead);
        Assert.Equal(fell, session.Position);
        Assert.Equal(session.Resource.HpMax, session.Resource.Hp);
        Assert.DoesNotContain(stone, p => GameSessionTestHelpers.Op(p) == 0x3621);
        Assert.Equal(RidePackets.RespawnReply(0), stone[0]);
        var abnormal = stone.FindIndex(p => GameSessionTestHelpers.Op(p) == 0x382a);
        var reborn = stone.FindIndex(p => p.SequenceEqual(RidePackets.Reborn(session.PlayerKey)));
        var bars = stone.FindIndex(p => GameSessionTestHelpers.Op(p) == 0x8d00);
        Assert.True(abnormal > 0 && reborn > abnormal && bars > reborn && bars < stone.Count - 1, string.Join(" ", stone.Select(p => GameSessionTestHelpers.Op(p).ToString("x4"))));
        Assert.Contains(10003, session.ActiveBuffs);
    }

    static bool IsLoadProtection(byte[] p) =>
        GameSessionTestHelpers.Op(p) == 0x382a && p.AsSpan().IndexOf(BitConverter.GetBytes(10002)) >= 0;

    [Fact]
    public async Task Every_map_load_has_abnormal_10002_after_the_passives_before_the_stats()
    {
        const long t = Now + 800_000;
        var session = new GameSession(GameData.Embedded,
            new GameSessionOptions { World = new SharedWorld(GameData.Embedded), CharacterLevel = 10, TrainingDummy = true }, new Random(31))
        { Store = new FakeCharacterStore() };
        await session.HandleAsync(GameSessionTestHelpers.Login(), t);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), t);
        var wireId = ((FakeCharacterStore)session.Store!).States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), t);
        var entry = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t);
        await session.TickAsync(t + 9_000);   // the entry's 10002 is over: the teleport adds a new one
        session.Teleport(1010, GameSessionTestHelpers.MapStartOrDefault(1010), t + 10_000);
        var teleport = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t + 10_001);
        foreach (var load in new[] { entry, teleport })
        {
            var appear = load.FindIndex(p => GameSessionTestHelpers.Op(p) == 0x3646);
            var protection = load.FindIndex(IsLoadProtection);
            var stats = load.FindIndex(p => GameSessionTestHelpers.Op(p) == 0x364a);
            Assert.True(appear >= 0 && protection > appear && stats == protection + 1,
                string.Join(" ", load.Select(p => GameSessionTestHelpers.Op(p).ToString("x4"))));
            Assert.Single(load, IsLoadProtection);
        }
    }

    [Fact]
    public async Task Load_complete_after_revive_restores_hp()
    {
        const long t = Now + 300_000;
        var session = await EnterAscendedAsync(new FakeCharacterStore(), t);
        session.NpcHitsPlayer(session.Resource.HpMax * 10, t + 1000, killerKey: 2001);
        await session.HandleAsync(new PacketWriter().U16(RidePackets.RespawnRq).U8(RidePackets.RespawnAuto).I64(0).ToArray(), t + 2000);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t + 2100);
        Assert.True(session.Resource.Hp > 0);
        Assert.Equal(session.Resource.HpMax, session.Resource.Hp);
    }

    [Fact]
    public void Field_respawn_is_the_respawn_point_nearest_to_the_fall()
    {
        // Map 1010 (Light field): its *_Respawn points are the Daeva Hunter spots. The old pick was the first by name
        // (DaevaHunter_L1_10_Respawn) wherever the character fell.
        var data = GameData.Embedded;
        var points = data.MapPoints(1010).Where(p => p.Key.EndsWith("_Respawn", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(points.Count > 2);
        foreach (var (name, p) in points)
        {
            var fell = new Vector3d(p[0] + 30, p[1] - 20, p[2]);
            Assert.Equal(name, data.MapRespawn(1010, 1, fell)?.Name);
        }
        Assert.Equal(points.Select(p => p.Key).Order(StringComparer.Ordinal).First(), data.MapRespawn(1010, 1)?.Name);
    }

    [Fact]
    public void Respawn_prefers_the_race_points_then_the_nearest()
    {
        // Map 20 (Reshanta): Abyss_Reshanta_A_Dark_Respawn, _Light_Respawn and a plain _Respawn.
        var data = GameData.Embedded;
        var dark = data.MapPoints(20)["Abyss_Reshanta_A_Dark_Respawn"];
        var nearDark = new Vector3d(dark[0], dark[1], dark[2]);
        Assert.Equal("Abyss_Reshanta_A_Light_Respawn", data.MapRespawn(20, 1, nearDark)?.Name);
        Assert.Equal("Abyss_Reshanta_A_Dark_Respawn", data.MapRespawn(20, 2, nearDark)?.Name);
    }

    [Fact]
    public async Task Revives_and_refusals_are_logged()
    {
        const long t = Now + 600_000;
        var lines = new List<string>();
        var before = GameSession.ReviveLog;
        GameSession.ReviveLog = lines.Add;
        try
        {
            var session = await EnterAscendedAsync(new FakeCharacterStore(), t);
            session.NpcHitsPlayer(session.Resource.HpMax * 10, t + 1000, killerKey: 2001);
            var refused = await session.HandleAsync(new PacketWriter().U16(RidePackets.RespawnRq).U8(RidePackets.RespawnItem).I64(424242).ToArray(), t + 1500);
            Assert.Contains(refused, p => p.SequenceEqual(RidePackets.RespawnReply(1)));
            Assert.Contains(lines, l => l.Contains("item refused: no item with uid 424242"));
            await session.HandleAsync(new PacketWriter().U16(RidePackets.RespawnRq).U8(RidePackets.RespawnAuto).I64(0).ToArray(), t + 2000);
            Assert.Contains(lines, l => l.Contains($"respawn type {RidePackets.RespawnAuto}"));
            Assert.Contains(lines, l => l.Contains(" to ") && l.Contains("fell at ("));
        }
        finally { GameSession.ReviveLog = before; }
    }
}
