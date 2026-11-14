using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class DeadshotChargingTests
{
    const int Deadshot = 14_010_000;
    const int DeadshotStep3 = 14_010_003;
    const long Now = 1_760_012_000_000;

    static readonly byte[] OfficialChargingStartCapture =
        Convert.FromHexString("1A3890C6D50002E82600000000E98457C7CE8F97C7857C264600");

    static readonly byte[] OfficialChargingStartReplyCapture =
        Convert.FromHexString("1B38000090C6D500");

    static readonly byte[] OfficialChargingStartNotifyCapture =
        Convert.FromHexString("1C38B05B0190C6D5000602AECD02206F2543BC540547EBB514C70018414640359E3F01");

    static readonly byte[] OfficialChargingEndReplyCapture =
        Convert.FromHexString("1E3801000090C6D5000000000090C6D500");

    static readonly float OfficialChargeRate = BitConverter.ToSingle(Convert.FromHexString("40359E3F"));

    static byte[] ChargingStartWire(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(GameSession.SkillChargingStartRq).I32(skillId).U8(2).Varint(targetKey).I32(0).Vec3(at).U8(0).ToArray();

    static byte[] ChargingEndWire(int skillId, int step = 1) =>
        new PacketWriter().U16(GameSession.SkillChargingEndRq).I32(step).I32(skillId).U8(1).U8(0).ToArray();

    static async Task<GameSession> EnterRangerAsync()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var creation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Race: 1, Class: "Ranger" });
        store.Items.Add(FakeCharacterStore.EquippedItem(data.StarterWeapon(data.Profile(creation, 1)!)!.Value, 900, 1));
        var session = new GameSession(data,
            new GameSessionOptions { World = new SharedWorld(GameData.Embedded), CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true },
            new Random(11)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Charge").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    static int AbnormalId(byte[] packet)
    {
        var reader = new PacketReader(packet) { Offset = 2 };
        _ = reader.Varint();
        var count = (int)reader.Varint();
        Assert.Equal(1, count);
        _ = reader.U8();
        _ = reader.Varint();
        return reader.I32();
    }

    [Fact]
    public void Skill_charging_start_reply_matches_official_u16_result_then_skill()
    {
        var reply = GamePackets.SkillChargingStartReply(0, Deadshot);
        Assert.Equal(OfficialChargingStartReplyCapture, reply);
    }

    [Fact]
    public void Skill_charging_end_reply_matches_official_u8_result_skill_step_selected()
    {
        // Our capture is a step-0 release: u8 1, result u16, skill, step i32 0, selected = the charged skill.
        var reply = GamePackets.SkillChargingEndReply(0, Deadshot, 0, Deadshot);
        Assert.Equal(OfficialChargingEndReplyCapture, reply);
        // The Addiboii fork's recording of a step-3 release (2026-11-06 #147623).
        Assert.Equal(Convert.FromHexString("1E38010000" + "90C6D500" + "03000000" + "93C6D500"),
            GamePackets.SkillChargingEndReply(0, Deadshot, 3, DeadshotStep3));
    }

    [Fact]
    public void Skill_charging_start_notify_matches_official_capture_byte_for_byte()
    {
        var officialTail = OfficialChargingStartNotifyCapture.AsSpan(5);
        var yaw = BitConverter.ToSingle(officialTail.Slice(9));
        var loc = new Vector3d(
            BitConverter.ToSingle(officialTail.Slice(13)),
            BitConverter.ToSingle(officialTail.Slice(17)),
            BitConverter.ToSingle(officialTail.Slice(21)));
        var notify = GamePackets.SkillChargingStartNotify(11_696, Deadshot, 6, 42_670, yaw, loc, OfficialChargeRate);
        Assert.Equal(OfficialChargingStartNotifyCapture, notify);
        Assert.Equal(OfficialChargeRate, GameSession.SkillChargeRate);
    }

    [Fact]
    public void Charged_skills_of_every_class_name_their_steps()
    {
        var data = GameData.Embedded;
        var deadshot = data.Charge(Deadshot);
        Assert.Equal(Deadshot, data.Skill(Deadshot)!.Charge);
        Assert.NotNull(deadshot);
        Assert.Equal([500, 1000, 1500], deadshot!.Steps.Select(s => s[0]));
        Assert.Equal([14_010_001, 14_010_002, DeadshotStep3], deadshot.Steps.Select(s => s[1]));
        // Templar, Ranger, Sorcerer, Spiritmaster, Chanter and Cleric charged skills (Skill.dat ChargeId).
        foreach (var skill in new[] { 12_090_000, Deadshot, 15_060_000, 17_060_000, 19_160_000, 19_200_000 })
        {
            var charge = data.Charge(data.Skill(skill)!.Charge);
            Assert.NotNull(charge);
            Assert.All(charge!.Steps, step => Assert.NotEmpty(data.Skill(step[1])!.Damage));
            Assert.All(charge.Steps, step => Assert.Equal(data.CooldownGroup(skill), data.CooldownGroup(step[1])));
        }
        Assert.True(data.Charge(17_060_000)!.Movable);
        Assert.False(deadshot.Movable);
    }

    [Theory]
    // Held ms + 200 ms allowance, times rate 1.236, against ChargeTime 500 / 1000 / 1500.
    [InlineData(0, 0)]
    [InlineData(200, 0)]
    [InlineData(300, 1)]
    [InlineData(1_000, 2)]
    [InlineData(1_100, 3)]
    [InlineData(5_000, 3)]
    public void Charge_step_follows_charge_time_over_rate(long heldMs, int expected)
    {
        var deadshot = GameData.Embedded.Charge(Deadshot)!;
        Assert.Equal(expected, GameSession.ChargeStepReached(deadshot, heldMs, GameSession.SkillChargeRate));
    }

    [Fact]
    public void Charge_step_fits_the_fork_recordings_at_their_rate()
    {
        // Addiboii fork b1d445a: at rate 1.5786 the recorded holds were 0.59 s step 1, 0.75 s step 2, 1.00 s step 3.
        var deadshot = GameData.Embedded.Charge(Deadshot)!;
        Assert.Equal(0, GameSession.ChargeStepReached(deadshot, 0, 1f));
        Assert.Equal(1, GameSession.ChargeStepReached(deadshot, 300, 1f));
        Assert.Equal(3, GameSession.ChargeStepReached(deadshot, 1_300, 1f));
        Assert.Equal(3, GameSession.ChargeStepReached(deadshot, 1_000 - GameSession.ChargeStepToleranceMs, 1.5786f));
    }

    [Fact]
    public void ParseSkillChargingStart_reads_official_0x381a_capture()
    {
        var parsed = GameSession.ParseSkillChargingStart(OfficialChargingStartCapture);
        Assert.NotNull(parsed);
        Assert.Equal(Deadshot, parsed!.SkillId);
        Assert.Equal(4968, parsed.TargetKey);
        Assert.Equal(-55_172.91015625, parsed.Location.X, 3);
        Assert.Equal(-77_599.609375, parsed.Location.Y, 3);
        Assert.Equal(10_655.1298828125, parsed.Location.Z, 3);
    }

    [Fact]
    public async Task Deadshot_charge_start_sends_381c_381b_and_caster_charging_abnormal()
    {
        var session = await EnterRangerAsync();
        var mob = session.Targets![Combat.TrainingDummyKey];
        var start = await session.HandleAsync(ChargingStartWire(Deadshot, mob.Key, mob.Position), Now + 60_000);
        var ntIndex = start.FindIndex(p => GameSessionTestHelpers.Op(p) == GameSession.SkillChargingStartNt);
        var rsIndex = start.FindIndex(p => GameSessionTestHelpers.Op(p) == GameSession.SkillChargingStartRs);
        var abnIndex = start.FindIndex(p => GameSessionTestHelpers.Op(p) == 0x382a);
        Assert.True(ntIndex >= 0 && rsIndex > ntIndex && abnIndex > rsIndex);
        Assert.Equal(OfficialChargingStartReplyCapture, start[rsIndex]);
        var abnormal = start[abnIndex];
        Assert.Equal(GameSession.SkillChargingAbnormalId, AbnormalId(abnormal));
    }

    [Fact]
    public async Task Deadshot_charge_release_fires_skill_and_clears_charging_abnormal()
    {
        var session = await EnterRangerAsync();
        var mob = session.Targets![Combat.TrainingDummyKey];
        var startAt = Now + 60_000;
        await session.HandleAsync(ChargingStartWire(Deadshot, mob.Key, mob.Position), startAt);
        var release = await session.HandleAsync(ChargingEndWire(Deadshot, step: 0), startAt + 400);
        var startIndex = release.FindIndex(p => GameSessionTestHelpers.Op(p) == 0x3802);
        var endIndex = release.FindIndex(p => GameSessionTestHelpers.Op(p) == GameSession.SkillChargingEndRs);
        var removeIndex = release.FindIndex(p => GameSessionTestHelpers.Op(p) == 0x382c);
        Assert.True(startIndex >= 0 && endIndex > startIndex);
        Assert.Equal(OfficialChargingEndReplyCapture, release[endIndex]);
        Assert.True(removeIndex > endIndex);
        Assert.DoesNotContain(release, p => GameSessionTestHelpers.Op(p) == 0x3801);
        Assert.False(session.HasPendingSkillChargeForTests);
    }

    static int StartedSkill(byte[] skillStart)
    {
        var reader = new PacketReader(skillStart) { Offset = 2 };
        _ = reader.Varint();
        _ = reader.U8();
        return reader.I32();
    }

    [Fact]
    public async Task Full_hold_releases_the_last_step_skill_and_the_charged_skill_takes_the_cooldown()
    {
        var session = await EnterRangerAsync();
        var mob = session.Targets![Combat.TrainingDummyKey];
        var startAt = Now + 60_000;
        await session.HandleAsync(ChargingStartWire(Deadshot, mob.Key, mob.Position), startAt);
        var release = await session.HandleAsync(ChargingEndWire(DeadshotStep3, step: 3), startAt + 1_300);
        var start = release.FindIndex(p => GameSessionTestHelpers.Op(p) == 0x3802);
        Assert.True(start >= 0);
        Assert.Equal(DeadshotStep3, StartedSkill(release[start]));
        Assert.Equal(GamePackets.SkillChargingEndReply(0, Deadshot, 3, DeadshotStep3), release[start + 1]);
        Assert.False(session.HasPendingSkillChargeForTests);
        // The step skills share Deadshot's GroupCoolTimeId: a new charge right after is refused.
        var again = await session.HandleAsync(ChargingStartWire(Deadshot, mob.Key, mob.Position), startAt + 2_000);
        Assert.Contains(GamePackets.SkillChargingStartReply(1, Deadshot), again);
        Assert.DoesNotContain(again, p => GameSessionTestHelpers.Op(p) == GameSession.SkillChargingStartNt);
    }

    [Fact]
    public async Task Release_step_is_clamped_to_what_the_held_time_reached()
    {
        var session = await EnterRangerAsync();
        var mob = session.Targets![Combat.TrainingDummyKey];
        var startAt = Now + 60_000;
        await session.HandleAsync(ChargingStartWire(Deadshot, mob.Key, mob.Position), startAt);
        // 400 ms held: (400 + 200) x 1.236 = 742 ms of charge, step 1, though the client names step 3.
        var release = await session.HandleAsync(ChargingEndWire(DeadshotStep3, step: 3), startAt + 400);
        var start = release.FindIndex(p => GameSessionTestHelpers.Op(p) == 0x3802);
        Assert.Equal(14_010_001, StartedSkill(release[start]));
        Assert.Contains(GamePackets.SkillChargingEndReply(0, Deadshot, 1, 14_010_001), release);
    }

    [Fact]
    public async Task Release_naming_a_skill_outside_the_charge_is_refused()
    {
        var session = await EnterRangerAsync();
        var mob = session.Targets![Combat.TrainingDummyKey];
        var startAt = Now + 60_000;
        await session.HandleAsync(ChargingStartWire(Deadshot, mob.Key, mob.Position), startAt);
        var release = await session.HandleAsync(ChargingEndWire(15_060_001, step: 1), startAt + 800);
        Assert.Equal([GamePackets.SkillChargingEndReply(1, Deadshot, 1, 15_060_001)], release);
    }

    [Fact]
    public async Task Pending_hold_charge_clears_after_timeout()
    {
        var session = await EnterRangerAsync();
        var mob = session.Targets![Combat.TrainingDummyKey];
        var startAt = Now + 120_000;
        await session.HandleAsync(ChargingStartWire(Deadshot, mob.Key, mob.Position), startAt);
        Assert.True(session.HasPendingSkillChargeForTests);
        // Silence after the last 0x381a counts as the button released: the charge fires and its abnormal goes.
        var tick = await session.TickAsync(startAt + GameSession.SkillChargeTimeoutMs + 1);
        Assert.Contains(tick, p => GameSessionTestHelpers.Op(p) == 0x382c);
        Assert.False(session.HasPendingSkillChargeForTests);
    }
}
