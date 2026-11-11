using System.Threading.Channels;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Field bosses (docs/FIELD_BOSSES.md): the client's WorldMapFieldNamed bosses (tools/data/field-bosses.json) respawn on their own
// timer instead of GameSession.RespawnMs, and the kill and the respawn time are stored in the world database
// (game.field_bosses, migration 104), so a restart keeps a dead boss dead until its time. OBSERVED (official field boss tables
// 0x9101 in our captures of 2026-11-02 to 11-05): the respawn time is fixed at the kill and kept to the millisecond; the
// periods vary from kill to kill (101008 and 101009 ~3.0 h, 101006 at most 1.8 h, others at least 5.9 h; the big bosses
// 101021 and 101024 at least 10.8 h). The client data has no period (PeriodSpawnId names a server table), so the default
// ranges are CANDIDATE and staff can change them per boss.

/// <summary>A field boss: its WorldMapFieldNamed code, map, NPC and default respawn range.</summary>
public sealed record FieldBossDefinition(int Code, int MapId, int NpcId, string Kind, int DefaultMinMinutes, int DefaultMaxMinutes);

/// <summary>A field boss for the staff page. Times are Unix ms. Status: alive, dead (until RespawnAt) or due (its time has
/// come; it appears at the next tick of a player on the map).</summary>
public sealed record FieldBossView(int Code, int MapId, int NpcId, string Name, int Level, string Kind, string Status, bool MapLoaded,
    long? KilledAt, long? RespawnAt, int MinMinutes, int MaxMinutes, int DefaultMinMinutes, int DefaultMaxMinutes, bool CustomTimer);

/// <summary>The host's field boss timers: definitions, the last kill and respawn of each, staff ranges; thread-safe.</summary>
public sealed class FieldBossTimers
{
    /// <summary>CANDIDATE default ranges in minutes. Named monsters (grade 1-5 on the starter field maps): 1.5-6 h, covering the
    /// observed ~1.8 h to 5.9 h. Field bosses (grade 6+, the Abyss and the level-80 regions): 6-12 h (observed at least 10.8 h).</summary>
    public const int NamedMinMinutes = 90, NamedMaxMinutes = 360, BossMinMinutes = 360, BossMaxMinutes = 720;
    /// <summary>The longest range staff may set (7 days).</summary>
    public const int MaxMinutes = 7 * 24 * 60;

    sealed class State
    {
        public long? KilledAt, RespawnAt;
        public int? MinMinutes, MaxMinutes;
    }

    readonly Lock gate = new();
    readonly Dictionary<int, State> states = [];
    readonly Random random;
    readonly Channel<Func<IFieldBossStore, Task>> writes = Channel.CreateUnbounded<Func<IFieldBossStore, Task>>(new() { SingleReader = true });
    IFieldBossStore? store;

    public FieldBossTimers(GameData data, Random? random = null)
    {
        this.random = random ?? new Random();
        var list = new List<FieldBossDefinition>();
        foreach (var (code, mapId, npcId) in data.FieldBosses)
        {
            var spawn = data.NpcPlaced(npcId, mapId);
            var boss = spawn is null || spawn.Grade >= 6 || spawn.Level >= 65;
            list.Add(new FieldBossDefinition(code, mapId, npcId, boss ? "boss" : "named",
                boss ? BossMinMinutes : NamedMinMinutes, boss ? BossMaxMinutes : NamedMaxMinutes));
        }
        Definitions = list;
        ByCode = list.ToDictionary(d => d.Code);
    }

    public IReadOnlyList<FieldBossDefinition> Definitions { get; }
    public IReadOnlyDictionary<int, FieldBossDefinition> ByCode { get; }

    /// <summary>Whether the stored timers were read (until then a kill is kept and stored, the rest is the default).</summary>
    public bool Loaded { get; private set; }

    /// <summary>The respawn range in minutes (the staff's, else the default).</summary>
    public (int Min, int Max) Range(int code)
    {
        var d = ByCode[code];
        lock (gate)
            return states.TryGetValue(code, out var s) && s.MinMinutes is { } min && s.MaxMinutes is { } max ? (min, max) : (d.DefaultMinMinutes, d.DefaultMaxMinutes);
    }

    /// <summary>The boss died now: its respawn time (Unix ms), drawn from its range, stored.</summary>
    public long Killed(int code, long now)
    {
        var (min, max) = Range(code);
        long respawn;
        lock (gate)
        {
            respawn = now + (long)((min + random.NextDouble() * (max - min)) * 60_000);
            var s = StateOf(code);
            s.KilledAt = now;
            s.RespawnAt = respawn;
        }
        Persist(code);
        return respawn;
    }

    /// <summary>When the boss died (Unix ms), while it waits for its respawn.</summary>
    public long? KilledAt(int code, long now)
    {
        lock (gate) return states.TryGetValue(code, out var s) && s.RespawnAt > now ? s.KilledAt : null;
    }

    /// <summary>The stored respawn time while it is still ahead.</summary>
    public long? DeadUntil(int code, long now)
    {
        lock (gate) return states.TryGetValue(code, out var s) && s.RespawnAt > now ? s.RespawnAt : null;
    }

    /// <summary>Staff: the boss is due now (the last kill stays on record).</summary>
    public void RespawnNow(int code, long now)
    {
        lock (gate)
        {
            var s = StateOf(code);
            if (s.RespawnAt is null || s.RespawnAt <= now) return;
            s.RespawnAt = now;
        }
        Persist(code);
    }

    /// <summary>Staff: a respawn range in minutes (null, null: the default again). False when out of bounds.</summary>
    public bool SetRange(int code, int? min, int? max)
    {
        if (min is null != max is null) return false;
        if (min is { } a && max is { } b && (a < 1 || b < a || b > MaxMinutes)) return false;
        lock (gate)
        {
            var s = StateOf(code);
            s.MinMinutes = min;
            s.MaxMinutes = max;
        }
        writes.Writer.TryWrite(store => store.SaveTimerAsync(code, min, max));
        return true;
    }

    /// <summary>Takes the stored rows (a kill this host made since it started and the staff's ranges set here win).</summary>
    public void Apply(IReadOnlyList<FieldBossRecord> rows)
    {
        lock (gate)
        {
            foreach (var row in rows)
            {
                if (!ByCode.ContainsKey(row.Code)) continue;
                var s = StateOf(row.Code);
                var killed = row.KilledAt?.ToUnixTimeMilliseconds();
                if (s.KilledAt is null || killed > s.KilledAt)
                {
                    s.KilledAt = killed;
                    s.RespawnAt = row.RespawnAt?.ToUnixTimeMilliseconds();
                }
                if (s.MinMinutes is null && row.MinMinutes is { } min && row.MaxMinutes is { } max)
                {
                    s.MinMinutes = min;
                    s.MaxMinutes = max;
                }
            }
            Loaded = true;
        }
    }

    /// <summary>Stores the timers in <paramref name="target"/> from now on (one writer, in order).</summary>
    public void Attach(IFieldBossStore target, Action<string>? log)
    {
        store = target;
        _ = Task.Run(async () =>
        {
            await foreach (var write in writes.Reader.ReadAllAsync())
                try { await write(target); }
                catch (Exception error) { log?.Invoke($"Field boss timer not stored: {error.Message}"); }
        });
    }

    /// <summary>Waits until every write queued so far is stored (tests; a no-op without a store).</summary>
    public Task FlushAsync()
    {
        if (store is null) return Task.CompletedTask;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        writes.Writer.TryWrite(_ => { done.SetResult(); return Task.CompletedTask; });
        return done.Task;
    }

    /// <summary>The staff view of one boss (<paramref name="map"/>: its map when loaded; call under the world lock).</summary>
    public FieldBossView View(FieldBossDefinition d, MapInstance? map, GameData data, long now)
    {
        var target = map?.FieldBossTarget(d.Code);
        var spawn = target is null ? data.NpcPlaced(d.NpcId, d.MapId) : null;
        long? killedAt, respawnAt;
        bool custom;
        lock (gate)
        {
            states.TryGetValue(d.Code, out var s);
            killedAt = s?.KilledAt;
            respawnAt = s?.RespawnAt;
            custom = s?.MinMinutes is not null;
        }
        var (min, max) = Range(d.Code);
        var status = target is { Dead: true } ? (respawnAt > now || map!.RespawnAt.GetValueOrDefault(target.Key) > now ? "dead" : "due")
            : target is null && respawnAt > now ? "dead" : "alive";
        return new FieldBossView(d.Code, d.MapId, d.NpcId, target?.Name ?? spawn?.Name ?? "", target?.Level ?? spawn?.Level ?? 0, d.Kind, status, map is not null,
            killedAt, status == "alive" ? null : respawnAt, min, max, d.DefaultMinMinutes, d.DefaultMaxMinutes, custom);
    }

    State StateOf(int code)
    {
        if (!states.TryGetValue(code, out var s)) states[code] = s = new State();
        return s;
    }

    void Persist(int code)
    {
        long? killed, respawn;
        lock (gate) { killed = states[code].KilledAt; respawn = states[code].RespawnAt; }
        writes.Writer.TryWrite(store => store.SaveStateAsync(code,
            killed is { } k ? DateTimeOffset.FromUnixTimeMilliseconds(k) : null, respawn is { } r ? DateTimeOffset.FromUnixTimeMilliseconds(r) : null));
    }
}

/// <summary>Reads the stored field boss timers once the world server starts (retried until it works) and stores changes.</summary>
public static class FieldBossWorld
{
    public static void Configure(IFieldBossStore? store, SharedWorld? world, Action<string>? log)
    {
        if (world is null || store is null) return;
        world.FieldBosses.Attach(store, log);
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await LoadAsync(store, world);
                    log?.Invoke($"Field bosses: {world.FieldBosses.Definitions.Count} timers loaded");
                    return;
                }
                catch (Exception error)
                {
                    log?.Invoke($"Field boss timers unavailable: {error.Message}");
                }
                await Task.Delay(TimeSpan.FromSeconds(10));
            }
        });
    }

    /// <summary>Reads the stored timers and applies them to the maps already loaded.</summary>
    public static async Task LoadAsync(IFieldBossStore store, SharedWorld world, CancellationToken ct = default)
    {
        var rows = await store.LoadAsync(ct);
        await world.Lock.EnterExclusiveAsync(ct);
        try
        {
            world.FieldBosses.Apply(rows);
            var now = world.Clock();
            foreach (var map in world.Maps) map.SyncFieldBosses(world.FieldBosses, now);
        }
        finally { world.Lock.ExitExclusive(); }
    }
}

/// <summary>A shared map's field bosses (the first spawn of each boss's NPC on the map; later copies are ordinary monsters).</summary>
public sealed partial class MapInstance
{
    Dictionary<int, int> fieldBossCodes = [];   // target key → code
    Dictionary<int, int> fieldBossKeys = [];    // code → target key
    FieldBossTimers? fieldBosses;

    /// <summary>Finds this map's field bosses and keeps the stored dead ones dead until their time (shared maps only).</summary>
    public void SyncFieldBosses(FieldBossTimers timers, long now)
    {
        if (Instance != 0) return;
        fieldBosses = timers;
        if (fieldBossKeys.Count == 0)
            foreach (var d in timers.Definitions.Where(d => d.MapId == MapId))
                if (Targets.Values.Where(t => t.NpcId == d.NpcId && t.OwnerKey == 0).MinBy(t => t.Key) is { } t)
                {
                    fieldBossCodes[t.Key] = d.Code;
                    fieldBossKeys[d.Code] = t.Key;
                }
        var packets = new List<byte[]>();
        foreach (var (code, key) in fieldBossKeys)
        {
            if (timers.DeadUntil(code, now) is not { } until || !Targets.TryGetValue(key, out var t)) continue;
            if (!t.Dead)
            {
                t.Hp = 0;
                packets.AddRange(Ai.Killed(t));
            }
            if (!t.CorpseRemoved) packets.Add(GamePackets.Death(key));
            t.CorpseRemoved = true;
            RespawnAt[key] = until;
        }
        BroadcastAll(packets);
    }

    /// <summary>The boss's target on this map (null when the map has none).</summary>
    public Target? FieldBossTarget(int code) => fieldBossKeys.TryGetValue(code, out var key) ? Targets.GetValueOrDefault(key) : null;

    /// <summary>The target died now: its respawn time when it is a field boss (stored), else null (the caller's delay).</summary>
    public long? FieldBossKilled(Target target, long now) =>
        fieldBosses is { } timers && fieldBossCodes.TryGetValue(target.Key, out var code) ? timers.Killed(code, now) : null;

    /// <summary>When a dead field boss's corpse goes (CorpseMs after its kill); null for anything else.</summary>
    public long? FieldBossCorpseAt(int key, long now) =>
        fieldBosses is { } timers && fieldBossCodes.TryGetValue(key, out var code) && timers.KilledAt(code, now) is { } killed ? killed + GameSession.CorpseMs : null;

    /// <summary>Staff: the boss appears at the next tick of a player on the map. False when it is not dead.</summary>
    public bool RespawnFieldBoss(int code, long now)
    {
        if (FieldBossTarget(code) is not { Dead: true } t) return false;
        RespawnAt[t.Key] = now;
        return true;
    }

    /// <summary>The field bosses among the dead that respawn now (staff "respawn all").</summary>
    void RespawnFieldBosses(long now)
    {
        if (fieldBosses is not { } timers) return;
        foreach (var (code, key) in fieldBossKeys)
            if (Targets.TryGetValue(key, out var t) && t.Dead) timers.RespawnNow(code, now);
    }
}
