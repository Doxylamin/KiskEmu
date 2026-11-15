using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>A point reward tier (RewardDungeon.dat: NeedPoint and its items).</summary>
public sealed record DailyRewardTier([property: JsonPropertyName("need")] int Need, [property: JsonPropertyName("items")] long[][] Items);

/// <summary>A buff of the daily's choice (DungeonBuff.dat): its id on 0x6105, its name, grade and abnormal (BuffValue1).</summary>
public sealed record DailyBuff(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("grade")] string Grade,
    [property: JsonPropertyName("abnormal")] int Abnormal);

/// <summary>A slot of the choice (DungeonBuffGroup.dat BuffSlot) and the buffs it may draw.</summary>
public sealed record DailyBuffSlot(
    [property: JsonPropertyName("slot")] int Slot,
    [property: JsonPropertyName("buffs")] DailyBuff[] Buffs);

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
    [property: JsonPropertyName("seconds")] int Seconds = 0,
    [property: JsonPropertyName("buff_slots")] DailyBuffSlot[]? BuffSlots = null,
    [property: JsonPropertyName("ready")] int Ready = 0,
    [property: JsonPropertyName("clear_bonus")] int ClearBonus = 0,
    [property: JsonPropertyName("min_level")] int MinLevel = 0)
{
    /// <summary>The dungeon offers a buff choice at the entry (Dungeon.dat DungeonBuffGroupName; OBSERVED 0x6105 in 900001).</summary>
    public bool HasBuffChoice => BuffSlots is { Length: > 0 };

    /// <summary>A buff of the choice by its DungeonBuff.dat id.</summary>
    public DailyBuff? Buff(int id) => BuffSlots?.SelectMany(s => s.Buffs).FirstOrDefault(b => b.Id == id);

    /// <summary>
    /// The three offered buffs, one per slot, none twice: OBSERVED 2026-11-15 (official, three entries) {1:10, 2:6, 3:4},
    /// {1:1004, 2:9, 3:1}, {1:1004, 2:7, 3:1006}. The draw is CANDIDATE (uniform over the slot's DungeonBuffGroup rows).
    /// </summary>
    public List<(int Slot, int BuffId)> DrawBuffs(Random rng)
    {
        var picked = new List<(int Slot, int BuffId)>();
        foreach (var slot in BuffSlots ?? [])
        {
            var free = slot.Buffs.Where(b => picked.All(p => p.BuffId != b.Id)).ToList();
            if (free.Count > 0) picked.Add((slot.Slot, free[rng.Next(free.Count)].Id));
        }
        return picked;
    }

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
