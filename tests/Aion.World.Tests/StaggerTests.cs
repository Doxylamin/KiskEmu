using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// The groggy guard (Stagger.cs, tools/data/stagger.json from SkillEffect.dat GroggyGuardDecreaseEnergy and NpcData GroggyTime) and
/// the skills that need a staggered target (Rapid Scattershot, SkillCond HasAbnormalEffectType Stagger). The byte checks rebuild the
/// community fork's recording 20261019-231752: the lake cave boss 2701034 (key 30114) drained under player 9655's casts
/// 50 -> 40 -> 25 -> 23 -> 13 -> 11 -> 9 -> 7 -> broken.
/// </summary>
public sealed class StaggerTests
{
    const long Now = 1_760_004_000_000;
    const int Combustion = 16_040_000;
    const int RapidScattershot = 16_340_000;
    const int Dummy = Combat.TrainingDummyKey;
    const int CaveBoss = 2_701_034;

    static string Hex(IEnumerable<byte[]> packets) => string.Concat(packets.Select(Convert.ToHexStringLower));

    static Target Boss(int key = 30114, int grade = 5) =>
        new() { Key = key, Name = "boss", Hp = 100_000, HpMax = 100_000, NpcId = CaveBoss, Grade = grade };

    static byte[] Cast(int skill, int target = Dummy) =>
        new PacketWriter().U16(0x3800).U16(0).I32(skill).U8(2).Varint(target).F32(0).F32(100).F32(200).F32(300).ToArray();

    static async Task<GameSession> EnterElementalistAsync()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { World = new SharedWorld(GameData.Embedded), CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true },
            new Random(9)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Stagger").U8(1).U32(6).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    static bool Accepted(List<byte[]> replies) =>
        replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

    [Fact]
    public void Tables_name_the_guarded_npcs_and_the_drains_per_skill()
    {
        Assert.True(Stagger.HasGuard(CaveBoss));
        Assert.Equal(5000, Stagger.GroggyTimeMs(CaveBoss));
        Assert.False(Stagger.HasGuard(Combat.SandbagNpcId));
        Assert.Equal(50, Stagger.MaxOf(CaveBoss));
        Assert.Equal(1200, Stagger.MaxOf(2_300_104));
        Assert.Equal(Stagger.GuardMax, Stagger.MaxOf(2_701_401));
        Assert.Equal([new Stagger.DrainRow(3, 1_119_000_022, 10)], Stagger.Drains(11_190_000));
        Assert.Equal(15, Stagger.DrainOf(11_290_001));
        Assert.Equal(3, Stagger.DrainOf(Combustion));
        Assert.Empty(Stagger.Drains(11_020_000));   // the basic attack drains nothing
    }

    [Fact]
    public void Update_groggy_info_layout()
    {
        Assert.Equal("05e0a2eb010301320000002800000002", Convert.ToHexStringLower(GamePackets.UpdateGroggyInfo(30114, 1, 50, 40)));
        Assert.Equal("05e0a2eb01000302", Convert.ToHexStringLower(GamePackets.UpdateGroggyInfo(30114, 3, null, null)));
        // The dodge's 0x3804 switch 00 keeps seq 1; a drain carries its hit's seq in the u32 and the u8.
        Assert.Equal(GamePackets.SkillEffect(1, 2, 3, 4, 5), GamePackets.SkillEffect(1, 2, 3, 4, 5, 1));
    }

    [Fact]
    public void Drains_rebuild_the_recorded_boss_fight_and_break_into_the_groggy_abnormal()
    {
        var boss = Boss();
        Assert.Equal("0438a2eb010000b74bf0beaa00e102d695b24203000000904e0300" + "05e0a2eb010301320000002800000002",
            Hex(Stagger.Drain(boss, 11_190_000, 9655, 0xe1, Now)));
        Assert.Equal("0438a2eb010000b74b9145ac00e202b02c4b4301000000904e0100" + "05e0a2eb010301320000001900000002",
            Hex(Stagger.Drain(boss, 11_290_001, 9655, 0xe2, Now)));
        Assert.Equal("0438a2eb010000b74bd0ffa700f3024ced9f4101000000904e0100" + "05e0a2eb010301320000001700000002",
            Hex(Stagger.Drain(boss, 11_010_000, 9655, 0xf3, Now)));
        foreach (var (skill, left) in new[] { (11_190_000, 13), (11_010_000, 11), (11_420_000, 9), (11_010_000, 7) })
            Assert.Equal(GamePackets.UpdateGroggyInfo(30114, 1, 50, left), Stagger.Drain(boss, skill, 9655, 1, Now).Last());

        var broken = Stagger.Drain(boss, 11_190_000, 9655, 0x19, Now);
        Assert.Equal("0438a2eb010000b74bf0beaa001902d695b24203000000904e0300" + "05e0a2eb01000302", Hex(broken.Take(2)));
        Assert.Equal(3, broken.Count);
        Assert.Equal(GameSessionTestHelpers.Op(PlayerPackets.AbnormalAdd(30114, [new AbnormalWire(1, Stagger.GroggyAbnormal, 5000, Now + 5000)])),
            GameSessionTestHelpers.Op(broken[2]));
        Assert.Contains(Convert.ToHexStringLower(BitConverter.GetBytes(Stagger.GroggyAbnormal)), Convert.ToHexStringLower(broken[2]));
        Assert.Equal(0, boss.GroggyEnergy);
        Assert.True(boss.Held);
        Assert.Equal(Now + 5000, boss.StaggerUntil);

        // Still broken: no drain, no refill before GroggyTime.
        Assert.Empty(Stagger.Drain(boss, 11_190_000, 9655, 1, Now + 4999));
        Assert.Null(Stagger.Refill(boss, Now + 4999));
        // GroggyTime later the guard refills to its maximum and drains again.
        Assert.Equal(GamePackets.UpdateGroggyInfo(30114, 1, 50, 50), Stagger.Refill(boss, Now + 5000));
        Assert.Null(boss.GroggyEnergy);
        Stagger.Tick(new Dictionary<int, Target> { [boss.Key] = boss }, Now + 5000);
        Assert.False(boss.Held);
        Assert.Equal(GamePackets.UpdateGroggyInfo(30114, 1, 50, 40), Stagger.Drain(boss, 11_190_000, 9655, 1, Now + 6000).Last());
    }

    [Fact]
    public void A_due_refill_goes_out_before_the_next_drain()
    {
        var boss = Boss();
        boss.GroggyEnergy = 0;
        boss.GroggyRefillAt = Now;
        var replies = Stagger.Drain(boss, 11_190_000, 9655, 1, Now + 10);
        Assert.Equal(GamePackets.UpdateGroggyInfo(30114, 1, 50, 50), replies[0]);
        Assert.Equal(GamePackets.UpdateGroggyInfo(30114, 1, 50, 40), replies[^1]);
    }

    [Fact]
    public void No_drain_without_a_guard_or_a_drain_row()
    {
        Assert.Empty(Stagger.Drain(new Target { Key = 5, Name = "m", HpMax = 1, Hp = 1, NpcId = Combat.SandbagNpcId }, 11_190_000, 1, 1, Now));
        Assert.Empty(Stagger.Drain(Boss(6), 11_020_000, 1, 1, Now));
    }

    [Fact]
    public void Drain_rows_ride_the_hit_of_their_seq_or_the_last_hit()
    {
        Assert.Null(Stagger.DrainsOnHit(11_190_000, 0, 1, lastHit: false));
        Assert.Null(Stagger.DrainsOnHit(11_190_000, 1, 2, lastHit: false));
        Assert.Single(Stagger.DrainsOnHit(11_190_000, 2, 3, lastHit: false)!);
        Assert.Single(Stagger.DrainsOnHit(11_190_000, 0, 1, lastHit: true)!);   // a one-hit cast still drains once
        Assert.Null(Stagger.DrainsOnHit(11_190_000, 1, 1, lastHit: true));       // a second row of the same seq does not drain again
    }

    [Fact]
    public void A_guarded_boss_shows_its_guard_after_the_appear()
    {
        var boss = Boss();
        var appear = Stagger.WithGuard(GamePackets.NpcAppear(boss.Appearance()), boss).ToList();
        Assert.Equal(2, appear.Count);
        Assert.Equal(0x3641, GameSessionTestHelpers.Op(appear[0]));
        Assert.Equal("05e0a2eb010301320000003200000002", Convert.ToHexStringLower(appear[1]));
        Assert.Null(Stagger.AppearInfo(Boss(grade: 4)));
        boss.GroggyEnergy = 0;
        Assert.Equal(GamePackets.UpdateGroggyInfo(30114, 3, null, null), Stagger.AppearInfo(boss));
        Stagger.Restore(boss);
        Assert.Null(boss.GroggyEnergy);
    }

    [Fact]
    public void Rapid_scattershot_requires_stagger_on_target_in_data()
    {
        var skill = GameSessionTestHelpers.LiveGameData.Skill(RapidScattershot)!;
        Assert.Equal("HasAbnormalEffectType", skill.SkillCond!.Type);
        Assert.Equal("Stagger", skill.SkillCond.AbnormalEffect);
        Assert.Equal("Target", skill.SkillCond.SelectTarget);
        Assert.Contains("Stagger", AbnormalSpecs.Types(Stagger.GroggyAbnormal));
    }

    [Fact]
    public async Task Rapid_scattershot_refused_without_stagger()
    {
        var session = await EnterElementalistAsync();
        var replies = await session.HandleAsync(Cast(RapidScattershot), Now + 60_000);
        Assert.False(Accepted(replies));
    }

    [Fact]
    public async Task Rapid_scattershot_accepted_when_cast_target_is_staggered()
    {
        var session = await EnterElementalistAsync();
        var t = Now + 60_000;
        session.Targets![Dummy].StaggerUntil = t + 5000;
        session.Targets![Dummy].Held = true;
        Assert.True(Accepted(await session.HandleAsync(Cast(RapidScattershot), t)));
    }

    [Fact]
    public async Task Combustion_drains_a_guarded_npc_between_its_hit_and_its_hp()
    {
        var session = await EnterElementalistAsync();
        var dummy = session.Targets[Dummy];
        var boss = new Target { Key = 1001, Name = "boss", Hp = 1_000_000, HpMax = 1_000_000, NpcId = CaveBoss, Grade = 5, Position = dummy.Position };
        ((Dictionary<int, Target>)session.Targets)[boss.Key] = boss;
        var t = Now + 60_000;
        Assert.True(Accepted(await session.HandleAsync(Cast(Combustion, boss.Key), t)));
        var landed = await GameSessionTestHelpers.AfterSkillHitsAsync(session, t, 3000);
        var guardAt = landed.FindIndex(p => p.SequenceEqual(GamePackets.UpdateGroggyInfo(1001, 1, 50, 47)));
        Assert.True(guardAt > 0, "0xe005 energy 47 after Combustion's drain of 3");
        Assert.Equal(0x3804, GameSessionTestHelpers.Op(landed[guardAt - 1]));
        Assert.Equal(0, landed[guardAt - 1][4]);   // switch 00 after the varint key 1001 (2 bytes)
        Assert.Equal(GamePackets.NpcHp(1001, 1)[..4], landed[guardAt + 1][..4]);   // the NPC's HP right after (opcode and key)
        Assert.Equal(47, boss.GroggyEnergy);
        // Only once per cast, on one hit (the later 0xe005 is the guard after the boss's first appear to this player).
        Assert.Single(landed, p => GameSessionTestHelpers.Op(p) == 0x3804 && p[2] == 0xe9 && p[3] == 0x07 && p[4] == 0);
    }
}
