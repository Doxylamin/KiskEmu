using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// Wing.dat / WingEnchantEffect.dat (tools/data/wings.json): stats while a wing is equipped and stats for owning it,
/// per enchant level (+0..+10; wings without enchant rows have only +0).
/// </summary>
public sealed class WingTables
{
    public sealed record Wing(int Id, string Key, string Race, string Name, string Rarity, bool Shown,
        IReadOnlyDictionary<string, int> Equip, IReadOnlyList<IReadOnlyDictionary<string, int>> OwnedByLevel)
    {
        public int MaxEnchant => Math.Max(0, OwnedByLevel.Count - 1);

        /// <summary>Owned stats at an enchant level (clamped to the levels the wing has).</summary>
        public IReadOnlyDictionary<string, int> Owned(int enchant) =>
            OwnedByLevel.Count == 0 ? new Dictionary<string, int>() : OwnedByLevel[Math.Clamp(enchant, 0, OwnedByLevel.Count - 1)];
    }

    sealed record WingRow(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("race")] string Race,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("rarity")] string Rarity,
        [property: JsonPropertyName("shown")] bool Shown,
        [property: JsonPropertyName("equip")] Dictionary<string, int>? Equip,
        [property: JsonPropertyName("owned_by_level")] List<Dictionary<string, int>>? OwnedByLevel);

    sealed record File([property: JsonPropertyName("wings")] List<WingRow> Wings);

    readonly Dictionary<int, Wing> wings;

    WingTables(File file) => wings = file.Wings.ToDictionary(w => w.Id, w => new Wing(w.Id, w.Key, w.Race, w.Name, w.Rarity, w.Shown,
        w.Equip ?? [], (w.OwnedByLevel ?? []).Select(l => (IReadOnlyDictionary<string, int>)l).ToList()));

    static readonly Lazy<WingTables> embedded = new(() =>
    {
        using var stream = typeof(WingTables).Assembly.GetManifestResourceStream("wings.json")
            ?? throw new InvalidOperationException("Missing embedded game data wings.json");
        return new WingTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidOperationException("Empty wings.json"));
    });

    public static WingTables Embedded => embedded.Value;

    public IReadOnlyCollection<Wing> All => wings.Values;

    public Wing? Of(int id) => wings.GetValueOrDefault(id);

    /// <summary>Equipped-wing stat (0 for no wing / unknown id).</summary>
    public int EquipStat(int wingId, string stat) => Of(wingId)?.Equip.GetValueOrDefault(stat) ?? 0;

    /// <summary>Owned-wing stat at an enchant level.</summary>
    public int OwnedStat(int wingId, int enchant, string stat) => Of(wingId)?.Owned(enchant).GetValueOrDefault(stat) ?? 0;
}
