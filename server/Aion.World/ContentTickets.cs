using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// Content tickets (tools/data/expedition.json, tools/expedition_data.py): the 73 tickets of ContentsTicket.dat, per dungeon
/// the tickets its MapEntrance row checks and when it takes them, and what the expedition reward cube costs (port of the
/// community fork addiboii 87afb91 / feadae2 / 660fe39, forge #72). Count tickets are counted: a character starts with the
/// OBSERVED new-character count (`start`), each daily / weekly recharge time passed adds its count, each TimeRechargeCycle adds
/// its count, up to the maximum (a count already above it, as items give, stays: OBSERVED 60000001 at 840 of 560 and ticket 1
/// at 21 of 12 in our capture of 2026-11-12). CANDIDATE: the recharge times are seconds after midnight KST (as the shops'
/// 57600 s reset; the fork reads them as UTC), the cycles count from the Unix epoch (the fork's recorded 600 s ticks fall on
/// the clock's ten-minute marks). OURS: OwnerType Server tickets are counted per character. Time tickets (seconds of play:
/// the Abyss, growth dungeons) are not counted.
/// </summary>
public sealed class ContentTicketTables
{
    public sealed record Ticket(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("max")] int Max,
        [property: JsonPropertyName("daily")] int[]? Daily = null,
        [property: JsonPropertyName("weekly")] int[]? Weekly = null,
        [property: JsonPropertyName("cycle")] int[]? Cycle = null,
        [property: JsonPropertyName("owner")] string? Owner = null,
        [property: JsonPropertyName("start")] long Start = 0)
    {
        public int Id { get; set; }
        public bool Counted => Type == "Count" && Max > 0;
    }

    /// <summary>A dungeon entry's ticket use: the ticket, how many, and when (None: checked only, Enter, Clear, Unclaimed).</summary>
    public sealed record Use(int TicketId, int Count, string Deduct);

    sealed record File(
        [property: JsonPropertyName("tickets")] Dictionary<string, Ticket> Tickets,
        [property: JsonPropertyName("dungeons")] Dictionary<string, JsonElement[][]>? Dungeons,
        [property: JsonPropertyName("cube_tickets")] Dictionary<string, int[]>? CubeTickets);

    /// <summary>9 hours: the recharge times are KST (CANDIDATE, as the shops' reset).</summary>
    const long KstOffsetMs = 9 * 3_600_000L;
    const long DayMs = 86_400_000L;

    readonly Dictionary<int, Ticket> tickets;
    readonly Dictionary<int, Use[]> dungeons;
    readonly Dictionary<int, (int TicketId, int Count)> cubeTickets;
    readonly IReadOnlyList<Ticket> all;

    ContentTicketTables(File file)
    {
        tickets = file.Tickets.ToDictionary(p => int.Parse(p.Key, System.Globalization.CultureInfo.InvariantCulture), p => p.Value);
        foreach (var (id, t) in tickets) t.Id = id;
        all = [.. tickets.Values.OrderBy(t => t.Id)];
        dungeons = (file.Dungeons ?? []).ToDictionary(p => int.Parse(p.Key, System.Globalization.CultureInfo.InvariantCulture),
            p => p.Value.Where(u => u.Length >= 3).Select(u => new Use(u[0].GetInt32(), u[1].GetInt32(), u[2].GetString() ?? "None")).ToArray());
        cubeTickets = (file.CubeTickets ?? []).Where(p => p.Value.Length >= 2)
            .ToDictionary(p => int.Parse(p.Key, System.Globalization.CultureInfo.InvariantCulture), p => (p.Value[0], p.Value[1]));
    }

    static readonly Lazy<ContentTicketTables> embedded = new(() =>
    {
        using var stream = typeof(ContentTicketTables).Assembly.GetManifestResourceStream("expedition.json")
            ?? throw new InvalidOperationException("Missing embedded game data expedition.json");
        return new ContentTicketTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("expedition.json"));
    });

    public static ContentTicketTables Embedded => embedded.Value;
    public Ticket? Of(int id) => tickets.GetValueOrDefault(id);
    /// <summary>Every ticket, by id (the order 0x610b lists them).</summary>
    public IReadOnlyList<Ticket> All => all;
    public IReadOnlyList<Use> UsesOf(int dungeonId) => dungeons.GetValueOrDefault(dungeonId) ?? [];
    public int DungeonsWithUses => dungeons.Count;
    /// <summary>What opening the dungeon's reward cube costs (RewardOdEnergyCube TicketName / ConsumeTicketCount), or null.</summary>
    public (int TicketId, int Count)? CubeCost(int dungeonId) => cubeTickets.TryGetValue(dungeonId, out var cost) ? cost : null;

    /// <summary>The count after the recharges between <paramref name="from"/> and <paramref name="to"/> (ms), up to the maximum.</summary>
    public static int Recharged(Ticket ticket, int count, long from, long to)
    {
        if (count >= ticket.Max || to <= from) return count;
        long added = 0;
        if (ticket.Daily is [var daySecond, var dailyCount] && dailyCount > 0)
            added += Boundaries(from, to, DayMs, daySecond * 1000L - KstOffsetMs) * dailyCount;
        if (ticket.Weekly is [var weekday, var weekSecond, var weeklyCount] && weeklyCount > 0)
            // 1970-01-01 was a Thursday (4): the week's anchor is the weekday's time within the Unix week.
            added += Boundaries(from, to, 7 * DayMs, ((weekday - 4 + 7) % 7) * DayMs + weekSecond * 1000L - KstOffsetMs) * weeklyCount;
        if (ticket.Cycle is [var cycleSeconds, var cycleCount] && cycleSeconds > 0 && cycleCount > 0)
            added += Boundaries(from, to, cycleSeconds * 1000L, 0) * cycleCount;
        return (int)Math.Min(ticket.Max, count + added);
    }

    /// <summary>How many times t = offset (mod period) falls in (from, to].</summary>
    static long Boundaries(long from, long to, long period, long offset) =>
        Math.Max(0, FloorDiv(to - offset, period) - FloorDiv(from - offset, period));

    static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);
}
