using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Custom NPCs (staff dashboard: NPCs; realm table game.custom_npcs, docs/CUSTOM_NPCS.md). OURS: the server places them as
// friendly NPCs from an official base template (look, level, talk prompt) at keys 880000-899999 (20 per NPC: one per
// placement), clear of map NPCs (2000-90000), players (100000+), staff spawns (600000+) and Kibelisks (900000+).
// A session whose client has the custom NPC patch pak sees the NPC's own template id (its custom name); the others see the
// base template. The dialog runs in GameSession.CustomNpcs.cs.

/// <summary>The custom NPCs one host places, with a version that changes whenever they do.</summary>
public sealed class CustomNpcDirectory
{
    public const int FirstKey = 880_000, KeysPerNpc = 20;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    sealed record State(long Version, IReadOnlyList<CustomNpc> Npcs, string Json);
    State state = new(0, [], "[]");

    public long Version => Volatile.Read(ref state).Version;
    public IReadOnlyList<CustomNpc> Npcs => Volatile.Read(ref state).Npcs;

    /// <summary>
    /// OURS until the launcher reports the pak (CANDIDATE, Part C): every client is taken to have the patch pak
    /// (Aion:CustomNpcs:AssumePatchedClients) or none has.
    /// </summary>
    public bool AssumePatchedClients { get; set; }

    /// <summary>Sets the NPCs (only the enabled ones are placed); the version changes when they differ. True when changed.</summary>
    public bool Publish(IReadOnlyList<CustomNpc> npcs)
    {
        var enabled = npcs.Where(n => n.Enabled && CustomNpcRules.IsCustom(n.TemplateId)).OrderBy(n => n.TemplateId).ToList();
        var json = JsonSerializer.Serialize(enabled, Json);
        var current = Volatile.Read(ref state);
        if (json == current.Json) return false;
        Volatile.Write(ref state, new State(current.Version + 1, enabled, json));
        return true;
    }

    public static bool IsCustomKey(int key) => key is >= FirstKey and < FirstKey + (CustomNpcRules.LastTemplateId - CustomNpcRules.FirstTemplateId + 1) * KeysPerNpc;

    /// <summary>The entity key of an NPC's placement (the same on every refresh, so the client's view stays consistent).</summary>
    public static int KeyOf(int templateId, int placement) => FirstKey + (templateId - CustomNpcRules.FirstTemplateId) * KeysPerNpc + placement;
}

/// <summary>Reads the custom NPCs (the realm's catalogue) into the host's directory every few seconds.</summary>
public static class CustomNpcWorld
{
    static readonly TimeSpan Every = TimeSpan.FromSeconds(10);

    /// <summary>Starts the refresh for a world server; nothing without a catalogue or a shared world.</summary>
    public static void Configure(ICustomNpcCatalog? catalog, SharedWorld? world, bool assumePatchedClients, Action<string>? log)
    {
        if (world is null) return;
        world.CustomNpcs.AssumePatchedClients = assumePatchedClients;
        if (catalog is null) return;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    if (world.CustomNpcs.Publish(await catalog.NpcsAsync()))
                        log?.Invoke($"Custom NPCs: {world.CustomNpcs.Npcs.Count} enabled (version {world.CustomNpcs.Version})");
                }
                catch (Exception error)
                {
                    log?.Invoke($"Custom NPCs unavailable: {error.Message}");
                }
                await Task.Delay(Every);
            }
        });
    }
}

public sealed partial class MapInstance
{
    long customNpcVersion;

    /// <summary>
    /// Places, moves and removes this map's custom NPCs when the directory changed (shared maps only). Call under the world lock.
    /// Changed or removed NPCs leave the view of everyone who saw them; the visibility check shows them again (new look and name).
    /// </summary>
    public void SyncCustomNpcs(CustomNpcDirectory directory, GameData data)
    {
        if (Instance != 0 || customNpcVersion == directory.Version) return;
        customNpcVersion = directory.Version;
        var wanted = new Dictionary<int, Target>();
        foreach (var npc in directory.Npcs)
            for (var i = 0; i < npc.Placements.Count && i < CustomNpcDirectory.KeysPerNpc; i++)
            {
                var at = npc.Placements[i];
                if (at.MapId != MapId) continue;
                var key = CustomNpcDirectory.KeyOf(npc.TemplateId, i);
                if (data.NpcFromTemplate(npc.BaseTemplateId, npc.DisplayName, [at.X, at.Y, at.Z], at.Yaw) is { } t)
                    wanted[key] = new Target
                    {
                        Key = key, Name = t.Name, Hp = Combat.HpFor(t), HpMax = Combat.HpFor(t), Position = new Vector3d(at.X, at.Y, at.Z),
                        NpcId = t.NpcId, Level = t.Level, Direction = at.Yaw, Relationship = t.Relationship, Aggressive = false,
                        WalkSpeed = t.WalkSpeed, RunSpeed = t.RunSpeed, Radius = t.Radius, Function = t.Function, NoRotation = t.NoRotation,
                        Air = t.Air, SubType = t.SubType, Grade = t.Grade, Custom = npc,
                    };
            }
        foreach (var key in Targets.Keys.Where(CustomNpcDirectory.IsCustomKey).ToList())
        {
            var old = Targets[key];
            if (!old.CorpseRemoved) Forget(key);
            if (!wanted.ContainsKey(key))
            {
                // Targets are never deleted (keys stay valid for anything holding one): it leaves and never respawns.
                old.Hp = 0;
                old.CorpseRemoved = true;
                RespawnAt.Remove(key);
            }
        }
        foreach (var (key, target) in wanted) Targets[key] = target;
        SpatialGrid.InvalidateNpcs(Targets);
    }

    /// <summary>Everyone who sees the NPC stops seeing it (out-of-view removal).</summary>
    void Forget(int key)
    {
        foreach (var player in Players.Values)
            if (player.Session.ForgetNpc(key) is { } despawn) player.Send([despawn]);
    }
}
