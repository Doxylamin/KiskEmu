using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// Skill specialisation parts (tools/data/specialized-skills.json: SpecializedSkill/Parts/Slot.dat; ported from addiboii's
/// fork). A mastery skill has five parts, each needing a parent skill level; its three slots open at skill level 8/12/20. The
/// child skill the client casts is the parent plus the equipped parts' digits ascending (OBSERVED rule; every child of the
/// table follows it). Stigma parts are not here: they follow the stigma's level (stigma-skills.json).
/// </summary>
public sealed class SpecializedSkillTables
{
    sealed record File(
        [property: JsonPropertyName("slot_levels")] int[] SlotLevels,
        [property: JsonPropertyName("stigma_slots")] int StigmaSlots,
        [property: JsonPropertyName("parts")] Dictionary<string, int[][]> Parts,
        [property: JsonPropertyName("children")] Dictionary<string, int[]> Children,
        [property: JsonPropertyName("chain_children")] Dictionary<string, int[]>? ChainChildren,
        [property: JsonPropertyName("skills")] Dictionary<string, SkillDefinition>? Skills);

    readonly File file;
    readonly Dictionary<int, (int Parent, int Level)> partInfo = [];
    readonly Dictionary<int, int> childParent = [];
    readonly Dictionary<int, HashSet<int>> childrenOf = [];

    SpecializedSkillTables(File file)
    {
        this.file = file;
        foreach (var (parent, parts) in file.Parts)
            foreach (var p in parts) partInfo[p[0]] = (int.Parse(parent, CultureInfo.InvariantCulture), p[1]);
        // A chain step's children (Skill.dat chain links of the children) count as that step's children.
        foreach (var (parent, kids) in file.Children.Concat(file.ChainChildren ?? []))
        {
            var id = int.Parse(parent, CultureInfo.InvariantCulture);
            var set = childrenOf.TryGetValue(id, out var s) ? s : childrenOf[id] = [];
            foreach (var kid in kids) { childParent[kid] = id; set.Add(kid); }
        }
    }

    static readonly Lazy<SpecializedSkillTables> embedded = new(() =>
    {
        using var stream = typeof(SpecializedSkillTables).Assembly.GetManifestResourceStream("specialized-skills.json")
            ?? throw new InvalidOperationException("Missing embedded game data specialized-skills.json");
        return new SpecializedSkillTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("specialized-skills.json"));
    });

    public static SpecializedSkillTables Embedded => embedded.Value;

    static string Key(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The parent level each of a mastery skill's slots opens at (Slot_1..3: 8, 12, 20).</summary>
    public IReadOnlyList<int> SlotLevels => file.SlotLevels;
    /// <summary>Does this skill have parts (a mastery parent of SpecializedSkillParts.dat)?</summary>
    public bool HasParts(int skillId) => file.Parts.ContainsKey(Key(skillId));
    /// <summary>The parent skill and the parent level a part needs, else null.</summary>
    public (int Parent, int Level)? Part(int partId) => partInfo.TryGetValue(partId, out var p) ? p : null;
    /// <summary>The parent of a child skill (SpecializedSkill.dat, or the chain step a chained child belongs to), else null.</summary>
    public int? ParentOf(int childId) => childParent.TryGetValue(childId, out var p) ? p : null;
    /// <summary>Is <paramref name="childId"/> a child of <paramref name="parent"/> (SpecializedSkill.dat, or a chain step's child)?</summary>
    public bool IsChild(int parent, int childId) => childrenOf.TryGetValue(parent, out var kids) && kids.Contains(childId);
    /// <summary>Cast data of a child class-skills.json does not carry (Skill.dat via the catalogue), else null.</summary>
    public SkillDefinition? Skill(int skillId) => file.Skills?.GetValueOrDefault(Key(skillId));

    /// <summary>The digits suffix of a set of parts (parts 30 and 40 → 340): the child is parent + suffix, 0 for no parts.</summary>
    public static int Suffix(int parent, IEnumerable<int> parts)
    {
        var suffix = 0;
        foreach (var d in parts.Select(p => (p - parent) / 10).Order()) suffix = suffix * 10 + d;
        return suffix * 10;
    }
}
