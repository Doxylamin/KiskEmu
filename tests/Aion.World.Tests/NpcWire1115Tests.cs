using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>NPC wire corrections from the official recordings of 2026-11-04 and 2026-11-15 (#81).</summary>
public sealed class NpcWire1115Tests
{
    [Theory]
    [InlineData("1d378fbd0122f7d07cd07c01", 24207, -9, 0, 175.517578125)]   // 2026-11-04 i=7272: dy left out
    [InlineData("1d37949701240c9c3e9c3e01", 19348, 0, 12, 88.04443359375)]  // i=2303: dx left out
    [InlineData("1d37e1d7012014ed14ed01", 27617, 0, 0, 333.39111328125)]     // i=392741: neither
    public void Move_delta_leaves_out_zero_dx_and_dy(string hex, int key, int dx, int dy, double heading) =>
        Assert.Equal(Convert.FromHexString(hex), GamePackets.NpcMoveDelta(key, dx, dy, 0, heading, run: false));

    [Fact]
    public void Move_delta_with_both_deltas_keeps_both_bytes() =>
        Assert.Equal(Convert.FromHexString("1d37" + "b410" + "26" + "ff" + "1f" + "6650" + "6650" + "01"),
            GamePackets.NpcMoveDelta(2100, -1, 31, 0, 0x5066 * 360.0 / 65536, run: false));

    static (NpcAi Ai, Dictionary<int, Target> Map, Target Monster) Monster(bool aggressive)
    {
        var monster = new Target
        {
            Key = 2002, Name = "agrint", Hp = 665, HpMax = 665, Level = 7, Position = Vector3d.Zero, WalkSpeed = 100, RunSpeed = 200, Radius = 120,
            Aggressive = aggressive, Aggro = new AggroShape("Circle", 1000, 0, 0, 1000, "Upper"),
        };
        return (new NpcAi(new Random(2)), new Dictionary<int, Target> { [monster.Key] = monster }, monster);
    }

    [Fact]
    public void Monster_hit_first_sends_no_first_aggro_but_targets_the_attacker()
    {
        var (ai, map, monster) = Monster(aggressive: false);
        ai.Tick(map, new Vector3d(5000, 0, 0), 0);
        var engage = ai.Engage(map, monster, 100, attackerKey: 100_001, damage: 10);
        Assert.DoesNotContain(engage, p => GameSessionTestHelpers.Op(p) == 0x8d52);
        var combat = engage.FindIndex(p => p.SequenceEqual(GamePackets.CombatState(2002, true)));
        var target = engage.FindIndex(p => p.SequenceEqual(GamePackets.TargetUpdate(2002, 100_001)));
        Assert.True(combat >= 0 && target > combat, "0x8d21 on, then 0x3835 to the attacker");
    }

    [Fact]
    public void Monster_that_notices_the_player_sends_first_aggro_target_and_combat()
    {
        var (ai, map, _) = Monster(aggressive: true);
        var tick = ai.Tick(map, new Vector3d(5000, 0, 0), 0);
        tick = ai.Tick(map, new Vector3d(400, 0, 0), 100);
        Assert.True(tick[0].SequenceEqual(GamePackets.NpcFirstAggro(2002)));
        Assert.True(tick[1].SequenceEqual(GamePackets.TargetUpdate(2002, NpcAi.PlayerKey)));
        Assert.Contains(tick, p => p.SequenceEqual(GamePackets.CombatState(2002, true)));
    }
}
