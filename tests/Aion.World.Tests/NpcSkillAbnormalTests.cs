using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Monster skills put their abnormals on the player (#83): 0x382a mask 0x13, 0x3816 knockdown, DoT 0x3805 mask 0x0a, slow 0x364a.</summary>
public sealed class NpcSkillAbnormalTests
{
    static readonly long Now = 1_700_000_100_000;
    const int Npc = 39_586;
    static readonly Vector3d NpcAt = new(100, 200, 300);

    static Task<GameSession> Enter() => GameSessionTestHelpers.EnterWorldAsync(new FakeCharacterStore(), Now, level: 55, rng: new Random(4));

    static PacketReader AbnormalEntry(byte[] add, int key)
    {
        var r = new PacketReader(add) { Offset = 2 };
        Assert.Equal(key, (int)r.Varint());
        return r;
    }

    [Fact]
    public async Task Monster_skill_abnormal_names_the_npc_its_skill_level_and_position()
    {
        var session = await Enter();
        // 1224900 puts 12000141 (Dot_NormalCalc every 2000 ms) on for 20 s (OBSERVED 2026-11-04 i=19704).
        var hit = session.NpcHitsPlayer(100, Now + 100, killerKey: Npc, killerSkill: 1224900, hitSeq: 1, casterAt: NpcAt);
        var add = Assert.Single(hit, p => GameSessionTestHelpers.Op(p) == 0x382a);
        Assert.Contains(12000141, session.ActiveBuffs);
        var hex = Convert.ToHexString(add);
        var r = AbnormalEntry(add, session.PlayerKey);
        Assert.Equal(1, (int)r.Varint());   // one abnormal
        Assert.Equal(0x13, r.U8());
        Assert.Contains(Convert.ToHexString(NativeVarint.Encode((uint)Npc)) + "01" + Convert.ToHexString(BitConverter.GetBytes(1224900)), hex);
        Assert.EndsWith(Convert.ToHexString(BitConverter.GetBytes(100f)) + Convert.ToHexString(BitConverter.GetBytes(200f)) + Convert.ToHexString(BitConverter.GetBytes(300f)), hex);

        var again = session.NpcHitsPlayer(100, Now + 200, killerKey: Npc, killerSkill: 1224900, hitSeq: 2, casterAt: NpcAt);
        Assert.DoesNotContain(again, p => GameSessionTestHelpers.Op(p) is 0x382a or 0x382b);
    }

    [Fact]
    public async Task Monster_damage_over_time_ticks_0x0a_every_interval_with_the_npc_as_caster()
    {
        var session = await Enter();
        session.NpcHitsPlayer(100, Now + 100, killerKey: Npc, killerSkill: 1224900, hitSeq: 1, casterAt: NpcAt);
        var ticks = new List<byte[]>();
        for (var t = 200; t <= 4_200; t += 100) ticks.AddRange(await session.TickAsync(Now + t));
        var dot = ticks.Where(p => GameSessionTestHelpers.Op(p) == 0x3805).ToList();
        Assert.Equal(2, dot.Count);   // +2000 and +4000
        var amount = (int)Math.Round(100 * AbnormalCombat.NpcDotShareOfHit);
        foreach (var p in dot)
        {
            var r = new PacketReader(p) { Offset = 2 };
            Assert.Equal(session.PlayerKey, (int)r.Varint());
            Assert.Equal(0x0a, r.U8());
            Assert.Equal(Npc, (int)r.Varint());
            r.Varint();
            Assert.Equal(120001411, r.I32());
            Assert.Equal(amount, (int)r.Varint());
            Assert.Equal(1224900, r.I32());
        }
        // Each tick takes the amount off the HP and sends it (0x8d00 right after the 0x3805).
        foreach (var p in dot) Assert.Equal(0x8d00, GameSessionTestHelpers.Op(ticks[ticks.IndexOf(p) + 1]));
    }

    [Fact]
    public async Task Knockdown_instant_sends_0x3816_after_the_abnormal_at_the_players_position()
    {
        var session = await Enter();
        // 1216520 puts 12165201 (KnockdownInstant) on for 2.8 s.
        var hit = session.NpcHitsPlayer(100, Now + 100, killerKey: Npc, killerSkill: 1216520, hitSeq: 1, casterAt: NpcAt);
        Assert.Contains(hit, p => GameSessionTestHelpers.Op(p) == 0x382a);
        Assert.DoesNotContain(hit, p => GameSessionTestHelpers.Op(p) == 0x3816);
        var later = await session.TickAsync(Now + 100 + NpcDotShortTick());
        var move = Assert.Single(later, p => GameSessionTestHelpers.Op(p) == 0x3816);
        Assert.Equal(PlayerPackets.AbnormalMoveInstant(session.PlayerKey, Npc, 12165201 * 10 + 1, session.Position!.Value, 4), move);
        Assert.Equal([0x04, 0x01, 0x01], move[^3..]);
    }

    static int NpcDotShortTick() => GameSession.KnockdownInstantDelayMs;

    [Fact]
    public async Task Monster_slow_lowers_move_speed_with_0x364a_stat_205()
    {
        var session = await Enter();
        // 1217770 puts 12172701 (StatChange MoveSpeed -3000) on for 10 s.
        var hit = session.NpcHitsPlayer(100, Now + 100, killerKey: Npc, killerSkill: 1217770, hitSeq: 1, casterAt: NpcAt);
        var stats = hit.Where(p => GameSessionTestHelpers.Op(p) == 0x364a).ToList();
        Assert.Contains(GamePackets.CharStatsUpdate(session.PlayerKey, [(GameSession.MoveSpeedStat, -3000)], session.Resource!.HpMax), stats);
        Assert.DoesNotContain(hit, p => GameSessionTestHelpers.Op(p) == 0x364b);
    }
}
