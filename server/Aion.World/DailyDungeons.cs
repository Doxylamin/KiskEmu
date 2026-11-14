using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>A point reward tier (RewardDungeon.dat: NeedPoint and its items).</summary>
public sealed record DailyRewardTier([property: JsonPropertyName("need")] int Need, [property: JsonPropertyName("items")] long[][] Items);

/// <summary>
/// A daily dungeon (Dungeon.dat DungeonType Daily; tools/daily_dungeon_data.py): the countdown's wave (WaveData.dat id of
/// DungeonWaveGroupName), the kill points (DungeonPoint.dat), the point rewards (RewardDungeon.dat groups RewardLight /
/// RewardDark) and Dungeon.dat ResurrectionType. <see cref="Seconds"/>: the countdown, OBSERVED in the official capture
/// (2026-11-12, 900001: 90 s); 0 where no capture says (no countdown then).
/// </summary>
public sealed record DailyDungeon(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("subtype")] string SubType,
    [property: JsonPropertyName("wave")] int Wave,
    [property: JsonPropertyName("show_points")] bool ShowPoints,
    [property: JsonPropertyName("resurrection")] string Resurrection,
    [property: JsonPropertyName("kill_points")] Dictionary<string, int> KillPoints,
    [property: JsonPropertyName("subtype_points")] Dictionary<string, int> SubtypePoints,
    [property: JsonPropertyName("reward_light")] DailyRewardTier[] RewardLight,
    [property: JsonPropertyName("reward_dark")] DailyRewardTier[] RewardDark,
    [property: JsonPropertyName("seconds")] int Seconds = 0)
{
    /// <summary>
    /// DungeonPoint.dat: a KillNpc row by the NPC's id, else the KillNpcSubType row of its subtype. OBSERVED (official
    /// 2026-11-12, 900001): +40 per NormalMonster_03..08 kill and +50 for NormalMonster_09, as the table says.
    /// </summary>
    public int KillPoint(int npcId, string? subType) =>
        KillPoints.TryGetValue(npcId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var points) ? points
        : subType is { Length: > 0 } && SubtypePoints.TryGetValue(subType, out var bySubtype) ? bySubtype : 0;

    /// <summary>
    /// The reward for the run's points: the highest tier whose NeedPoint they reach (OBSERVED, official 2026-11-12: 1730
    /// points paid 4300 CoinEnchant, the 1500 tier of Reward_Daily_Wave_Light). Below the lowest tier nothing (CANDIDATE).
    /// </summary>
    public List<(int ItemId, long Count)> Reward(int points, bool dark) =>
        ((dark ? RewardDark : RewardLight).Where(t => points >= t.Need).OrderByDescending(t => t.Need).FirstOrDefault()?.Items ?? [])
            .Where(i => i.Length >= 2).Select(i => ((int)i[0], i[1])).ToList();

    /// <summary>Dungeon.dat ResurrectionType Restart: the respawn (not the item's revive in place) leaves the dungeon.</summary>
    public bool RespawnLeaves => Resurrection == "Restart";

    sealed record DailyFile([property: JsonPropertyName("dungeons")] Dictionary<string, DailyDungeon> Dungeons);

    static readonly Lazy<IReadOnlyDictionary<int, DailyDungeon>> all = new(() =>
    {
        using var stream = typeof(DailyDungeon).Assembly.GetManifestResourceStream("daily-dungeons.json")
            ?? throw new InvalidOperationException("Missing embedded game data daily-dungeons.json");
        var file = JsonSerializer.Deserialize<DailyFile>(stream) ?? throw new InvalidOperationException("Empty daily-dungeons.json");
        return file.Dungeons.ToDictionary(e => int.Parse(e.Key, System.Globalization.CultureInfo.InvariantCulture), e => e.Value);
    });

    public static DailyDungeon? Of(int dungeonId) => all.Value.GetValueOrDefault(dungeonId);
}
