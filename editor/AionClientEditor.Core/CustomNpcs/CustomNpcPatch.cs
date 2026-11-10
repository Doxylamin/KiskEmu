using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AionClientEditor.Editing;
using CUE4Parse.GameTypes.Aion2.Objects;

namespace AionClientEditor.CustomNpcs;

/// <summary>Custom NPC texts for the client patch pak, as exported from the staff dashboard (custom-npcs.json).</summary>
public sealed class CustomNpcDefinitions
{
    public List<CustomNpcDefinition> Npcs { get; set; } = [];

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static CustomNpcDefinitions Parse(string json) =>
        JsonSerializer.Deserialize<CustomNpcDefinitions>(json, Json) ?? throw new FormatException("Empty definitions file");

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

public sealed class CustomNpcDefinition
{
    public string Slug { get; set; } = "";
    /// <summary>New NpcData id in the custom range (<see cref="CustomNpcPatch.TemplateIdMin"/>..Max).</summary>
    public long TemplateId { get; set; }
    /// <summary>Official NpcData id to copy (look, size, behaviour). Players without the pak see this NPC.</summary>
    public long BaseTemplateId { get; set; }
    /// <summary>Display name per locale (en-US required; other locales fall back to it).</summary>
    public Dictionary<string, string> Name { get; set; } = [];
    public List<CustomNpcTalk> Talks { get; set; } = [];
}

public sealed class CustomNpcTalk
{
    /// <summary>Talk key, unique per NPC; the client talk name is <c>KISK_Npc_{slug}_{key}</c>.</summary>
    public string Key { get; set; } = "";
    /// <summary>One entry per line ("continue" step); each maps locale to text.</summary>
    public List<Dictionary<string, string>> Lines { get; set; } = [];
}

/// <summary>Names and keys the pak gives each custom NPC; shared with the server so both sides agree.</summary>
public static class CustomNpcNames
{
    public const string DefaultLocale = "en-US";

    public static string NpcName(string slug) => $"N_KISK_{slug}";
    public static string NameStringKey(string slug) => $"STR_KISK_NPC_{slug}";
    public static string NameBodyKey(string slug) => $"String_STR_KISK_NPC_{slug}_body";
    public static string TalkName(string slug, string talkKey) => $"KISK_Npc_{slug}_{talkKey}";
    public static string LineKey(string slug, string talkKey, int order) => $"NpcTalk_KISK_{slug}_{talkKey}_{order}_cv_text";
}

/// <summary>Result of applying definitions: what the pak contains, for custom-npcs.lock.json.</summary>
public sealed record CustomNpcPatchSummary(IReadOnlyList<CustomNpcPatchSummary.Npc> Npcs)
{
    public sealed record Npc(string Slug, long TemplateId, long BaseTemplateId, string NpcName, IReadOnlyList<Talk> Talks);
    public sealed record Talk(string Key, string TalkName, int Lines, long FirstRowId);
}

/// <summary>
/// Adds custom NPCs to loaded client tables (OBSERVED layout of the supported build):
/// <list type="bullet">
/// <item>NpcData: a copy of the base NPC with the new id, <c>Name = N_KISK_{slug}</c> and
/// <c>Desc.Key = STR_KISK_NPC_{slug}</c> (the nameplate text).</item>
/// <item>String: a row <c>STR_KISK_NPC_{slug}</c> whose <c>Body.Key</c> is the L10N key holding the name.</item>
/// <item>NpcTalk: one row per line, sharing the talk <c>Name</c>, numbered by <c>Order</c>, spoken by the NPC
/// (<c>SpeakerType Npc</c>, <c>SpeakerValue = N_KISK_{slug}</c>), text in <c>ChatDescription.Key</c>.</item>
/// <item>L10NString (every locale and copy): the name and line texts.</item>
/// </list>
/// </summary>
public static class CustomNpcPatch
{
    public const long TemplateIdMin = 2_999_000;
    public const long TemplateIdMax = 2_999_999;
    /// <summary>NpcTalk row ids for custom lines (official ids end near 1.11e9; int32 max is 2.147e9).</summary>
    public const long TalkRowIdBase = 1_999_000_000;
    /// <summary>Official full-screen quest talk line used as the row template (HQ1101010_00_Accept, line 1).</summary>
    public const long DefaultTalkTemplateRowId = 110_101_001;

    static readonly Regex SlugPattern = new("^[A-Za-z0-9]{1,32}$", RegexOptions.CultureInvariant);
    static readonly Regex TalkKeyPattern = new("^[A-Za-z0-9]{1,24}$", RegexOptions.CultureInvariant);
    public const int MaxTextLength = 500;
    public const int MaxNameLength = 40;
    public const int MaxLinesPerTalk = 20;

    public static IReadOnlyList<string> Validate(CustomNpcDefinitions definitions)
    {
        var errors = new List<string>();
        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<long>();
        foreach (var npc in definitions.Npcs)
        {
            var at = $"NPC '{npc.Slug}'";
            if (!SlugPattern.IsMatch(npc.Slug)) errors.Add($"{at}: slug must be 1-32 letters or digits");
            if (!slugs.Add(npc.Slug)) errors.Add($"{at}: duplicate slug");
            if (npc.TemplateId is < TemplateIdMin or > TemplateIdMax)
                errors.Add($"{at}: templateId must be {TemplateIdMin}-{TemplateIdMax}");
            if (!ids.Add(npc.TemplateId)) errors.Add($"{at}: duplicate templateId {npc.TemplateId}");
            if (npc.BaseTemplateId is <= 0 or (>= TemplateIdMin and <= TemplateIdMax))
                errors.Add($"{at}: baseTemplateId must be an official NpcData id");
            CheckTexts(npc.Name, MaxNameLength, $"{at} name", errors);

            var talkKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var talk in npc.Talks)
            {
                var talkAt = $"{at} talk '{talk.Key}'";
                if (!TalkKeyPattern.IsMatch(talk.Key)) errors.Add($"{talkAt}: key must be 1-24 letters or digits");
                if (!talkKeys.Add(talk.Key)) errors.Add($"{talkAt}: duplicate key");
                if (talk.Lines.Count is 0 or > MaxLinesPerTalk) errors.Add($"{talkAt}: needs 1-{MaxLinesPerTalk} lines");
                for (var i = 0; i < talk.Lines.Count; i++)
                    CheckTexts(talk.Lines[i], MaxTextLength, $"{talkAt} line {i + 1}", errors);
            }
        }
        return errors;
    }

    static void CheckTexts(Dictionary<string, string> texts, int maxLength, string at, List<string> errors)
    {
        if (!texts.TryGetValue(CustomNpcNames.DefaultLocale, out var main) || string.IsNullOrWhiteSpace(main))
            errors.Add($"{at}: {CustomNpcNames.DefaultLocale} text is required");
        foreach (var (locale, text) in texts)
        {
            if (text.Length > maxLength) errors.Add($"{at} ({locale}): longer than {maxLength} characters");
            if (text.Contains('\0')) errors.Add($"{at} ({locale}): contains a null character");
        }
    }

    /// <summary>Applies the definitions to the tables in place. <paramref name="l10n"/> maps each L10NString path to its file.</summary>
    public static CustomNpcPatchSummary Apply(CustomNpcDefinitions definitions, FAion2DataTableFile npcData,
        FAion2DataTableFile npcTalk, FAion2DataTableFile strings, IReadOnlyDictionary<string, FAion2L10NFile> l10n,
        long talkTemplateRowId = DefaultTalkTemplateRowId)
    {
        var errors = Validate(definitions);
        if (errors.Count > 0) throw new InvalidOperationException("Invalid definitions:\n" + string.Join("\n", errors));
        if (DataTableRows.FindRow(npcTalk, talkTemplateRowId) is null)
            throw new KeyNotFoundException($"NpcTalk template row {talkTemplateRowId} not found");

        var nextTalkRowId = TalkRowIdBase;
        var summary = new List<CustomNpcPatchSummary.Npc>();
        foreach (var npc in definitions.Npcs.OrderBy(n => n.TemplateId))
        {
            var npcName = CustomNpcNames.NpcName(npc.Slug);
            var baseRow = DataTableRows.FindRow(npcData, npc.BaseTemplateId)
                ?? throw new KeyNotFoundException($"Base NpcData row {npc.BaseTemplateId} not found");
            var baseNameKey = DataTableRows.GetPath(baseRow, "Desc.Key")
                ?? throw new InvalidOperationException($"Base NpcData row {npc.BaseTemplateId} has no Desc.Key");

            DataTableRows.AppendRow(npcData, npc.TemplateId, npc.BaseTemplateId, new Dictionary<string, string>
            {
                ["Name"] = npcName,
                ["Desc.Key"] = CustomNpcNames.NameStringKey(npc.Slug),
            });
            DataTableRows.AppendRowByName(strings, CustomNpcNames.NameStringKey(npc.Slug), baseNameKey,
                new Dictionary<string, string> { ["Body.Key"] = CustomNpcNames.NameBodyKey(npc.Slug) });
            AddText(l10n, CustomNpcNames.NameBodyKey(npc.Slug), npc.Name);

            var talks = new List<CustomNpcPatchSummary.Talk>();
            foreach (var talk in npc.Talks)
            {
                var talkName = CustomNpcNames.TalkName(npc.Slug, talk.Key);
                var firstRowId = nextTalkRowId;
                for (var i = 0; i < talk.Lines.Count; i++)
                {
                    var order = i + 1;
                    var lineKey = CustomNpcNames.LineKey(npc.Slug, talk.Key, order);
                    DataTableRows.AppendRow(npcTalk, nextTalkRowId++, talkTemplateRowId, new Dictionary<string, string>
                    {
                        ["Name"] = talkName,
                        ["Order"] = order.ToString(CultureInfo.InvariantCulture),
                        ["ChatTitle.Key"] = "None",
                        ["ChatDescription.Key"] = lineKey,
                        ["SpeakerType"] = "ENpcTalkSpeakerType::Npc",
                        ["SpeakerValue"] = npcName,
                        ["Camera"] = "default",
                        ["CameraTarget"] = npcName,
                        ["Act"] = "None",
                        ["FinishTalk"] = "None",
                        ["FinishAct"] = "None",
                        ["CvRes"] = "None",
                    });
                    AddText(l10n, lineKey, talk.Lines[i]);
                }
                talks.Add(new CustomNpcPatchSummary.Talk(talk.Key, talkName, talk.Lines.Count, firstRowId));
            }
            summary.Add(new CustomNpcPatchSummary.Npc(npc.Slug, npc.TemplateId, npc.BaseTemplateId, npcName, talks));
        }
        return new CustomNpcPatchSummary(summary);
    }

    static void AddText(IReadOnlyDictionary<string, FAion2L10NFile> l10n, string key, Dictionary<string, string> texts)
    {
        foreach (var (path, file) in l10n)
        {
            var locale = LocaleOf(path);
            var text = texts.TryGetValue(locale, out var localized) && !string.IsNullOrWhiteSpace(localized)
                ? localized
                : texts[CustomNpcNames.DefaultLocale];
            if (!file.Entries.TryAdd(key, text))
                throw new InvalidOperationException($"{path} already has key {key}");
        }
    }

    /// <summary>Locale folder of an L10NString path, e.g. ".../L10N/de-DE/L10NString.dat" gives "de-DE".</summary>
    public static string LocaleOf(string l10nPath)
    {
        var parts = l10nPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[^2] : throw new FormatException($"No locale in {l10nPath}");
    }
}
