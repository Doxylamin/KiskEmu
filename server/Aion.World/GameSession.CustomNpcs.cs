using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Custom NPC dialogs (CustomNpcWorld.cs). The interaction is the client's own: 0xe240 opens it (0xe241), 0xe242 ends it.
// The first dialog node whose condition holds is the one that plays.
// - With the patch pak, the client knows the node's NpcTalk rows (KISK_Npc_<slug>_<node>): the server plays them with
//   PlayNpcTalk (GamePackets.Caption), the same lower-third lines as the tutorial captions. CANDIDATE until the in-game
//   check: that the lines wait for the interaction key, and when the client sends 0xe242 (it may close at once, 15 ms,
//   when it has no talk of its own to play; the actions then run while the lines still show).
// - Without it, each interaction shows the next line as a centre-screen notice in the client's language ("Name: line");
//   the player talks again to continue.
// The node's actions run on the 0xe242 after its last line. Flags are the character's own per NPC
// (game.character_settings "custom_npc_flags"), saved before any item is granted so a one-time reward is never paid twice.
public sealed partial class GameSession
{
    const string CustomNpcFlagsSetting = "custom_npc_flags";
    /// <summary>The Notice cultures in order (ELanguageCode - 1), as locales.</summary>
    static readonly string[] NoticeLocales = ["ko-KR", "de-DE", "en-US", "es-ES", "fr-FR", "ja-JP", "pt-BR", "ru-RU", "zh-CN", "zh-TW"];

    /// <summary>Per NPC slug: flag → value.</summary>
    Dictionary<string, Dictionary<string, string>> customNpcFlags = [];
    /// <summary>The custom dialog open with an NPC (0xe240 .. 0xe242) and the line it showed.</summary>
    (int NpcKey, CustomNpc Npc, CustomNpcNode Node, int Line)? openCustomTalk;
    /// <summary>Without the pak: the next line of a dialog in progress, by NPC key.</summary>
    readonly Dictionary<int, (string Node, int Line)> customTalkProgress = [];

    /// <summary>Whether this client has the custom NPC patch pak (names and dialog rows of every custom NPC).</summary>
    public bool HasCustomNpcPak => options.World?.CustomNpcs.AssumePatchedClients == true;

    void LoadCustomNpcFlags(IReadOnlyDictionary<string, JsonElement> settings)
    {
        try
        {
            customNpcFlags = settings.TryGetValue(CustomNpcFlagsSetting, out var v)
                ? v.Deserialize<Dictionary<string, Dictionary<string, string>>>() ?? [] : [];
        }
        catch (JsonException) { customNpcFlags = []; }
    }

    public bool CustomNpcFlag(string slug, string flag) => customNpcFlags.TryGetValue(slug, out var flags) && flags.ContainsKey(flag);

    /// <summary>The appear this player gets: a custom NPC shows its own template (custom name) when the client has the pak.</summary>
    NpcAppearance SessionAppear(Target t) =>
        t.Custom is { } custom && HasCustomNpcPak ? t.Appearance() with { NpcId = custom.TemplateId } : t.Appearance();

    /// <summary>Stops seeing an NPC (the map moved or removed it); the out-of-view removal, or null when it was not seen.</summary>
    public byte[]? ForgetNpc(int key)
    {
        if (!seenNpcs.Remove(key)) return null;
        if (openCustomTalk?.NpcKey == key) openCustomTalk = null;
        customTalkProgress.Remove(key);
        return GamePackets.Death(key, GamePackets.DespawnOutOfView);
    }

    /// <summary>The node an interaction plays now, or null.</summary>
    CustomNpcNode? CustomNpcNodeFor(CustomNpc npc) => npc.Nodes.FirstOrDefault(n => CustomNpcConditionHolds(npc, n.When));

    bool CustomNpcConditionHolds(CustomNpc npc, CustomNpcCondition? when) => when is null
        || (when.MinLevel == 0 || level >= when.MinLevel)
        && (when.MaxLevel == 0 || level <= when.MaxLevel)
        && (when.Flag is null || CustomNpcFlag(npc.Slug, when.Flag))
        && (when.NoFlag is null || !CustomNpcFlag(npc.Slug, when.NoFlag))
        && (when.ItemId == 0 || HeldCount(when.ItemId) >= when.ItemCount)
        && (when.Race == 0 || when.Race == (Profile()?.Race ?? 0));

    /// <summary>0xe240 on a custom NPC: plays its dialog node (after the 0xe241 the caller sends).</summary>
    List<byte[]> CustomNpcTalkOpened(Target npc, CustomNpc custom)
    {
        openCustomTalk = null;
        if (CustomNpcNodeFor(custom) is not { } node) return [];
        if (HasCustomNpcPak)
        {
            openCustomTalk = (npc.Key, custom, node, node.Lines.Count - 1);
            return [GamePackets.Caption(CustomNpcRules.TalkName(custom.Slug, node.Key))];
        }
        var line = customTalkProgress.TryGetValue(npc.Key, out var progress) && progress.Node == node.Key && progress.Line < node.Lines.Count ? progress.Line : 0;
        openCustomTalk = (npc.Key, custom, node, line);
        var more = line < node.Lines.Count - 1;
        return [LocalizedNotice(locale => $"{CustomNpcRules.Text(custom.Name, locale)}: {CustomNpcRules.Text(node.Lines[line], locale)}{(more ? " …" : "")}")];
    }

    /// <summary>0xe242 on a custom NPC: after the last line the node's actions run; before it, the next talk shows the next line.</summary>
    async Task<List<byte[]>> CustomNpcTalkFinishedAsync(int npcKey, long now, CancellationToken ct)
    {
        if (openCustomTalk is not { } open || open.NpcKey != npcKey) return [];
        openCustomTalk = null;
        if (open.Line < open.Node.Lines.Count - 1)
        {
            customTalkProgress[npcKey] = (open.Node.Key, open.Line + 1);
            return [];
        }
        customTalkProgress.Remove(npcKey);
        return await RunCustomNpcActionsAsync(open.Npc, open.Node, now, ct);
    }

    async Task<List<byte[]>> RunCustomNpcActionsAsync(CustomNpc npc, CustomNpcNode node, long now, CancellationToken ct)
    {
        var replies = new List<byte[]>();
        var items = new List<(int TemplateId, long Count)>();
        var flagsChanged = false;
        (int MapId, Vector3d At)? teleport = null;
        foreach (var action in node.Actions)
            switch (action.Type)
            {
                case "give_item": items.Add((action.ItemId, action.Count)); break;
                case "give_kina": items.Add((KinaItemId, action.Count)); break;
                case "set_flag" when action.Flag is { } flag:
                    if (!customNpcFlags.TryGetValue(npc.Slug, out var flags)) customNpcFlags[npc.Slug] = flags = [];
                    flagsChanged |= flags.TryAdd(flag, "1");
                    break;
                case "clear_flag" when action.Flag is { } flag:
                    flagsChanged |= customNpcFlags.TryGetValue(npc.Slug, out var held) && held.Remove(flag);
                    break;
                case "notice" when action.Text is { } text: replies.Add(Notice(text)); break;
                case "teleport": teleport = (action.MapId, new Vector3d(action.X, action.Y, action.Z)); break;
            }
        // Flags first: a dialog that pays once sets its flag, and a failed save pays nothing.
        if (flagsChanged) await SaveSettingAsync(CustomNpcFlagsSetting, customNpcFlags, ct);
        if (items.Count > 0 && Store is { } store && CharacterId is { } character)
        {
            var granted = await Stored(async () => await store.GrantLootAsync(character, items, ct) ? [[]] : []);
            if (granted.Count > 0) replies.AddRange(await InventoryChangesAsync(ct, IncreaseQuestReward, 0));
        }
        if (teleport is { } to && data.SpawnMaps.Contains(to.MapId))
            replies.AddRange(Teleport(to.MapId, to.At, now));
        return replies;
    }

    /// <summary>A centre-screen notice with one text per client language (see <see cref="Notice(string, int, string)"/>).</summary>
    static byte[] LocalizedNotice(Func<string, string> textFor, int size = 20, string color = "FFFFFF")
    {
        const int EnUs = 2;
        var writer = PacketWriter.Opcode(NoticeNt).U16(Math.Clamp(size, 0, 200)).String(color).Varint(NoticeLocales.Length);
        foreach (var locale in NoticeLocales) writer.String(Clip(textFor(locale), 255));
        return writer.I32(EnUs).ToArray();
    }
}
