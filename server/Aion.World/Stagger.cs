using System.Globalization;
using System.Text.Json;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// The groggy guard (stagger, client 그로기) of NPCs, from tools/data/stagger.json (tools/stagger_data.py, build 26262663 tables).
/// Ported from the community fork (Addiboii, c625de5 / 43ff6be / f166332); it replaces our wiki-tooltip stagger gauge.
/// OBSERVED (client tables, build 26262663): each skill's drain = SkillEffect.dat GroggyGuardDecreaseEnergy rows of the groups its
/// Skill.dat SkillEffectTimeDataList names (seq = the row's place in that list); which NPCs have a guard and for how long they stay
/// groggy = NpcData.dat NpcGroggyType / GroggyTime.
/// OBSERVED (the fork's 2.0.6 recording 20261019-231752, boss 2701034, 0xe005 #30737-#33475): every guard drop equals the table
/// value of the player's skill, once per cast, on the row's hit: 0x3804 switch 00 with that effect id and the hit's seq, 0xe005
/// (mask 03 state 01 max energy), then the NPC's HP; at 0 the guard breaks (mask 00 state 03).
/// OBSERVED (community recording 2026-11-06 090856, 756 x 0xe005): a broken guard refills to its maximum GroggyTime after the
/// break (mask 03 state 01 energy = max, 5.10-5.15 s for GroggyTime 5000) and drains again; at the break the NPC gets abnormal
/// 10000002 for GroggyTime from itself (after the 0xe005).
/// At the appear (0x3641) of a guarded NPC of grade 5 and up, its guard follows (AppearInfo). OBSERVED (the fork's recordings
/// 2026-10-19 to 11-06): 4 of 5 such appears, none for the 18 appears of guarded grade 1-4 NPCs. CANDIDATE: the grade is the rule.
/// OURS: the NPC is held while groggy (the official stops its running cast, 0x380c, not sent yet), and the guard is full again
/// after a respawn. Not sent yet: the system skills 1000009/1001450 and 10000099 the official casts at the break.
/// </summary>
public static class Stagger
{
    /// <summary>CANDIDATE: the guard's maximum energy of an NPC no recording shows (the first recorded HeroMonster's 50).</summary>
    public const int GuardMax = 50;
    /// <summary>The abnormal of a broken guard (OBSERVED 10000002, GroggyTime ms, caster the NPC itself).</summary>
    public const int GroggyAbnormal = 10000002;
    /// <summary>
    /// Exception to the table rule, labelled: the guard maximum is in none of the client tables we read (NpcData has GroggyTime
    /// and NpcGroggyType only), so the recorded maxima per NpcData id. OBSERVED: 2701034 50 (ours and the fork's recordings),
    /// 2300104 and 2300108 1200, 2300171 240 (community recording 2026-11-06 090856). Others: GuardMax (CANDIDATE).
    /// </summary>
    static readonly Dictionary<int, int> RecordedGuardMax = new() { [2701034] = 50, [2300104] = 1200, [2300108] = 1200, [2300171] = 240 };
    static int abnormalUid = 0x6000_0000;
    /// <summary>0xe005 states as recorded: 01 while the guard holds, 03 once it broke (CANDIDATE names).</summary>
    public const byte GuardHolding = 1, GuardBroken = 3;
    /// <summary>The lowest NPC grade whose guard is shown at its appear (CANDIDATE, see above).</summary>
    public const int AppearGrade = 5;

    /// <summary>One drain row of a skill: its hit seq (1-based SkillEffectTimeDataList place), effect id and energy.</summary>
    public sealed record DrainRow(int Seq, int EffectId, int Amount);

    sealed record Tables(Dictionary<int, DrainRow[]> Skills, Dictionary<int, int> GroggyTime);

    static readonly Lazy<Tables> tables = new(() =>
    {
        using var stream = typeof(Stagger).Assembly.GetManifestResourceStream("stagger.json");
        if (stream is null) return new([], []);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        var decrease = root.GetProperty("decrease").EnumerateObject()
            .ToDictionary(p => int.Parse(p.Name, CultureInfo.InvariantCulture), p => p.Value.GetInt32());
        var skills = new Dictionary<int, DrainRow[]>();
        foreach (var row in root.GetProperty("skills").EnumerateObject())
        {
            var drains = row.Value.EnumerateArray()
                .Select(e => (Seq: e[0].GetInt32(), Effect: e[1].GetInt32()))
                .Where(e => decrease.ContainsKey(e.Effect))   // GroggyGuardIncreaseEnergy rows are kept in the file, not applied
                .Select(e => new DrainRow(e.Seq, e.Effect, decrease[e.Effect])).ToArray();
            if (drains.Length > 0) skills[int.Parse(row.Name, CultureInfo.InvariantCulture)] = drains;
        }
        var groggy = new Dictionary<int, int>();
        foreach (var row in root.GetProperty("npcs").EnumerateObject())
            if (row.Value[0].GetString() == "GroggyGuard")
                groggy[int.Parse(row.Name, CultureInfo.InvariantCulture)] = row.Value[1].GetInt32();
        return new(skills, groggy);
    });

    /// <summary>A skill's guard drains (empty when it drains nothing).</summary>
    public static IReadOnlyList<DrainRow> Drains(int skillId) => tables.Value.Skills.TryGetValue(skillId, out var d) ? d : [];

    /// <summary>A skill's whole drain per cast.</summary>
    public static int DrainOf(int skillId) => Drains(skillId).Sum(d => d.Amount);

    /// <summary>Whether the NpcData template has a groggy guard (NpcGroggyType GroggyGuard).</summary>
    public static bool HasGuard(int npcId) => tables.Value.GroggyTime.ContainsKey(npcId);

    /// <summary>NpcData GroggyTime (ms) of a guarded NPC, else 0.</summary>
    public static int GroggyTimeMs(int npcId) => tables.Value.GroggyTime.GetValueOrDefault(npcId);

    /// <summary>The guard's maximum energy of an NPC (RecordedGuardMax, else GuardMax).</summary>
    public static int MaxOf(int npcId) => RecordedGuardMax.GetValueOrDefault(npcId, GuardMax);

    /// <summary>
    /// The drain rows that ride one scheduled hit of a cast: those whose seq is past the previous hit's seq (0 for the first) and at
    /// most this hit's, and on the last hit every row past it (OURS: our hit list can be shorter than SkillEffectTimeDataList, and
    /// the official drains once per cast). Null when none.
    /// </summary>
    public static IReadOnlyList<DrainRow>? DrainsOnHit(int skillId, int previousSeq, int seq, bool lastHit)
    {
        if (seq <= previousSeq) return null;
        var rows = Drains(skillId).Where(d => d.Seq > previousSeq && (d.Seq <= seq || lastHit)).ToList();
        return rows.Count > 0 ? rows : null;
    }

    /// <summary>The guard sent right after a guarded boss's 0x3641 (holding with the energy left, or broken), else null.</summary>
    public static byte[]? AppearInfo(Target target)
    {
        if (target.Dead || target.Grade < AppearGrade || !HasGuard(target.NpcId)) return null;
        if (target.GroggyEnergy == 0) return GamePackets.UpdateGroggyInfo(target.Key, GuardBroken, null, null);
        var max = MaxOf(target.NpcId);
        return GamePackets.UpdateGroggyInfo(target.Key, GuardHolding, max, target.GroggyEnergy ?? max);
    }

    /// <summary>An NPC's 0x3641 and, for a guarded boss, its guard right after it (AppearInfo).</summary>
    public static IEnumerable<byte[]> WithGuard(byte[] appear, Target target) =>
        AppearInfo(target) is { } guard ? [appear, guard] : [appear];

    /// <summary>A broken guard whose GroggyTime has passed refills: 0xe005 mask 03 state 01 energy = max (OBSERVED), else null.</summary>
    public static byte[]? Refill(Target target, long now)
    {
        if (target.GroggyEnergy != 0 || now < target.GroggyRefillAt) return null;
        target.GroggyEnergy = null;
        var max = MaxOf(target.NpcId);
        return GamePackets.UpdateGroggyInfo(target.Key, GuardHolding, max, max);
    }

    /// <summary>A respawned NPC's guard is full and it is not groggy (OURS).</summary>
    public static void Restore(Target target)
    {
        target.GroggyEnergy = null;
        target.GroggyRefillAt = 0;
        target.StaggerUntil = 0;
    }

    /// <summary>The groggy NPCs whose GroggyTime is over act again (OURS hold, see the class summary).</summary>
    public static void Tick(IReadOnlyDictionary<int, Target>? targets, long now)
    {
        if (targets is null) return;
        foreach (var t in targets.Values)
        {
            if (t.StaggerUntil <= 0 || now < t.StaggerUntil) continue;
            t.StaggerUntil = 0;
            t.Held = false;
        }
    }

    /// <summary>
    /// One cast's drain of a guarded NPC's guard: per drain row the 0x3804 switch 00 with its effect id and hit seq, then 0xe005
    /// with the energy left, or the broken state at 0 followed by the groggy abnormal; the guard refills GroggyTime later (Refill).
    /// Nothing for an NPC without a guard, a skill without drains or a guard still broken. A refill due but not yet sent goes first.
    /// <paramref name="rows"/> limits the drain to the rows of one hit (DrainsOnHit); null drains every row of the skill.
    /// The drain is only for the hits of a skill on an NPC; the old PvP / player gauge is gone (players have no guard in NpcData).
    /// </summary>
    public static List<byte[]> Drain(Target target, int skill, int actorKey, byte actionUid, long now, IReadOnlyList<DrainRow>? rows = null)
    {
        rows ??= Drains(skill);
        if (target.Dead || target.OwnerKey != 0 || !HasGuard(target.NpcId) || rows.Count == 0) return [];
        var replies = new List<byte[]>();
        if (Refill(target, now) is { } refilled) replies.Add(refilled);
        if (target.GroggyEnergy == 0) return replies;
        var max = MaxOf(target.NpcId);
        foreach (var drain in rows)
        {
            replies.Add(GamePackets.SkillEffect(target.Key, actorKey, skill, actionUid, drain.EffectId, drain.Seq));
            target.GroggyEnergy = Math.Max(0, (target.GroggyEnergy ?? max) - drain.Amount);
            if (target.GroggyEnergy == 0)
            {
                var groggy = Math.Max(1, GroggyTimeMs(target.NpcId));
                target.GroggyRefillAt = now + groggy;
                target.StaggerUntil = now + groggy;
                target.Held = true;
                replies.Add(GamePackets.UpdateGroggyInfo(target.Key, GuardBroken, null, null));
                replies.Add(PlayerPackets.AbnormalAdd(target.Key, [new AbnormalWire(Interlocked.Increment(ref abnormalUid), GroggyAbnormal, groggy,
                    now + groggy, CasterKey: target.Key, Level: 1, RangeBase: target.Position)]));
                break;
            }
            replies.Add(GamePackets.UpdateGroggyInfo(target.Key, GuardHolding, max, target.GroggyEnergy));
        }
        return replies;
    }
}
