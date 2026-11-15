using Aion.Protocol.Native;

namespace Aion.World;

// Experience and levels. Experience is held per level (Exp.dat NeedExp to the next level), as the official
// level-ups show (capture 20261013-144836: ExpUpdate with exp 0 and the reward equal to NeedExp, then
// LevelUpdate). A level-up sends, in the official order, the contents it unlocks (0xe258), 0x8d07 ExpUpdate,
// 0x8d08 LevelUpdate, the skills the class learns (0x5101 AddSkillList, reason LevelUp), and 0x8d91 to those
// nearby only; stats follow the new level (PcStatLevel). Kill and quest level-ups do not refill HP/MP on the player
// (OBSERVED in the 2026-10-13/19 recordings, 9 level-ups: HP keeps regenerating from where it was; the A2Core fork of
// addiboii found the same).
// Kill experience, ported from the A2Core fork of addiboii (forge #72), which fitted 1425 recorded kill rewards
// (ExpUpdate reason 1, own levels 1-45). CANDIDATE until our own capture confirms it (#75 item 7):
// - none below own level 10 (the fork: none at 3-7, the first at 10; level 9 unrecorded). Our 2026-10-30/31 captures
//   showed no kill reward at all, every ExpUpdate there a quest reward (reason 2), which this rule does not contradict.
// - none inside an instance (dungeons, walk-in quest layers, arenas: instanceKey != 0; the fork's ~520 kills).
// - grade 3 pays 390.8 x 1.085^monster level (the fork's fit, within 0.25 % on 21 levels); grades 1/2/4 pay 5/13,
//   8/13 and 18/13 of it, grade 5 and up as grade 4. By the gap (own level minus the monster's): full up to +5 and for
//   stronger monsters, 0.8 at +6, 0.6 at +7, 0.4 at +8, 0.2 at +9, 0.1 from +10.
// - nearby party members share it evenly (the fork saw it for two members; more is OURS).
// Scheduled experience events (SharedWorld.ExpRate) multiply kill and quest rewards.
public sealed partial class GameSession
{
    long experience;
    public long Experience => experience;
    /// <summary>The first own level at which kills pay (CANDIDATE, see above).</summary>
    public const int KillExperienceLevel = 10;
    /// <summary>The grade-3 reward at monster level L: KillExperienceBase x KillExperienceGrowth^L (CANDIDATE fit).</summary>
    public const double KillExperienceBase = 390.8, KillExperienceGrowth = 1.085;
    /// <summary>The monsters killed since the last experience update (cast replies collect them): level, grade, in an instance.</summary>
    readonly List<(int Level, int Grade, bool Instance)> killedLevels = [];
    const byte ExpReasonNpc = 1, ExpReasonQuest = 2;   // EExpModifyReason kNpcReward, kQuestReward

    /// <summary>A grade's share of the grade-3 reward (5 : 8 : 13 : 18 for grades 1-4; grade 5 and up as 4). CANDIDATE.</summary>
    public static double KillGradeFactor(int grade) => grade switch { <= 1 => 5 / 13.0, 2 => 8 / 13.0, 3 => 1, _ => 18 / 13.0 };

    /// <summary>The share by the level gap, own level minus the monster's. CANDIDATE.</summary>
    public static double KillGapFactor(int gap) => gap switch { <= 5 => 1, 6 => 0.8, 7 => 0.6, 8 => 0.4, 9 => 0.2, _ => 0.1 };

    /// <summary>The experience a kill pays before event rates and party sharing (the rules above).</summary>
    public static long KillReward(int ownLevel, int npcLevel, int grade, bool instance)
    {
        if (instance || ownLevel < KillExperienceLevel) return 0;
        var mob = Math.Max(1, npcLevel);
        return (long)Math.Round(KillExperienceBase * Math.Pow(KillExperienceGrowth, mob) * KillGradeFactor(grade) * KillGapFactor(ownLevel - mob));
    }

    /// <summary>This character's experience for a kill of a monster of this level and grade where it stands now.</summary>
    public long KillExperience(int npcLevel, int grade = 3) => KillReward(level, npcLevel, grade, instanceKey != 0);

    /// <summary>The experience of the kills since the last update: shared with nearby party members, then applied.</summary>
    async Task<List<byte[]>> KillExperienceAsync(CancellationToken ct)
    {
        if (killedLevels.Count == 0) return [];
        var reward = killedLevels.Sum(k => KillReward(level, k.Level, k.Grade, k.Instance));
        killedLevels.Clear();
        var gained = (long)Math.Round(reward * (options.World?.ExpRate ?? 1));
        if (gained <= 0) return [];
        // Party members nearby share it evenly (no party bonus); each applies its share on its own loop.
        var members = NearbyPartyMembers();
        var share = Math.Max(1, gained / (members.Count + 1));
        foreach (var member in members) member.Session.ShareExperience(share, ct);
        return await GainExperienceAsync(experience + share, share, ExpReasonNpc, ct);
    }

    /// <summary>
    /// Applies a new experience total (the current level's experience plus the gain): level-ups while it
    /// reaches NeedExp, stored, and the messages in the official order.
    /// </summary>
    async Task<List<byte[]>> GainExperienceAsync(long total, long gained, byte reason, CancellationToken ct)
    {
        if (reason == ExpReasonQuest && gained > 0)
        {
            var scaled = (long)Math.Round(gained * (options.World?.ExpRate ?? 1));
            total += scaled - gained;
            gained = scaled;
        }
        var before = level;
        var newLevel = level;
        while (newLevel < data.MaxLevel && data.NeedExp(newLevel) is > 0 and var need && total >= need)
        {
            total -= need;
            newLevel++;
        }
        if (newLevel >= data.MaxLevel) total = Math.Min(total, Math.Max(0, data.NeedExp(data.MaxLevel) - 1));
        if (Store is { } store && CharacterId is { } character)
        {
            var saved = await Stored(async () => await store.SaveProgressAsync(character, newLevel, total, ct) ? [[]] : []);
            if (saved.Count == 0) return [];
            if (selected is { } sel && sel.Summary.CharacterId == character)
                selected = sel with { Progress = sel.Progress with { Level = newLevel, Experience = total } };
        }
        experience = total;
        var replies = new List<byte[]>();
        if (newLevel == before)
        {
            replies.Add(GamePackets.ExpUpdate(total, gained, reason));
            return replies;
        }
        var unlockedBefore = GamePackets.UnlockedContents(before).ToHashSet();
        var skillsBefore = learned.ToHashSet();
        level = newLevel;
        LogLevelUp(before, newLevel);
        var daevanionNodesBefore = daevanionNodes.Count;
        EnsureStartNodes();
        RefreshKitSkillLevels();
        replies.AddRange(AchieveSet("PCLevel", level));
        var unlockedList = GamePackets.UnlockedContents(level).Where(t => !unlockedBefore.Contains(t)).ToList();
        if (!unlockedBefore.Contains(DaevanionContentsUnlock) && HasLevelUnlockedClassBoard())
            unlockedList.Add(DaevanionContentsUnlock);
        if (unlockedList.Count > 0)
            replies.Add(GamePackets.ContentsUnlockUpdate(unlockedList.Distinct().Order().ToArray()));
        replies.Add(GamePackets.ExpUpdate(total, gained, reason));
        replies.Add(GamePackets.LevelUpdate(level, level, total));
        if (className is { } cls && Profile() is { } profile)
        {
            var kit = data.SkillListForClient(cls, level, profile);
            learned = kit.Select(k => k.SkillId).ToHashSet();
            AddStigmaSkillsToLearned();
            var added = kit.Where(k => !skillsBefore.Contains(k.SkillId)).ToList();
            if (added.Count > 0)
            {
                var originals = KitOriginalLevels(added);
                var daevanion = SkillWireDaevanionBonuses(added);
                var levels = added.Select(s => (s.SkillId, SkillWireDisplayLevel(s.SkillId))).ToList();
                replies.Add(GamePackets.AddSkillList(playerKey, levels, originalLevels: originals, additionalDaevanion: daevanion));
            }
            RebuildWireClientSkillList();
            replies.AddRange(PassiveAbnormals());
            replies.Add(SkillPointUpdate());
        }
        var broadcast = PlayerPackets.LevelBroadcast(playerKey, level);
        worldPlayer?.Map?.Broadcast(worldPlayer, [broadcast]);
        replies.AddRange(RefreshStats());
        if (daevanionNodes.Count > daevanionNodesBefore)
        {
            replies.Add(DaevanionBoardInfo());
            replies.Add(DaevanionPointUpdate());
        }
        // A level-up from kill or party experience re-checks the quests as the quest-reward level-up does (that one runs
        // the re-check itself after the reward): PCLevel steps and level-gated Auto quests move on now, not at the next
        // quest change. OURS (CANDIDATE order: after the level-up messages; no kill level-up near a level-gated quest in
        // the recordings). Idea from Addiboii/A2Core 13a897e.
        if (reason != ExpReasonQuest && questLog is not null)
            replies.AddRange(await QuestChangesAsync(questLog.Recheck(level), ct));
        return replies;
    }
}
