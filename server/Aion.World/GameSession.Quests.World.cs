using Aion.Application.Gameplay;
using Aion.Core.Characters;
using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    // Kill/object/area quest handlers (UseEnvObj, EnterVolumePC, fog, Kibelisks).
    // ---------- Quest world (objects, areas, fog, Kibelisks) ----------

    // ---------- World-map fog (the clouds on the M map) ----------

    /// <summary>Uncovered fog groups (stored; per connection on development logins).</summary>
    readonly SortedSet<int> fog = [];
    long fogCheckAt;
    public IReadOnlyCollection<int> Fog => fog;
    public const long FogCheckMs = 500;

    /// <summary>Entering a subzone uncovers its fog group: stored, then 0x56ad. Checked at most every FogCheckMs.</summary>
    async Task<List<byte[]>> UncoverFogAsync(Vector3d at, long now, CancellationToken ct)
    {
        if (resources is null || now < fogCheckAt) return [];
        fogCheckAt = now + FogCheckMs;
        var replies = ObjectVisibility(at, now);
        replies.AddRange(await AreaQuestsAsync(at, ct));
        if (Subzones.GroupAt(worldMap, at) is { } group && !fog.Contains(group)) replies.AddRange(await OpenFogAsync([group], ct));
        replies.AddRange(DismountIfSubzoneForbidden());
        return replies;
    }

    // ---------- Quest objects and trigger areas ----------

    /// <summary>Quest objects this client has been sent (index on the map).</summary>
    readonly HashSet<int> shownObjects = [];
    /// <summary>Trigger actors spawned for this player (index in the map's trigger list).</summary>
    readonly HashSet<int> shownTriggers = [];
    /// <summary>A map event's instance (dungeon, layer): long-range objects and every trigger actor are sent at entry (OBSERVED).</summary>
    bool InstanceMap => instanceKey != 0 && MapEventData.Serves(worldMap);
    /// <summary>
    /// Objects a map event's UseEnvObj objective consumed in this layer: gone for this player (OBSERVED 2026-10-19: each phoenix egg of
    /// HQ002 despawned 100 ms after its use, 0x3635 state 4 reason 3, and never came back; before this they could be used without end).
    /// </summary>
    readonly HashSet<int> usedObjects = [];

    /// <summary>Used-up objects on their way out: EnvObjDie at DieAt, the despawn (state 4, reason 3) at GoneAt.</summary>
    readonly List<(int Index, long DieAt, long GoneAt)> objectRemovals = [];

    /// <summary>
    /// When a used-up object dies and despawns, after its use. OBSERVED (community fork's recordings, 191 EnvObjDie in 40 files;
    /// addiboii's fork): EnvObjDie 0x8d05 about 50 ms after the use's 0x8d6d (176 of 185), about 650 or 1150 ms for objects with
    /// an EnvObjData FinishDuration of 500 or 1000 (9), then the despawn 0x3635 state 4 reason 3 about 50 ms later (188 of the
    /// despawns after a use; state 1 reason 1: 6). CANDIDATE: FinishDuration + 150 ms for the other FinishDurations (up to 50 min
    /// in the data; only 500 and 1000 were recorded).
    /// </summary>
    public static (long Die, long Gone) RemovalDelays(int finishMs) => finishMs > 0 ? (finishMs + 150, finishMs + 200) : (50, 100);

    /// <summary>The object dies and despawns for this player on its timing (it stays shown until then).</summary>
    void ScheduleRemoval(int index, long now)
    {
        var objects = WorldObjects.ObjectsOn(worldMap);
        var (die, gone) = RemovalDelays(index < objects.Count ? objects[index].FinishMs : 0);
        objectRemovals.RemoveAll(r => r.Index == index);
        objectRemovals.Add((index, now + die, now + gone));
    }

    /// <summary>The due EnvObjDie and despawns of used-up objects (only those this player still has shown).</summary>
    List<byte[]> ObjectRemovalTick(long now)
    {
        if (objectRemovals.Count == 0) return [];
        var replies = new List<byte[]>();
        for (var i = 0; i < objectRemovals.Count; i++)
        {
            var (index, dieAt, goneAt) = objectRemovals[i];
            var key = WorldObjects.FirstKey + index;
            if (dieAt != long.MinValue && now >= dieAt)
            {
                if (shownObjects.Contains(index)) replies.Add(GamePackets.EnvObjDie(key));
                objectRemovals[i] = (index, long.MinValue, goneAt);
            }
            if (now >= goneAt && shownObjects.Remove(index)) replies.Add(GamePackets.EnvObjDespawn(key, 4, 3));
        }
        objectRemovals.RemoveAll(r => now >= r.GoneAt);
        return replies;
    }
    /// <summary>Map-event EnvObjChangePhase per object index on this map layer.</summary>
    readonly Dictionary<int, byte> envObjPhase = [];
    /// <summary>Map-event SetEnvObjCanInteract; when absent the client default is interactable.</summary>
    readonly Dictionary<int, bool> envObjCanInteract = [];
    /// <summary>A timed interaction in progress: object index, key, when it completes.</summary>
    (int Index, int Key, long DueMs)? pendingUse;

    byte[] EnvObjSpawnPacket(int index, WorldObject o, long now)
    {
        var key = WorldObjects.FirstKey + index;
        // An Empyrean Trace: its own form with the spawn's achievement (GameSession.EmpyreanTraces.cs).
        if (IsTrace(o)) return GamePackets.TraceSpawn(key, o.Env, o.At, o.Yaw, now, o.UniqueKey, (byte)o.Relationship, o.Achievement);
        envObjPhase.TryGetValue(index, out var phase);
        // Usage=Monolith: client opens MonolithResonate (0xe30d) from 0xe30c, not InteractEnvObj (EnvObjData InteractTime 0).
        var canInteract = o.Monolith ? false : PlayerMayUseEnvObj(index, o);
        return GamePackets.EnvObjSpawn(key, o.Env, o.At, o.Yaw, now, o.UniqueKey, (byte)o.Relationship,
            state: 1, canInteract: canInteract ? (byte)1 : (byte)0, reason: 1, phase: phase);
    }

    void PushEnvObjSpawn(int index, WorldObject o, long now)
    {
        mapEventOut.Add(EnvObjSpawnPacket(index, o, now));
        foreach (var notice in MonolithEnvObjNotices(index, o)) mapEventOut.Add(notice);
    }

    bool EnvObjInteractAllowed(int index) => !envObjCanInteract.TryGetValue(index, out var ok) || ok;

    /// <summary>Spawns quest objects within WorldObjects.VisibleRange (0x3634) and despawns those left behind (0x3635).</summary>
    List<byte[]> ObjectVisibility(Vector3d at, long now)
    {
        var replies = new List<byte[]>();
        var objects = WorldObjects.ObjectsOn(worldMap);
        // The objects shown (to despawn) and those placed in range (SpatialGrid), in index order.
        var candidates = new SortedSet<int>(shownObjects);
        candidates.UnionWith(SpatialGrid.Objects(worldMap).Near(at.X, at.Y, WorldObjects.VisibleRange));
        var instance = InstanceMap;
        if (instance) candidates.UnionWith(WorldObjects.LongRangeOn(worldMap));
        foreach (var i in candidates)
        {
            if (i >= objects.Count) continue;
            var o = objects[i];
            double dx = o.Position[0] - at.X, dy = o.Position[1] - at.Y;
            var near = (instance && o.LongRange || dx * dx + dy * dy <= WorldObjects.VisibleRange * WorldObjects.VisibleRange) && !MapEventHidden(i, o);
            if (CarryBoxExpired(i, now) && shownObjects.Remove(i))
            {
                replies.Add(GamePackets.EnvObjDespawn(WorldObjects.FirstKey + i));
                continue;
            }
            if (near && !Gathered(i, now) && !Broken(i, now) && !CarryBoxGone(i, now) && !usedObjects.Contains(i) && shownObjects.Add(i))
            {
                replies.Add(EnvObjSpawnPacket(i, o, now));
                replies.AddRange(MonolithEnvObjNotices(i, o));
            }
            else if (!near && shownObjects.Remove(i))
                replies.Add(GamePackets.EnvObjDespawn(WorldObjects.FirstKey + i));
        }
        // Trigger actors: all of an instance map's at once (official), elsewhere within TriggerRange.
        var triggers = WorldObjects.TriggersOn(worldMap);
        for (var i = 0; i < triggers.Count; i++)
        {
            var t = triggers[i];
            if (t.Env == 0) continue;
            double dx = t.Position[0] - at.X, dy = t.Position[1] - at.Y;
            var near = instance || dx * dx + dy * dy <= WorldObjects.TriggerRange * WorldObjects.TriggerRange;
            if (near && shownTriggers.Add(i))
                replies.Add(GamePackets.TriggerSpawn(WorldObjects.FirstTriggerKey + i, t.Env, t.At, t.Direction, now, t.Id));
            else if (!near && shownTriggers.Remove(i))
                replies.Add(GamePackets.EnvObjDespawn(WorldObjects.FirstTriggerKey + i));
        }
        return replies;
    }

    /// <summary>EnterVolumePC (trigger area, widened by the objective's value) and EnterSubZone objectives of active quests.</summary>
    async Task<List<byte[]>> AreaQuestsAsync(Vector3d at, CancellationToken ct)
    {
        if (questLog is null || !questLog.OpenAreaObjectives().Any()) return [];
        var triggers = WorldObjects.TriggersOn(worldMap);
        bool Inside(QuestObjective o) => o.Target is { } name && (o.Type == "EnterSubZone"
            ? Subzones.InSubzone(worldMap, name, at)
            : triggers.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)
                && t.Contains(at, o.Value ?? 0)));
        var changed = questLog.Entered(Inside, level);
        return changed.Count == 0 ? [] : await Stored(() => QuestChangesAsync(changed, ct));
    }

    /// <summary>CollectItem objectives follow bag counts; called after inventory reloads.</summary>
    async Task<List<byte[]>> SyncQuestCollectItemsAsync(CancellationToken ct)
    {
        if (questLog is null) return [];
        if (Store is null || CharacterId is null)
        {
            Note("SyncQuestCollectItems skipped: character or store not loaded");
            return [];
        }
        var itemIds = questLog.All.Where(q => q.Status == QuestLog.Incomplete).SelectMany(q =>
            questLog.CurrentStep(q) is { } step
                ? step.Objectives.Take(6).Where(o => o.Type == "CollectItem" && o.Item is { }).Select(o => o.Item!.Value)
                : []).Distinct().ToList();
        if (itemIds.Count == 0) return [];
        var changed = new Dictionary<string, QuestRecord>();
        foreach (var itemId in itemIds)
        {
            var held = inventory.Values.Where(i => i.ItemId == itemId).Sum(i => i.Count);
            foreach (var record in questLog.Holding(itemId, held, level))
            {
                Note($"SyncQuestCollectItems item={itemId} held={held} quest={record.Code} step={record.Step} goals=[{string.Join(',', record.Goals)}]");
                changed[record.Code] = record;
            }
        }
        return changed.Count == 0 ? [] : await Stored(() => QuestChangesAsync(changed.Values, ct));
    }

    /// <summary>EnvObjData InteractConditionList CombatPower gate; open quest / map-event UseEnvObj steps are never blocked server-side.</summary>
    bool EnvObjCombatPowerRefused(int index, WorldObject o)
    {
        if (o.RequiredCombatPower <= 0 || combatPower >= o.RequiredCombatPower) return false;
        if (questLog?.OpenUseEnvObjIds().Contains(o.Env) == true) return false;
        if (QuestCollectItemRescueEnv(o)) return false;
        if (mapEventRuns.Any(r => r.WantsObject(o.Name, o.EnvDataName))) return false;
        return true;
    }

    /// <summary>
    /// Using a quest object (0x3636 with its key): in range, 0x3638 started. OBSERVED (community fork's recordings 2026-11-04
    /// to 06): an object with an InteractTime (EnvObjData) answers 0x3637 at once and at the end sends 0x363a with 0x8d6d
    /// (finish state) when it is used up or 0x363b (available) when it stays; an instant one sends 0x363a, 0x8d6d, [0x363b],
    /// then the 0x3637 reply (capture 20261013-144836 #10090-10093), then UseEnvObj quest progress. CANDIDATE: the rule
    /// "0x8d6d = used up, 0x363b = usable again" (8 env ids in the fork's recordings). Gather nodes and carried-item boxes keep
    /// 0x363a, 0x8d6d, 0x363b, 0x3637 at the end (not recorded; without 0x8d6d/0x363b the client kept the gather result panel open).
    /// Empyrean Traces have their own order (GameSession.EmpyreanTraces.cs).
    /// </summary>
    async Task<List<byte[]>> UseObjectAsync(byte[] plain, long now, CancellationToken ct)
    {
        var objects = WorldObjects.ObjectsOn(worldMap);
        // Entering a trigger actor's volume (the client reports it): started, completed, finish and available states, the
        // reply (OBSERVED for every subspace trigger). The objectives themselves go by the position reports.
        foreach (var key in EnvObjKeys(plain))
            if (key is { } tk && tk - WorldObjects.FirstTriggerKey is var ti && ti >= 0 && ti < WorldObjects.TriggersOn(worldMap).Count)
                return [GamePackets.InteractEnvObjStarted(tk, playerKey), GamePackets.InteractEnvObjCompleted(tk, playerKey),
                    GamePackets.InteractEnvObjFinishState(tk), GamePackets.InteractEnvObjAvailableState(tk), GamePackets.InteractEnvObjReply(tk)];
        int? index = null;
        foreach (var key in EnvObjKeys(plain))
            if (key is { } k && k - WorldObjects.FirstKey is var i && i >= 0 && i < objects.Count) { index = i; break; }
        // No such object: refuse it, or the client waits for the reply with the player locked.
        if (index is not { } found || Gathered(found, now) || Broken(found, now) || CarryBoxGone(found, now) || usedObjects.Contains(found))
            return [GamePackets.InteractEnvObjReply(EnvObjKeys(plain).FirstOrDefault(k => k is not null) ?? 0, 1)];
        var o = objects[found];
        var key2 = WorldObjects.FirstKey + found;
        // A refusal carries the available state too, so the client's interaction never stays open on it.
        if (!PlayerMayUseEnvObj(found, o) || CarryRefused(o)) return [GamePackets.InteractEnvObjReply(key2, 1), GamePackets.InteractEnvObjAvailableState(key2)];
        if (EnvObjCombatPowerRefused(found, o)) return [GamePackets.InteractEnvObjReply(key2, 1), GamePackets.InteractEnvObjAvailableState(key2)];
        if (o.Gather is not null && GatherFailReason(o) is { } gatherFail) return GatherRefusedPackets(key2, gatherFail);
        if (o.FlightRing) return FlyThroughRing(key2, now);
        if (position is { } at && Math.Sqrt(Math.Pow(at.X - o.Position[0], 2) + Math.Pow(at.Y - o.Position[1], 2)) > o.InteractRange + WorldObjects.InteractSlack)
            return [GamePackets.InteractEnvObjReply(key2, 1)];
        // An Empyrean Trace (GameSession.EmpyreanTraces.cs): the reply comes with the start, not after the use (OBSERVED).
        if (IsTrace(o)) return StartTraceUse(found, o, key2, now);
        var replies = new List<byte[]> { GamePackets.InteractEnvObjStarted(key2, playerKey) };
        var interactTime = o.InteractTime > 0 ? o.InteractTime : o.Carry is not null ? CarryPickupMs : 0;
        if (interactTime > 0)
        {
            pendingUse = (found, key2, now + interactTime);
            if (ReplyAtUseStart(o)) replies.Add(GamePackets.InteractEnvObjReply(key2));   // OBSERVED: the reply at the start
            return replies;
        }
        replies.AddRange(await CompleteUseAsync(found, ct, now));
        return replies;
    }

    /// <summary>
    /// Flying through a flight ring (EnvObjData Usage FlightRing; the client reports it as an interaction): the reply alone, the
    /// two abnormals the official sent (18999040 for 3 s, 18999041 for 0.1 s; not in the extracted tables) and the flight points
    /// refilled (OBSERVED capture 20261019-231752 #37632-#37639: 183000 back to 250000 within 2.6 s; ours at once).
    /// </summary>
    List<byte[]> FlyThroughRing(int key, long now)
    {
        var replies = new List<byte[]> { GamePackets.InteractEnvObjReply(key) };
        var at = position ?? Vector3d.Zero;
        replies.Add(PlayerPackets.AbnormalAdd(playerKey, [new AbnormalWire(++buffUid, FlightRingAbnormal, 3000, now + 3000, CasterKey: playerKey, RangeBase: at)]));
        replies.Add(PlayerPackets.AbnormalAdd(playerKey, [new AbnormalWire(++buffUid, FlightRingAbnormal + 1, 100, now + 100, CasterKey: playerKey, RangeBase: at)]));
        if (resources is { FpMax: > 0 } r && r.Fp < r.FpMax)
        {
            r.Fp = r.FpMax;
            replies.Add(r.FpUpdate());
        }
        return replies;
    }

    public const int FlightRingAbnormal = 18999040;

    const byte IncreaseEnvObjReward = 41; // EIncreaseReason kGatherSource (interaction loot)

    /// <summary>An env object's reward tables rolled and granted; the tables are returned so a treasure chest can be used up.</summary>
    async Task<(List<byte[]> Packets, string[] Tables, bool Granted)> GrantEnvObjLootAsync(WorldObject obj, CancellationToken ct)
    {
        var tables = LootTables.Embedded.ResolveEnvLootTableNames(obj.Env, obj.Name, obj.EnvName);
        if (tables.Length == 0) return ([], tables, false);
        var rolled = LootTables.Embedded.RollEnv(obj, profileRace, rng);
        if (rolled.Count == 0) return ([], tables, false);
        if (Store is not { } store || CharacterId is not { } character) return ([], tables, false);
        var granted = await Stored(async () => await store.GrantLootAsync(character, rolled, ct) ? [[]] : []);
        return granted.Count == 0 ? ([], tables, false) : (await InventoryChangesAsync(ct, IncreaseEnvObjReward, 0), tables, true);
    }

    /// <summary>A timed use of this object answers 0x3637 at its start, not at its end (OBSERVED); gather nodes and carried-item boxes do not.</summary>
    static bool ReplyAtUseStart(WorldObject o) => o.InteractTime > 0 && o.Gather is null && o.Carry is null;

    async Task<List<byte[]>> CompleteUseAsync(int index, CancellationToken ct, long now = 0, bool timed = false)
    {
        var objects = WorldObjects.ObjectsOn(worldMap);
        if (index >= objects.Count) return [];
        // An Empyrean Trace: its own order, the reply went with the start (GameSession.EmpyreanTraces.cs).
        if (IsTrace(objects[index])) return await CollectTraceAsync(index, objects[index], now, ct);
        var key = WorldObjects.FirstKey + index;
        var (used, usedUp) = await ObjectUsedAsync(index, now, ct);
        // A gather node: 0x363a with its uses taken and 0x363b, then the gather's own packets (OBSERVED 2026-11-12; no 0x8d6d, no 0x3637).
        if (objects[index].Gather is { } node)
            return [GamePackets.InteractEnvObjCompleted(key, playerKey, GatherUsesTaken(index, node)), GamePackets.InteractEnvObjAvailableState(key), .. used];
        var replies = new List<byte[]> { GamePackets.InteractEnvObjCompleted(key, playerKey) };
        if (objects[index].Carry is not null)
            replies.AddRange([GamePackets.InteractEnvObjFinishState(key), GamePackets.InteractEnvObjAvailableState(key), GamePackets.InteractEnvObjReply(key)]);
        else if (timed && ReplyAtUseStart(objects[index]))
            replies.Add(usedUp ? GamePackets.InteractEnvObjFinishState(key) : GamePackets.InteractEnvObjAvailableState(key));
        else
        {
            replies.Add(GamePackets.InteractEnvObjFinishState(key));
            if (!usedUp) replies.Add(GamePackets.InteractEnvObjAvailableState(key));
            replies.Add(GamePackets.InteractEnvObjReply(key));
        }
        replies.AddRange(used);
        return replies;
    }

    /// <summary>What using an object does past the interaction packets; UsedUp when the object is gone for this player afterwards.</summary>
    async Task<(List<byte[]> Packets, bool UsedUp)> ObjectUsedAsync(int index, long now, CancellationToken ct)
    {
        var objects = WorldObjects.ObjectsOn(worldMap);
        var key = WorldObjects.FirstKey + index;
        var replies = new List<byte[]>();
        if (objects[index].Gather is not null) replies.AddRange(await CompleteGatherAsync(index, objects[index], now, ct));
        var destroyed = CarryUsed(index, objects[index], now, replies);
        replies.AddRange(await CreditUseEnvObjQuestAsync(objects[index].Env, ct));
        var (lootPackets, tables, granted) = await GrantEnvObjLootAsync(objects[index], ct);
        replies.AddRange(lootPackets);
        // A field/dungeon treasure chest is used up once it paid out (from A2Core-ChanAge); quest props stay.
        var chestUsedUp = granted && LootTables.TreasureChestConsumes(tables);
        // A carried-item box comes back after its FinishDuration even when a map event counts it (FierceHornCave BombBox_02).
        var consumed = chestUsedUp
            || objects[index].Carry is null && (CopyOwner ?? this).mapEventRuns.Any(r => r.WantsObject(objects[index].Name, objects[index].EnvDataName));
        replies.AddRange(MapEventUsedObject(objects[index].Name, now, objects[index].EnvDataName));
        if (destroyed)
        {
            // A tower that took the carried bomb: gone at once, as a broken object (OURS).
            usedObjects.Add(index);
            shownObjects.Remove(index);
            replies.Add(GamePackets.EnvObjDespawn(key, 4, 3));
        }
        else if (consumed)
        {
            // EnvObjDie and the despawn 4/3 on the object's timing (OBSERVED, see RemovalDelays).
            usedObjects.Add(index);
            ScheduleRemoval(index, now);
        }
        replies.AddRange(ApplyEnvObjSelfAction(objects[index], now));
        // The expedition's reward cube (Usage OdEnergyCube): the final boss's reward, once per player per run.
        if (objects[index].Cube) replies.AddRange(await CubeRewardAsync(null, now, ct));
        replies.AddRange(Achieve("UseEnvObjSpawnerName", 1, e => e.Has(objects[index].Name), now));
        replies.AddRange(Achieve("UseEnvObj", 1, e => e.Envs?.Contains(objects[index].Env) == true, now));
        return (replies, consumed || destroyed);
    }

    async Task<List<byte[]>> OpenFogAsync(IReadOnlyList<int> groups, CancellationToken ct)
    {
        var added = groups.Where(g => !fog.Contains(g)).Distinct().Order().ToList();
        if (Store is { } store && CharacterId is { } character && added.Count > 0)
        {
            var stored = await Stored(async () =>
            {
                var opened = await store.OpenMapFogAsync(character, added, ct);
                return opened.Select(g => BitConverter.GetBytes(g)).ToList();
            });
            added = stored.Select(b => BitConverter.ToInt32(b)).ToList();
        }
        fog.UnionWith(added);
        return [.. added.Select(GamePackets.AddSubzoneGroup), .. added.Count > 0 ? Achieve("EnterSubzone", added.Count) : []];
    }

    /// <summary>GM RevealMap: every fog group uncovered (stored), sent as the full list.</summary>
    public async Task<List<byte[]>> RevealMapAsync(CancellationToken ct)
    {
        if (resources is null) throw new InvalidOperationException("Not in the world.");
        await OpenFogAsync(Subzones.AllGroups, ct);
        return [GamePackets.OpenSubzoneGroups(fog.ToList())];
    }

    // ---------- Kibelisks (teleport artifacts) ----------

    /// <summary>TeleportArtifact ids the character has bound to (stored; per connection on development logins).</summary>
    readonly List<int> engraved = [];
    public IReadOnlyList<int> Engraved => engraved;

    /// <summary>World entry: the engraved list (0x9232) and the map's Kibelisks (0x3634).</summary>
    List<byte[]> KibeliskSpawns(int mapId, long now)
    {
        var replies = new List<byte[]> { GamePackets.EngravedTeleportArtifacts(engraved) };
        var placed = Kibelisks.OnMap(mapId);
        for (var i = 0; i < placed.Count; i++)
        {
            var k = placed[i];
            replies.Add(GamePackets.EnvObjSpawn(Kibelisks.FirstKey + i, k.EnvObj, new Vector3d(k.Position[0], k.Position[1], k.Position[2]), k.Yaw, now, k.UniqueKey, k.Relationship));
        }
        return replies;
    }

    /// <summary>
    /// Candidate object keys in an InteractEnvObj_RQ. The live client (2026-10-15) sends a 01 byte, then
    /// the varint key, i64 0 and the EnvObj id; the earlier guesses (key right after the opcode) are kept after it.
    /// </summary>
    static int?[] EnvObjKeys(byte[] plain) =>
        [plain.Length > 3 && plain[2] == 1 ? QuestKey(plain, 3) : null, QuestKey(plain, 2), plain.Length >= 6 ? BitConverter.ToInt32(plain, 2) : null];

    /// <summary>The raw body of the last InteractEnvObj_RQ (research log).</summary>
    public byte[]? LastEnvObjRequest { get; private set; }

    /// <summary>
    /// Binding to a Kibelisk (official order): 0x3638 started, 0x363a completed, 0x3637 reply, then
    /// 0x9233 when newly bound and the quest progress (EngraveTeleportArtifact). The object is the
    /// one whose key the request carries (varint or i32 after the opcode).
    /// </summary>
    async Task<List<byte[]>> InteractEnvObjAsync(byte[] plain, long now, CancellationToken ct)
    {
        LastEnvObjRequest = plain;
        var placed = Kibelisks.OnMap(worldMap);
        int? index = null;
        foreach (var key in EnvObjKeys(plain))
            if (key is { } k && k - Kibelisks.FirstKey is var i && i >= 0 && i < placed.Count) { index = i; break; }
        if (index is not { } found) return await UseObjectAsync(plain, now, ct);
        var kibelisk = placed[found];
        var objectKey = Kibelisks.FirstKey + found;
        var replies = new List<byte[]>
        {
            GamePackets.InteractEnvObjStarted(objectKey, playerKey), GamePackets.InteractEnvObjCompleted(objectKey, playerKey), GamePackets.InteractEnvObjReply(objectKey),
        };
        var added = !engraved.Contains(kibelisk.ArtifactId);
        if (added && Store is { } store && CharacterId is { } character)
            added = (await Stored(async () => await store.EngraveTeleportArtifactAsync(character, kibelisk.ArtifactId, ct) ? [Array.Empty<byte>()] : [])).Count == 1;
        if (added)
        {
            engraved.Add(kibelisk.ArtifactId);
            replies.Add(GamePackets.EngravedTeleportArtifactAdd(kibelisk.ArtifactId));
            replies.AddRange(Achieve("EngraveTeleportArtifact", 1, e => e.Has(kibelisk.Artifact)));
        }
        if (questLog is not null)
            replies.AddRange(await Stored(() => QuestChangesAsync(questLog.Engraved(kibelisk.Artifact, level), ct)));
        return replies;
    }

    /// <summary>UseTeleportArtifact_RS results (CANDIDATE: the usmap names kTeleportArtifact_Fail_*, not their values).</summary>
    const int ArtifactNotFound = 1, ArtifactNotEngraved = 2, ArtifactCanNotMove = 3;
    /// <summary>How far from its Kibelisk a map's TeleportArtifact_* arrival point stands (Poeta 780 cm, Verteron within 3000).</summary>
    const double ArtifactArrivalRange = 3000;

    /// <summary>
    /// The artifact teleport's cost item (OBSERVED official 2026-11-15: four teleports each used one 530559489, 35 down to 32,
    /// decrease reason 56; no Kina, no Return Scroll). The item is not in the exported item data, so its name is unknown.
    /// </summary>
    const int ArtifactTeleportItemId = 530559489;
    const byte DecreaseArtifactTeleport = 56;

    /// <summary>
    /// UseTeleportArtifact_RQ 0x9236 (the world map's teleport confirm): an engraved Kibelisk only. OBSERVED official order
    /// (2026-11-15, four teleports): the own 0x3647 `02 00` with 0x3621 reason 2, the load, and after the appearance
    /// (0x3633, 0x3646) 0x9237 {0, artifact} and 0x561b using up one teleport item (530559489, decrease 56). Without the
    /// item the move is free (unrecorded; the item is not handed out here).
    /// </summary>
    async Task<List<byte[]>> UseTeleportArtifactAsync(int artifactId, long now, CancellationToken ct)
    {
        List<byte[]> Reply(int result) => [GamePackets.UseTeleportArtifactReply(artifactId, result)];
        if (!Kibelisks.TryPlacement(artifactId, out var mapId, out var at)) return Reply(ArtifactNotFound);
        if (!engraved.Contains(artifactId)) return Reply(ArtifactNotEngraved);
        if (resources is null || dead || reloading) return Reply(ArtifactCanNotMove);
        var paid = new List<byte[]>();
        if (Store is { } store && CharacterId is { } character && TeleportItem() is { } item)
        {
            var used = await Stored(async () => await store.ConsumeItemAsync(character, item.InstanceId, item.Revision, 1, ct) ? [[]] : []);
            if (used.Count == 0) return Reply(ArtifactCanNotMove);
            paid = await InventoryChangesAsync(ct, 0, DecreaseArtifactTeleport);
        }
        Note($"UseTeleportArtifact {artifactId}: map {mapId}");
        afterAppear.AddRange(Reply(0));
        afterAppear.AddRange(paid);
        return PcTeleport(mapId, ArtifactArrival(mapId, at), now);
    }

    ItemState? TeleportItem() =>
        storedItems.Values.FirstOrDefault(s => s.Location == "bag" && s.Quantity > 0 && s.TemplateId == ArtifactTeleportItemId);

    /// <summary>The map's TeleportArtifact_* point nearest the Kibelisk (MapData TeleportDataMap), else the Kibelisk itself.</summary>
    Vector3d ArtifactArrival(int mapId, Vector3d kibelisk)
    {
        var best = kibelisk;
        var bestDistance = ArtifactArrivalRange;
        foreach (var (name, p) in data.MapPoints(mapId))
        {
            if (!name.StartsWith("TeleportArtifact", StringComparison.OrdinalIgnoreCase) || p.Length < 3) continue;
            var d = Math.Sqrt(Math.Pow(p[0] - kibelisk.X, 2) + Math.Pow(p[1] - kibelisk.Y, 2) + Math.Pow(p[2] - kibelisk.Z, 2));
            if (d < bestDistance) { bestDistance = d; best = new Vector3d(p[0], p[1], p[2]); }
        }
        return best;
    }
}
