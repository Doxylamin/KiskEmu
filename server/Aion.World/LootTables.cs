using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// Monster and env-object loot from tools/data/loot.json (tools/loot_data.py: RewardData, RewardItem,
/// RewardPackage, RewardPackageItem, plus reward_manual_* env tables keyed by EnvObjData id in envs).
/// Every table entry is an independent chance of 1e8 (items with a count range, packages that pick one item by weight).
/// CANDIDATE roll reading (tables often sum above 1e8). Prologue monsters (Poeta) have no tables.
/// </summary>
public sealed class LootTables
{
    sealed record Table(
        [property: JsonPropertyName("race")] int Race,
        [property: JsonPropertyName("items")] long[][] Items,
        [property: JsonPropertyName("packages")] JsonElement[][] Packages);

    sealed record LootFile(
        [property: JsonPropertyName("npcs")] Dictionary<string, string[]> Npcs,
        [property: JsonPropertyName("envs")] Dictionary<string, string[]>? Envs,
        [property: JsonPropertyName("tables")] Dictionary<string, Table> Tables,
        [property: JsonPropertyName("packages")] Dictionary<string, long[][]> Packages);

    readonly LootFile file;
    LootTables(LootFile file) => this.file = file;

    static readonly Lazy<LootTables> embedded = new(() =>
    {
        using var stream = typeof(LootTables).Assembly.GetManifestResourceStream("loot.json")
            ?? throw new InvalidOperationException("Missing embedded game data loot.json");
        return new LootTables(JsonSerializer.Deserialize<LootFile>(stream) ?? throw new InvalidOperationException("Empty loot.json"));
    });

    public static LootTables Embedded => embedded.Value;

    public bool HasLoot(int npcId) => file.Npcs.ContainsKey(npcId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public bool HasEnvLoot(int envId) => ResolveEnvLootTableNames(envId, null, null).Length > 0;

    public bool HasEnvLoot(WorldObject obj) => ResolveEnvLootTableNames(obj.Env, obj.Name, obj.EnvName).Length > 0;

    /// <summary>
    /// RewardData tables named *_participant_all (90 field bosses and field elites): the personal reward of every player
    /// who fought the monster. Their 100% item is the bound chest (e.g. reward_FieldBoss_Named_WH4_2400800_participant_all
    /// -> 533700375, RewardBox Box_FieldBoss_A_48_b). The name says who gets it; the delivery is CANDIDATE.
    /// </summary>
    public const string ParticipantSuffix = "_participant_all";

    static bool IsParticipant(string table) => table.EndsWith(ParticipantSuffix, StringComparison.Ordinal);

    string[] NpcTables(int npcId) => file.Npcs.GetValueOrDefault(npcId.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? [];

    /// <summary>The items one kill drops for a character of a race (1 Light, 2 Dark), merged by item: every table
    /// except the participant ones (<see cref="RollParticipant"/>).</summary>
    public List<(int ItemId, long Count)> Roll(int npcId, int race, Random rng) =>
        RollTables(NpcTables(npcId).Where(t => !IsParticipant(t)).ToArray(), race, rng);

    /// <summary>Whether a monster has a participant reward table.</summary>
    public bool HasParticipantLoot(int npcId) => NpcTables(npcId).Any(IsParticipant);

    /// <summary>One participant's roll of the monster's *_participant_all tables (empty when it has none: no fallback).</summary>
    public List<(int ItemId, long Count)> RollParticipant(int npcId, int race, Random rng) =>
        RollTables(NpcTables(npcId).Where(IsParticipant).ToArray(), race, rng);

    /// <summary>Items granted when an env object interaction completes (reward_manual_* tables).</summary>
    public List<(int ItemId, long Count)> RollEnv(int envId, int race, Random rng) =>
        RollTables(ResolveEnvLootTableNames(envId, null, null), race, rng);

    /// <summary>Items granted for a placed env object (EnvObjData id, spawner name, optional EnvObjData name).</summary>
    public List<(int ItemId, long Count)> RollEnv(WorldObject obj, int race, Random rng) =>
        RollTables(ResolveEnvLootTableNames(obj.Env, obj.Name, obj.EnvName), race, rng);

    /// <summary>
    /// Reward table names for an env interaction: loot.json envs, then reward_manual_{EnvObjData name}, reward_manual_{spawner}
    /// and the field-treasure tables named by env id (from A2Core-ChanAge, Artezianec).
    /// </summary>
    public string[] ResolveEnvLootTableNames(int envId, string? spawnerName, string? envDataName)
    {
        var names = new List<string>();
        void TryAdd(string? table)
        {
            if (string.IsNullOrEmpty(table) || !file.Tables.ContainsKey(table) || names.Contains(table)) return;
            names.Add(table);
        }
        foreach (var table in file.Envs?.GetValueOrDefault(envId.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? [])
            TryAdd(table);
        if (!string.IsNullOrEmpty(envDataName)) TryAdd($"reward_manual_{envDataName}");
        if (!string.IsNullOrEmpty(spawnerName)) TryAdd($"reward_manual_{spawnerName}");
        TryAdd($"reward_manual_FieldEvent_L_TreasureBox_{envId}");
        TryAdd($"reward_manual_FieldEvent_D_TreasureBox_{envId}");
        return names.ToArray();
    }

    /// <summary>Whether loot from these tables uses the chest up (field/dungeon treasure, not reusable quest props).</summary>
    public static bool TreasureChestConsumes(string[] tableNames) =>
        tableNames.Any(n => n.Contains("TreasureBox", StringComparison.Ordinal) || n.Contains("Dungeon_Seal", StringComparison.Ordinal));

    /// <summary>Whether any reward_manual table wired to this env can grant the item (CollectItem rescue gating).</summary>
    public bool EnvCanGrantItem(int envId, int itemId, int race) =>
        TableNamesGrantItem(ResolveEnvLootTableNames(envId, null, null), itemId, race);

    /// <summary>Whether reward_manual_{EnvObjData.Name} lists the item (same tables as <see cref="RollEnv"/>).</summary>
    public bool ManualEnvTableGrantsItem(string envObjDataName, int itemId, int race) =>
        TableNamesGrantItem(ResolveEnvLootTableNames(0, null, envObjDataName), itemId, race);

    bool TableNamesGrantItem(string[]? tableNames, int itemId, int race)
    {
        foreach (var name in tableNames ?? [])
        {
            if (!file.Tables.TryGetValue(name, out var table) || table.Race != 0 && table.Race != race) continue;
            foreach (var item in table.Items)
                if (item.Length >= 1 && item[0] == itemId) return true;
            foreach (var package in table.Packages)
            {
                if (package.Length < 1 || !file.Packages.TryGetValue(package[0].GetString()!, out var entries)) continue;
                foreach (var e in entries)
                    if (e.Length >= 1 && e[0] == itemId) return true;
            }
        }
        return false;
    }

    List<(int ItemId, long Count)> RollTables(string[]? tableNames, int race, Random rng)
    {
        var drops = new Dictionary<int, long>();
        void Add(long item, long count) { if (count > 0) drops[(int)item] = drops.GetValueOrDefault((int)item) + count; }
        long Count(long min, long max) => min >= max ? min : min + (long)(rng.NextDouble() * (max - min + 1));
        bool Hit(long chance) => rng.NextDouble() * 100_000_000 < chance;
        foreach (var name in tableNames ?? [])
        {
            if (!file.Tables.TryGetValue(name, out var table) || table.Race != 0 && table.Race != race) continue;
            foreach (var item in table.Items)
                if (item.Length >= 4 && Hit(item[1])) Add(item[0], Count(item[2], item[3]));
            foreach (var package in table.Packages)
            {
                if (package.Length < 3 || !Hit(package[1].GetInt64()) || !file.Packages.TryGetValue(package[0].GetString()!, out var entries) || entries.Length == 0) continue;
                for (var n = 0; n < Math.Max(1, package[2].GetInt32()); n++)
                {
                    var pick = rng.NextDouble() * entries.Sum(e => e[1]);
                    foreach (var e in entries)
                    {
                        if ((pick -= e[1]) >= 0) continue;
                        Add(e[0], Count(e[2], e[3]));
                        break;
                    }
                }
            }
        }
        return drops.Select(d => (d.Key, d.Value)).ToList();
    }
}
