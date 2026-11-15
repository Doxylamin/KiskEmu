using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Live 2026-10-30: after the 9-byte accept the client waits for 0x3806 before the next cast.</summary>
public sealed class SkillEndTests
{
    static readonly long Now = 1_700_000_100_000;

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(9).ToArray();

    static async Task<List<byte[]>> CastAndTick()
    {
            var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now, level: 25, rng: new Random(5));
            var dummy = session.Targets![Combat.TrainingDummyKey];
            var all = new List<byte[]>(await session.HandleAsync(Cast(11020000, dummy.Key, dummy.Position), Now + 50));
            for (var t = 100; t <= 3000; t += 100) all.AddRange(await session.TickAsync(Now + 50 + t));
            return all;
    }

    [Fact]
    public async Task Accepted_cast_is_ended_with_skill_end()
    {
        var all = await CastAndTick();
        Assert.Contains(all, p => GameSessionTestHelpers.Op(p) == 0x3801);
        Assert.Contains(all, p => GameSessionTestHelpers.Op(p) == 0x3806);
    }

    /// <summary>
    /// OBSERVED 2026-11-04 / 2026-11-15: a cast that cuts the actor's running skill brings that skill's 0x3806 with reason 0x0c right
    /// before the new 0x3802; the cut skill sends no later natural end.
    /// </summary>
    [Fact]
    public async Task Next_cast_cuts_the_running_skill_with_reason_0x0c()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now, level: 25, rng: new Random(5));
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var first = await session.HandleAsync(Cast(11020000, dummy.Key, dummy.Position), Now + 50);
        Assert.Single(first, p => GameSessionTestHelpers.Op(p) == 0x3802);
        Assert.DoesNotContain(first, p => GameSessionTestHelpers.Op(p) == 0x3806);

        var second = await session.HandleAsync(Cast(11010000, dummy.Key, dummy.Position), Now + 350);
        var cut = second.FindIndex(p => GameSessionTestHelpers.Op(p) == 0x3806);
        var next = second.FindIndex(p => GameSessionTestHelpers.Op(p) == 0x3802);
        Assert.True(cut >= 0 && next == cut + 1, "the cut 0x3806 comes right before the next 0x3802");
        var end = second[cut];
        Assert.Equal(11020000, BitConverter.ToInt32(end, end.Length - 6));
        Assert.Equal(GamePackets.SkillEndCut, end[^1]);

        var later = new List<byte[]>();
        for (var t = 400; t <= 3000; t += 100) later.AddRange(await session.TickAsync(Now + t));
        Assert.DoesNotContain(later, p => GameSessionTestHelpers.Op(p) == 0x3806 && BitConverter.ToInt32(p, p.Length - 6) == 11020000);
        Assert.Contains(later, p => GameSessionTestHelpers.Op(p) == 0x3806 && BitConverter.ToInt32(p, p.Length - 6) == 11010000 && p[^1] == 0);
    }
}
