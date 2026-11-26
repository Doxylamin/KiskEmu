using System.Globalization;
using Aion.Protocol.Native;

namespace Aion.World;

// "/bug <text>" (docs/BUG_REPORTS.md): what this session knows at the moment of a report, so a later reader can reproduce the
// bug without asking the player. Captured on the session's own loop (InvokeAsync); cheap enough to run on demand (a scan of
// the map's NPCs and objects near the player and a copy of the connection's packet ring).
public sealed record BugContext(
    string ReportedAt, long ServerMs, BugServer Server, BugCharacter Character, BugLocation? Location, BugQuests Quests,
    IReadOnlyList<BugMapEvent> MapEvents, double NearbyRadius, IReadOnlyList<BugSpawn> Nearby, IReadOnlyList<BugObject> NearbyObjects,
    BugPackets Packets);

public sealed record BugServer(string? Commit, int WorldId, bool SharedWorld);

public sealed record BugCharacter(Guid? AccountId, Guid? Id, string? Name, int Race, string RaceName, string? ClassName, int Level,
    string? StaffRole, int Hp, int HpMax, bool Dead, bool InCombat);

public sealed record BugLocation(int MapId, string MapName, int? Instance, double X, double Y, double Z, double Heading,
    int SubzoneId, string? Subzone, string? Area);

public sealed record BugQuests(IReadOnlyList<BugQuest> Active, IReadOnlyList<BugQuestEvent> RecentEvents, IReadOnlyList<BugQuest> LastRewarded)
{
    /// <summary>Codes of the active quests and of those finished in the recent events (the dashboard's quest filter).</summary>
    public IReadOnlyList<string> Codes() =>
        Active.Select(q => q.Code).Concat(RecentEvents.Select(e => e.Code)).Distinct(StringComparer.Ordinal).ToList();
}

public sealed record BugQuest(string Code, int Id, string? Title, string? Type, int Status, string StatusName, int Step, int[] Goals,
    int? QuestMap, int? StartNpc, int? FinishNpc, int? StepNpc, IReadOnlyList<BugObjective> StepObjectives);

public sealed record BugObjective(string Type, int? Npc, int[]? Npcs, int? Item, string? Target, int Count);

public sealed record BugQuestEvent(long AgoMs, string Kind, string Code, int Step, int Status, int[]? Goals);

public sealed record BugMapEvent(int Id, string Name, int Status, int Stage, int Step, int[] Goals, long AgeMs);

/// <summary>An NPC near the player; Source names where it comes from (a MapData row in tools/data/world-npcs.json, ...).</summary>
public sealed record BugSpawn(int Key, int NpcId, string Name, int Level, double X, double Y, double Z, double Heading, double Distance,
    bool Dead, bool Hostile, int Relationship, string Function, bool Seen, BugSpawnSource Source);

public sealed record BugSpawnSource(string Kind, string? File = null, int? Map = null, int? Index = null, uint? UniqueKey = null,
    double[]? Home = null, double? HomeYaw = null, int? OwnerKey = null);

public sealed record BugObject(int Key, int Env, string Name, string? EnvName, double X, double Y, double Z, double Yaw, double Distance,
    bool Shown, string File, int Map, int Index, int UniqueKey);

public sealed record BugPackets(int Capacity, int PreviewBytes, IReadOnlyList<BugPacket> Recent);

/// <summary>One message of the connection: time before the report, C(lient) or S(erver), opcode, name, length, body start.</summary>
public sealed record BugPacket(long AgoMs, string Dir, string Op, string? Name, int Length, string? Hex);

public sealed partial class GameSession
{
    /// <summary>Default radius of the nearby spawns in a report: 50 m (units are centimetres).</summary>
    public const double BugNearbyRadius = 5000;
    const int BugNearbyLimit = 120, BugObjectLimit = 60, QuestEventsKept = 40;

    /// <summary>The player's facing from the latest movement report (degrees, as the client sends it).</summary>
    double lastHeading;
    /// <summary>The latest quest accepts, steps, completions and rewards of this connection (oldest first, bounded).</summary>
    readonly Queue<(long Ms, string Kind, string Code, int Step, int Status, int[]? Goals)> questEvents = new();

    void NoteQuestEvent(string kind, string code, int step, int status, int[]? goals)
    {
        if (questEvents.Count >= QuestEventsKept) questEvents.Dequeue();
        questEvents.Enqueue((lastNow, kind, code, step, status, goals is null ? null : [.. goals]));
    }

    static string QuestStatusName(int status) => status switch
    {
        QuestLog.Incomplete => "incomplete",
        QuestLog.Complete => "complete",
        QuestLog.Rewarded => "rewarded",
        _ => status.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>The report context now. Call on this session's loop.</summary>
    public BugContext CaptureBugContext(long now, DateTimeOffset at, Guid? accountId, string? commit, Func<ushort, string?>? opcodeName = null,
        double radius = BugNearbyRadius)
    {
        var mapId = MapId;
        var at3 = position;
        BugLocation? location = null;
        if (mapId is { } map && at3 is { } p)
        {
            var area = Subzones.AreaContaining(map, p);
            location = new BugLocation(map, MapNames.Of(map, data), SharedMap?.Instance, Math.Round(p.X, 1), Math.Round(p.Y, 1), Math.Round(p.Z, 1),
                Math.Round(worldPlayer?.Heading ?? lastHeading, 2), area?.Subzone ?? Subzones.DefaultSubzone(map), area?.Name, MapNames.AreaAt(map, p));
        }
        var character = new BugCharacter(accountId, CharacterId, CharacterName, CharacterRace,
            CharacterRace switch { 1 => "Light", 2 => "Dark", _ => "?" }, className, level, StaffRole,
            resources?.Hp ?? 0, resources?.HpMax ?? 0, dead, inCombat);
        var runs = (CopyOwner ?? this).mapEventRuns;
        return new BugContext(at.ToString("O", CultureInfo.InvariantCulture), now, new BugServer(commit, options.WorldId, worldPlayer is not null),
            character, location, BugQuestState(now),
            runs.Select(r => new BugMapEvent(r.Id, r.Name, r.Status, r.StageIndex, r.StepIndex, [.. r.Goals], now - r.StartedMs)).ToList(),
            radius,
            mapId is { } m && at3 is { } near ? BugNearbySpawns(m, near, radius) : [],
            mapId is { } om && at3 is { } nearObj ? BugNearbyObjects(om, nearObj, radius) : [],
            new BugPackets(ConnectionPacketTrace.Capacity, ConnectionPacketTrace.PreviewBytes, PacketTrace.Snapshot().Select(e => new BugPacket(
                now - e.Ms, e.Outbound ? "S" : "C", "0x" + e.Opcode.ToString("x4", CultureInfo.InvariantCulture), opcodeName?.Invoke(e.Opcode), e.Length,
                e.Redacted ? "(redacted)" : e.Preview.Length == 0 ? null : Convert.ToHexString(e.Preview).ToLowerInvariant()
                    + (e.Length - 2 > e.Preview.Length ? "…" : ""))).ToList()));
    }

    BugQuests BugQuestState(long now)
    {
        if (questLog is not { } log) return new BugQuests([], [], []);
        BugQuest View(Aion.Application.Gameplay.QuestRecord r)
        {
            var quest = log.Book.Find(r.Code);
            var step = quest?.Steps.FirstOrDefault(s => s.Order == r.Step);
            return new BugQuest(r.Code, r.QuestId, quest?.Title, quest?.Type, r.Status, QuestStatusName(r.Status), r.Step, [.. r.Goals],
                quest?.Map, quest?.StartNpc, quest?.FinishNpc, step?.Npc,
                (step?.Objectives ?? []).Select(o => new BugObjective(o.Type, o.Npc, o.Npcs, o.Item, o.Target, o.Count)).ToList());
        }
        var active = log.All.Where(q => q.Status is QuestLog.Incomplete or QuestLog.Complete).OrderByDescending(q => q.Uid).Select(View).ToList();
        var events = questEvents.Reverse().Select(e => new BugQuestEvent(now - e.Ms, e.Kind, e.Code, e.Step, e.Status, e.Goals)).ToList();
        var rewarded = log.All.Where(q => q.Status == QuestLog.Rewarded).OrderByDescending(q => q.Uid).Take(10).Select(View).ToList();
        return new BugQuests(active, events, rewarded);
    }

    List<BugSpawn> BugNearbySpawns(int mapId, Vector3d at, double radius)
    {
        if (targets is null) return [];
        var spawns = data.Spawns(mapId);
        var keyBase = Combat.NpcKeyBase(mapId, spawns.Count);
        BugSpawnSource Source(Target t)
        {
            var index = t.Key - keyBase;
            if (t.OwnerKey != 0) return new BugSpawnSource("summon", OwnerKey: t.OwnerKey);
            if (t.Key == Combat.TrainingDummyKey) return new BugSpawnSource("training_dummy");
            if (CustomNpcDirectory.IsCustomKey(t.Key)) return new BugSpawnSource("custom_npc", File: "realm game.custom_npcs");
            if (index >= 0 && index < spawns.Count && spawns[index].NpcId == t.NpcId)
            {
                var row = spawns[index];
                return new BugSpawnSource("map_data", "tools/data/world-npcs.json", mapId, index, row.UniqueKey,
                    [.. row.Position.Select(v => Math.Round(v, 1))], Math.Round(row.Yaw, 2));
            }
            if (t.Key >= Kibelisks.FirstKey) return new BugSpawnSource("kibelisk");
            if (t.Key >= MapInstance.FirstStaffKey) return new BugSpawnSource("staff_spawn");
            return new BugSpawnSource("runtime");
        }
        var list = new List<BugSpawn>();
        foreach (var t in targets.Values)
        {
            if (t.CorpseRemoved && t.Dead) continue;
            var d = Math.Sqrt((t.Position.X - at.X) * (t.Position.X - at.X) + (t.Position.Y - at.Y) * (t.Position.Y - at.Y));
            if (d > radius) continue;
            list.Add(new BugSpawn(t.Key, t.NpcId, t.Name, t.Level, Math.Round(t.Position.X, 1), Math.Round(t.Position.Y, 1), Math.Round(t.Position.Z, 1),
                Math.Round(t.Direction, 2), Math.Round(d), t.Dead, t.Hostile, t.Relationship, t.Function, seenNpcs.Contains(t.Key), Source(t)));
        }
        return list.OrderBy(s => s.Distance).Take(BugNearbyLimit).ToList();
    }

    List<BugObject> BugNearbyObjects(int mapId, Vector3d at, double radius)
    {
        var objects = WorldObjects.ObjectsOn(mapId);
        var list = new List<BugObject>();
        for (var i = 0; i < objects.Count; i++)
        {
            var o = objects[i];
            if (o.Position.Length < 3) continue;
            var d = Math.Sqrt((o.Position[0] - at.X) * (o.Position[0] - at.X) + (o.Position[1] - at.Y) * (o.Position[1] - at.Y));
            if (d > radius) continue;
            list.Add(new BugObject(WorldObjects.FirstKey + i, o.Env, o.Name, o.EnvName, Math.Round(o.Position[0], 1), Math.Round(o.Position[1], 1),
                Math.Round(o.Position[2], 1), Math.Round(o.Yaw, 2), Math.Round(d), shownObjects.Contains(i), "tools/data/world-objects.json", mapId, i, o.UniqueKey));
        }
        return list.OrderBy(o => o.Distance).Take(BugObjectLimit).ToList();
    }
}
