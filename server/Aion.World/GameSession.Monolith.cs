using Aion.Protocol.Native;

namespace Aion.World;

// Monoliths: the Empyrean Traces (GameSession.EmpyreanTraces.cs) give the region's token item ("Empyrean Trace: Altgard" =
// Monolith_token_D_s_01a_b ...), which is resonated with the region's Monolith. Ported from zaraza87/A2Core
// (GameSession.EmpyreanTraces.cs, #72) onto our live opcodes (#74).
//
// A Monolith's resonance count is the tokens put in so far, one per token (OBSERVED 2026-11-12: six tokens took Monolith 1001
// from 264 to 270). MonolithLevel.dat NeedTokenCount is the cumulative count of each level (Altgard / Verteron 30 levels up to
// 560, Eltnen / Morheim 8 up to 45, Reshanta 20 up to 300) and each level's RewardMonolith row gives permanent stats and items
// (tools/data/monoliths.json). The count is stored per character (migration 111 game.character_monoliths).
//
// OBSERVED (ours): MonolithList_NT 0xe30b at entry with every Monolith of the race, also those never resonated (a Light
// character: 1001, 1002, 3001); MonolithResonate_RS 0xe30e with u16 result and an empty reward list when no level was passed.
// CANDIDATE (fork): the request {monolith i32, item count i32, object key i32}, the result codes, the rewards of every level
// passed listed in the reply, the stats. The quest steps "Resonate with the Monolith" (HQ1102125 step 4, HQ2102105 step 5)
// follow from the bag: level 2 gives the Amulet Enhance Scroll they collect. No proximity or InteractEnvObj fallback (#72).
public sealed partial class GameSession
{
    static MonolithTables MonolithData => MonolithTables.Embedded;

    /// <summary>Resonance count per Monolith (migration 111).</summary>
    readonly Dictionary<int, int> monolithCounts = [];
    Dictionary<string, int>? monolithStats;
    /// <summary>Monolith ids the client has been sent MonolithUnlock_NT for.</summary>
    readonly HashSet<int> monolithUnlocked = [];
    /// <summary>Monolith env-object indexes spawned before LoadPost tab 2; flushed with 0xe30c/0xe341 after.</summary>
    readonly HashSet<int> pendingMonolithNoticeIndexes = [];
    /// <summary>After MonolithList_NT at entry the client accepts Monolith.dat ids in Info/Unlock (LoadPost tab 2 still re-flushes).</summary>
    bool monolithNoticesEnabled;

    /// <summary>EResult (usmap; CANDIDATE values from zaraza87/A2Core EResult.cs).</summary>
    const int MonolithResonateFailNotEnoughCountItem = 15954, MonolithResonateFailMaxLevel = 15955, MonolithResonateFailInvalidItemCount = 15956,
        MonolithFailNotUnlock = 15960;
    /// <summary>EIncreaseReason of the resonance rewards (CANDIDATE: the objects' reward reason 49); the tokens go with the item-use decrease.</summary>
    const byte IncreaseMonolithReward = GamePackets.IncreaseEnvObjReward;

    public int MonolithCount(int id) => monolithCounts.GetValueOrDefault(id);

    void LoadMonoliths(IReadOnlyDictionary<int, int> stored)
    {
        monolithCounts.Clear();
        foreach (var (id, count) in stored)
            if (MonolithData.Of(id) is not null && count > 0) monolithCounts[id] = count;
        monolithStats = null;
    }

    void ResetMonolithClientState()
    {
        monolithNoticesEnabled = false;
        pendingMonolithNoticeIndexes.Clear();
    }

    /// <summary>Call after MonolithList_NT on world entry so the first 0x3634 can carry 0xe30c/0xe341 (OBSERVED: before list, wrong env ids).</summary>
    void EnableMonolithNoticesAfterList() => monolithNoticesEnabled = true;

    int MonolithRace => profileRace == 0 ? 1 : profileRace;

    /// <summary>Every Monolith of the race with its count (OBSERVED: 1001, 1002, 3001 for a Light character).</summary>
    List<(int MonolithId, int ResonateCount)> MonolithListEntries() =>
        MonolithData.OfRace(MonolithRace).Select(m => (m.Id, MonolithCount(m.Id))).ToList();

    byte[] MonolithListAtEntry() => GamePackets.MonolithList(MonolithListEntries());

    /// <summary>The Monolith a placed Monolith object resonates with for this character (Reshanta: its race's).</summary>
    MonolithTables.Monolith? MonolithOfObject(WorldObject o) => o.Monolith ? MonolithData.OfObject(o.Env, MonolithRace) : null;

    IEnumerable<byte[]> MonolithEnvObjNotices(int index, WorldObject o, bool resendUnlock = false)
    {
        if (!o.Monolith) yield break;
        if (!monolithNoticesEnabled)
        {
            pendingMonolithNoticeIndexes.Add(index);
            yield break;
        }
        foreach (var packet in MonolithEnvObjWireNotices(index, o, resendUnlock)) yield return packet;
    }

    IEnumerable<byte[]> MonolithEnvObjWireNotices(int index, WorldObject o, bool resendUnlock)
    {
        if (MonolithOfObject(o) is not { } monolith) yield break;
        var key = WorldObjects.FirstKey + index;
        var count = MonolithCount(monolith.Id);
        yield return GamePackets.MonolithInfo(monolith.Id, count, key);
        if (resendUnlock || monolithUnlocked.Add(monolith.Id))
            yield return GamePackets.MonolithUnlock(monolith.Id, count);
    }

    List<byte[]> FlushMonolithNoticesAfterLoadPost()
    {
        monolithNoticesEnabled = true;
        var replies = new List<byte[]>();
        var objects = WorldObjects.ObjectsOn(worldMap);
        var indexes = new HashSet<int>(pendingMonolithNoticeIndexes);
        pendingMonolithNoticeIndexes.Clear();
        foreach (var index in shownObjects) indexes.Add(index);
        foreach (var index in indexes.OrderBy(i => i))
        {
            if (index < 0 || index >= objects.Count || !objects[index].Monolith) continue;
            replies.AddRange(MonolithEnvObjWireNotices(index, objects[index], resendUnlock: true));
        }
        return replies;
    }

    /// <summary>
    /// 0xe30d MonolithResonate_RQ {monolith i32, item count i32, object key i32}: puts that many of the Monolith's tokens in (at most
    /// what the last level needs), stores the count, grants the items of every level passed and refreshes the stats. The reply
    /// first, then the bag changes (OBSERVED order 2026-11-12).
    /// </summary>
    async Task<List<byte[]>> MonolithResonateRequestAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 10) return [];
        var id = BitConverter.ToInt32(plain, 2);
        var asked = BitConverter.ToInt32(plain, 6);
        var monolith = MonolithData.Of(id);
        var previous = MonolithCount(id);
        List<byte[]> Fail(int result) => [GamePackets.MonolithResonateReply(result, id, monolith?.Token ?? 0, 0, previous, previous, [])];
        if (monolith is null || monolith.Race != MonolithRace) return Fail(MonolithFailNotUnlock);
        if (asked <= 0) return Fail(MonolithResonateFailInvalidItemCount);
        if (previous >= monolith.MaxCount) return Fail(MonolithResonateFailMaxLevel);
        var used = Math.Min(asked, monolith.MaxCount - previous);
        if (Store is not { } store || CharacterId is not { } character || CostStacks(monolith.Token, used) is not { } costs)
            return Fail(MonolithResonateFailNotEnoughCountItem);
        var count = previous + used;
        var passed = monolith.Levels.Where(l => l.Need > previous && l.Need <= count).ToList();
        var rewards = passed.SelectMany(l => l.Items).GroupBy(i => i.Item).Select(g => (Item: g.Key, Count: g.Sum(i => i.Count))).ToList();
        var saved = await Stored(async () => await store.ResonateMonolithAsync(character, costs, id, previous, count,
            rewards.Select(r => (r.Item, (long)r.Count)).ToList(), ct) ? [[]] : []);
        if (saved.Count == 0) return Fail(MonolithResonateFailNotEnoughCountItem);
        monolithCounts[id] = count;
        monolithStats = null;
        var replies = new List<byte[]> { GamePackets.MonolithResonateReply(0, id, monolith.Token, used, previous, count, rewards) };
        replies.AddRange(await InventoryChangesAsync(ct, IncreaseMonolithReward, DecreaseItemUse));
        if (passed.Any(l => l.Stats.Count > 0)) replies.AddRange(RefreshStats());
        Note($"Monolith {monolith.Name}: resonated {used} ({previous} -> {count}, level {monolith.LevelAt(previous)} -> {monolith.LevelAt(count)})");
        return replies;
    }

    Task<List<byte[]>> SyncMonolithAccountAchievementsAsync(byte[] plain) =>
        Task.FromResult<List<byte[]>>([GamePackets.SyncMonolithAccountAchievementsReply()]);

    /// <summary>The stats of the Monolith levels reached (RewardMonolith Stat rewards), summed.</summary>
    IReadOnlyDictionary<string, int> MonolithStats()
    {
        if (monolithStats is { } cached) return cached;
        var sum = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, count) in monolithCounts)
            foreach (var level in MonolithData.Of(id)?.Levels.Where(l => l.Need <= count) ?? [])
                foreach (var (stat, value) in level.Stats) sum[stat] = sum.GetValueOrDefault(stat) + value;
        return monolithStats = sum;
    }

    /// <summary>
    /// A Monolith stat (PvP stats and SPMax reach the sheet; the Empyrean Lord stats Life, Destiny, Time ... have no place in our
    /// stat sheet yet).
    /// </summary>
    int MonolithStat(string stat) => monolithCounts.Count == 0 ? 0 : MonolithStats().GetValueOrDefault(stat);

    /// <summary>Checks only: a Monolith's count as stored.</summary>
    internal void SetMonolithForTests(int id, int count) { monolithCounts[id] = count; monolithStats = null; }
    internal int MonolithStatForTests(string stat) => MonolithStat(stat);
    internal byte[] MonolithListForTests() => MonolithListAtEntry();
}
