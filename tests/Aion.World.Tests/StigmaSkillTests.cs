using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Stigma skills (official capture 2026-11-12, a Cleric's four stigmas reset and bought back).</summary>
public sealed class StigmaSkillTests
{
    const long Now = 1_760_040_000_000;
    const int EarthPunishment = 17400000;
    const ushort SkillAdd = 0x380f, SkillDelete = 0x3810, ItemSkillPoint = 0x3811, SpecializedSkillUpdate = 0x3907;

    static ushort Op(byte[] p) => GameSessionTestHelpers.Op(p);

    static byte[] LevelUp(int skill) => new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(skill).ToArray();

    /// <summary>A character of the level and ascension grade in the world; returns the session, its class and the entry burst.</summary>
    static async Task<(GameSession Session, string Class, List<byte[]> Entry)> EnterAsync(FakeCharacterStore store, int level, int ascension)
    {
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { WorldId = store.WorldId }, new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        if (store.States.Count == 0) await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        store.States[0] = store.States[0] with { Ascension = ascension, Progress = store.States[0].Progress with { Level = level } };
        var state = store.States[0];
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)state.WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        var entry = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 1);
        return (session, GameData.Embedded.Profile(state.CreationId, state.Gender)!.Class, entry);
    }

    static (byte Type, int Spent) Points(byte[] p)
    {
        Assert.Equal(GameSession.SkillPointUpdateNt, Op(p));
        Assert.Equal(1, p[2]);
        return (p[3], BitConverter.ToUInt16(p, 4));
    }

    [Fact]
    public void Data_matches_the_official_stigma_tables_and_the_observed_refund()
    {
        var data = GameData.Embedded;
        Assert.Contains(EarthPunishment, data.StigmaSkills("Cleric"));
        Assert.All(new[] { "Gladiator", "Templar", "Assassin", "Ranger", "Sorcerer", "Elementalist", "Cleric", "Chanter" },
            cls => Assert.Equal(13, data.StigmaSkills(cls).Count));
        Assert.Equal((1, 22, 3), data.StigmaLevelCost(EarthPunishment, 1));
        Assert.Equal(new[] { 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 4, 4, 4, 4, 4, 8, 8, 8, 8, 8 },
            Enumerable.Range(1, 20).Select(l => data.StigmaLevelCost(EarthPunishment, l)!.Value.Cost));
        Assert.Null(data.StigmaLevelCost(EarthPunishment, 21));
        Assert.Equal([(17400010, 5, 1), (17400020, 10, 2), (17400030, 15, 3), (17400040, 20, 4)], data.StigmaParts(EarthPunishment));
        Assert.Equal(EarthPunishment, data.StigmaPartParent(17400010));
        Assert.Equal((0, 1, 29), (data.StigmaPoints(22), data.StigmaPoints(23), data.StigmaPoints(45)));
        // The official reset refunded 30 for Earth Punishment 8, Noble Aura 5, Prayer of Amplification 7, Light of Protection 5.
        int Cost(int skill, int lvl) => Enumerable.Range(1, lvl).Sum(l => data.StigmaLevelCost(skill, l)!.Value.Cost);
        Assert.Equal(30, Cost(17400000, 8) + Cost(17410000, 5) + Cost(17430000, 7) + Cost(17440000, 5));
        Assert.Equal("Earth Punishment", data.Skill(EarthPunishment)?.Name);
        Assert.NotNull(data.Skill(17400010));
    }

    [Fact]
    public async Task Stigma_needs_the_third_ascension_grade_and_level_22()
    {
        var store = new FakeCharacterStore();
        var (session, cls, _) = await EnterAsync(store, 45, 2);
        var stigma = GameData.Embedded.StigmaSkills(cls)[0];
        var refused = await session.HandleAsync(LevelUp(stigma), Now + 2);
        Assert.Equal(GameSession.SkillAcquireNotEnoughAscensionGrade, BitConverter.ToUInt16(refused[0], 2));
        Assert.Equal((2, 0), Points(refused[1]));

        var low = new FakeCharacterStore();
        var (young, youngClass, _) = await EnterAsync(low, 21, 3);
        var tooLow = await young.HandleAsync(LevelUp(GameData.Embedded.StigmaSkills(youngClass)[0]), Now + 2);
        Assert.Equal(GameSession.SkillAcquireNotEnoughCharLevel, BitConverter.ToUInt16(tooLow[0], 2));

        var other = GameData.Embedded.StigmaSkills(cls == "Cleric" ? "Gladiator" : "Cleric")[0];
        var wrongClass = await session.HandleAsync(LevelUp(other), Now + 3);
        Assert.Equal(GameSession.SkillAcquireInvalidClass, BitConverter.ToUInt16(wrongClass[0], 2));
    }

    [Fact]
    public async Task Buying_levels_learns_the_stigma_unlocks_its_part_and_spends_stigma_points_only()
    {
        var store = new FakeCharacterStore();
        var (session, cls, _) = await EnterAsync(store, 45, 3);
        var data = GameData.Embedded;
        var stigma = data.StigmaSkills(cls)[0];
        var part = data.StigmaParts(stigma)[0].Child;
        var skillPoints = session.SkillPointsLeft;
        Assert.Equal(29, session.StigmaPointsLeft);

        // Level 1: 0x3907 (five slots), 0x3656, 0x3840 ok, 0x380f SkillAdd reason SkillPointAdd, 0x5105 type 2.
        var first = await session.HandleAsync(LevelUp(stigma), Now + 2);
        Assert.Equal(new ushort[] { SpecializedSkillUpdate, 0x3656, GameSession.SkillLevelUpRs, SkillAdd, GameSession.SkillPointUpdateNt }, first.Select(Op));
        var slots = new PacketWriter().U16(SpecializedSkillUpdate).I32(stigma).U8(5);
        foreach (var (child, _, slot) in data.StigmaParts(stigma)) slots.U8((byte)slot).I32(child);
        Assert.Equal(slots.U8(5).I32(0).ToArray(), first[0]);
        Assert.Equal(new PacketWriter().U16(GameSession.SkillLevelUpRs).U16(0).I32(stigma).ToArray(), first[2]);
        Assert.Equal(new PacketWriter().U16(SkillAdd).U8(1).I32(stigma).U8(1).U8(1).Zeros(5).U8(2).ToArray(), first[3]);
        Assert.Equal((2, 1), Points(first[4]));

        for (var l = 2; l <= 4; l++) await session.HandleAsync(LevelUp(stigma), Now + 2);
        // Level 5 unlocks the first part (0x380f ChangeSkill before the reply), the stigma itself gets a 0x3813 (reason 3).
        var fifth = await session.HandleAsync(LevelUp(stigma), Now + 3);
        Assert.Equal(new PacketWriter().U16(SkillAdd).U8(1).I32(part).U8(5).U8(5).Zeros(5).U8(6).ToArray(), fifth[0]);
        Assert.Equal(GameSession.SkillLevelUpRs, Op(fifth[2]));
        Assert.Equal(new PacketWriter().U16(GameSession.SkillLevelUpdateNt).Varint(1).I32(stigma).U8(1).I32(stigma).U8(5).U8(5).Zeros(5).U8(3).ToArray(), fifth[3]);
        Assert.Equal((2, 5), Points(fifth[4]));
        // Level 6: the unlocked part follows first (0x3813 reason ChangeSkill), it costs 2.
        var sixth = await session.HandleAsync(LevelUp(stigma), Now + 4);
        Assert.Equal(new PacketWriter().U16(GameSession.SkillLevelUpdateNt).Varint(1).I32(part).U8(1).I32(part).U8(6).U8(6).Zeros(5).U8(6).ToArray(), sixth[0]);
        Assert.Equal((2, 7), Points(sixth[^1]));

        Assert.Equal(6, session.StigmaSkillLevel(stigma));
        Assert.Equal(22, session.StigmaPointsLeft);
        Assert.Equal(skillPoints, session.SkillPointsLeft);
        Assert.Equal((6, 6), (store.Skills.Single(s => s.SkillId == stigma).Purchased, store.Skills.Single(s => s.SkillId == stigma).Level));
    }

    [Fact]
    public async Task The_pool_runs_out_and_a_stigma_shard_adds_a_point()
    {
        var store = new FakeCharacterStore();
        var (session, cls, _) = await EnterAsync(store, 45, 3);
        var stigmas = GameData.Embedded.StigmaSkills(cls);
        // 29 points: one stigma to 10 (5 + 10 = 15), a second to 9 (5 + 8 = 13), 1 left; the next level there costs 2.
        for (var l = 0; l < 10; l++) await session.HandleAsync(LevelUp(stigmas[0]), Now + 2);
        for (var l = 0; l < 9; l++) await session.HandleAsync(LevelUp(stigmas[1]), Now + 2);
        Assert.Equal(1, session.StigmaPointsLeft);
        var refused = await session.HandleAsync(LevelUp(stigmas[1]), Now + 3);
        Assert.Equal(GameSession.SkillAcquireNotEnoughSkillPoint, BitConverter.ToUInt16(refused[0], 2));

        const int shard = 531900010;
        Assert.Equal("GetStigmaShard", RewardBoxes.Embedded.Use(shard)!.Effect);
        store.Items.Add(FakeCharacterStore.BagItem(shard, 1, 902));
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now + 4);
        var used = await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(902).ToArray(), Now + 5);
        Assert.Equal(new PacketWriter().U16(ItemSkillPoint).U8(2).U16(1).ToArray(), used.Single(p => Op(p) == ItemSkillPoint));
        Assert.DoesNotContain(store.Items, i => i.TemplateId == shard && i.Quantity > 0);
        Assert.Equal(2, session.StigmaPointsLeft);
        var tenth = await session.HandleAsync(LevelUp(stigmas[1]), Now + 6);
        Assert.Equal(0, BitConverter.ToUInt16(tenth.Single(p => Op(p) == GameSession.SkillLevelUpRs), 2));
        Assert.Equal(0, session.StigmaPointsLeft);
        Assert.True(store.Settings.ContainsKey("skill_item_points"));
    }

    [Fact]
    public async Task A_wisdom_stone_adds_skill_points()
    {
        var store = new FakeCharacterStore();
        var (session, _, _) = await EnterAsync(store, 20, 1);
        var before = session.SkillPointsLeft;
        store.Items.Add(FakeCharacterStore.BagItem(531900002, 1, 903));
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now + 2);
        var used = await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(903).ToArray(), Now + 3);
        Assert.Equal(new PacketWriter().U16(ItemSkillPoint).U8(1).U16(10).ToArray(), used.Single(p => Op(p) == ItemSkillPoint));
        Assert.Equal(before + 10, session.SkillPointsLeft);
    }

    [Fact]
    public async Task Full_reset_unlearns_the_stigmas_in_the_official_order_and_survives_a_relog()
    {
        var store = new FakeCharacterStore();
        var (session, cls, _) = await EnterAsync(store, 45, 3);
        var stigmas = GameData.Embedded.StigmaSkills(cls).Take(2).ToList();
        for (var l = 0; l < 6; l++) await session.HandleAsync(LevelUp(stigmas[0]), Now + 2);
        await session.HandleAsync(LevelUp(stigmas[1]), Now + 2);
        var part = GameData.Embedded.StigmaParts(stigmas[0])[0].Child;

        // Relog keeps them: 0x5100 lists the stigmas and the part, the entry 0x5105 type 2 carries the spent 5 + 2 + 1.
        var (relogged, _, entry) = await EnterAsync(store, 45, 3);
        var list = entry.Single(p => Op(p) == 0x5100);
        Assert.Contains(stigmas[0], SkillIds(list));
        Assert.Contains(part, SkillIds(list));
        Assert.Contains(entry, p => Op(p) == GameSession.SkillPointUpdateNt && p[3] == 2 && Points(p) == (2, 8));
        Assert.Equal(6, relogged.StigmaSkillLevel(stigmas[0]));
        var options = entry.Single(p => Op(p) == 0x3900);
        Assert.True(Contains(options, new PacketWriter().I32(stigmas[0]).U8(5).U8(1).I32(part).ToArray()));

        var reset = await relogged.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelResetRq).U8(2).I32(0).ToArray(), Now + 3);
        Assert.Equal(new ushort[] { SkillDelete, 0x3656, GameSession.SkillLevelResetRs, GameSession.SkillLevelUpdateNt, GameSession.SkillPointUpdateNt }, reset.Select(Op));
        Assert.Equal(new PacketWriter().U16(SkillDelete).I32(part).ToArray(), reset[0]);
        var ordered = stigmas.Order().ToList();
        Assert.Equal(new PacketWriter().U16(GameSession.SkillLevelResetRs).U16(0).U8(2).I32(0).Varint(2).I32(ordered[0]).U8(0).I32(ordered[1]).U8(0)
            .Varint(1).U8(2).U16(8).ToArray(), reset[2]);
        Assert.Equal((2, 0), Points(reset[4]));
        Assert.Equal(29, relogged.StigmaPointsLeft);

        var (afterReset, _, again) = await EnterAsync(store, 45, 3);
        Assert.DoesNotContain(stigmas[0], SkillIds(again.Single(p => Op(p) == 0x5100)));
        Assert.Equal(0, afterReset.StigmaSkillLevel(stigmas[0]));
    }

    /// <summary>Skill ids of a 0x5100 list (count, then flag-1 entries of 16 bytes or flag-5 ones of 17).</summary>
    static List<int> SkillIds(byte[] list)
    {
        var ids = new List<int>();
        var count = list[2];
        var at = 3;
        for (var i = 0; i < count; i++)
        {
            var flag = list[at];
            ids.Add(BitConverter.ToInt32(list, at + 1));
            at += flag == 5 ? 17 : 16;
        }
        return ids;
    }

    static bool Contains(byte[] haystack, byte[] needle) =>
        Enumerable.Range(0, haystack.Length - needle.Length + 1).Any(i => haystack.AsSpan(i, needle.Length).SequenceEqual(needle));
}
