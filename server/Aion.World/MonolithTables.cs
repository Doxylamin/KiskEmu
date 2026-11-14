using System.Globalization;
using System.Text.Json;

namespace Aion.World;

/// <summary>
/// The Monoliths, their levels and the trace / Monolith objects (embedded tools/data/monoliths.json). Ported from zaraza87/A2Core
/// (GameSession.EmpyreanTraces.cs, tools/monolith_data.py; #72): the JSON is theirs as built from a client table export
/// (Monolith / MonolithLevel / RewardMonolith / EnvObjData / RewardData / Item.dat) we do not have locally, so it is taken as is.
/// The ids are Monolith.dat's (1001 Verteron, 1002 Eltnen, 2001 Altgard, 2002 Morheim, 3001 / 4001 Reshanta Light / Dark): the
/// ones our official MonolithList_NT captures carry (1001, 1002, 3001 for a Light character, OBSERVED 2026-11-12).
/// </summary>
public sealed class MonolithTables
{
    /// <summary>A level: the cumulative token count that reaches it (NeedTokenCount), its items and its permanent stats.</summary>
    public sealed record Level(int Number, int Need, IReadOnlyList<(int Item, int Count, int Enchant)> Items, IReadOnlyDictionary<string, int> Stats);

    public sealed record Monolith(int Id, string Name, int Race, int Token, string? UnlockQuest, int UnlockStep, IReadOnlyList<string> Groups, IReadOnlyList<Level> Levels)
    {
        /// <summary>The count that reaches the last level.</summary>
        public int MaxCount => Levels.Count == 0 ? 0 : Levels.Max(l => l.Need);
        /// <summary>The level a resonance count reaches.</summary>
        public int LevelAt(int count) => Levels.Where(l => l.Need <= count).Select(l => l.Number).DefaultIfEmpty(1).Max();
    }

    public IReadOnlyDictionary<int, Monolith> Monoliths { get; }
    /// <summary>Monolith object (EnvObjData id) -> the Monoliths it resonates with (Reshanta: both races').</summary>
    public IReadOnlyDictionary<int, int[]> Objects { get; }
    /// <summary>Trace EnvObjData ids -> template names.</summary>
    public IReadOnlyDictionary<int, string> Traces { get; }
    /// <summary>Express voucher item -> (monolith, map).</summary>
    public IReadOnlyDictionary<int, (int Monolith, int Map)> Vouchers { get; }

    static int Id(string text) => int.Parse(text, CultureInfo.InvariantCulture);

    MonolithTables(JsonElement root)
    {
        var monoliths = new Dictionary<int, Monolith>();
        foreach (var m in root.GetProperty("monoliths").EnumerateObject())
        {
            var v = m.Value;
            var levels = v.GetProperty("levels").EnumerateArray().Select(l => new Level(l[0].GetInt32(), l[1].GetInt32(),
                l[2].EnumerateArray().Select(i => (i[0].GetInt32(), i[1].GetInt32(), i[2].GetInt32())).ToList(),
                l[3].EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32(), StringComparer.OrdinalIgnoreCase))).OrderBy(l => l.Number).ToList();
            var id = Id(m.Name);
            monoliths[id] = new Monolith(id, v.GetProperty("name").GetString() ?? "", v.GetProperty("race").GetInt32(), v.GetProperty("token").GetInt32(),
                v.GetProperty("unlock_quest").GetString(), v.GetProperty("unlock_step").GetInt32(),
                v.GetProperty("groups").EnumerateArray().Select(g => g.GetString() ?? "").ToList(), levels);
        }
        Monoliths = monoliths;
        Objects = root.GetProperty("objects").EnumerateObject().ToDictionary(p => Id(p.Name), p => p.Value.EnumerateArray().Select(x => x.GetInt32()).ToArray());
        Traces = root.GetProperty("traces").EnumerateObject().ToDictionary(p => Id(p.Name), p => p.Value.GetString() ?? "");
        Vouchers = root.GetProperty("vouchers").EnumerateObject().ToDictionary(p => Id(p.Name), p => (p.Value[0].GetInt32(), p.Value[1].GetInt32()));
    }

    static readonly Lazy<MonolithTables> embedded = new(() =>
    {
        using var stream = typeof(MonolithTables).Assembly.GetManifestResourceStream("monoliths.json")
            ?? throw new InvalidOperationException("monoliths.json is not embedded");
        using var doc = JsonDocument.Parse(stream);
        return new MonolithTables(doc.RootElement);
    });

    public static MonolithTables Embedded => embedded.Value;

    public Monolith? Of(int id) => Monoliths.GetValueOrDefault(id);
    /// <summary>Every Monolith of a race, by id (MonolithList_NT lists them all, also those never resonated: OBSERVED).</summary>
    public IEnumerable<Monolith> OfRace(int race) => Monoliths.Values.Where(m => m.Race == race).OrderBy(m => m.Id);

    /// <summary>The Monolith a Monolith object resonates with for a race: the race's, else the first listed.</summary>
    public Monolith? OfObject(int env, int race) =>
        (Objects.GetValueOrDefault(env) ?? []).Select(Of).OfType<Monolith>().OrderBy(m => m.Race == race ? 0 : 1).FirstOrDefault();
}
