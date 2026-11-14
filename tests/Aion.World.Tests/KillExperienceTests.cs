using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Kill experience (GameSession.Experience.cs), CANDIDATE: the A2Core fork of addiboii's fit of 1425 recorded kill rewards.
/// Grade 3 pays 390.8 x 1.085^monster level, grades 1/2/4 5/13, 8/13, 18/13 of it, less from a gap of +6, none below
/// own level 10 or inside an instance.
/// </summary>
public sealed class KillExperienceTests
{
    const long Now = 1_760_300_000_000;

    static bool Near(long value, long recorded) => Math.Abs(value - recorded) <= recorded * 0.0025;

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(9).ToArray();

    [Fact]
    public void Formula_matches_the_recorded_rewards()
    {
        Assert.Equal(884, GameSession.KillReward(10, 10, 3, false));
        // 300 for each level-25 grade 3 kill by a far higher character (the fork's recording 2026-11-05).
        Assert.Equal(300, GameSession.KillReward(36, 25, 3, false));
        // Within 0.25 % of the fork's recorded values.
        Assert.True(Near(GameSession.KillReward(30, 25, 3, false), 2998));
        Assert.True(Near(GameSession.KillReward(30, 25, 2, false), 1845));
        Assert.True(Near(GameSession.KillReward(39, 34, 4, false), 8650));
    }

    [Theory]
    [InlineData(1, 5 / 13.0)]
    [InlineData(2, 8 / 13.0)]
    [InlineData(3, 1.0)]
    [InlineData(4, 18 / 13.0)]
    [InlineData(5, 18 / 13.0)]
    public void Grade_scales_the_grade_3_reward(int grade, double factor)
    {
        var grade3 = GameSession.KillExperienceBase * Math.Pow(GameSession.KillExperienceGrowth, 20);
        Assert.Equal((long)Math.Round(grade3 * factor), GameSession.KillReward(20, 20, grade, false));
    }

    [Theory]
    [InlineData(-6, 1.0)]
    [InlineData(0, 1.0)]
    [InlineData(5, 1.0)]
    [InlineData(6, 0.8)]
    [InlineData(7, 0.6)]
    [InlineData(8, 0.4)]
    [InlineData(9, 0.2)]
    [InlineData(10, 0.1)]
    [InlineData(24, 0.1)]
    public void Level_gap_reduces_weaker_monsters(int gap, double factor) =>
        Assert.Equal(factor, GameSession.KillGapFactor(gap));

    [Fact]
    public void Stronger_monsters_pay_their_own_level_in_full() =>
        Assert.Equal(GameSession.KillReward(30, 30, 3, false), GameSession.KillReward(10, 30, 3, false));

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(9)]
    public void None_below_level_10(int ownLevel)
    {
        Assert.Equal(0, GameSession.KillReward(ownLevel, ownLevel, 3, false));
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = ownLevel }, new Random(1));
        Assert.Equal(0, session.KillExperience(ownLevel));
    }

    [Fact]
    public void None_inside_an_instance() =>
        Assert.Equal(0, GameSession.KillReward(45, 40, 3, true));

    [Fact]
    public async Task Kill_in_a_dungeon_copy_pays_nothing()
    {
        const int dungeon = 900001;
        Assert.True(GameSession.IsInstanceMapId(dungeon));
        var store = new FakeCharacterStore { Progress = (30, 0) };
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 30, rng: new Random(4));
        Assert.True(session.KillExperience(25) > 0);
        session.Teleport(dungeon, Vector3d.Zero, Now + 1);
        Assert.NotEqual(0, session.InstanceKey);
        Assert.Equal(0, session.KillExperience(25));
    }

    [Fact]
    public async Task Field_kill_sends_the_npc_reward()
    {
        var store = new FakeCharacterStore { Progress = (25, 0) };
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(8));
        var dummy = session.Targets![Combat.TrainingDummyKey];
        dummy.Hp = 1;
        var before = session.Experience;
        var expected = GameSession.KillReward(25, Combat.SandbagLevel, dummy.Grade, false);
        Assert.Equal(88, expected);   // level-10 grade 3 sandbag, gap +15: 884 x 0.1

        const int skillId = 11020000;
        var castAt = Now + 1000;
        var replies = await session.HandleAsync(Cast(skillId, dummy.Key, dummy.Position), castAt);
        replies.AddRange(await GameSessionTestHelpers.AfterSkillHitsAsync(session, castAt, 3000));

        Assert.True(dummy.Dead);
        Assert.Equal(before + expected, session.Experience);
        Assert.Contains(replies, p => p.SequenceEqual(GamePackets.ExpUpdate(before + expected, expected, 1)));
    }

    [Fact]
    public async Task Kill_level_up_does_not_refill_hp()
    {
        var store = new FakeCharacterStore { Progress = (12, 0) };
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 12, rng: new Random(6));
        session.SetResource('H', 100);
        session.ShareExperience(GameData.Embedded.NeedExp(12));
        var replies = await session.TickAsync(Now + 10);
        Assert.Equal(13, session.Level);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x8d08);
        Assert.Equal(100, session.Resource.Hp);
        Assert.True(session.Resource.HpMax > 100);
    }
}
