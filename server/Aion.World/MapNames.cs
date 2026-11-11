using System.Text;
using System.Text.RegularExpressions;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Names for maps and areas a player reads (the account site's character page). The client's own display names are
/// localized strings this server does not load, so the main maps carry region names here and an area is its subzone
/// name made readable.
/// </summary>
public static partial class MapNames
{
    /// <summary>
    /// CANDIDATE region names of the main maps, from the region words in their teleport point names (map-points.json:
    /// QuestStep_Teleport_Poeta_* on 1000, Arrive_Verteron_Portal on 1010, Arrive_Eltnen_Portal on 1011,
    /// QuestStep_Teleport_Ishalgen_* on 1100, QuestStep_Teleport_Altgard_* on 1110, Tel_Enter_Morheim on 1111,
    /// Abyss_Reshanta_* on 20–23) and the Light/Dark starting maps named in GameSession.Instances.cs. Not the client's
    /// localized names.
    /// </summary>
    static readonly Dictionary<int, string> Regions = new()
    {
        [1000] = "Poeta",
        [1010] = "Verteron",
        [1011] = "Eltnen",
        [1100] = "Ishalgen",
        [1110] = "Altgard",
        [1111] = "Morheim",
        [20] = "Reshanta",
        [21] = "Reshanta",
        [22] = "Reshanta",
        [23] = "Reshanta",
    };

    /// <summary>The map's name: its region, the region of the map it is a layer of (Map.dat BaseMapId), else "Map {id}".</summary>
    public static string Of(int mapId, GameData? data = null)
    {
        if (Regions.TryGetValue(mapId, out var region)) return region;
        var baseMap = (data ?? GameData.Embedded).GeometryMap(mapId);
        return baseMap != mapId && Regions.TryGetValue(baseMap, out var parent) ? parent : $"Map {mapId}";
    }

    /// <summary>The subzone at a position, readable ("L_A_VerteronFortressRuin_Subzone" → "Verteron Fortress Ruin"), else null.</summary>
    public static string? AreaAt(int mapId, Vector3d at) => Subzones.AreaAt(mapId, at) is { } name ? Readable(name) : null;

    /// <summary>A subzone name without its race prefix and suffix, in words.</summary>
    public static string? Readable(string subzone)
    {
        var name = Prefix().Replace(Suffix().Replace(subzone, ""), "");
        var words = new StringBuilder();
        foreach (var part in name.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            if (words.Length > 0) words.Append(' ');
            words.Append(CamelBreak().Replace(part, " "));
        }
        return words.Length == 0 ? null : words.ToString();
    }

    [GeneratedRegex("_Subzone$", RegexOptions.IgnoreCase)]
    private static partial Regex Suffix();
    [GeneratedRegex("^[LD][0-9]?_(?:[AB]_)?")]
    private static partial Regex Prefix();
    [GeneratedRegex("(?<=[a-z])(?=[A-Z])")]
    private static partial Regex CamelBreak();
}
