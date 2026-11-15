using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// The daily wave dungeon (DungeonWave_0001, 900001): the 90 s countdown, the points per kill (DungeonPoint.dat), the clear
/// with the point reward when the countdown ends (RewardDungeon.dat), and the respawn that leaves (ResurrectionType Restart).
/// Bytes OBSERVED in the official run of 2026-11-12 (capture-vm official-20261112-r2, timeline-13328).
/// </summary>
public sealed class DailyDungeonTests
{
    const int Dungeon = 900001;
    const long Now = 1_760_012_300_000;
    static int Op(byte[] p) => p[0] | (p[1] << 8);
    static string Hex(byte[] p) => Convert.ToHexString(p).ToLowerInvariant();
    /// <summary>The hostile NPCs still standing (the test world's training dummy is not the dungeon's).</summary>
    static List<string> Standing(GameSession session) =>
        session.Targets.Values.Where(t => t.Hostile && !t.Dead && t.Name != "Training Dummy").Select(t => t.Name).ToList();

    [Fact]
    public void Table_points_and_rewards_match_the_official_run()
    {
        var daily = DailyDungeon.Of(Dungeon)!;
        Assert.Equal(85, daily.Wave);   // WaveData.dat DungeonWave_0001_Wave_L_B_03, the id on 0x6014
        Assert.Equal(90, daily.Seconds);
        Assert.True(daily.RespawnLeaves);
        Assert.Equal(40, daily.KillPoint(2701418, "NormalMonster"));   // NormalMonster_04: +40 per kill officially
        Assert.Equal(50, daily.KillPoint(2701423, "NormalMonster"));   // NormalMonster_09: the one +50
        Assert.Equal(1000, daily.KillPoint(2701427, "EliteMonster"));  // KillNpcSubType EliteMonster
        Assert.Equal(0, daily.KillPoint(2701690, "NormalMonster"));    // the turrets have no row
        Assert.Equal([(930100023, 4300L)], daily.Reward(1730, dark: false));   // official: 1730 points paid 4300 CoinEnchant
        Assert.Equal([(930100023, 4300L)], daily.Reward(1730, dark: true));
        Assert.Equal([(930100023, 10000L)], daily.Reward(12_000, dark: false));
        Assert.Empty(daily.Reward(99, dark: false));
        Assert.Equal(0, DailyDungeon.Of(900002)!.Seconds);   // no capture for the other two dailies: no countdown
    }

    [Fact]
    public void The_buff_choice_names_match_the_official_screen()
    {
        // OBSERVED 2026-11-15 (official, three entries of 900001): the 0x6105 ids against the choice screen, left to right.
        var daily = DailyDungeon.Of(Dungeon)!;
        Assert.True(daily.HasBuffChoice);
        Assert.Equal([1, 2, 3], daily.BuffSlots!.Select(s => s.Slot));
        foreach (var (id, name) in new[] { (10, "Elite Hunter"), (6, "Ignoring the Weak"), (4, "Footwork"), (1004, "Poisoned by Mana"),
                     (9, "Ignoring the Elite"), (1, "Slayer"), (7, "Worm Carnage"), (1006, "Targeting Weakness") })
            Assert.Equal(name, daily.Buff(id)!.Name);
        Assert.Equal("Special", daily.Buff(10)!.Grade);
        Assert.Equal(19003001, daily.Buff(1)!.Abnormal);
        var drawn = daily.DrawBuffs(new Random(7));
        Assert.Equal([1, 2, 3], drawn.Select(d => d.Slot));
        Assert.Equal(3, drawn.Select(d => d.BuffId).Distinct().Count());
        Assert.False(DailyDungeon.Of(900002)!.HasBuffChoice);
    }

    [Fact]
    public void Packets_match_the_official_bytes()
    {
        Assert.Equal("0461a1bb0d00c20d", Hex(GameSession.DungeonPoint(Dungeon, 1730)));
        Assert.Equal("0461a1bb0d0000", Hex(GameSession.DungeonPoint(Dungeon, 0)));
        Assert.Equal("026103a1bb0d00015a00000000000000c20d0137337037cc10000000000000",
            Hex(GameSession.WaveDungeonCompleteResult(Dungeon, 1, 90, 1730, [(930100023, 4300)])));
        Assert.Equal("146055000000995717d1a101000000", Hex(GameSession.WaveCountdown(85, 0x01a1d1175799, enterMap: false)));
    }

    static async Task<(GameSession Session, List<byte[]> Entry, FakeCharacterStore Store)> EnterAsync()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };   // MapEntrance: level 30 or more
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 45);
        var entry = await MapEventScenarioHelpers.EnterDungeonByIdAsync(session, Dungeon, Now + 200);
        Assert.True(session.MapId == Dungeon, string.Join(" | ", session.MapEventNotes));
        return (session, entry, store);
    }

    [Fact]
    public async Task The_countdown_runs_90_s_kills_count_points_and_its_end_pays_the_point_reward()
    {
        var (session, prepare, store) = await EnterAsync();
        // Prepare (1) at the entry with no monsters; Process, the countdown and the waves 5 s later.
        Assert.Equal(GameSession.DungeonStatePrepare, prepare.Last(p => Op(p) == GameSession.DungeonStateNt)[6]);
        Assert.DoesNotContain(prepare, p => Op(p) == GameSession.WaveCountdownNt);
        var go = session.Copy!.ProcessAtMs;
        Assert.DoesNotContain(await session.TickAsync(go - 100), p => Op(p) == GameSession.WaveCountdownNt);
        Assert.DoesNotContain(Standing(session), n => n.Contains("Wave_L"));   // the map's lasers stand from the entry
        var entry = await session.TickAsync(go + 10);
        var state = entry.Last(p => Op(p) == GameSession.DungeonStateNt);
        Assert.Equal(GameSession.DungeonStateProcess, state[6]);
        var start = BitConverter.ToInt64(state, 7);
        Assert.Equal(0, BitConverter.ToInt64(state, 15));   // Process carries no end (official); the countdown does
        var countdown = Assert.Single(entry, p => Op(p) == GameSession.WaveCountdownNt);
        Assert.Equal(85, BitConverter.ToInt32(countdown, 2));
        var end = BitConverter.ToInt64(countdown, 6);
        Assert.Equal(start + 90_000, end);
        Assert.Contains(prepare, p => p.SequenceEqual(GameSession.DungeonPoint(Dungeon, 0)));

        await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "DungeonWave_0001_Sub_MapEvent", go + 300, attempts: 16);
        var daily = DailyDungeon.Of(Dungeon)!;
        var skill = MapEventScenarioHelpers.BasicAttackSkill(session);
        var now = go + 2_000;
        var expected = 0;
        var sent = new List<byte[]>();
        foreach (var target in session.Targets.Values.Where(t => t.Name.Contains("Wave_L_21") && !t.Dead).Take(10).ToList())
        {
            await MapEventScenarioHelpers.SyncPositionAsync(session, target.Position with { X = target.Position.X + 120 }, now);
            target.Hp = 1;
            sent.AddRange(await MapEventScenarioHelpers.CombatCastAsync(session, skill, target.Key, target.Position, now += 50));
            now += MapEventScenarioHelpers.SkillHitsLandMs(skill) + 100;
            sent.AddRange(await session.TickAsync(now));
            expected += daily.KillPoint(target.NpcId, target.SubType);
        }
        Assert.True(expected >= 100, $"{expected} points");
        Assert.Equal(expected, session.Copy!.Points);
        Assert.Contains(sent, p => p.SequenceEqual(GameSession.DungeonPoint(Dungeon, expected)));

        // Not over before the end; at the end: the result with the reward, the points, Complete for the 30 s hold.
        Assert.DoesNotContain(await session.TickAsync(end - 100), p => Op(p) == GameSession.WaveDungeonCompleteResultNt);
        var over = await session.TickAsync(end + 10);
        var reward = daily.Reward(expected, dark: false);
        Assert.NotEmpty(reward);
        var result = over.FindIndex(p => Op(p) == GameSession.WaveDungeonCompleteResultNt);
        Assert.True(result >= 0);
        Assert.Equal(GameSession.WaveDungeonCompleteResult(Dungeon, GameSession.WaveDungeonGrade, 90, expected, reward), over[result]);
        var complete = over.First(p => Op(p) == GameSession.DungeonStateNt);
        Assert.Equal(GameSession.DungeonStateComplete, complete[6]);
        Assert.Equal(30_000, BitConverter.ToInt64(complete, 15) - BitConverter.ToInt64(complete, 7));
        // The reward reaches the bag (EIncreaseReason 58), the monsters left standing go, the waves stop.
        Assert.Contains(store.Items, i => i.TemplateId == reward[0].ItemId && i.Quantity == reward[0].Count);
        Assert.Empty(Standing(session));
        Assert.All(session.MapEventRuns, r => Assert.True(r.Finished));
        await session.TickAsync(end + 5_000);
        Assert.Empty(Standing(session));
        // No second result; End (4) after the hold, for 600 s.
        var later = await session.TickAsync(end + 30_100);
        Assert.DoesNotContain(later, p => Op(p) == GameSession.WaveDungeonCompleteResultNt);
        var ended = later.First(p => Op(p) == GameSession.DungeonStateNt);
        Assert.Equal(GameSession.DungeonStateEnd, ended[6]);
        Assert.Equal(1, store.Items.Count(i => i.TemplateId == reward[0].ItemId && i.Quantity == reward[0].Count));
    }

    [Fact]
    public async Task A_death_ends_the_run_at_once_and_the_exit_revives()
    {
        // User report 2026-11-15: the clock ran on after the death and the Exit button left the player dead outside.
        var (session, _, _) = await EnterAsync();
        var go = session.Copy!.ProcessAtMs;
        await session.TickAsync(go + 10);
        session.NpcHitsPlayer(session.Resource.HpMax * 10, go + 3_000, killerKey: 2001);
        Assert.True(session.Dead);
        var over = await session.TickAsync(go + 3_100);
        Assert.Contains(over, p => Op(p) == GameSession.WaveDungeonCompleteResultNt);
        Assert.True(session.Copy!.Cleared);
        var exit = await session.HandleAsync([0x04, 0x40], go + 5_000);
        Assert.Contains(exit, p => Op(p) == 0x3621);
        Assert.False(session.Dead);
        Assert.Equal(0, session.InstanceKey);
        Assert.Equal(session.Resource.HpMax, session.Resource.Hp);
    }

    [Fact]
    public async Task The_respawn_leaves_the_daily_dungeon()
    {
        var (session, _, _) = await EnterAsync();
        var t = Now + 3_000;
        session.NpcHitsPlayer(session.Resource.HpMax * 10, t, killerKey: 2001);
        Assert.True(session.Dead);
        var rez = await session.HandleAsync(new PacketWriter().U16(RidePackets.RespawnRq).U8(RidePackets.RespawnKisk).I64(0).ToArray(), t + 2_000);
        Assert.Contains(rez, p => p.SequenceEqual(RidePackets.RespawnReply(0)));
        Assert.False(session.Dead);
        Assert.Equal(0, session.InstanceKey);
        var load = rez.First(p => Op(p) == 0x3621);
        Assert.NotEqual(Dungeon, BitConverter.ToInt32(load, 6));
    }
}
