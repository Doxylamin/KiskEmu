using System.Text.RegularExpressions;
using Aion.Core.Accounts;

namespace Aion.Application.Gameplay;

/// <summary>Where a custom NPC stands: map, position (cm) and facing (degrees).</summary>
public sealed record CustomNpcPlacement(int MapId, double X, double Y, double Z, double Yaw);

/// <summary>
/// When a dialog node applies; every set field must hold. Flags are the character's own for this NPC. Race 0 both, 1 Light,
/// 2 Dark. ItemId with ItemCount: the character holds at least that many in the bag.
/// </summary>
public sealed record CustomNpcCondition(int MinLevel = 0, int MaxLevel = 0, string? Flag = null, string? NoFlag = null,
    int ItemId = 0, long ItemCount = 0, int Race = 0);

/// <summary>
/// What happens when a node's dialog ends. Type: give_item (ItemId, Count), give_kina (Count), set_flag / clear_flag (Flag),
/// notice (Text), teleport (MapId, X, Y, Z).
/// </summary>
public sealed record CustomNpcAction(string Type, int ItemId = 0, long Count = 0, string? Flag = null, string? Text = null,
    int MapId = 0, double X = 0, double Y = 0, double Z = 0);

/// <summary>
/// One dialog: its lines (locale → text; en-US required) play one after another, the player continuing with the interaction
/// key. The first node whose condition holds is the one an interaction plays; its actions run when the dialog ends.
/// </summary>
public sealed record CustomNpcNode(string Key, IReadOnlyList<IReadOnlyDictionary<string, string>> Lines, CustomNpcCondition? When,
    IReadOnlyList<CustomNpcAction> Actions);

/// <summary>
/// A server-placed NPC that talks (realm table game.custom_npcs, migration 102). TemplateId is its own NpcData row
/// (2999000-2999999) that the client patch pak adds with the custom name and dialog rows; BaseTemplateId is the official
/// NpcData row it copies (look, and what players without the pak see).
/// </summary>
public sealed record CustomNpc(int TemplateId, string Slug, int BaseTemplateId, IReadOnlyDictionary<string, string> Name,
    IReadOnlyList<CustomNpcPlacement> Placements, IReadOnlyList<CustomNpcNode> Nodes, bool Enabled, DateTimeOffset UpdatedAt)
{
    /// <summary>The English name (the fallback text without the patch pak).</summary>
    public string DisplayName => Name.GetValueOrDefault(CustomNpcRules.DefaultLocale) ?? Slug;
}

/// <summary>The enabled custom NPCs (cached by the implementations; a world server reads them over the realm link).</summary>
public interface ICustomNpcCatalog
{
    Task<IReadOnlyList<CustomNpc>> NpcsAsync(CancellationToken ct = default);
}

/// <summary>The realm's custom NPCs: staff list them, admins create, change and remove them (audited).</summary>
public interface ICustomNpcAdmin : ICustomNpcCatalog
{
    /// <summary>Creates (TemplateId 0: the next free id) or replaces an NPC; throws GameplayException with a CustomNpcRules code.</summary>
    Task<CustomNpc> SaveAsync(VerifiedAccount actor, CustomNpc npc, CancellationToken ct = default);
    Task<bool> DeleteAsync(VerifiedAccount actor, int templateId, CancellationToken ct = default);
    /// <summary>All NPCs, disabled ones too, for staff.</summary>
    Task<IReadOnlyList<CustomNpc>> ListAsync(VerifiedAccount actor, CancellationToken ct = default);
}

public static partial class CustomNpcRules
{
    /// <summary>Custom NpcData ids: above every official one (the client's highest is 2990928).</summary>
    public const int FirstTemplateId = 2_999_000, LastTemplateId = 2_999_999;
    /// <summary>Official NpcData ids a custom NPC may copy.</summary>
    public const int FirstOfficialTemplateId = 2_000_000, LastOfficialTemplateId = 2_998_999;
    public const int MaxNameLength = 40, MaxLineLength = 500, MaxLines = 20, MaxNodes = 20, MaxActions = 10, MaxPlacements = 20;
    public const long MaxItemCount = 1_000_000, MaxKina = 1_000_000_000;
    public const string DefaultLocale = "en-US";
    public static readonly IReadOnlyList<string> ActionTypes = ["give_item", "give_kina", "set_flag", "clear_flag", "notice", "teleport"];

    [GeneratedRegex("^[A-Za-z0-9]{1,32}$")] private static partial Regex SlugPattern();
    [GeneratedRegex("^[A-Za-z0-9]{1,24}$")] private static partial Regex NodeKeyPattern();
    [GeneratedRegex("^[A-Za-z0-9_]{1,32}$")] private static partial Regex FlagPattern();
    [GeneratedRegex("^[a-z]{2}-[A-Z]{2}$")] private static partial Regex LocalePattern();

    public static bool IsCustom(int templateId) => templateId is >= FirstTemplateId and <= LastTemplateId;
    public static bool IsSlug(string? slug) => slug is not null && SlugPattern().IsMatch(slug);
    public static bool IsFlag(string? flag) => flag is not null && FlagPattern().IsMatch(flag);

    /// <summary>The NpcTalk row name the patch pak gives a node (the client tool's CustomNpcNames.TalkName).</summary>
    public static string TalkName(string slug, string nodeKey) => $"KISK_Npc_{slug}_{nodeKey}";

    /// <summary>The error code of an invalid NPC, or null. The world checks that the base template exists.</summary>
    public static string? Validate(CustomNpc npc)
    {
        if (npc.TemplateId != 0 && !IsCustom(npc.TemplateId)) return "invalid_template_id";
        if (!IsSlug(npc.Slug)) return "invalid_slug";
        if (npc.BaseTemplateId is < FirstOfficialTemplateId or > LastOfficialTemplateId) return "invalid_base_template";
        if (!ValidTexts(npc.Name, MaxNameLength)) return "invalid_name";
        if (npc.Placements.Count > MaxPlacements || npc.Placements.Any(p => p.MapId <= 0 || !Finite(p.X, p.Y, p.Z, p.Yaw))) return "invalid_placement";
        if (npc.Nodes.Count is 0 or > MaxNodes) return "invalid_nodes";
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in npc.Nodes)
        {
            if (!NodeKeyPattern().IsMatch(node.Key ?? "") || !keys.Add(node.Key!)) return "invalid_node_key";
            if (node.Lines.Count is 0 or > MaxLines || node.Lines.Any(l => !ValidTexts(l, MaxLineLength))) return "invalid_lines";
            if (node.When is { } w && (w.MinLevel is < 0 or > 100 || w.MaxLevel is < 0 or > 100 || w.MaxLevel != 0 && w.MaxLevel < w.MinLevel
                || w.Flag is not null && !IsFlag(w.Flag) || w.NoFlag is not null && !IsFlag(w.NoFlag)
                || w.ItemId < 0 || w.ItemCount < 0 || w.ItemCount > MaxItemCount || (w.ItemId == 0) != (w.ItemCount == 0) || w.Race is < 0 or > 2))
                return "invalid_condition";
            if (node.Actions.Count > MaxActions || node.Actions.Any(a => !ValidAction(a))) return "invalid_action";
        }
        return null;
    }

    static bool ValidAction(CustomNpcAction a) => a.Type switch
    {
        // Quna is the realm's account wallet: an NPC cannot hand it out.
        "give_item" => a.ItemId > 0 && a.ItemId != QunaWalletRules.ItemId && a.Count is >= 1 and <= MaxItemCount,
        "give_kina" => a.Count is >= 1 and <= MaxKina,
        "set_flag" or "clear_flag" => IsFlag(a.Flag),
        "notice" => a.Text is { Length: > 0 and <= 200 } && a.Text.Trim().Length > 0,
        "teleport" => a.MapId > 0 && Finite(a.X, a.Y, a.Z),
        _ => false,
    };

    static bool ValidTexts(IReadOnlyDictionary<string, string>? texts, int max) =>
        texts is not null && texts.TryGetValue(DefaultLocale, out var en) && en.Trim().Length > 0
        && texts.All(t => LocalePattern().IsMatch(t.Key) && t.Value.Length <= max && !t.Value.Contains('\0'));

    static bool Finite(params double[] values) => values.All(double.IsFinite);

    /// <summary>A text in the locale, else English.</summary>
    public static string Text(IReadOnlyDictionary<string, string> texts, string? locale) =>
        locale is not null && texts.TryGetValue(locale, out var t) && t.Trim().Length > 0 ? t : texts.GetValueOrDefault(DefaultLocale) ?? "";

    /// <summary>
    /// The client tool's input (kisk-client-tool custom-npcs --definitions): every NPC's template ids, names and one talk per
    /// dialog node. The same JSON shape the tool reads (camelCase).
    /// </summary>
    public static object Export(IEnumerable<CustomNpc> npcs) => new
    {
        npcs = npcs.OrderBy(n => n.TemplateId).Select(n => new
        {
            slug = n.Slug,
            templateId = n.TemplateId,
            baseTemplateId = n.BaseTemplateId,
            name = n.Name,
            talks = n.Nodes.Select(node => new { key = node.Key, lines = node.Lines }),
        }),
    };
}
