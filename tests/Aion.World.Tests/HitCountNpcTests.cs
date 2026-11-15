using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Hit-count monsters (NpcData bHitCountBased) and NPCs players cannot hit (#84).</summary>
public sealed class HitCountNpcTests
{
    static readonly long Now = 1_700_000_100_000;

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(9).ToArray();

    [Fact]
    public void Hit_count_template_has_seven_hp_and_its_flag()
    {
        // DraTotem 2290017 (the HQ002 map event's totem) and 2290030 (OBSERVED 7/7 on 2026-11-15) are bHitCountBased.
        foreach (var id in new[] { 2290017, 2290030 })
        {
            var spawn = GameData.Embedded.NpcFromTemplate(id, "totem", [0, 0, 0], 0);
            Assert.NotNull(spawn);
            Assert.True(spawn.HitCount);
            Assert.Equal(7, Combat.HpFor(spawn));
        }
        Assert.False(GameData.Embedded.NpcFromTemplate(2400032, "dummy", [0, 0, 0], 0)!.HitCount);
    }

    [Fact]
    public void Every_hit_on_a_hit_count_npc_lands_as_one_never_critical()
    {
        var totem = new Target { Key = 2001, Name = "totem", Hp = 7, HpMax = 7, HitCount = true };
        var plain = new Target { Key = 2002, Name = "mob", Hp = 700, HpMax = 700 };
        IReadOnlyList<Hit> hits = [new Hit(250, true, 1), new Hit(90, false, 2)];
        Assert.All(totem.Landed(hits), h => Assert.Equal((1, false), (h.Amount, h.Critical)));
        Assert.Same(hits, plain.Landed(hits));
        Assert.Equal(1, totem.Landed(400));
        Assert.Equal(400, plain.Landed(400));
    }

    [Fact]
    public async Task Player_hits_take_a_hit_count_npc_down_one_hp_at_a_time()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now, level: 25, rng: new Random(5));
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var map = Assert.IsAssignableFrom<IDictionary<int, Target>>(session.Targets);
        var totem = map[880_401] = new Target
        {
            Key = 880_401, Name = "totem", NpcId = 2290017, Hp = 7, HpMax = 7, HitCount = true, Relationship = 6,
            Position = dummy.Position with { X = dummy.Position.X + 50 }, Radius = 100,
        };
        var all = new List<byte[]>(await session.HandleAsync(Cast(11020000, totem.Key, totem.Position), Now + 50));
        for (var t = 100; t <= 3000; t += 100) all.AddRange(await session.TickAsync(Now + 50 + t));
        Assert.Equal(6, totem.Hp);
        Assert.Contains(all, p => GameSessionTestHelpers.Op(p) == 0x3804);
    }

    [Fact]
    public async Task Area_hits_skip_untouchable_npcs_but_a_picked_one_is_hit()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now, level: 25, rng: new Random(5));
        var at = session.Position!.Value;
        var map = Assert.IsAssignableFrom<IDictionary<int, Target>>(session.Targets);
        Target Add(int key, bool untouchable) => map[key] = new Target
        {
            Key = key, Name = "mob", NpcId = 2400032, Hp = 100_000, HpMax = 100_000, Relationship = 6, Untouchable = untouchable,
            Position = at with { X = at.X + 100 + key % 10 * 10 }, Radius = 100,
        };
        var wall = Add(880_501, untouchable: true);
        var mob = Add(880_502, untouchable: false);
        Assert.False(wall.Attackable);
        // A self-targeted cast (the caster's own key) hits the NPCs around it: the untouchable one is left alone.
        var all = new List<byte[]>(await session.HandleAsync(Cast(11020000, session.PlayerKey, at), Now + 50));
        for (var t = 100; t <= 3000; t += 100) all.AddRange(await session.TickAsync(Now + 50 + t));
        Assert.True(mob.Hp < mob.HpMax);
        Assert.Equal(wall.HpMax, wall.Hp);
        // Picked directly, it is hit (map events need some of them killed).
        all.AddRange(await session.HandleAsync(Cast(11020000, wall.Key, wall.Position), Now + 5_000));
        for (var t = 100; t <= 3000; t += 100) all.AddRange(await session.TickAsync(Now + 5_000 + t));
        Assert.True(wall.Hp < wall.HpMax);
    }

    [Fact]
    public void Companion_does_not_pick_an_untouchable_monster()
    {
        var companion = new Target { Key = 2150, Name = "N_Robin", Hp = 100, HpMax = 100, Position = Vector3d.Zero, WalkSpeed = 126, RunSpeed = 500,
            Relationship = 13, Aggressive = true, Function = "Companion" };
        var controller = new Target { Key = 2151, Name = "M_Controller", Hp = 100, HpMax = 100, Position = new Vector3d(300, 0, 0), Relationship = 11,
            Aggressive = true, Untouchable = true };
        var targets = new Dictionary<int, Target> { [2150] = companion, [2151] = controller };
        var ai = new NpcAi(new Random(6));
        var players = new List<(int Key, Vector3d Position)> { (100_001, new Vector3d(-200, 0, 0)) };
        ai.Tick(targets, players, Now, null);
        ai.Follow(companion, 100_001, true, Now);
        ai.Tick(targets, players, Now + 600, null);
        Assert.NotEqual(NpcAi.Mode.Chase, ai.Brains[2150].Mode);
    }
}
