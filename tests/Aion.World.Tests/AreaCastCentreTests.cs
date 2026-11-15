using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Self-targeted area casts: the client's location counts only within the skill's reach (#85, OURS anti-cheat).</summary>
public sealed class AreaCastCentreTests
{
    static readonly long Now = 1_700_000_100_000;
    const int Skill = 11020000;

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(9).ToArray();

    [Fact]
    public async Task Client_location_is_used_only_within_use_range_plus_tolerance()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now, level: 25, rng: new Random(5));
        var at = session.Position!.Value;
        var reach = Math.Max(0, GameData.Embedded.SkillUseRangeCm(Skill)) + GameSession.CastRangeToleranceCm;
        var near = at with { X = at.X + reach - 10 };
        var far = at with { X = at.X + reach + 500 };
        Assert.Equal(near, session.SelfCastCentre(Skill, near));
        Assert.Equal(at, session.SelfCastCentre(Skill, far));
    }

    [Fact]
    public async Task Self_cast_with_a_far_location_does_not_hit_the_monster_there()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now, level: 25, rng: new Random(5));
        var at = session.Position!.Value;
        var map = Assert.IsAssignableFrom<IDictionary<int, Target>>(session.Targets);
        var mob = map[880_601] = new Target
        {
            Key = 880_601, Name = "mob", NpcId = 2400032, Hp = 100_000, HpMax = 100_000, Relationship = 6,
            Position = at with { X = at.X + 5_000 }, Radius = 100,
        };
        var all = new List<byte[]>(await session.HandleAsync(Cast(Skill, session.PlayerKey, mob.Position), Now + 50));
        for (var t = 100; t <= 3000; t += 100) all.AddRange(await session.TickAsync(Now + 50 + t));
        Assert.Equal(mob.HpMax, mob.Hp);
    }
}
