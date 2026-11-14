using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Mastery skill specialisation (#72, ported from addiboii's fork): equip, unequip and swap of parts, the children they make,
/// 0x3900 and 0x5100 at world entry, and the child's cast.
/// </summary>
public sealed class SkillSpecializationTests
{
    const long Now = 1_760_050_000_000;
    const int KeenStrike = 11020000, MockingBlade = 11290000;
    const ushort SkillAdd = 0x380f, SkillDelete = 0x3810, SkillLevelUpdate = 0x3813, SpecializedUpdate = 0x3907, SkillList = 0x5100;

    static ushort Op(byte[] p) => GameSessionTestHelpers.Op(p);

    static byte[] Equip(int part, int slot) => new PacketWriter().U16(GameSession.SpecializedEquipRq).I32(part).U8((byte)slot).ToArray();
    static byte[] Unequip(int part) => new PacketWriter().U16(GameSession.SpecializedUnequipRq).I32(part).ToArray();
    static byte[] Swap(int a, int b) => new PacketWriter().U16(GameSession.SpecializedChangeRq).I32(a).I32(b).ToArray();

    static byte[] Cast(int skillId, int targetKey) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(new Vector3d(100, 200, 300)).U8(9).ToArray();

    /// <summary>A level-55 Gladiator in the world with the skill levels bought as given (and any parts already stored).</summary>
    static async Task<(GameSession Session, List<byte[]> Entry)> EnterAsync(FakeCharacterStore store, params (int Skill, int Level)[] levels)
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var creation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Race: 1 } p && p.Class == "Gladiator");
        if (store.States.Count == 0)
        {
            store.Progress = (55, 0);
            store.Items.Add(FakeCharacterStore.EquippedItem(data.StarterWeapon(data.Profile(creation, 1)!)!.Value, 900, 1));
            foreach (var (skill, level) in levels) store.Skills.Add(new SkillState(skill, level, null, level));
        }
        var session = new GameSession(data,
            new GameSessionOptions { World = new SharedWorld(data), CharacterLevel = 55, WorldId = store.WorldId, TrainingDummy = true },
            new Random(11)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        if (store.States.Count == 0)
            await session.HandleAsync(new PacketWriter().U16(0x361b).String("Spec").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        var entry = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return (session, entry);
    }

    static (int Result, int Part) Result(byte[] p) => (BitConverter.ToUInt16(p, 2), BitConverter.ToInt32(p, 4));

    static int AddedSkill(byte[] p) => BitConverter.ToInt32(p, 3);

    static byte[] Update(int parent, params (int Slot, int Part)[] slots)
    {
        var w = new PacketWriter().U16(SpecializedUpdate).I32(parent).U8((byte)slots.Length);
        foreach (var (slot, part) in slots) w.U8((byte)slot).I32(part);
        return w.ToArray();
    }

    /// <summary>The 0x3900 slots of one skill: (slot, part) pairs.</summary>
    static List<(int Slot, int Part)> OptionSlots(byte[] packet, int skill)
    {
        var r = new PacketReader(packet.AsSpan(2).ToArray());
        var count = (int)r.Varint();
        for (var i = 0; i < count; i++)
        {
            var id = r.I32();
            var slots = Enumerable.Range(0, r.U8()).Select(_ => ((int)r.U8(), r.I32())).ToList();
            if (id == skill) return slots;
        }
        throw new Xunit.Sdk.XunitException($"{skill} not in 0x3900");
    }

    [Fact]
    public void Data_reads_the_parts_slots_and_children()
    {
        var spec = SpecializedSkillTables.Embedded;
        Assert.Equal([8, 12, 20], spec.SlotLevels);
        Assert.Equal((KeenStrike, 12), spec.Part(11020040));
        Assert.True(spec.IsChild(KeenStrike, 11020340));
        Assert.True(spec.IsChild(11030000, 11030340));   // the chain step's child (Skill.dat chain links)
        Assert.Equal(11030000, spec.ParentOf(11030340));
        Assert.Equal(340, SpecializedSkillTables.Suffix(KeenStrike, [11020040, 11020030]));
        // Every child has cast data, from class-skills.json or the specialisation file.
        Assert.NotNull(GameData.Embedded.Skill(11100140));
        Assert.Equal(GameData.Embedded.Skill(11290000)!.Name, GameData.Embedded.Skill(11290140)!.Name);
    }

    [Fact]
    public async Task Equip_adds_the_child_and_its_chain_steps_then_replies_and_updates_the_slot()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, (KeenStrike, 12));

        var first = await session.HandleAsync(Equip(11020030, 1), Now + 1_000);
        // 0x380f per child (Keen Strike's and its chain steps', reason 6 ChangeSkill), 0x3902 ok, 0x3907 the slot.
        var adds = first.TakeWhile(p => Op(p) == SkillAdd).ToList();
        Assert.Equal(11020030, AddedSkill(adds[0]));
        Assert.Contains(adds, p => AddedSkill(p) == 11030030);
        Assert.All(adds, p => Assert.Equal(6, p[^1]));
        Assert.Equal(12, adds[0][7]);
        Assert.Equal(new PacketWriter().U16(GameSession.SpecializedEquipRs).U16(0).I32(11020030).U8(1).ToArray(), first[adds.Count]);
        Assert.Equal(Update(KeenStrike, (1, 11020030)), first[adds.Count + 1]);
        Assert.Equal([new SkillPartRecord(KeenStrike, 1, 11020030)], store.SkillParts);

        // A second part: the old child goes (0x3810), the pair's child comes (11020340, digits ascending).
        var second = await session.HandleAsync(Equip(11020040, 2), Now + 2_000);
        Assert.Equal(SkillDelete, Op(second[0]));
        Assert.Equal(11020030, BitConverter.ToInt32(second[0], 2));
        Assert.Contains(second, p => Op(p) == SkillAdd && AddedSkill(p) == 11020340);
        Assert.Contains(second, p => Op(p) == SkillAdd && AddedSkill(p) == 11030340);
        Assert.Equal(11020340, session.SpecializedChild(KeenStrike));
        Assert.Equal(Update(KeenStrike, (2, 11020040)), second[^1]);
    }

    [Fact]
    public async Task Equip_refuses_closed_slots_low_levels_foreign_parts_and_a_part_twice()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, (KeenStrike, 12));
        static int Code(List<byte[]> r) => Assert.Single(r) is var p && Op(p) == GameSession.SpecializedEquipRs ? Result(p).Result : -1;

        Assert.Equal(GameSession.SkillModifyMinLevel, Code(await session.HandleAsync(Equip(11020030, 3), Now + 1)));   // slot 3 opens at 20
        Assert.Equal(GameSession.SkillModifyMinLevel, Code(await session.HandleAsync(Equip(11020050, 1), Now + 2)));   // part 50 needs 16
        Assert.Equal(GameSession.SkillModifyNotFound, Code(await session.HandleAsync(Equip(12010010, 1), Now + 3)));   // not a learned skill's
        Assert.Equal(GameSession.SkillModifyNotFound, Code(await session.HandleAsync(Equip(11020030, 0), Now + 4)));
        Assert.Equal(0, Result((await session.HandleAsync(Equip(11020030, 1), Now + 5)).Single(p => Op(p) == GameSession.SpecializedEquipRs)).Result);
        Assert.Equal(GameSession.SkillModifyOverlapped, Code(await session.HandleAsync(Equip(11020030, 2), Now + 6)));
        Assert.Equal([new SkillPartRecord(KeenStrike, 1, 11020030)], store.SkillParts);
    }

    [Fact]
    public async Task Unequip_deletes_the_child_and_adds_the_one_left()
    {
        var store = new FakeCharacterStore();
        store.SkillParts.AddRange([new(KeenStrike, 1, 11020030), new(KeenStrike, 2, 11020040)]);
        var (session, _) = await EnterAsync(store, (KeenStrike, 12));
        Assert.Equal(11020340, session.SpecializedChild(KeenStrike));

        var off = await session.HandleAsync(Unequip(11020030), Now + 1_000);
        Assert.Equal(11020340, BitConverter.ToInt32(off[0], 2));
        Assert.Equal(SkillDelete, Op(off[0]));
        Assert.Contains(off, p => Op(p) == SkillAdd && AddedSkill(p) == 11020040);
        Assert.Equal(new PacketWriter().U16(GameSession.SpecializedUnequipRs).U16(0).I32(11020030).ToArray(), off[^2]);
        Assert.Equal(Update(KeenStrike, (1, 0)), off[^1]);
        Assert.Equal([new SkillPartRecord(KeenStrike, 2, 11020040)], store.SkillParts);

        var notOn = await session.HandleAsync(Unequip(11020030), Now + 2_000);
        Assert.Equal(GameSession.SkillModifyNotFound, Result(Assert.Single(notOn)).Result);
    }

    [Fact]
    public async Task Swap_moves_two_parts_without_changing_the_child_or_puts_a_new_part_in_place()
    {
        var store = new FakeCharacterStore();
        store.SkillParts.AddRange([new(KeenStrike, 1, 11020030), new(KeenStrike, 2, 11020040)]);
        var (session, _) = await EnterAsync(store, (KeenStrike, 12));

        var swapped = await session.HandleAsync(Swap(11020030, 11020040), Now + 1_000);
        // The digits are sorted, so the child stays 11020340: no 0x380f / 0x3810, only the reply and both slots.
        Assert.Equal(new PacketWriter().U16(GameSession.SpecializedChangeRs).U16(0).I32(11020030).I32(11020040).ToArray(), swapped[0]);
        Assert.Equal(Update(KeenStrike, (1, 11020040), (2, 11020030)), swapped[1]);
        Assert.Equal(2, swapped.Count);

        var replaced = await session.HandleAsync(Swap(11020030, 11020010), Now + 2_000);
        Assert.Contains(replaced, p => Op(p) == SkillAdd && AddedSkill(p) == 11020140);
        Assert.Equal(Update(KeenStrike, (2, 11020010)), replaced[^1]);
        Assert.Equal(11020140, session.SpecializedChild(KeenStrike));
    }

    [Fact]
    public async Task World_entry_lists_the_parts_in_0x3900_and_the_children_in_0x5100()
    {
        var store = new FakeCharacterStore();
        store.SkillParts.AddRange([new(KeenStrike, 1, 11020030), new(KeenStrike, 2, 11020040)]);
        var (_, entry) = await EnterAsync(store, (KeenStrike, 12));

        var options = entry.Single(p => Op(p) == 0x3900);
        Assert.Equal([(1, 11020030), (2, 11020040), (3, 0)], OptionSlots(options, KeenStrike));
        // The other active skills keep three empty slots (official 2026-11-12).
        Assert.Equal([(1, 0), (2, 0), (3, 0)], OptionSlots(options, MockingBlade));
        var list = entry.Single(p => Op(p) == SkillList);
        Assert.True(Contains(list, 11020340) && Contains(list, 11030340));
        Assert.False(Contains(list, 11020030));
    }

    [Fact]
    public async Task A_parent_level_down_below_the_slot_drops_its_part_from_the_child()
    {
        var store = new FakeCharacterStore();
        store.SkillParts.AddRange([new(KeenStrike, 1, 11020030), new(KeenStrike, 2, 11020040)]);
        var (session, _) = await EnterAsync(store, (KeenStrike, 12));

        // 12 -> 11 closes slot 2 (and part 40 needs 12): the child goes back to 11020030 before the reply.
        var down = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelDownRq).I32(KeenStrike).ToArray(), Now + 1_000);
        Assert.Equal(SkillDelete, Op(down[0]));
        Assert.Contains(down.TakeWhile(p => Op(p) != GameSession.SkillLevelDownRs), p => Op(p) == SkillAdd && AddedSkill(p) == 11020030);
        Assert.Equal(11020030, session.SpecializedChild(KeenStrike));
        // The part stays stored (only the requests change parts).
        Assert.Equal(2, store.SkillParts.Count);

        // 11 -> 10 keeps the child: it follows the parent's level (0x3813 reason 6) before the reply.
        var again = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelDownRq).I32(KeenStrike).ToArray(), Now + 2_000);
        Assert.Equal(SkillLevelUpdate, Op(again[0]));
        Assert.Equal(11020030, BitConverter.ToInt32(again[0], 3));
        Assert.Equal(6, again[0][^1]);
    }

    [Fact]
    public async Task A_child_is_cast_only_with_its_parts_on()
    {
        var store = new FakeCharacterStore();
        var (session, _) = await EnterAsync(store, (MockingBlade, 12));
        var map = Assert.IsType<Dictionary<int, Target>>(session.Targets);
        var at = session.Position!.Value;
        map[880_402] = new Target
        {
            Key = 880_402, Name = "Mob", NpcId = 2400032, Hp = 1_000_000, HpMax = 1_000_000, Relationship = 6,
            Position = at with { X = at.X + 150 }, Direction = 0, Radius = 100, RunSpeed = 600, WalkSpeed = 200,
        };

        var refused = await session.HandleAsync(Cast(11290140, 880_402), Now + 10_000);
        Assert.DoesNotContain(refused, p => Op(p) == 0x3802);

        await session.HandleAsync(Equip(11290010, 1), Now + 11_000);
        await session.HandleAsync(Equip(11290040, 2), Now + 11_100);
        Assert.Equal(11290140, session.SpecializedChild(MockingBlade));
        var other = await session.HandleAsync(Cast(11290120, 880_402), Now + 12_000);   // a child of parts not on
        Assert.DoesNotContain(other, p => Op(p) == 0x3802);
        var cast = await session.HandleAsync(Cast(11290140, 880_402), Now + 13_000);
        Assert.Contains(cast, p => Op(p) == 0x3802);
    }

    static bool Contains(byte[] packet, int skill)
    {
        var bytes = BitConverter.GetBytes(skill);
        return packet.AsSpan().IndexOf(bytes) >= 0;
    }
}
