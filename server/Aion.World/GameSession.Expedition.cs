using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Expedition difficulty tiers and affixes (tools/data/expedition.json, tools/expedition_data.py); the tickets are ContentTicketTables.</summary>
public sealed class ExpeditionTables
{
    public sealed record LevelTier(
        [property: JsonPropertyName("level")] int Level,
        [property: JsonPropertyName("dungeon_id")] int DungeonId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("affixes")] int[] Affixes);

    public sealed record ExpeditionGroup(
        [property: JsonPropertyName("group")] string Group,
        [property: JsonPropertyName("levels")] LevelTier[] Levels);

    public sealed record AffixRow([property: JsonPropertyName("name")] string Name);

    sealed record File(
        [property: JsonPropertyName("expeditions")] Dictionary<string, ExpeditionGroup>? Expeditions,
        [property: JsonPropertyName("affixes")] Dictionary<string, AffixRow>? Affixes);

    readonly File file;
    readonly Dictionary<int, ExpeditionGroup> expeditionByContent = [];
    readonly Dictionary<int, ExpeditionGroup> expeditionByDungeon = [];

    ExpeditionTables(File file)
    {
        this.file = file;
        foreach (var (_, group) in file.Expeditions ?? [])
            foreach (var tier in group.Levels)
            {
                expeditionByContent[tier.DungeonId] = group;
                expeditionByDungeon[tier.DungeonId] = group;
            }
    }

    static readonly Lazy<ExpeditionTables> embedded = new(() =>
    {
        using var stream = typeof(ExpeditionTables).Assembly.GetManifestResourceStream("expedition.json")
            ?? throw new InvalidOperationException("Missing embedded game data expedition.json");
        return new ExpeditionTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidOperationException("Empty expedition.json"));
    });

    public static ExpeditionTables Embedded => embedded.Value;

    public ExpeditionGroup? GroupForContent(int contentDungeonId) =>
        expeditionByContent.GetValueOrDefault(contentDungeonId)
        ?? expeditionByDungeon.GetValueOrDefault(contentDungeonId);

    public int ResolveDungeonId(int contentDungeonId, byte dungeonLevel)
    {
        if (GroupForContent(contentDungeonId) is not { Levels: { Length: > 0 } levels })
            return contentDungeonId;
        return (levels.FirstOrDefault(l => l.Level == dungeonLevel) ?? levels[0]).DungeonId;
    }

    public bool AffixesAllowed(int contentDungeonId, byte dungeonLevel, IReadOnlyList<int> affixes)
    {
        if (affixes.Count == 0) return true;
        if (GroupForContent(contentDungeonId) is not { Levels: { Length: > 0 } levels }) return false;
        var tier = levels.FirstOrDefault(l => l.Level == dungeonLevel) ?? levels[0];
        var allowed = tier.Affixes.ToHashSet();
        return affixes.All(allowed.Contains);
    }
}

public sealed partial class GameSession
{
    public const ushort PartyDungeonClearNt = 0x9333 /* CANDIDATE: usmap 0x3A69 + 0x58ca, the run of the OBSERVED 0x9330/0x9331 (#74) */, PartyDungeonMiddleBossKillNt = 0x572e /* OBSERVED (AVION2: 10 mid-boss kills, npc u32) */;

    static ExpeditionTables Expeditions => ExpeditionTables.Embedded;

    /// <summary>PartyDungeonClear_NT after a party dungeon script clear (CANDIDATE score breakdown).</summary>
    static byte[] PartyDungeonClear(long elapsedMs, DungeonInfo room)
    {
        var clearSec = (int)Math.Min(elapsedMs / 1000, int.MaxValue);
        var timeScore = room.Limit > 0 ? Math.Max(0, (room.Limit - clearSec) * 10) : 0;
        return PacketWriter.Opcode(PartyDungeonClearNt).I32(1).I32(clearSec).I32(1).I32(timeScore).I32(timeScore).I32(timeScore)
            .I32(0).I32(1000).I32(0).I32(1).ToArray();
    }

    static byte[] PartyDungeonMiddleBossKill(int npcId) => PacketWriter.Opcode(PartyDungeonMiddleBossKillNt).I32(npcId).ToArray();

    IEnumerable<GameSession> DungeonCopySessions()
    {
        if (copy is null) { yield return this; yield break; }
        if (worldPlayer?.Map is not { } map) { yield return this; yield break; }
        foreach (var player in map.Players.Values)
            if (player.Session.copy == copy) yield return player.Session;
    }

    void SendPartyDungeonClear(DungeonInfo room, long elapsedMs)
    {
        if (room.Type != "Party") return;
        var pkt = PartyDungeonClear(elapsedMs, room);
        foreach (var session in DungeonCopySessions())
            if (session == this) mapEventOut.Add(pkt);
            else session.worldPlayer?.Send([pkt]);
    }

    /// <summary>Clear score, complete/end dungeon state — every member in the copy (not only the script host).</summary>
    void BroadcastPartyDungeonClearEnd(DungeonInfo room, long elapsedMs, int dungeonId, long now, long holdMs)
    {
        if (room.Type != "Party") return;
        var packets = new[]
        {
            DungeonCompleteResult(dungeonId, true),
            DungeonState(dungeonId, DungeonStateComplete, now, now),
            DungeonState(dungeonId, DungeonStateEnd, now, now + holdMs),
            PartyDungeonClear(elapsedMs, room),
        };
        foreach (var session in DungeonCopySessions())
            foreach (var pkt in packets)
                if (session == this) mapEventOut.Add(pkt);
                else session.worldPlayer?.Send([pkt]);
    }

    /// <summary>A dungeon boss's hit against this player after the Oct 28 2026 boss attack cuts (Expedition and Transcendence stages).</summary>
    int DungeonBossDamage(int damage, int killerKey)
    {
        if (killerKey == 0 || copy is not { } c) return damage;
        var npcId = NpcDataIdForKey(killerKey);
        if (npcId <= 0) return damage;
        if (MapEventTables.Embedded.Dungeon(c.DungeonId) is not { } room || !Patch20261028.IsDungeonBoss(room, npcId)) return damage;
        var factor = Patch20261028.BossDamageFactor(c.DungeonId, room) * c.Frenzy.Hit(killerKey, lastNow, Patch20261028.FrenzyMs(room));
        return factor > 1 ? (int)Math.Round(damage * factor) : Patch20261028.ScaleBossDamage(damage, factor);
    }

    List<byte[]> NotifyDungeonDeathCount()
    {
        if (copy is not { } c) return [];
        var pkt = PacketWriter.Opcode(DungeonDeathLimitNt).I32(c.Deaths).ToArray();
        mapEventOut.Add(pkt);
        foreach (var member in CopyMembers()) member.worldPlayer?.Send([pkt]);
        return [pkt];
    }
}
