using Aion.Protocol.Native;

namespace Aion.World;

// Gathering. Gather nodes are EnvObjs (EnvObjData Usage GatherSource) placed by MapData, spawned with the quest objects
// (0x3634, tools/world_objects.py "gather": material, count, bonus item, bonus count, mastery, gauge, attempts). The client starts one
// with InteractEnvObj_RQ 0x3636 (the timed interaction already served) or GatherSourceStart_RQ 0x8d30 (0x8d run,
// catalogue index + 0x89d1; which one is not observed). On completion: the material (and, OURS, the bonus item one
// time in five) and GatherSourceCompleted_NT 0x8d33. Every gather, won or lost, spends one of the node's uses (EnvObjData
// InteractCount, usually 3; OBSERVED in game 2026-11-14: a Farming Odyle node gives three gathers, with a bar that runs
// down); the last one uses the node up for this player for GatherRespawnMs (OURS).
// Gather mastery (migration 029): a node needs its GatherSource MasteryLevel; each gather pays an eighth of the level's
// GatherMastery need (OURS) as GatherExpUpdate_NT 0x8d35 {exp i64, gained i64}, a level-up GatherLevelUpdate_NT 0x8d36
// {level i32, exp i64, skill points i32, specialized points i32} (usmap fields, CANDIDATE). The gather skill tree
// (MyGatherSkillList_NT, points) is not modelled: the points stay 0.
public sealed partial class GameSession
{
    /// <summary>0x8d33 result (OBSERVED): 0 gathered, 1 gathered a better grade, 2 failed.</summary>
    const int GatherFailed = 2;

    // OBSERVED (83 official GatherSourceCompleted_NT): u8 result, then a constant 0x3a byte, then the varint target key
    // and five i32 reward fields. The meaning of 0x3a is unknown; mirror it.
    static PacketWriter GatherCompleted(int result, long key) => PacketWriter.Opcode(GatherCompletedNt).U8((byte)result).U8(0x3a).Varint(key);
    public const ushort GatherStartRq = 0x8d30, GatherStartRs = 0x8d31, GatherStopRq = 0x8d32, GatherCompletedNt = 0x8d33, GatherExpUpdateNt = 0x8d35,
        GatherLevelUpdateNt = 0x8d36;
    public const long GatherRespawnMs = 60_000;
    const byte IncreaseGatherSource = 41;   // EIncreaseReason kGatherSource
    /// <summary>Gather nodes used up by this player: object index -> when it is back.</summary>
    readonly Dictionary<int, long> gathered = [];
    public Func<int> GatherBonusRoll { get; set; } = () => Random.Shared.Next(5);
    /// <summary>The turn rolls of the gathering mini-game (0..9999).</summary>
    public Func<int> GatherRoll { get; set; } = () => Random.Shared.Next(10_000);
    int gatherLevel = 1;
    long gatherExp;
    /// <summary>Uses left on a node for this player (object index -> count), while some were used: every gather takes one.</summary>
    readonly Dictionary<int, int> gatherAttempts = [];

    /// <summary>
    /// A gather's discovery (GatherSourceStart_RS _is_discovered): the node yields its material's better grade. OBSERVED
    /// (2026-11-15, 15 official gathers, 4 discovered): 0x8d31 _is_discovered and _basic_discovery_result 1, _material_group_key
    /// 2 (1 otherwise), then 0x8d33 and 0x8d34 result 1 with Fine Odyle (610610014) for Odyle (610610013). The chance is
    /// CANDIDATE (a quarter, about what the sample shows); lost attempts never showed one.
    /// </summary>
    public Func<int> GatherDiscoveryRoll { get; set; } = () => Random.Shared.Next(10_000);
    public const int GatherDiscoveryChance = 2_500;
    const int GatherDiscovered = 1;

    /// <summary>The node (object index) whose won attempt was discovered and completes with the better grade.</summary>
    int? gatherDiscoveredIndex;

    sealed record GatherGradeFile([property: System.Text.Json.Serialization.JsonPropertyName("better")] Dictionary<string, int> Better);

    /// <summary>Common gather material -> its better grade (tools/data/gather-grades.json, tools/gather_grades.py).</summary>
    static readonly Lazy<IReadOnlyDictionary<int, int>> gatherBetterGrades = new(() =>
    {
        using var stream = typeof(GameSession).Assembly.GetManifestResourceStream("gather-grades.json");
        if (stream is null) return new Dictionary<int, int>();
        var file = System.Text.Json.JsonSerializer.Deserialize<GatherGradeFile>(stream);
        return (file?.Better ?? []).ToDictionary(kv => int.Parse(kv.Key, System.Globalization.CultureInfo.InvariantCulture), kv => kv.Value);
    });

    public static int? GatherBetterGrade(int material) => gatherBetterGrades.Value.TryGetValue(material, out var better) ? better : null;

    public int GatherAttemptsLeft(int index) => gatherAttempts.GetValueOrDefault(index, -1);

    /// <summary>
    /// How long the client plays an attempt's turns before the server may end it (OBSERVED, ~80 official gathers: the end
    /// packets came 1.7 s after the start for one turn, 12.7 s for ten; about 1.3 s a turn plus 0.4 s, for won and lost
    /// attempts alike). Ending sooner closed the mini-game the moment it opened.
    /// </summary>
    public static long GatherRaceMs(int turns) => 400 + 1300L * Math.Max(1, turns);

    /// <summary>A lost attempt whose end (0x8d33 result 2, the attempt spent) waits for the client's turns to play out.</summary>
    (int Index, int Key, long DueMs)? pendingGatherLoss;

    /// <summary>
    /// One turn of the mini-game: EGatherTurnResult and the turn's gain in gauge units before and after gather-skill effects,
    /// for the success gauge or the fail gauge (OBSERVED, two official 0x8d31 of 2026-11-12: a success turn carries only the
    /// success pair, a fail turn only the fail pair; not percent, not cumulative; equal pairs without skills).
    /// </summary>
    public readonly record struct GatherTurn(byte Result, int SuccessBefore, int SuccessAfter, int FailBefore, int FailAfter);

    const byte TurnEpicCritical = 1, TurnCritical = 2, TurnBasic = 3, TurnSmall = 4, TurnFailBasic = 5, TurnFailSmall = 6;
    /// <summary>The gauge both sides race to: a Beginner node's GatherGauge (360, OBSERVED: the lost attempt's fails sum to 360).</summary>
    const int GatherBasicGain = 360;

    /// <summary>
    /// A turn's gain in gauge units. OBSERVED (2026-11-12): small 50, basic 110, epic 200, fail basic 144, fail small 96.
    /// Critical (2) unrecorded: 160 (OURS, between basic and epic).
    /// </summary>
    static int TurnGain(byte result) => result switch
    {
        TurnEpicCritical => 200, TurnCritical => 160, TurnBasic => 110, TurnSmall => 50, TurnFailBasic => 144, _ => 96,
    };

    /// <summary>
    /// The turns of one gathering attempt (turn chances OURS; the official rules are unrecorded). Each turn succeeds with 65%
    /// plus 3 points per mastery level over the node (35..95%) and adds its gain to that side's gauge; the last turn's gain
    /// is capped at what the gauge still needs (OBSERVED: a final fail of 24). The attempt ends when either gauge is full.
    /// </summary>
    List<GatherTurn> GatherTurns(int target, int nodeMastery)
    {
        var turns = new List<GatherTurn>();
        var chance = Math.Clamp(6500 + 300 * (gatherLevel - nodeMastery), 3500, 9500);
        int success = 0, fail = 0;
        while (turns.Count < 30 && success < target && fail < target)
        {
            if (GatherRoll() < chance)
            {
                var kind = GatherRoll();
                var result = kind < 200 ? TurnEpicCritical : kind < 1200 ? TurnCritical : kind < 2700 ? TurnSmall : TurnBasic;
                var gain = Math.Min(TurnGain(result), target - success);
                success += gain;
                turns.Add(new GatherTurn(result, gain, gain, 0, 0));
            }
            else
            {
                var result = GatherRoll() < 3000 ? TurnFailSmall : TurnFailBasic;
                var gain = Math.Min(TurnGain(result), target - fail);
                fail += gain;
                turns.Add(new GatherTurn(result, 0, 0, gain, gain));
            }
        }
        return turns;
    }

    /// <summary>Whether the turns fill the success gauge.</summary>
    static bool GatherWon(IReadOnlyList<GatherTurn> turns, int target) => turns.Sum(t => t.SuccessAfter) >= target;

    public (int Level, long Exp) GatherMastery => (gatherLevel, gatherExp);

    /// <summary>0x8d36 GatherLevelUpdate_NT {level u16, exp i64, skill points u16, specialized points u16} (OBSERVED `368d 0200 0f00000000000000 0100 0000`).</summary>
    byte[] GatherLevelUpdate() => PacketWriter.Opcode(GatherLevelUpdateNt).U16(gatherLevel).I64(gatherExp).U16(0).U16(0).ToArray();

    /// <summary>0x8d34 after 0x8d33: player key, the 0x8d33 result, 0x3a, node key (OBSERVED `348d 884e 02 3a 9a8401`).</summary>
    byte[] GatherEnded(int result, long key) => PacketWriter.Opcode(0x8d34).Varint(playerKey).U8((byte)result).U8(0x3a).Varint(key).ToArray();

    /// <summary>A node's uses taken so far, for 0x363a: all of them once it is used up.</summary>
    int GatherUsesTaken(int index, long[] node)
    {
        var total = node.Length >= 7 && node[6] > 0 ? (int)node[6] : 3;
        return gatherAttempts.TryGetValue(index, out var left) ? total - left : total;
    }

    /// <summary>Mastery exp for a gather (OURS): the level's need over eight; the packets, with the level-up.</summary>
    async Task<List<byte[]>> GatherMasteryExpAsync(CancellationToken ct, bool lost = false)
    {
        var need = Crafts.GatherMasteryNeed(gatherLevel);
        if (need <= 0) return [];
        // A lost attempt pays too, about a third (OBSERVED 2026-11-12: 6 for a lost attempt, 20 for a won one).
        var gained = Math.Max(1, lost ? need / 24 : need / 8);
        gatherExp += gained;
        var replies = new List<byte[]> { PacketWriter.Opcode(GatherExpUpdateNt).I64(gatherExp).I64(gained).ToArray() };
        var leveled = false;
        while (need > 0 && gatherExp >= need && gatherLevel < CraftTables.MaxMastery)
        {
            gatherExp -= need;
            gatherLevel++;
            leveled = true;
            need = Crafts.GatherMasteryNeed(gatherLevel);
        }
        if (Store is { } store && CharacterId is { } character)
            await Stored(async () => { await store.SaveMasteryAsync(character, "gather", 0, gatherLevel, gatherExp, ct); return []; });
        if (leveled)
        {
            replies.Add(GatherLevelUpdate());
            replies.AddRange(await QuestMasteryAutoAcquireAsync(ct));
        }
        return replies;
    }

    /// <summary>
    /// Patch of Oct 28 2026: gathering needs character level 45 and the server pays 1,000 successful gathers per game day.
    /// Stored characters only (development logins keep gathering for tests); <paramref name="take"/> counts the gather against the day.
    /// </summary>
    bool GatherRefused(long now, bool take)
    {
        if (Store is null) return false;
        if (Patch20261028.GatherBlock(level) is not null) return true;
        if (options.World?.GatherLimiter is not { } limiter) return false;
        return take ? !limiter.TryTake(now) : !limiter.HasRoom(now);
    }

    /// <summary>ESlotRestrictIconType sent as 0x3639 _reason when a gather is refused (OBSERVED), or null when allowed.</summary>
    int? GatherFailReason(WorldObject node)
    {
        if (Store is null) return null;
        if (Patch20261028.GatherBlock(level) is not null) return GamePackets.EnvObjGatherFailReason.LackLevel;
        if (node.Gather is { } g && g.Length >= 5 && g[4] > gatherLevel) return GamePackets.EnvObjGatherFailReason.LackMasteryLevel;
        return null;
    }

    List<byte[]> GatherRefusedPackets(int objectKey, int reason) =>
        [GamePackets.InteractEnvObjInterrupted(objectKey, reason, playerKey), GamePackets.InteractEnvObjAvailableState(objectKey)];

    bool Gathered(int index, long now)
    {
        if (!gathered.TryGetValue(index, out var until)) return false;
        if (now < until) return true;
        gathered.Remove(index);
        return false;
    }

    /// <summary>
    /// 0x8d30 GatherSourceStart_RQ {target key}: the attempt's turns are rolled at once and sent in 0x8d31 (GatherTurnResult
    /// {EGatherTurnResult, no extra discovery, no skill effects, success gauge before/after, fail gauge before/after}; an
    /// empty turn list left the result panel waiting). A won attempt starts the node's timed interaction (completion as for
    /// 0x3636: the material, 0x8d33); a lost one ends with 0x8d33 result 2. Either costs one of the node's uses, and the last
    /// one uses the node up; until then it is available again.
    /// </summary>
    async Task<List<byte[]>> GatherStartAsync(byte[] plain, long now, CancellationToken ct)
    {
        uint key;
        try { key = new PacketReader(plain) { Offset = 2 }.Varint(); } catch (ProtocolException) { return []; }
        var objects = WorldObjects.ObjectsOn(worldMap);
        var index = (int)key - WorldObjects.FirstKey;
        byte[] Reply(int result, int attempts, bool won, IReadOnlyList<GatherTurn> turns, bool discovered = false)
        {
            // OBSERVED (88 official GatherSourceStart_RS): _mastery_grade_quest is one bool sharing the open bool byte.
            // A discovery sets _is_discovered and _basic_discovery_result and material group 2 (OBSERVED 2026-11-15).
            var masteryGradeQuest = TryElevateGatherCollect(out _, out _);
            var w = PacketWriter.Opcode(GatherStartRs).U16(result).Varint(key).I32(attempts).Bool(won).Bool(discovered).Varint(0)
                .Bool(discovered).Varint(0).I32(discovered ? 2 : 1).Varint(turns.Count);
            foreach (var t in turns) w.U8(t.Result).Bool(false).Varint(0).I32(t.SuccessBefore).I32(t.SuccessAfter).I32(t.FailBefore).I32(t.FailAfter);
            return w.Bool(masteryGradeQuest).ToArray();
        }
        byte[] GatherCompletedFail(long[] gatherNode) =>
            GatherCompleted(GatherFailed, key).I32((int)gatherNode[0]).I32(0).I32(0).I32(0).I32(0).ToArray();

        List<byte[]> GatherStartAborted(long[] gatherNode)
        {
            var replies = new List<byte[]> { Reply(1, 0, false, []) };
            replies.Add(GatherCompletedFail(gatherNode));
            return replies;
        }

        if (index < 0 || index >= objects.Count || objects[index].Gather is not { } node)
            return [Reply(1, 0, false, [])];
        if (Gathered(index, now)) return GatherStartAborted(node);
        var keyInt = (int)key;
        if (GatherFailReason(objects[index]) is { } fail)
            return [Reply(1, 0, false, []), ..GatherRefusedPackets(keyInt, fail)];
        if (GatherRefused(now, take: false)) return GatherStartAborted(node);
        // The node's GatherSource MasteryLevel (the fifth gather value) gates it.
        var target = node.Length >= 6 && node[5] > 0 ? (int)node[5] : GatherBasicGain;
        var total = node.Length >= 7 && node[6] > 0 ? (int)node[6] : 3;
        var attempts = gatherAttempts.TryGetValue(index, out var left) ? left : total;
        // 0x8d31's u32 counts the gathers on this node so far, this one included (OBSERVED), not the attempts left.
        var gatherNumber = total - attempts + 1;
        var turns = GatherTurns(target, node.Length >= 5 ? (int)node[4] : 1);
        var won = GatherWon(turns, target);
        if (won)
        {
            var replies = await UseObjectAsync(new PacketWriter().U16(0x3636).Varint(key).ToArray(), now, ct);
            var refused = replies.Any(b => b[0] == 0x37 && b[1] == 0x36 && b.Length > 2 && b[2] != 0);
            var discovered = false;
            if (!refused)
            {
                // The node's timed interaction completes only once the turns have played out on the client.
                if (pendingUse is { } use && use.Key == keyInt)
                    pendingUse = use with { DueMs = Math.Max(use.DueMs, now + GatherRaceMs(turns.Count)) };
                discovered = GatherBetterGrade((int)node[0]) is not null && GatherDiscoveryRoll() < GatherDiscoveryChance;
            }
            gatherDiscoveredIndex = discovered ? index : null;
            // OBSERVED order: 0x3638 (the interaction started) before 0x8d31.
            return replies.Count > 0 && replies[0] is [0x38, 0x36, ..]
                ? [replies[0], Reply(refused ? 1 : 0, gatherNumber, !refused, turns, discovered), .. replies.Skip(1)]
                : [Reply(refused ? 1 : 0, gatherNumber, !refused, turns, discovered), .. replies];
        }
        pendingUse = null;
        pendingGatherLoss = (index, keyInt, now + GatherRaceMs(turns.Count));
        return [GamePackets.InteractEnvObjStarted(keyInt, playerKey), Reply(0, gatherNumber, false, turns)];
    }

    /// <summary>
    /// A lost attempt's end once its turns have played out; the attempt is spent and the last one uses the node up. OBSERVED order
    /// (2026-11-12): 0x363a with the uses taken, 0x363b, the mastery exp, 0x8d33 result 2, 0x8d34.
    /// </summary>
    async Task<List<byte[]>> GatherLossTickAsync(long now, CancellationToken ct)
    {
        if (pendingGatherLoss is not { } loss || now < loss.DueMs) return [];
        pendingGatherLoss = null;
        var objects = WorldObjects.ObjectsOn(worldMap);
        if (loss.Index < 0 || loss.Index >= objects.Count || objects[loss.Index].Gather is not { } node) return [];
        var index = loss.Index;
        var key = loss.Key;
        var total = node.Length >= 7 && node[6] > 0 ? (int)node[6] : 3;
        var attempts = (gatherAttempts.TryGetValue(index, out var left) ? left : total) - 1;
        if (attempts <= 0)
        {
            gatherAttempts.Remove(index);
            gathered[index] = now + GatherRespawnMs;
            ScheduleRemoval(index, now);   // EnvObjDie and the despawn 4/3 on the object's timing (OBSERVED, see RemovalDelays)
        }
        else gatherAttempts[index] = attempts;
        var lost = new List<byte[]> { GamePackets.InteractEnvObjCompleted(key, playerKey, GatherUsesTaken(index, node)) };
        if (attempts > 0) lost.Add(GamePackets.InteractEnvObjAvailableState(key));
        lost.AddRange(await GatherMasteryExpAsync(ct, lost: true));
        lost.Add(GatherCompleted(GatherFailed, key).I32((int)node[0]).I32(0).I32(0).I32(0).I32(0).ToArray());
        lost.Add(GatherEnded(GatherFailed, key));
        return lost;
    }

    /// <summary>0x8d32 GatherSourceStop_RQ {target key}: the gather in progress is cancelled; the node is available again (0x363b).</summary>
    List<byte[]> GatherStop(byte[] plain)
    {
        uint key;
        try { key = new PacketReader(plain) { Offset = 2 }.Varint(); } catch (ProtocolException) { return []; }
        if (pendingUse is { } use && use.Key == (int)key) pendingUse = null;
        if (pendingGatherLoss is { } loss && loss.Key == (int)key) pendingGatherLoss = null;
        return [GamePackets.InteractEnvObjAvailableState((int)key)];
    }

    /// <summary>A gather node's interaction completed: the material, 0x8d33, the node used up (0x3635).</summary>
    async Task<List<byte[]>> CompleteGatherAsync(int index, WorldObject node, long now, CancellationToken ct)
    {
        var g = node.Gather!;
        int key = WorldObjects.FirstKey + index, item = (int)g[0], count = (int)g[1];
        // A discovered attempt yields the better grade, with 0x8d33 / 0x8d34 result 1 (OBSERVED 2026-11-15).
        var discovered = gatherDiscoveredIndex == index && GatherBetterGrade(item) is not null;
        gatherDiscoveredIndex = null;
        if (discovered) item = GatherBetterGrade(item)!.Value;
        var result = discovered ? GatherDiscovered : 0;
        if (GatherRefused(now, take: true))
            return [GatherCompleted(GatherFailed, key).I32(item).I32(0).I32(0).I32(0).I32(0).ToArray()];
        var bonus = g.Length >= 4 && g[2] > 0 && GatherBonusRoll() == 0 ? ((int)g[2], (int)g[3]) : (0, 0);
        var rewards = new List<(int, long)> { (item, count) };
        if (bonus.Item1 > 0) rewards.Add((bonus.Item1, bonus.Item2));
        if (TryElevateGatherCollect(out var elevateItem, out _) && HeldCount(elevateItem) < 1)
            rewards.Add((elevateItem, 1));
        var replies = new List<byte[]>();
        if (Store is { } store && CharacterId is { } character)
        {
            var granted = await Stored(async () => await store.GrantLootAsync(character, rewards, ct) ? [[]] : []);
            if (granted.Count == 0)
                return [GatherCompleted(GatherFailed, key).I32(item).I32(0).I32(0).I32(0).I32(0).ToArray()];
            replies.AddRange(await InventoryChangesAsync(ct, IncreaseGatherSource, 0));
        }
        // The gather takes one of the node's uses; the last one uses it up, the others leave it available (0x363b sent with the
        // interaction's end, ObjectUsedAsync).
        var total = g.Length >= 7 && g[6] > 0 ? (int)g[6] : 3;
        var usesLeft = (gatherAttempts.TryGetValue(index, out var before) ? before : total) - 1;
        if (usesLeft <= 0)
        {
            gathered[index] = now + GatherRespawnMs;
            gatherAttempts.Remove(index);
            ScheduleRemoval(index, now);   // EnvObjDie and the despawn 4/3 on the object's timing (OBSERVED, see RemovalDelays)
        }
        else gatherAttempts[index] = usesLeft;
        // OBSERVED order (2026-11-12): the mastery exp (and level), the item, 0x8d33, 0x8d34.
        replies.InsertRange(0, await GatherMasteryExpAsync(ct));
        replies.AddRange(AchieveGather(gatherLevel));
        replies.Add(GatherCompleted(result, key).I32(item).I32(count).I32(0).I32(bonus.Item1).I32(bonus.Item2).ToArray());
        replies.Add(GatherEnded(result, key));
        return replies;
    }
}
