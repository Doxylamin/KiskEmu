using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    public const ushort RewardLimitResetNt = 0x3C05, RewardLimitUpdateNt = 0x3C04, DungeonCoolTimesNt = 0x3A60;
    long characterDailyResetMs;
    long characterWeeklyResetMs;
    readonly Dictionary<int, (string Period, int Count)> rewardLimits = [];
    readonly Dictionary<int, long> dungeonCoolUntilMs = [];
    bool rewardLimitsLoaded;
    // Saved only when changed (each save is a whole transaction: delete and re-insert every row), at most once a
    // second from the tick; logout and disconnect force the last one. Set the flag at every change to the two maps.
    bool rewardLimitsDirty, dungeonCooltimesDirty;
    long rewardLimitsFlushAt, dungeonCooltimesFlushAt;
    public const int ContentLimitsFlushMs = 1000;

    void LoadRewardLimits(IReadOnlyList<RewardLimitRecord> rows)
    {
        rewardLimits.Clear();
        foreach (var row in rows) rewardLimits[row.LimitType] = (row.Period, row.Count);
        rewardLimitsLoaded = true;
        rewardLimitsDirty = false;
    }

    async Task FlushRewardLimitsAsync(long now, CancellationToken ct, bool force = false)
    {
        if (!rewardLimitsDirty || (!force && now < rewardLimitsFlushAt)) return;
        if (!rewardLimitsLoaded || Store is not { } store || CharacterId is not { } character) return;
        rewardLimitsFlushAt = now + ContentLimitsFlushMs;
        // Cleared before the save so a change made while it runs (the turn is released for it) is saved next time.
        rewardLimitsDirty = false;
        var rows = rewardLimits.Select(kv => new RewardLimitRecord(kv.Key, kv.Value.Period, kv.Value.Count)).ToList();
        if ((await Stored(async () => await store.SaveRewardLimitsAsync(character, rows, ct) ? [[]] : [])).Count == 0)
            rewardLimitsDirty = true;
    }

    void LoadDungeonCooltimes(IReadOnlyList<DungeonCooltimeRecord> rows)
    {
        dungeonCoolUntilMs.Clear();
        foreach (var row in rows) dungeonCoolUntilMs[row.DungeonId] = row.UntilMs;
        dungeonCooltimesDirty = false;
    }

    async Task FlushDungeonCooltimesAsync(long now, CancellationToken ct, bool force = false)
    {
        if (!dungeonCooltimesDirty || (!force && now < dungeonCooltimesFlushAt)) return;
        if (Store is not { } store || CharacterId is not { } character) return;
        dungeonCooltimesFlushAt = now + ContentLimitsFlushMs;
        dungeonCooltimesDirty = false;
        var rows = dungeonCoolUntilMs.Select(kv => new DungeonCooltimeRecord(kv.Key, kv.Value)).ToList();
        if ((await Stored(async () => await store.SaveDungeonCooltimesAsync(character, rows, ct) ? [[]] : [])).Count == 0)
            dungeonCooltimesDirty = true;
    }

    /// <summary>Lazy resets on login / after a missed world tick while offline.</summary>
    List<byte[]> ApplyLazyPeriodicResets(long now, bool notifyClient)
    {
        var replies = new List<byte[]>();
        var daily = ContentResetSchedule.PeriodStartMs(1, now);
        var weekly = ContentResetSchedule.PeriodStartMs(2, now);
        if (daily > characterDailyResetMs)
        {
            characterDailyResetMs = daily;
            replies.AddRange(ResetDailyAchievementStates(now));
            if (notifyClient) replies.Add(RewardLimitResetPacket([1]));
        }
        if (weekly > characterWeeklyResetMs)
        {
            characterWeeklyResetMs = weekly;
            replies.AddRange(ResetWeeklyAchievementStates(now));
            if (notifyClient) replies.Add(RewardLimitResetPacket([2]));
        }
        PruneExpiredDungeonCooltimes(now);
        return replies;
    }

    List<byte[]> ResetDailyAchievementStates(long now)
    {
        var touched = new List<int>();
        foreach (var (id, state) in achievements.ToList())
        {
            if (Achievements.Of(id) is not { Reset: 1 }) continue;
            if (ContentResetSchedule.AchievementCycleStartMs(1, now) <= state.UpdatedMs) continue;
            achievements[id] = new AchievementState { UpdatedMs = now };
            achievementDirty.Add(id);
            touched.Add(id);
        }
        return touched.Count == 0 ? [] : [AchievementUpdate(touched)];
    }

    List<byte[]> ResetWeeklyAchievementStates(long now)
    {
        var touched = new List<int>();
        foreach (var (id, state) in achievements.ToList())
        {
            if (Achievements.Of(id) is not { Reset: 2 }) continue;
            if (ContentResetSchedule.AchievementCycleStartMs(2, now) <= state.UpdatedMs) continue;
            achievements[id] = new AchievementState { UpdatedMs = now };
            achievementDirty.Add(id);
            touched.Add(id);
        }
        return touched.Count == 0 ? [] : [AchievementUpdate(touched)];
    }

    internal void PruneExpiredDungeonCooltimes(long now)
    {
        foreach (var id in dungeonCoolUntilMs.Keys.Where(k => dungeonCoolUntilMs[k] <= now).ToList())
            if (dungeonCoolUntilMs.Remove(id)) dungeonCooltimesDirty = true;
    }

    static byte[] RewardLimitResetPacket(IReadOnlyList<int> types)
    {
        var w = PacketWriter.Opcode(RewardLimitResetNt).Varint((uint)types.Count);
        foreach (var t in types) w.I32(t);
        return w.ToArray();
    }

    byte[] DungeonCoolTimesList(long now)
    {
        PruneExpiredDungeonCooltimes(now);
        var w = PacketWriter.Opcode(DungeonCoolTimesNt).Varint((uint)dungeonCoolUntilMs.Count);
        foreach (var (dungeonId, until) in dungeonCoolUntilMs) w.I32(dungeonId).I64(until);
        return w.ToArray();
    }

    List<byte[]> ContentResetWorldTick(long now)
    {
        if (options.World is not { } world) return [];
        if (!world.Resets.NeedsTick(now)) return [];
        var plan = world.Resets.Plan(now);
        world.Resets.ScheduleNext(now);
        if (!plan.DailyCrossed && !plan.WeeklyCrossed) return [];
        world.Resets.MarkApplied(plan);
        foreach (var player in world.Online.Values)
            player.Send(player.Session.ApplyLazyPeriodicResets(now, notifyClient: true));
        return [];
    }

    async Task EnsureContentResetsLoadedAsync(CancellationToken ct)
    {
        if (options.World is not { } world || Store is not { } store) return;
        if (!world.ResetsLoaded)
        {
            var cursor = await store.ResetCursorsAsync(ct);
            world.Resets.Load(cursor);
            world.ResetsLoaded = true;
        }
    }

    async Task FlushContentResetWorkAsync(CancellationToken ct)
    {
        if (options.World is not { } world || Store is not { } store) return;
        if (world.Resets.PendingDailyWorldWork)
        {
            var day = ContentResetSchedule.GameDayUtc(DateTimeOffset.FromUnixTimeMilliseconds(world.Resets.Snapshot().DailyBoundaryMs));
            await store.ApplyDailyLegionResetsAsync(day, ct);
            world.Resets.ClearPendingWorldWork(daily: true, weekly: false);
        }
        if (world.Resets.PendingWeeklyWorldWork)
        {
            await store.ApplyWeeklyLegionResetsAsync(world.Resets.Snapshot().WeeklyBoundaryMs, ct);
            world.Resets.ClearPendingWorldWork(daily: false, weekly: true);
        }
        if (world.Resets.Dirty)
        {
            var snap = world.Resets.Snapshot();
            world.Resets.ClearDirty();
            await store.SaveResetCursorsAsync(snap, ct);
        }
    }
}
