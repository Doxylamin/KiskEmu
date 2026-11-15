using Aion.Protocol.Native;

namespace Aion.World;

// The Shugo festival schedule (FieldEvent.dat kind ShugoFesta, EventSchedule.dat 1101-1109 / 2101-2109): FieldEventInfo_NT 0x6000
// {varint count, entries} at every full map load (after 0xe223) and FieldEventChanged_NT 0x6001 {entry} when an event changes.
// An entry is {mask u8 3, event id varint, EFieldEventStateType u8, open i64, start i64, end i64}; an event that is gone is
// {mask 0, id, state 0, 2100-01-01 00:00 KST}. Layout from zaraza87/A2Core (via A2Core-ChanAge db5199e), ported from zaraza87's
// fork, server/Aion.World/GameSession.Systems2.cs.
// OBSERVED (our recordings R1104 2026-11-04 and R1115 2026-11-15, Elyos; 112 of 112 0x6001 have this shape):
//   - every full hour the race's 9 events open in Prepare: {3, id, 1, open, open, open + 180 s} (R1115 i=11191 110101 at 11:00);
//   - 180 s later Progress: {3, id, 2, open, open + 180 s, start + ProgressTime} (R1115 i=22495), Racing 12 s longer (312 s);
//   - at the end the event is gone: {0, id, 0, never} (R1115 i=36543), one message per event in end order;
//   - the list holds the events in Prepare or Progress (`006000` outside the windows, R1112 i=854), in the order below;
//   - the progress times 175 / 300 (+12) / 210 / 210 / 230 / 300 / 300 / 195 / 300 s of 110101-110901.
// CANDIDATE: every hour of every day (EventSchedule.dat via zaraza87's systems2.json; recorded at 11, 12, 16 and 17 UTC), the
// Asmodian events (210101-210901: the client tables' times, zaraza87's recorded order), the open on the hour (official: one or two
// seconds late). Not built: the Dimensional Invasion events (120101/120201 at :30 for 600 s, recorded at 11:30 and 15:30 but not
// every hour: which one runs when is unknown), the entrance 0x6007/0x6008 and the mini-games.
public sealed partial class GameSession
{
    public const ushort FieldEventInfoNt = 0x6000, FieldEventChangedNt = 0x6001;
    /// <summary>EFieldEventStateType.</summary>
    public const byte FieldEventNone = 0, FieldEventPrepare = 1, FieldEventProgress = 2;
    /// <summary>The open time of an event that is gone (OBSERVED `8075d52abb030000`: 2100-01-01 00:00 KST).</summary>
    public const long FieldEventNeverMs = 4_102_412_400_000;
    /// <summary>The entry window before an event starts (OBSERVED 180 s, FieldEvent.dat ReadyDuration).</summary>
    public const long FieldEventReadyMs = 180_000;
    /// <summary>The schedule's period (CANDIDATE: EventSchedule.dat ActiveTimeList 00:00-23:00 every day).</summary>
    public const long FieldEventPeriodMs = 3_600_000;

    /// <summary>A Shugo festival event: its id (race x 100000 + group x 100 + 1) and how long it runs after the entry window.</summary>
    public sealed record FestivalEvent(int Id, long ProgressMs);

    /// <summary>
    /// The race's events in the order the official server lists and changes them. Elyos OBSERVED (R1104, R1115: groups 3, 1, 5, 2,
    /// 4, 6, 7, 9, 8 with Racing's 300 s + 12); Asmodian the client's times in zaraza87's recorded order (3, 5, 1, 4, 8, 7, 2, 6, 9).
    /// </summary>
    public static IReadOnlyList<FestivalEvent> FestivalEvents(int race) => race == 2 ? DarkFestival : LightFestival;

    static readonly FestivalEvent[] LightFestival =
    [
        new(110301, 312_000), new(110101, 175_000), new(110501, 210_000), new(110201, 300_000), new(110401, 210_000),
        new(110601, 230_000), new(110701, 300_000), new(110901, 195_000), new(110801, 300_000),
    ];

    static readonly FestivalEvent[] DarkFestival =
    [
        new(210301, 312_000), new(210501, 210_000), new(210101, 175_000), new(210401, 210_000), new(210801, 300_000),
        new(210701, 300_000), new(210201, 300_000), new(210601, 230_000), new(210901, 195_000),
    ];

    /// <summary>An event's state at <paramref name="now"/>: the hour's open, Prepare for 180 s, then Progress until its end.</summary>
    public static (byte State, long Open, long Start, long End) FestivalState(FestivalEvent e, long now)
    {
        var open = now - now % FieldEventPeriodMs;
        var start = open + FieldEventReadyMs;
        var end = start + e.ProgressMs;
        return (now < start ? FieldEventPrepare : now < end ? FieldEventProgress : FieldEventNone, open, start, end);
    }

    /// <summary>One entry: Prepare {3, id, 1, open, open, start}, Progress {3, id, 2, open, start, end}, gone {0, id, 0, never}.</summary>
    static PacketWriter FestivalEntry(PacketWriter w, FestivalEvent e, (byte State, long Open, long Start, long End) run) => run.State switch
    {
        FieldEventPrepare => w.U8(3).Varint(e.Id).U8(FieldEventPrepare).I64(run.Open).I64(run.Open).I64(run.Start),
        FieldEventProgress => w.U8(3).Varint(e.Id).U8(FieldEventProgress).I64(run.Open).I64(run.Start).I64(run.End),
        _ => w.U8(0).Varint(e.Id).U8(FieldEventNone).I64(FieldEventNeverMs),
    };

    /// <summary>FieldEventInfo_NT 0x6000: the race's events in Prepare or Progress at <paramref name="now"/>.</summary>
    public static byte[] FieldEventInfo(int race, long now)
    {
        var running = FestivalEvents(race).Select(e => (e, FestivalState(e, now))).Where(x => x.Item2.State != FieldEventNone).ToList();
        var w = PacketWriter.Opcode(FieldEventInfoNt).Varint(running.Count);
        foreach (var (e, run) in running) FestivalEntry(w, e, run);
        return w.ToArray();
    }

    /// <summary>FieldEventChanged_NT 0x6001 of one event.</summary>
    public static byte[] FieldEventChanged(FestivalEvent e, (byte State, long Open, long Start, long End) run) =>
        FestivalEntry(PacketWriter.Opcode(FieldEventChangedNt), e, run).ToArray();

    /// <summary>What the client was told per event (state, open); null until the entry's list went out.</summary>
    Dictionary<int, (byte State, long Open)>? festivalShown;

    /// <summary>The list at a full map load; from then on the tick sends the changes.</summary>
    byte[] FieldEventInfoAtEntry(long now)
    {
        festivalShown = [];
        foreach (var e in FestivalEvents(profileRace))
            if (FestivalState(e, now) is { State: not FieldEventNone } run) festivalShown[e.Id] = (run.State, run.Open);
        return FieldEventInfo(profileRace, now);
    }

    /// <summary>The tick: FieldEventChanged_NT for every event whose state changed since the client heard of it, in the list's order.</summary>
    List<byte[]> FestivalTick(long now)
    {
        if (festivalShown is not { } shown) return [];
        List<byte[]>? changes = null;
        foreach (var e in FestivalEvents(profileRace))
        {
            var run = FestivalState(e, now);
            var running = run.State != FieldEventNone;
            var known = shown.TryGetValue(e.Id, out var last);
            if (running ? known && last == (run.State, run.Open) : !known) continue;
            if (running) shown[e.Id] = (run.State, run.Open);
            else shown.Remove(e.Id);
            (changes ??= []).Add(FieldEventChanged(e, run));
        }
        return changes ?? [];
    }
}
