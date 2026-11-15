using Aion.Protocol.Native;

namespace Aion.World;

// Expeditions (Dungeon.dat type Party) beyond the scripts: one DungeonCopy per copy (shared by the party through the
// MapInstance), its host (the session running the scripts; handed over when that player leaves), the time limit
// (ProcessStateHoldingTime), the entry rules (MapEntrance.dat: level, equipment item level, member cap), the reward
// cube (RewardOdEnergyCube.dat: the final boss's RewardCommon at the cube object, mid-boss rewards on their kill) and
// the dungeon state messages. Opcodes: the 0x54ca run (index + 0x54ca, anchored by ItemUse 0x5612, TitleList 0x5679,
// OpenSubzoneGroupIDs 0x56ac and AddSubzoneGroupID 0x56ad in the official recording): OdEnergyCubeRewardStart_NT 0x5713
// (OBSERVED, AVION2: the mid bosses whose shares are in the cube), OdEnergyCubeReward_RQ/RS 0x5714/0x5715 (RS OBSERVED,
// RQ between them CANDIDATE). The dungeon state family is OBSERVED at 0x6100-0x610b (catalogue 573-584 at
// 0x5ec3 + index): DungeonState_NT 0x6100, DungeonCompleteResult_NT 0x6101, DungeonClearKillNpc_NT 0x6108, DungeonDeathLimit_NT 0x6109.
// Their bodies are OBSERVED (ours and the community fork's recordings; 0x6100's length is open, capture #75 item 1). PartyDungeonClear_NT 0x9333 (CANDIDATE) and middle-boss 0x3829 on
// clear/kill; the entry tickets and the cube's energy cost in GameSession.ContentTickets.cs; death
// count on 0x6109 after each fall. Frenzy on pin: MapEvent SetMapEnvState (boss phases), not the Oct 21 Hard timer.
// Not modelled: cube ceiling, DungeonCoolTimes (0 in dat), formal DeathLimit rows (0 in dat).

/// <summary>The shared state of one dungeon copy.</summary>
public sealed class DungeonCopy(GameSession host, int dungeonId, long now)
{
    public GameSession Host { get; set; } = host;
    public int DungeonId { get; } = dungeonId;
    public long StartedMs { get; } = now;
    /// <summary>When the run fails for time (0: no limit).</summary>
    public long DeadlineMs { get; set; }
    public bool Cleared { get; set; }
    public bool Failed { get; set; }
    /// <summary>"Prepare" while a field event's ready time runs (its scripts wait for DungeonState Process), else "Process".</summary>
    public string Phase { get; set; } = "Process";
    public long ProcessAtMs { get; set; }
    /// <summary>Reward-cube bosses killed in this copy (NpcData ids).</summary>
    public HashSet<int> CubeBossesKilled { get; } = [];
    /// <summary>Player deaths in this copy (the death-limit achievements; 0x6109 after each death and at entry).</summary>
    public int Deaths { get; set; }
    /// <summary>When the clear happened and when its Complete state (Dungeon.dat CompleteStateHoldingTime) ends.</summary>
    public long ClearedMs { get; set; }
    public long CompleteEndsMs { get; set; }
    /// <summary>The End state (4) that follows Complete was sent.</summary>
    public bool EndSent { get; set; }
    /// <summary>Revives each character used in this run (character id, or player key for a development login), for the
    /// Dungeon.dat RebirthLimitCount.</summary>
    public Dictionary<string, int> Revives { get; } = [];
    /// <summary>The bosses' Frenzy clocks (the new Hard modes: 4 minutes).</summary>
    public BossFrenzy Frenzy { get; } = new();
    /// <summary>The final boss's cube reward each player took (player key → items), once per run.</summary>
    public Dictionary<int, List<(int ItemId, long Count)>> CubeTaken { get; } = [];
    /// <summary>A daily dungeon's points so far (DungeonPoint.dat; GameSession.DailyDungeons.cs).</summary>
    public int Points { get; set; }
    /// <summary>A daily dungeon's countdown: when it began and when it ends (0: none yet).</summary>
    public long WaveStartedMs { get; set; }
    public long WaveEndsMs { get; set; }
    /// <summary>A daily dungeon's buff choice: the offer (0x6105 {slot, buff}) and the buff picked (0: not yet).</summary>
    public List<(int Slot, int BuffId)> OfferedBuffs { get; set; } = [];
    public int ChosenBuff { get; set; }
}

public sealed partial class GameSession
{
    // OBSERVED (HQ001 layer, capture 20261013 #9687-9689, #14161-14167): catalogue 573-584 live at 0x5ec3 + index, not in the 0x54ca run.
    public const ushort DungeonStateNt = 0x6100, DungeonCompleteResultNt = 0x6101, DungeonClearKillNpcNt = 0x6108, DungeonDeathLimitNt = 0x6109, CubeRewardStartNt = 0x5713, CubeRewardRq = 0x5714, CubeRewardRs = 0x5715;
    public const byte DungeonStateProcess = 2, DungeonStateComplete = 3, DungeonStateEnd = 4;
    /// <summary>After the time limit the copy empties this long after the fail message (OURS).</summary>
    public const long DungeonFailExitMs = 5_000;
    /// <summary>How long the End state (4) runs after the Complete state's hold (OBSERVED by the community fork, 2026-11-06
    /// 234912 #45201 for sealed 310027 and the layer's #16209: 600 s).</summary>
    public const long DungeonEndStateMs = 600_000;

    DungeonCopy? copy;
    readonly List<List<(int ItemId, long Count)>> pendingRewards = [];

    public DungeonCopy? Copy => copy;
    Random Rng => rng;

    /// <summary>At a map load: the copy's shared state (the first one in hosts it; a solo copy keeps its own across reloads).</summary>
    void JoinCopy(MapInstance? sharedMap, int mapId, long now)
    {
        if (instanceKey == 0 || MapEventData.DungeonOfMap(mapId) is not { } room) { copy = null; return; }
        var id = MapEventData.DungeonIdOfMap(mapId);
        DungeonCopy Fresh()
        {
            var limit = room.Limit > 0 ? room.Limit : room.Progress;
            var fresh = new DungeonCopy(this, id, now) { DeadlineMs = limit > 0 && Timed(room) ? now + (room.Ready + limit) * 1000L : 0 };
            if (room.Ready > 0) { fresh.Phase = "Prepare"; fresh.ProcessAtMs = now + room.Ready * 1000L; }
            // A daily dungeon with a buff choice starts empty in Prepare (state 1) and waits for the pick; the monsters and the
            // countdown come 5 s after its reply (OBSERVED official 2026-11-15, three entries of 900001: 0x6105 at the entry,
            // the player chose 34 s, 15 min and 14 s later, Process exactly 5.0 s after each 0x6107). GameSession.DailyDungeons.cs.
            else if (DailyDungeon.Of(id) is { Seconds: > 0, HasBuffChoice: true } daily)
            {
                fresh.Phase = "Prepare";
                fresh.ProcessAtMs = long.MaxValue;
                fresh.OfferedBuffs = daily.DrawBuffs(rng);
            }
            // A daily without a choice has a fixed Prepare (OBSERVED official 2026-11-15, 900002: state 1 {entry, entry + 12 s},
            // its map event starts on DungeonState Prepare, Process when the 12 s are up).
            else if (DailyDungeon.Of(id) is { Seconds: > 0, Ready: > 0 } ready)
            {
                fresh.Phase = "Prepare";
                fresh.ProcessAtMs = now + ready.Ready * 1000L;
            }
            return fresh;
        }
        if (sharedMap is not null) copy = sharedMap.Dungeon ??= Fresh();
        // "Challenge again" after a daily's result is a fresh run (OBSERVED 2026-11-15: a new entry burst with a new 0x6105).
        else if (copy is null || copy.DungeonId != id || copy.Cleared && DailyDungeon.Of(id) is not null) copy = Fresh();
    }

    /// <summary>The dungeon types with a run timer and the dungeon state messages (Dungeon.dat ProcessStateHoldingTime).</summary>
    static bool Timed(DungeonInfo room) => room.Type is "Party" or "Seal" or "Awaken" or "Daily" or "BossChallenge" or "Raid" or "FieldEvent";
    public const byte DungeonStatePrepare = 1;

    string IMapEventHost.DungeonStateName => copy?.Phase ?? "Process";

    /// <summary>The host's tick: a field event's ready time ends, the event phase begins and its scripts start.</summary>
    List<byte[]> DungeonPhaseTick(long now)
    {
        if (copy is not { Phase: "Prepare" } c || c.Host != this || now < c.ProcessAtMs) return [];
        c.Phase = "Process";
        var replies = DailyProcessStart(c, now) ?? [DungeonState(c.DungeonId, DungeonStateProcess, now, c.DeadlineMs)];
        replies.AddRange(StartEligibleEvents(now));
        return replies;
    }

    /// <summary>
    /// MapEntrance.dat: the character's level and equipment item level, and the quest step the entry needs (one of
    /// QuestStepIdList active, or that quest done); staff and development logins (no stored character) pass.
    /// </summary>
    bool MeetsEntry(DungeonInfo room, long now, out string why)
    {
        why = "";
        if (Store is null) return true;
        if (Timed(room))
        {
            // A daily's level is Dungeon.dat RecommendLevel: our MapEntrance.dat export (2026-10-18) gates 900002 and 900003
            // at 99-99 from before they opened, and a level-45 character entered 900002 officially (2026-11-15).
            var (min, max) = room.Type == "Daily" && DailyDungeon.Of(MapEventData.DungeonIdOfMap(room.Map)) is { MinLevel: > 0 } d
                ? (d.MinLevel, 0) : (room.MinLevel, room.MaxLevel);
            if (min > 0 && level < min) { why = $"level {level} < {min}"; return false; }
            if (max > 0 && level > max) { why = $"level {level} > {max}"; return false; }
            var gate = Patch20261028.EntryItemLevel(room);
            if (gate > 0 && EquipItemLevel() < gate) { why = $"item level {EquipItemLevel()} < {gate}"; return false; }
        }
        if (!ContentTicketsAllowEntry(room, now, out why)) return false;
        if (room.QuestGate is { Length: > 0 } gates)
        {
            if (questLog is null && room.Type == "InstanceLayer") { why = "quest gate (no journal)"; return false; }
            if (questLog is null) return true;
            if (!questLog.InstanceLayerGateOpen(room) && !LayerUseEnvObjEntryOpen(room.Map))
            {
                why = $"quest step {string.Join('/', gates.Select(g => g[0].GetString()))} not open";
                return false;
            }
        }
        return true;
    }

    /// <summary>An active UseEnvObj step targets an object placed on the instance layer map (supplies after the script).</summary>
    bool LayerUseEnvObjEntryOpen(int layerMap)
    {
        if (questLog is null) return false;
        var envs = WorldObjects.ObjectsOn(layerMap).Select(o => o.Env).ToHashSet();
        return questLog.OpenUseEnvObjIds().Any(envs.Contains);
    }

    /// <summary>The copy a party member would join is full (MapEntrance MaxMember).</summary>
    static bool CopyFull(MapInstance inside, DungeonInfo room) => room.MaxMember > 0 && inside.Players.Count >= room.MaxMember;

    /// <summary>
    /// DungeonState_NT 0x6100 {dungeon i32, EDungeonState u8, start i64, end i64}, then a trailing u8 0 while
    /// <see cref="DungeonStateTrailingByte"/> holds. The fields are OBSERVED (ours: `60000000 02 [now] 0 00` at the layer entry;
    /// the community fork: byte-exact 2026-11-06 234912 #44953). The length is not settled (capture #75 item 1): ours read a
    /// trailing 00 (24 bytes with the opcode), the fork's recordings show 23 bytes in all 33 + 4 instances.
    /// </summary>
    internal static byte[] DungeonState(int dungeonId, byte state, long startMs, long endMs)
    {
        var w = PacketWriter.Opcode(DungeonStateNt).I32(dungeonId).U8(state).I64(startMs).I64(endMs);
        if (DungeonStateTrailingByte) w.U8(0);
        return w.ToArray();
    }

    /// <summary>CANDIDATE: 0x6100 ends in a u8 0 (our reading; the fork's recordings say no). Flip it once capture #75 item 1 answers.</summary>
    internal const bool DungeonStateTrailingByte = true;

    /// <summary>
    /// DungeonCompleteResult_NT 0x6101 {u8 0, dungeon i32, succeeded u8, rewards varint count + [item i32, count i64]}: OBSERVED
    /// `00 60000000 01 00` without rewards (ours, HQ001), and with them by the community fork (2026-11-06 234912 #44952, sealed
    /// 310027: its RewardLight ordered by item id).
    /// </summary>
    internal static byte[] DungeonCompleteResult(int dungeonId, bool succeeded, IReadOnlyList<(int ItemId, long Count)>? rewards = null)
    {
        var w = PacketWriter.Opcode(DungeonCompleteResultNt).U8(0).I32(dungeonId).U8((byte)(succeeded ? 1 : 0)).Varint((uint)(rewards?.Count ?? 0));
        foreach (var (item, count) in (rewards ?? []).OrderBy(r => r.ItemId)) w.I32(item).I64(count);
        return w.ToArray();
    }

    /// <summary>The clear reward this character gets (RewardDungeon.dat RewardLight / RewardDark by race).</summary>
    List<(int ItemId, long Count)>? ClearReward(DungeonInfo room) =>
        (profileRace == 2 ? room.RewardDark : room.RewardLight) is { Length: > 0 } reward
            ? reward.Where(r => r.Length >= 2).Select(r => ((int)r[0], r[1])).ToList() : null;

    /// <summary>
    /// After the running state at entry (OBSERVED): DungeonDeathLimit_NT 0x6109 with the copy's deaths (`00000000` at a first
    /// entry; `05000000` at a re-entry after a wipe, community fork 2026-11-06 090856 #104160), DungeonClearKillNpc_NT 0x6108 `00`.
    /// </summary>
    internal static IEnumerable<byte[]> DungeonLimitsAtEntry(int deaths = 0) =>
        [PacketWriter.Opcode(DungeonDeathLimitNt).I32(deaths).ToArray(), PacketWriter.Opcode(DungeonClearKillNpcNt).U8(0).ToArray()];

    /// <summary>The cleared copy's current state: Complete (3) for the hold, then End (4) for 600 s.</summary>
    static byte[] DungeonClearedState(DungeonCopy c) => c.EndSent
        ? DungeonState(c.DungeonId, DungeonStateEnd, c.CompleteEndsMs, c.CompleteEndsMs + DungeonEndStateMs)
        : DungeonState(c.DungeonId, DungeonStateComplete, c.ClearedMs, c.CompleteEndsMs);

    /// <summary>At the world entry of an expedition: its state (running until the deadline, or complete until the hold ends).</summary>
    List<byte[]> DungeonStateAtEntry(long now)
    {
        if (copy is not { } c || MapEventData.DungeonOfMap(worldMap) is not { } room) return [];
        // An instance layer (the story): running since the entry, no deadline (OBSERVED at the HQ001 entry).
        if (!Timed(room)) return c.Cleared ? [] : [DungeonState(c.DungeonId, DungeonStateProcess, c.StartedMs, 0), .. DungeonLimitsAtEntry(c.Deaths)];
        if (c.Failed) return [];
        if (c.Phase == "Prepare" && DailyPrepareAtEntry(c) is { } prepare) return prepare;   // GameSession.DailyDungeons.cs
        if (c.Phase == "Prepare")
            return [DungeonState(c.DungeonId, DungeonStatePrepare, c.StartedMs, c.ProcessAtMs)];
        if (DailyStateAtEntry(c, now) is { } daily) return daily;   // GameSession.DailyDungeons.cs
        // After the clear: the state it is in (OBSERVED by the community fork, 234912 #44953, #45201).
        if (c.Cleared) return [DungeonClearedState(c)];
        return [DungeonState(c.DungeonId, DungeonStateProcess, c.StartedMs, c.DeadlineMs), .. DungeonLimitsAtEntry(c.Deaths)];
    }

    /// <summary>The host's tick: the Complete state's hold ended, End (4) runs 600 s from there, for everyone in the copy.</summary>
    void DungeonEndTick(long now)
    {
        if (copy is not { Cleared: true, EndSent: false } c || c.Host != this || now < c.CompleteEndsMs) return;
        c.EndSent = true;
        var end = DungeonClearedState(c);
        foreach (var session in DungeonCopySessions())
            if (session == this) mapEventOut.Add(end);
            else session.worldPlayer?.Send([end]);
    }

    /// <summary>The host's tick: past the deadline the run fails and the copy empties.</summary>
    void DungeonTimeLimit(long now)
    {
        if (copy is not { } c || c.Host != this || c.DeadlineMs == 0 || c.Cleared || c.Failed || now < c.DeadlineMs) return;
        c.Failed = true;
        Note($"dungeon {c.DungeonId}: the time limit passed");
        mapEventOut.Add(DungeonState(c.DungeonId, DungeonStateEnd, c.StartedMs, now));
        mapEventOut.Add(DungeonCompleteResult(c.DungeonId, false));
        foreach (var session in CopyMembers().Prepend(this)) session.instanceExitAt ??= now + DungeonFailExitMs;
    }

    /// <summary>
    /// The host leaves its copy with others still inside: the scripts and their state move to one of them, who hosts
    /// from now on (its own tick drives them). Nothing to do when this player does not host.
    /// </summary>
    void HandOverCopy()
    {
        if (copy is not { } c || c.Host != this) return;
        var next = CopyMembers().FirstOrDefault(s => !s.reloading) ?? CopyMembers().FirstOrDefault();
        if (next is null) return;
        foreach (var run in mapEventRuns) run.Host = next;
        next.mapEventRuns.AddRange(mapEventRuns);
        next.dormantSpawners.UnionWith(dormantSpawners);
        next.startedSpawners.UnionWith(startedSpawners);
        next.removedObjects.UnionWith(removedObjects);
        next.pendingSpawns.AddRange(pendingSpawns);
        foreach (var (target, buffs) in npcBuffs) next.npcBuffs[target] = buffs;
        next.pendingExit ??= pendingExit;
        next.mapEventsMap = worldMap;
        next.mapEventNow = Math.Max(next.mapEventNow, mapEventNow);
        next.mapEventOut.AddRange(mapEventOut);
        mapEventRuns.Clear();
        dormantSpawners.Clear();
        startedSpawners.Clear();
        removedObjects.Clear();
        pendingSpawns.Clear();
        npcBuffs.Clear();
        mapEventOut.Clear();
        pendingExit = null;
        c.Host = next;
    }

    // ---------- The reward cube ----------

    /// <summary>A kill in the host's copy: the cube's final boss arms the cube (OdEnergyCubeRewardStart_NT), a mid boss pays its table to everyone now.</summary>
    void CubeKilled(Target target, long now)
    {
        if (copy is not { } c || c.Host != this || MapEventData.DungeonOfMap(worldMap) is not { Cube: { } cube } room || !c.CubeBossesKilled.Add(target.NpcId)) return;
        if (!cube.TryGetValue(target.NpcId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var table)) return;
        if (target.NpcId == room.CubeBoss)
        {
            // 0x5713 lists the mid bosses whose shares are in the cube (OBSERVED: the codes of the clear's 0x572e notices).
            var mids = c.CubeBossesKilled.Where(id => id != room.CubeBoss).ToList();
            var start = PacketWriter.Opcode(CubeRewardStartNt).Varint(mids.Count);
            foreach (var id in mids) start.I32(id);
            mapEventOut.Add(start.ToArray());
            return;
        }
        mapEventOut.Add(PartyDungeonMiddleBossKill(target.NpcId));
        foreach (var session in CopyMembers().Prepend(this))
            session.pendingRewards.Add(RollRewards(table, session.Rng));
    }

    /// <summary>Each table entry is an independent chance of 1e8 with a count range (as LootTables).</summary>
    static List<(int ItemId, long Count)> RollRewards(long[][] table, Random rng)
    {
        var items = new List<(int, long)>();
        foreach (var entry in table)
        {
            if (entry.Length < 4 || rng.NextInt64(100_000_000) >= entry[1]) continue;
            items.Add(((int)entry[0], entry[2] + rng.NextInt64(entry[3] - entry[2] + 1)));
        }
        return items;
    }

    Task<List<byte[]>> GrantPendingRewardsAsync(long now, CancellationToken ct) => GrantRewardsAsync(pendingRewards, IncreaseDropItemGet, now, ct);

    /// <summary>The daily dungeons' point rewards, with EIncreaseReason kDungeonReward (OBSERVED 58 on the official reward).</summary>
    Task<List<byte[]>> GrantDungeonRewardsAsync(long now, CancellationToken ct) => GrantRewardsAsync(pendingDungeonRewards, IncreaseDungeonReward, now, ct);

    async Task<List<byte[]>> GrantRewardsAsync(List<List<(int ItemId, long Count)>> pending, byte reason, long now, CancellationToken ct)
    {
        var batches = pending.ToList();
        pending.Clear();
        var items = batches.SelectMany(b => b).ToList();
        if (items.Count == 0) return [];
        if (Store is { } store && CharacterId is { } character)
        {
            var granted = await Stored(async () => await store.GrantLootAsync(character, items, ct) ? [[]] : []);
            if (granted.Count == 0) return [];
            var replies = await InventoryChangesAsync(ct, reason, 0);
            replies.AddRange(await AutoUseAcquiredItemsAsync(items.Select(i => i.Item1), now, ct));
            return replies;
        }
        return [];
    }

    /// <summary>
    /// The final boss's cube reward: on the cube object's use, or on OdEnergyCubeReward_RQ {reward count u8}; once per
    /// player per run (RewardOdEnergyCube ConsumeMaxCount 1; its ConsumeTicketCount of ticket 60000001 is taken first, result 1
    /// when not enough is left: GameSession.ContentTickets.cs). The
    /// request gets OdEnergyCubeReward_RS {result, count, [{item, count}]} with the items (the same ones again when the
    /// object use already paid them); result 1 when the boss is still alive or there is no cube here.
    /// </summary>
    async Task<List<byte[]>> CubeRewardAsync(byte[]? plain, long now, CancellationToken ct)
    {
        var c = copy;
        var room = MapEventData.DungeonOfMap(worldMap);
        var table = room?.Cube is { } cube && room.CubeBoss != 0 && c is not null && c.CubeBossesKilled.Contains(room.CubeBoss)
            ? cube.GetValueOrDefault(room.CubeBoss.ToString(System.Globalization.CultureInfo.InvariantCulture)) : null;
        if (table is null || c is null)
            return plain is null ? [] : [PacketWriter.Opcode(CubeRewardRs).U16(1).U8(0).Varint(0).ToArray()];
        if (!c.CubeTaken.TryGetValue(playerKey, out var items))
        {
            if (!PayCubeTicket(c.DungeonId, now))
                return plain is null ? [] : [PacketWriter.Opcode(CubeRewardRs).U16(1).U8(0).Varint(0).ToArray()];
            items = RollRewards(table, rng);
            c.CubeTaken[playerKey] = items;
            pendingRewards.Add(items);
        }
        // The energy first (OBSERVED in the community fork's recordings: 0x610c before the items).
        var replies = ContentTicketUpdates();
        replies.AddRange(await GrantPendingRewardsAsync(now, ct));
        if (plain is null) return replies;
        // _reward_count echoes the request's (OBSERVED 2 in all 9 official replies, 2026-11-02..05; 1 when the object was used).
        var rewardCount = plain is { Length: >= 3 } ? plain[2] : (byte)1;
        var writer = PacketWriter.Opcode(CubeRewardRs).U16(0).U8(rewardCount).Varint((uint)items.Count);
        foreach (var (item, count) in items) writer.I32(item).I32((int)Math.Min(count, int.MaxValue));
        return [.. replies, writer.ToArray()];
    }
}
