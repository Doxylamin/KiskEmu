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
}
