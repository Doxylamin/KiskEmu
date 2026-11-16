using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Chanter mantras (#79) against the official Sprint Mantra of 2026-11-15 (i=286204-286222).</summary>
public sealed class ToggleSkillTests
{
    const long Now = 1_760_200_000_000;
    const int SprintMantra = 18160000, SprintPulse = 18160001, ToggleAbnormal = 181600001, PulseAbnormal = 181600011;

    static ushort Op(byte[] p) => GameSessionTestHelpers.Op(p);

    static byte[] Toggle(int skillId, int targetKey, Vector3d at, byte change) =>
        new PacketWriter().U16(0x3800).U16(16 | 8).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(change).U8(9).ToArray();

    static async Task<GameSession> ChanterWithSprintMantraAsync()
    {
        var data = GameData.Embedded;
        var store = new FakeCharacterStore { Progress = (55, 0) };
        var creation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Race: 1 } p && p.Class == "Chanter");
        store.Items.Add(FakeCharacterStore.EquippedItem(data.StarterWeapon(data.Profile(creation, 1)!)!.Value, 900, 1));
        var session = new GameSession(data, new GameSessionOptions { World = new SharedWorld(data), CharacterLevel = 55, WorldId = store.WorldId },
            new Random(5)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Fx").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        store.States[0] = store.States[0] with { Ascension = 3 };
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);
        var bought = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(SprintMantra).ToArray(), Now + 1);
        Assert.Equal(0, BitConverter.ToUInt16(bought.Single(p => Op(p) == GameSession.SkillLevelUpRs), 2));
        return session;
    }

    [Fact]
    public void Sprint_mantra_pulses_its_system_skill()
    {
        var toggle = ToggleSkills.For(SprintMantra)!;
        Assert.Equal((ToggleAbnormal, SprintPulse, PulseAbnormal, 2400, 1000),
            (toggle.Abnormal, toggle.PulseSkill, toggle.PulseAbnormal, toggle.PulseDurationMs, toggle.PulseEveryMs));
        Assert.Equal("MoveSpeed", AbnormalSpecs.Info(PulseAbnormal)!.Effects.Single().Values[0]);
    }

    [Fact]
    public async Task Toggle_on_adds_the_abnormal_then_pulses_the_speed_buff_and_toggle_off_ends_both()
    {
        var session = await ChanterWithSprintMantraAsync();
        var at = session.Position!.Value;

        var on = await session.HandleAsync(Toggle(SprintMantra, session.PlayerKey, at, 1), Now + 1_000);
        var accept = on.Single(p => Op(p) == 0x3801);
        Assert.Equal(0, GameSession.CastResultCode(accept));
        Assert.Equal(1, accept[^1]);   // the change toggle echoed
        Assert.Contains(on, p => Op(p) == 0x3804 && p.AsSpan().IndexOf(BitConverter.GetBytes((uint)SprintMantra * 100 + 11)) > 0);
        Assert.Contains(ToggleAbnormal, session.ActiveBuffs);

        // About 50 ms later the System skill pulses: 0x3802, 0x3806, 0x3804, its abnormal for 2.4 s and 0x364a stat 205 +1370.
        var pulse = await session.TickAsync(Now + 1_100);
        Assert.Equal([0x3802, 0x3806, 0x3804, 0x382a], pulse.Select(Op).Where(o => o is 0x3802 or 0x3806 or 0x3804 or 0x382a).Select(o => (int)o).ToList());
        Assert.Contains(PulseAbnormal, session.ActiveBuffs);
        Assert.Contains(pulse, p => p.SequenceEqual(GamePackets.CharStatsUpdate(session.PlayerKey, [(GameSession.MoveSpeedStat, 1370)], 0)));

        // Every second the pulse refreshes the buff (0x382b), so it never runs out while the toggle is on.
        var later = await session.TickAsync(Now + 4_200);
        Assert.Contains(later, p => Op(p) == 0x382b);
        Assert.Contains(PulseAbnormal, session.ActiveBuffs);

        var off = await session.HandleAsync(Toggle(SprintMantra, session.PlayerKey, at, 0), Now + 4_300);
        Assert.Equal(0, GameSession.CastResultCode(off[0]));
        Assert.Contains(off, p => Op(p) == 0x382c);
        Assert.Contains(off, p => p.SequenceEqual(GamePackets.CharStatsUpdate(session.PlayerKey, [(GameSession.MoveSpeedStat, 0)], 0)));
        Assert.DoesNotContain(ToggleAbnormal, session.ActiveBuffs);
        Assert.DoesNotContain(PulseAbnormal, session.ActiveBuffs);
        Assert.Empty(session.ActiveToggleSkills);
        Assert.DoesNotContain(await session.TickAsync(Now + 6_000), p => Op(p) == 0x3802);
    }
}
