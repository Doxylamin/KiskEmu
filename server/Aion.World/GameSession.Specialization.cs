using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Skill specialisation of the mastery skills (SpecializedSkillTables, tools/data/specialized-skills.json; migration 113).
// Ported from addiboii's fork (commit a1c5058, issue #72). OBSERVED in the community recordings of 2026-11-04 to 06 (fork
// notes: 191014 #2504-2893 equip and unequip):
// - SpecializedSkillEquipParts_RQ 0x3901 {part i32, slot u8} -> [0x3810 the old child], 0x380f the new child (level = the
//   parent's, reason 6 ChangeSkill), 0x3902 {result u16, part, slot}, 0x3907 SpecializedSkillUpdate_NT {parent, count, {slot, part}}.
// - SpecializedSkillUnequipParts_RQ 0x3905 {part i32} -> 0x3810 the old child, [0x380f the next child], 0x3906 {result, part},
//   0x3907 {parent, 1, {slot, 0}}.
// - The child the client casts is the parent + the parts' digits ascending; chain steps follow their root's parts (11020000
//   with 30+40 lists 11030340 and 11040340 in 0x5100).
// OURS (kept over the fork, OBSERVED official 2026-11-12): 0x3900 at world entry lists every learned active skill with three
// slots (GamePackets.SkillOptionSlots) and the stigmas with their five; a mastery skill's slots now carry its equipped parts.
// The fork listed only the skills that have parts and wrote the stigma slots by the stigma's level; our capture contradicts both.
// The child shares its parent's cooldown through Skill.dat GroupCoolTimeId (11020340 -> 11020000), so the fork's own cooldown
// key is not needed.
// CANDIDATE (fork): SpecializedSkillChangePartsSlot 0x3903/0x3904 (usmap {part, part}; never recorded): swaps two equipped
// parts, or puts an unequipped part in the first one's slot; the refusal codes (EResult kSkill_Modify_*); the children's 0x3813
// (reason 6) when the parent's level changes; the chain steps' children deleted and added with the child, each its own message.
public sealed partial class GameSession
{
    public const ushort SpecializedEquipRq = 0x3901, SpecializedEquipRs = 0x3902, SpecializedChangeRq = 0x3903, SpecializedChangeRs = 0x3904,
        SpecializedUnequipRq = 0x3905, SpecializedUnequipRs = 0x3906;
    // EResult kSkill_Modify_NotFoundData / _OverlappedSkill / _MinSkillLevel (usmap of client 2.0.6, fork; CANDIDATE use).
    public const int SkillModifyNotFound = 0x1834, SkillModifyOverlapped = 0x1836, SkillModifyMinLevel = 0x183c;
    static SpecializedSkillTables Specialized => SpecializedSkillTables.Embedded;

    /// <summary>Equipped parts per mastery skill: slot → part, as stored (never trimmed at login).</summary>
    readonly Dictionary<int, SortedDictionary<int, int>> skillParts = [];

    void LoadSkillParts(IReadOnlyList<SkillPartRecord> rows)
    {
        skillParts.Clear();
        foreach (var r in rows)
            if (r.Slot >= 1 && r.PartId > 0) (skillParts.TryGetValue(r.SkillId, out var s) ? s : skillParts[r.SkillId] = [])[r.Slot] = r.PartId;
    }

    /// <summary>The equipped parts of a mastery skill by slot (for tests and 0x3900).</summary>
    public IReadOnlyDictionary<int, int> SkillParts(int parent) => skillParts.GetValueOrDefault(parent) ?? new SortedDictionary<int, int>();

    /// <summary>The parts in effect: stored parts in slots the skill's level opens, each part's own level reached.</summary>
    IReadOnlyList<int> ActiveParts(int parent)
    {
        if (!skillParts.TryGetValue(parent, out var slots)) return [];
        var at = SkillLevel(parent);
        return slots.Where(s => s.Key <= Specialized.SlotLevels.Count && at >= Specialized.SlotLevels[s.Key - 1]
                                && Specialized.Part(s.Value) is { } p && p.Parent == parent && at >= p.Level)
            .Select(s => s.Value).ToList();
    }

    /// <summary>The child skill a learned mastery skill is cast as (0: none, no parts in effect).</summary>
    public int SpecializedChild(int parent)
    {
        if (!learned.Contains(parent)) return 0;
        var parts = ActiveParts(parent);
        if (parts.Count == 0) return 0;
        var child = parent + SpecializedSkillTables.Suffix(parent, parts);
        return Specialized.IsChild(parent, child) ? child : 0;
    }

    /// <summary>The chain steps after a skill (Skill.dat chain_next, recursively).</summary>
    IEnumerable<int> ChainStepsAfter(int skill)
    {
        var todo = new Stack<int>(data.Skill(skill)?.ChainNext ?? []);
        var seen = new HashSet<int>();
        while (todo.Count > 0)
        {
            var step = todo.Pop();
            if (!seen.Add(step)) continue;
            yield return step;
            foreach (var next in data.Skill(step)?.ChainNext ?? []) todo.Push(next);
        }
    }

    /// <summary>Every child in effect: each mastery skill's and its chain steps' (0x5100).</summary>
    List<int> SpecializedChildren()
    {
        var list = new List<int>();
        foreach (var parent in learned.Where(Specialized.HasParts).Order())
        {
            var child = SpecializedChild(parent);
            if (child == 0) continue;
            list.Add(child);
            var suffix = child - parent;
            foreach (var step in ChainStepsAfter(parent))
                if (Specialized.IsChild(step, step + suffix)) list.Add(step + suffix);
        }
        return list;
    }

    /// <summary>
    /// A specialisation child the client may not cast: not the current child of its learned mastery skill (or of the chain step
    /// the child belongs to). Other skills are never refused here.
    /// </summary>
    bool SpecializedChildRefused(int skill)
    {
        if (Specialized.ParentOf(skill) is not { } parent) return false;
        var root = ChainRoot(parent);
        var rootChild = SpecializedChild(root);
        return rootChild == 0 || skill - parent != rootChild - root;
    }

    /// <summary>The 0x3900 slots of the learned mastery skills with parts on (three each); the stigmas' come from <see cref="StigmaSlotMap"/>.</summary>
    IReadOnlyDictionary<int, IReadOnlyList<(int Slot, int SkillId)>> SpecializedSlotMap()
    {
        var map = new Dictionary<int, IReadOnlyList<(int Slot, int SkillId)>>(StigmaSlotMap());
        foreach (var (parent, slots) in skillParts)
            if (learned.Contains(parent) && slots.Count > 0)
                map[parent] = Enumerable.Range(1, Specialized.SlotLevels.Count).Select(slot => (slot, slots.GetValueOrDefault(slot))).ToList();
        return map;
    }

    /// <summary>The 0x3810/0x380f for a skill and its chain steps when its child changes from one suffix to another.</summary>
    List<byte[]> ChildChange(int parent, int before, int after)
    {
        var replies = new List<byte[]>();
        foreach (var skill in ChainStepsAfter(parent).Prepend(parent))
        {
            var old = before == 0 ? 0 : skill + (before - parent);
            var now = after == 0 ? 0 : skill + (after - parent);
            if (old == now) continue;
            if (old != 0 && Specialized.IsChild(skill, old)) replies.Add(GamePackets.SkillDelete(old));
            if (now != 0 && Specialized.IsChild(skill, now))
                replies.Add(GamePackets.SkillAdd(now, SkillWireDisplayLevel(parent), SkillWireMasteryLevel(parent), SkillChangeSkill));
        }
        return replies;
    }

    /// <summary>
    /// A mastery skill's level changed (bought, refunded, reset): its child and the chain steps' children follow. A child that stays
    /// is updated to the new level (0x3813 reason 6, fork CANDIDATE); one that changes because a slot or part opened or closed is
    /// deleted and added.
    /// </summary>
    List<byte[]> SpecializedLevelChange(int skillId, int childBefore)
    {
        if (!Specialized.HasParts(skillId)) return [];
        var after = SpecializedChild(skillId);
        if (after != childBefore) return ChildChange(skillId, childBefore, after);
        if (after == 0) return [];
        var suffix = after - skillId;
        return [.. ChainStepsAfter(skillId).Prepend(skillId).Where(s => Specialized.IsChild(s, s + suffix))
            .Select(s => SkillLevelUpdate(s + suffix, SkillChangeSkill))];
    }

    async Task<bool> SaveSkillPartsAsync(int parent, SortedDictionary<int, int> next, CancellationToken ct)
    {
        skillParts.TryGetValue(parent, out var current);
        if (Store is { } store && CharacterId is { } character)
        {
            var expected = (current ?? []).Select(s => new SkillPartRecord(parent, s.Key, s.Value)).ToList();
            var parts = next.Select(s => new SkillPartRecord(parent, s.Key, s.Value)).ToList();
            var saved = await Stored(async () => await store.ReplaceSkillPartsAsync(character, parent, expected, parts, ct) ? [[]] : []);
            if (saved.Count == 0) return false;
        }
        if (next.Count == 0) skillParts.Remove(parent); else skillParts[parent] = next;
        return true;
    }

    /// <summary>The replies after the parts of a skill changed: the children, the reply, then 0x3907 with the changed slots.</summary>
    List<byte[]> PartsChanged(int parent, int before, byte[] reply, IReadOnlyList<(int Slot, int SkillId)> changed)
    {
        var replies = ChildChange(parent, before, SpecializedChild(parent));
        RebuildWireClientSkillList();
        return [.. replies, reply, GamePackets.SpecializedSkillUpdate(parent, changed)];
    }

    /// <summary>0x3901 {part i32, slot u8}: a part of a learned mastery skill on one of its open slots.</summary>
    async Task<List<byte[]>> EquipSkillPartAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 7) return [];
        var part = BitConverter.ToInt32(plain, 2);
        int slot = plain[6];
        byte[] Reply(int result) => PacketWriter.Opcode(SpecializedEquipRs).U16(result).I32(part).U8((byte)slot).ToArray();
        if (Specialized.Part(part) is not { } info || !learned.Contains(info.Parent) || slot < 1 || slot > Specialized.SlotLevels.Count)
            return [Reply(SkillModifyNotFound)];
        var parent = info.Parent;
        var at = SkillLevel(parent);
        if (at < info.Level || at < Specialized.SlotLevels[slot - 1]) return [Reply(SkillModifyMinLevel)];
        var next = new SortedDictionary<int, int>(skillParts.GetValueOrDefault(parent) ?? []);
        if (next.Any(s => s.Value == part && s.Key != slot)) return [Reply(SkillModifyOverlapped)];
        var before = SpecializedChild(parent);
        next[slot] = part;
        if (!await SaveSkillPartsAsync(parent, next, ct)) return [Reply(SkillModifyNotFound)];
        return PartsChanged(parent, before, Reply(0), [(slot, part)]);
    }

    /// <summary>0x3905 {part i32}: the part taken off its skill.</summary>
    async Task<List<byte[]>> UnequipSkillPartAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 6) return [];
        var part = BitConverter.ToInt32(plain, 2);
        byte[] Reply(int result) => PacketWriter.Opcode(SpecializedUnequipRs).U16(result).I32(part).ToArray();
        if (Specialized.Part(part) is not { } info || !skillParts.TryGetValue(info.Parent, out var slots) || !slots.ContainsValue(part))
            return [Reply(SkillModifyNotFound)];
        var parent = info.Parent;
        var slot = slots.First(s => s.Value == part).Key;
        var before = SpecializedChild(parent);
        var next = new SortedDictionary<int, int>(slots);
        next.Remove(slot);
        if (!await SaveSkillPartsAsync(parent, next, ct)) return [Reply(SkillModifyNotFound)];
        return PartsChanged(parent, before, Reply(0), [(slot, 0)]);
    }

    /// <summary>0x3903 {part a, part b} (CANDIDATE meaning): two equipped parts of one skill swap slots; an unequipped b takes a's slot.</summary>
    async Task<List<byte[]>> ChangeSkillPartSlotAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 10) return [];
        int a = BitConverter.ToInt32(plain, 2), b = BitConverter.ToInt32(plain, 6);
        byte[] Reply(int result) => PacketWriter.Opcode(SpecializedChangeRs).U16(result).I32(a).I32(b).ToArray();
        if (Specialized.Part(a) is not { } ia || Specialized.Part(b) is not { } ib || ia.Parent != ib.Parent || a == b
            || !skillParts.TryGetValue(ia.Parent, out var slots) || !slots.ContainsValue(a))
            return [Reply(SkillModifyNotFound)];
        var parent = ia.Parent;
        if (SkillLevel(parent) < ib.Level) return [Reply(SkillModifyMinLevel)];
        var slotA = slots.First(s => s.Value == a).Key;
        var next = new SortedDictionary<int, int>(slots) { [slotA] = b };
        var changed = new List<(int, int)> { (slotA, b) };
        if (slots.FirstOrDefault(s => s.Value == b) is { Value: > 0 } held)
        {
            next[held.Key] = a;
            changed.Add((held.Key, a));
        }
        var before = SpecializedChild(parent);
        if (!await SaveSkillPartsAsync(parent, next, ct)) return [Reply(SkillModifyNotFound)];
        return PartsChanged(parent, before, Reply(0), changed);
    }
}
