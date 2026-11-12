using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Core.Characters;
using Aion.Protocol.Native;

namespace Aion.World;

// Stigma skills (SkillAcquireData AcquireType Stigma, tools/data/stigma-skills.json). OBSERVED official 2026-11-12 (capture VM,
// a Cleric re-buying her four stigmas after a full reset):
// - The levels are bought with the same SkillLevelUp_RQ 0x383f / 0x3840 as the skill-point levels; the cost is stigma points
//   (ESkillPointType StigmaShard 2: 1 per level for 1-5, 2 for 6-10, 4 for 11-15, 8 for 16-20) and 0x5105 carries the stigma
//   points SPENT as type 2, exactly like type 1.
// - Level 1 learns the skill: 0x3907 SpecializedSkillUpdate (its parts in their slots) first, then 0x3656, 0x3840 and
//   0x380f SkillAdd {skill, 1, 1, reason SkillPointAdd} instead of a 0x3813.
// - At 5/10/15/20 a specialized part unlocks (SpecializedSkillParts: 17400010 at 5, ...): 0x380f SkillAdd {part, level, level,
//   ChangeSkill} before the reply; every unlocked part follows its stigma's level (0x3813 reason ChangeSkill, also before it).
// - The pool: Exp.dat BonusStigmaPoint at the character level (cumulative) plus the Stigma Shard items used (GetStigmaShard).
// - A full stigma reset (0x3843 type 2) is free: 0x3810 SkillDelete per part, 0x3656, 0x3844 {stigmas → 0, refund type 2},
//   one 0x3813 with every stigma at level 0 (reason SkillLevelModify), 0x5105 type 2.
// Skill-point items (Wisdom Stone, GetSkillPoint) add to the type-1 pool the same way; 0x3811 UpdateItemSkillPoint reports the
// item total of a type (OBSERVED 2026-11-04).
public sealed partial class GameSession
{
    const byte SkillPointAdd = 2, SkillChangeSkill = 6;   // ESkillAcquireReason
    public const int SkillAcquireInvalidLevel = 6186, SkillAcquireInvalidClass = 6187, SkillAcquireNotEnoughAscensionGrade = 6194;
    const string SkillItemPointsSetting = "skill_item_points";
    const int StigmaSlotCount = 5;   // SpecializedSkillSlot.dat: Stigma Slot_1..Slot_5

    /// <summary>Stigma levels bought (level 1+), from game.character_skills.purchased_level.</summary>
    readonly Dictionary<int, int> stigmaLevels = [];
    /// <summary>Skill points gained from items per ESkillPointType (Wisdom Stones type 1, Stigma Shards type 2), a character setting.</summary>
    readonly Dictionary<byte, int> skillItemPoints = [];

    /// <summary>
    /// A stigma or one of its specialized parts that the class kit does not grant. The kit wins: our Gladiator E-slot combo
    /// grants Rage Burst (11390000, a stigma in SkillAcquireData) by level, and it keeps working as a kit skill.
    /// </summary>
    bool IsStigmaFamily(int skillId) =>
        (data.IsStigmaSkill(skillId) || data.StigmaPartParent(skillId) is { } parent && !IsKitSkill(parent)) && !IsKitSkill(skillId);

    /// <summary>Granted without stigma points: the kit tables, the last 0x5100 baselines, or learned without a bought stigma level.</summary>
    bool IsKitSkill(int skillId) => kitSkillLevels.ContainsKey(skillId) || kitListBaselines.ContainsKey(skillId)
        || learned.Contains(skillId) && StigmaLevel(skillId) == 0;

    /// <summary>A stigma this character buys with stigma points (SkillLevelUp / Down / Reset go to the stigma handlers).</summary>
    bool IsBuyableStigma(int skillId) => data.IsStigmaSkill(skillId) && !IsKitSkill(skillId);

    /// <summary>The stigma's bought level; a part follows its stigma.</summary>
    int StigmaLevel(int skillId) => stigmaLevels.GetValueOrDefault(data.StigmaPartParent(skillId) ?? skillId);

    public int StigmaSkillLevel(int skillId) => StigmaLevel(skillId);

    int SpentStigmaPoints() =>
        stigmaLevels.Sum(s => Enumerable.Range(1, s.Value).Sum(l => data.StigmaLevelCost(s.Key, l)?.Cost ?? 0));

    int EarnedStigmaPoints() => data.StigmaPoints(level) + skillItemPoints.GetValueOrDefault(SkillPointTypeStigma);

    public int StigmaPointsLeft => Math.Max(0, EarnedStigmaPoints() - SpentStigmaPoints());

    int EarnedSkillPoints() => data.SkillPoints(level) + skillItemPoints.GetValueOrDefault(SkillPointTypePoint);

    IEnumerable<int> UnlockedStigmaParts(int skillId, int stigmaLevel) =>
        data.StigmaParts(skillId).Where(p => p.ParentLevel <= stigmaLevel).Select(p => p.Child);

    /// <summary>The learned stigmas and their unlocked parts, each at the stigma's level (0x5100 lists both, OBSERVED).</summary>
    IEnumerable<(int SkillId, int Level)> StigmaSkillEntries() =>
        stigmaLevels.Where(s => s.Value > 0).OrderBy(s => s.Key)
            .SelectMany(s => UnlockedStigmaParts(s.Key, s.Value).Prepend(s.Key).Select(id => (id, s.Value)));

    void AddStigmaSkillsToLearned()
    {
        foreach (var (id, _) in StigmaSkillEntries()) learned.Add(id);
    }

    /// <summary>The stigma's five 0x3900 / 0x3907 slots: each part in its initial slot, the rest 0.</summary>
    IReadOnlyList<(int Slot, int SkillId)> StigmaSlots(int skillId)
    {
        var parts = data.StigmaParts(skillId);
        return Enumerable.Range(1, StigmaSlotCount)
            .Select(slot => (slot, parts.FirstOrDefault(p => p.Slot == slot).Child)).ToList();
    }

    IReadOnlyDictionary<int, IReadOnlyList<(int Slot, int SkillId)>> StigmaSlotMap() =>
        stigmaLevels.Where(s => s.Value > 0).ToDictionary(s => s.Key, s => StigmaSlots(s.Key));

    byte[] StigmaPointUpdate() => GamePackets.SkillPointUpdate(SpentStigmaPoints(), SkillPointTypeStigma);

    /// <summary>
    /// World entry: the item point totals (0x3811, CANDIDATE: not seen at an official entry), the stigma points spent once the
    /// character has a stigma pool (level 23+ or shards), then the skill points spent last (single-entry 0x5105 packets, as
    /// every official one).
    /// </summary>
    List<byte[]> SkillPointsAtEntry()
    {
        var replies = skillItemPoints.Where(p => p.Value > 0).OrderBy(p => p.Key)
            .Select(p => GamePackets.UpdateItemSkillPoint(p.Key, p.Value)).ToList();
        if (EarnedStigmaPoints() > 0 || stigmaLevels.Count > 0) replies.Add(StigmaPointUpdate());
        replies.Add(SkillPointUpdate());
        return replies;
    }

    void LoadSkillItemPoints(IReadOnlyDictionary<string, JsonElement> settings)
    {
        skillItemPoints.Clear();
        try
        {
            if (settings.TryGetValue(SkillItemPointsSetting, out var value) && value.Deserialize<Dictionary<string, int>>() is { } stored)
                foreach (var (type, points) in stored)
                    if (byte.TryParse(type, out var t) && points > 0) skillItemPoints[t] = points;
        }
        catch (JsonException) { }
    }

    /// <summary>The stigma rows of game.character_skills (purchased_level is the stigma level; level 0 is kept as level 1, purchased 0).</summary>
    void LoadStigmaLevels(IEnumerable<SkillState> stored)
    {
        stigmaLevels.Clear();
        var own = className is { } cls ? data.StigmaSkills(cls).ToHashSet() : [];
        foreach (var s in stored)
            if (s.Purchased > 0 && own.Contains(s.SkillId) && !IsKitSkill(s.SkillId)) stigmaLevels[s.SkillId] = Math.Min(s.Purchased, 20);
    }

    async Task<bool> SaveStigmaLevelAsync(int skillId, int stigmaLevel, CancellationToken ct)
    {
        if (Store is not { } store || CharacterId is not { } character) return true;
        var saved = await Stored(async () => await store.SetSkillLevelAsync(character, skillId, Math.Max(1, stigmaLevel), stigmaLevel, ct) ? [[]] : []);
        return saved.Count > 0;
    }

    /// <summary>0x383f for a stigma: the next level for its stigma points (see the file comment for the official order).</summary>
    async Task<List<byte[]>> StigmaLevelUpAsync(int skillId, CancellationToken ct)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(SkillLevelUpRs).U16(result).I32(skillId).ToArray();
        var next = StigmaLevel(skillId) + 1;
        List<byte[]> Refuse(int result)
        {
            Note($"StigmaLevelUp refused skill={skillId} result={result} next={next} charLevel={level} ascension={selected?.Ascension ?? 1} left={StigmaPointsLeft} spent={SpentStigmaPoints()} earned={EarnedStigmaPoints()}");
            return [Reply(result), StigmaPointUpdate()];
        }
        if (className is not { } cls || !data.StigmaSkills(cls).Contains(skillId)) return Refuse(SkillAcquireInvalidClass);
        if (data.StigmaLevelCost(skillId, next) is not { } cost) return Refuse(SkillAcquireInvalidLevel);
        // Only the level-1 row names the grade (AscensionGrade_3); later levels say None.
        var needAscension = Math.Max(cost.NeedAscension, data.StigmaLevelCost(skillId, 1)?.NeedAscension ?? 0);
        if ((selected?.Ascension ?? 1) < needAscension) return Refuse(SkillAcquireNotEnoughAscensionGrade);
        if (cost.NeedLevel > level) return Refuse(SkillAcquireNotEnoughCharLevel);
        if (cost.Cost > StigmaPointsLeft) return Refuse(SkillAcquireNotEnoughSkillPoint);
        if (!await SaveStigmaLevelAsync(skillId, next, ct)) return [Reply(1), StigmaPointUpdate()];
        stigmaLevels[skillId] = next;
        learned.Add(skillId);
        var before = new List<byte[]>();
        if (next == 1) before.Add(GamePackets.SpecializedSkillUpdate(skillId, StigmaSlots(skillId)));
        foreach (var (child, parentLevel, _) in data.StigmaParts(skillId))
        {
            if (parentLevel == next)
            {
                learned.Add(child);
                before.Add(GamePackets.SkillAdd(child, next, next, SkillChangeSkill));
            }
            else if (parentLevel < next)
                before.Add(GamePackets.SkillLevelUpdates([child], next, SkillChangeSkill));
        }
        RecomputeCombatPower();
        RebuildWireClientSkillList();
        var learnedOrLevel = next == 1
            ? GamePackets.SkillAdd(skillId, 1, 1, SkillPointAdd)
            : GamePackets.SkillLevelUpdates([skillId], next, SkillPointLevelUp);
        return [.. before, CharCombatPowerUpdate(), Reply(0), learnedOrLevel, StigmaPointUpdate()];
    }

    /// <summary>0x3841 for a stigma: one level back, its points refunded; at 0 the stigma is unlearned (OURS: the official cost is unobserved).</summary>
    async Task<List<byte[]>> StigmaLevelDownAsync(int skillId, CancellationToken ct)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(SkillLevelDownRs).U16(result).I32(skillId).ToArray();
        var current = StigmaLevel(skillId);
        if (current <= 0 || !await SaveStigmaLevelAsync(skillId, current - 1, ct)) return [Reply(1), StigmaPointUpdate()];
        var next = current - 1;
        var deleted = new List<byte[]>();
        foreach (var (child, parentLevel, _) in data.StigmaParts(skillId))
        {
            if (parentLevel == current)
            {
                learned.Remove(child);
                deleted.Add(GamePackets.SkillDelete(child));
            }
            else if (parentLevel < current)
                deleted.Add(GamePackets.SkillLevelUpdates([child], next, SkillChangeSkill));
        }
        if (next == 0)
        {
            stigmaLevels.Remove(skillId);
            learned.Remove(skillId);
        }
        else stigmaLevels[skillId] = next;
        RecomputeCombatPower();
        RebuildWireClientSkillList();
        return [.. deleted, CharCombatPowerUpdate(), Reply(0), GamePackets.SkillLevelUpdates([skillId], next, SkillPointLevelDown), StigmaPointUpdate()];
    }

    /// <summary>
    /// 0x3843 type 2: every stigma (skill 0) or the one named back to level 0, free, the stigma points refunded. OBSERVED official
    /// 2026-11-12: 0x3810 per part, 0x3656, 0x3844 0000 02 00000000 04 {c0800901 00, ...} 01 02 1e00, 0x3813 (four at 0, reason 5),
    /// 0x5105 type 2. With nothing to reset: the reply alone, empty, with a zero refund. The skill-point levels are never touched.
    /// </summary>
    async Task<List<byte[]>> StigmaResetAsync(int skillId, CancellationToken ct)
    {
        var targets = (skillId == 0 ? stigmaLevels.Where(s => s.Value > 0).Select(s => s.Key) : StigmaLevel(skillId) > 0 && IsBuyableStigma(skillId) ? [skillId] : [])
            .Order().ToList();
        var spentBefore = SpentStigmaPoints();
        var deleted = new List<byte[]>();
        var reset = new List<int>();
        foreach (var id in targets)
        {
            if (!await SaveStigmaLevelAsync(id, 0, ct)) continue;
            foreach (var child in UnlockedStigmaParts(id, StigmaLevel(id)))
            {
                learned.Remove(child);
                deleted.Add(GamePackets.SkillDelete(child));
            }
            stigmaLevels.Remove(id);
            learned.Remove(id);
            reset.Add(id);
        }
        var reply = StigmaResetReply(skillId, reset, spentBefore - SpentStigmaPoints());
        if (reset.Count == 0) return [reply];
        RecomputeCombatPower();
        RebuildWireClientSkillList();
        return [.. deleted, CharCombatPowerUpdate(), reply, GamePackets.SkillLevelUpdates(reset, 0, SkillLevelModify), StigmaPointUpdate()];
    }

    static byte[] StigmaResetReply(int skillId, IReadOnlyList<int> reset, int refund)
    {
        var w = PacketWriter.Opcode(SkillLevelResetRs).U16(0).U8(SkillAcquireTypeStigma).I32(skillId).Varint((uint)reset.Count);
        foreach (var id in reset) w.I32(id).U8(0);
        return w.Varint(1).AppendSkillPointInfo(Math.Max(0, refund), SkillPointTypeStigma).ToArray();
    }

    /// <summary>
    /// A GetSkillPoint (Wisdom Stone, type 1) or GetStigmaShard (Stigma Shard, type 2) item: used up, its points added to the
    /// character's item points of that type (saved), then 0x3811 with the new item total (OBSERVED order: 0x561b, 0x5613, 0x3811).
    /// </summary>
    async Task<List<byte[]>?> UseSkillPointItemAsync(INativeCharacterStore store, Guid character, ItemState row, ItemUse use, byte pointType, bool reply, CancellationToken ct)
    {
        if (use.Values is not [var text, ..] || !int.TryParse(text, out var amount) || amount <= 0) return null;
        var used = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, 1, ct) ? [[]] : []);
        if (used.Count == 0) return null;
        skillItemPoints[pointType] = skillItemPoints.GetValueOrDefault(pointType) + amount;
        await SaveSettingAsync(SkillItemPointsSetting, skillItemPoints.ToDictionary(p => p.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), p => p.Value), ct);
        var replies = await InventoryChangesAsync(ct, 0, DecreaseItemUse);
        replies.AddRange(Used(row, reply));
        replies.Add(GamePackets.UpdateItemSkillPoint(pointType, skillItemPoints[pointType]));
        return replies;
    }
}
