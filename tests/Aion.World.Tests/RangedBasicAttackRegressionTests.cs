using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Regression (2026-11-02, after #455): ranged basic attacks and the Sorcerer Ice Chain -> Cold Wave chain were refused (cast_rules).
/// From A2Core-ChanAge (Artezianec); the out-of-range case is ours.</summary>
public sealed class RangedBasicAttackRegressionTests
{
    const long Now = 1_760_009_000_000;
    const int FlameArrow = 15_210_000;
    const int IceChain = 15_090_000;
    const int ColdWave = 15_100_000;
    const int Snipe = 14_020_000;

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(0).I32(skillId).U8(2).Varint(targetKey).F32(0)
            .F32((float)at.X).F32((float)at.Y).F32((float)at.Z).ToArray();

    static bool Accepted(IReadOnlyList<byte[]> replies) =>
        replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

    /// <summary>Enters the world <paramref name="offsetCm"/> away from the training dummy.</summary>
    static async Task<(GameSession Session, Target Dummy, Vector3d At)> EnterAsync(uint classId, string name, double offsetCm)
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var probe = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true }, new Random(1)) { Store = store };
        await probe.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await probe.HandleAsync(new PacketWriter().U16(0x361b).String(name).U8(1).U32(classId).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await probe.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await probe.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var dummy = probe.Targets![Combat.TrainingDummyKey];
        var at = new Vector3d(dummy.Position.X + offsetCm, dummy.Position.Y, dummy.Position.Z);
        // Move the caster next to the dummy at the requested distance.
        probe.SetPositionForTests(at);
        return (probe, dummy, at);
    }

    [Theory]
    [InlineData(7u, FlameArrow)]
    [InlineData(5u, Snipe)]
    public async Task Ranged_basic_attack_is_accepted_repeatedly_at_attack_speed_cadence(uint classId, int basic)
    {
        var (session, dummy, at) = await EnterAsync(classId, "RngBa" + classId, 1500);
        var t = Now + 5000;
        for (var i = 0; i < 6; i++)
        {
            Assert.True(Accepted(await session.HandleAsync(Cast(basic, dummy.Key, at), t)), $"swing {i}");
            t += session.HeldCastLockoutRemainingMs(t) + 50;
            t += 3500; // let any FixSlot combo window lapse so the root is cast again
        }
    }

    /// <summary>Skill.dat NeedSkillUseRange: Flame Arrow reaches 2000 cm (+150 tolerance + the dummy's radius), not 4000.</summary>
    [Theory]
    [InlineData(1500, true)]
    [InlineData(4000, false)]
    public async Task A_cast_out_of_its_use_range_is_refused(double offsetCm, bool accepted)
    {
        var (session, dummy, at) = await EnterAsync(7u, "RngOut" + (int)offsetCm, offsetCm);
        Assert.Equal(2000, GameSessionTestHelpers.LiveGameData.SkillUseRangeCm(FlameArrow));
        Assert.Equal(accepted, Accepted(await session.HandleAsync(Cast(FlameArrow, dummy.Key, at), Now + 5000)));
    }

    [Fact]
    public async Task Ice_chain_then_cold_wave_is_accepted()
    {
        var (session, dummy, at) = await EnterAsync(7u, "RngIc", 1500);
        var t = Now + 5000;
        Assert.True(Accepted(await session.HandleAsync(Cast(IceChain, dummy.Key, at), t)));
        Assert.Contains(ColdWave, session.ActiveChainSkills.Keys);
        t += session.HeldCastLockoutRemainingMs(t) + 50;
        var r = await session.HandleAsync(Cast(ColdWave, dummy.Key, at), t);
        Assert.True(Accepted(r));
    }
}
