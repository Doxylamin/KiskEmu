using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// The duty mission board's random reward packages (tools/data/duty-rewards.json: QuestReward.dat RandomReward -> RewardPackage.dat
/// -> RewardPackageItem.dat, exported by zaraza87's tools/duty_data.py): a package's five slots with their prob, each slot's
/// candidates {item, weight, count min, count max, enchant}.
/// </summary>
public sealed class DutyTables
{
    public sealed record Candidate(int Item, long Weight, long Min, long Max, byte Enchant);

    sealed class File
    {
        [JsonPropertyName("board_size")] public int BoardSize { get; set; } = 15;
        [JsonPropertyName("packages")] public Dictionary<string, JsonElement[][]> Packages { get; set; } = [];
        [JsonPropertyName("slots")] public Dictionary<string, long[][]> Slots { get; set; } = [];
    }

    readonly File file;

    DutyTables(File file) => this.file = file;

    static readonly Lazy<DutyTables> embedded = new(() =>
    {
        using var stream = typeof(DutyTables).Assembly.GetManifestResourceStream("duty-rewards.json")
            ?? throw new InvalidOperationException("duty-rewards.json is not embedded");
        return new DutyTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("duty-rewards.json"));
    });

    public static DutyTables Embedded => embedded.Value;

    /// <summary>GlobalSetting dutymission_quest_journal_acquired_count: the missions on a board (15, OBSERVED).</summary>
    public int BoardSize => file.BoardSize;

    /// <summary>A package's slots in order: (slot name, prob out of 100,000,000).</summary>
    public IReadOnlyList<(string Slot, long Prob)> Package(string name) =>
        file.Packages.TryGetValue(name, out var rows) ? rows.Select(r => (r[0].GetString() ?? "", r[1].GetInt64())).ToList() : [];

    public IReadOnlyList<Candidate> Slot(string name) =>
        file.Slots.TryGetValue(name, out var rows) ? rows.Select(r => new Candidate((int)r[0], r[1], r[2], r[3], (byte)r[4])).ToList() : [];
}

// The duty mission board (Quest.dat Type DutyMission, AcquireType None): DutyMissionQuestJournalStatus_NT 0x8d1c at the world entry,
// before the quest list. Layout from zaraza87/A2Core (via A2Core-ChanAge db5199e); the draw follows zaraza87's fork,
// server/Aion.World/GameSession.Duty.cs and DutyMissions.cs.
// OBSERVED (R1115 i=355, then 0x8d09 at i=371; the same 1419 bytes in R1112 i=96319, R1112b i=364 and five more R1115 entries):
// refreshed i32 0, rewarded i32 0, varint 15 missions {uid i32 1-15, quest id i32 (UQ1302016 ...), quest uid i32 0, publisher i32 0,
// status u8 1, varint 5 rewards {item i32, count varint, enchant u8, prob i64}}, expire i64, varint 0 deleted uids, weekly expire
// i64 (both 2026-10-16 07:00 UTC). The board is sent as stored even when its time is long past. Each reward is one candidate of the
// package's slot with that slot's prob (100M, 50M, 33M, 25M, 10M; every recorded reward is a candidate of its slot). A character
// whose board was never opened gets no 0x8d1c (the second character's entries in R1112 / R1115).
// OURS: the board is drawn at the first entry (officially when the board is first opened, 0x8d1a, which stays unanswered here, as
// do 0x8d13/0x8d14 and 0x8d1d-0x8d20): 15 missions of the race up to the character's level, each slot a weighted candidate.
// CANDIDATE: both expiry times at the next weekly reset (the recorded board's time is not our Wednesday reset).
public sealed partial class GameSession
{
    public const ushort DutyBoardNt = 0x8d1c;
    const string DutyBoardSetting = "duty_board";

    public sealed record DutyReward(int Item, long Count, byte Enchant, long Prob);
    public sealed record DutyMission(int Uid, int QuestId, int QuestUid, byte Status, DutyReward[] Rewards);
    public sealed record DutyBoard(int Refreshed, int Rewarded, DutyMission[] Missions, long Expire, long WeeklyExpire, int[]? Deleted = null);

    DutyBoard? dutyBoard;

    /// <summary>The character's mission board (null before the first).</summary>
    public DutyBoard? DutyBoardState => dutyBoard;

    /// <summary>DutyMissionQuestJournalStatus_NT 0x8d1c in the observed layout.</summary>
    public static byte[] DutyBoardNotice(DutyBoard board)
    {
        var w = PacketWriter.Opcode(DutyBoardNt).I32(board.Refreshed).I32(board.Rewarded).Varint(board.Missions.Length);
        foreach (var m in board.Missions)
        {
            w.I32(m.Uid).I32(m.QuestId).I32(m.QuestUid).I32(0).U8(m.Status).Varint(m.Rewards.Length);
            foreach (var r in m.Rewards) w.I32(r.Item).Varint((uint)r.Count).U8(r.Enchant).I64(r.Prob);
        }
        w.I64(board.Expire).Varint(board.Deleted?.Length ?? 0);
        foreach (var uid in board.Deleted ?? []) w.I32(uid);
        return w.I64(board.WeeklyExpire).ToArray();
    }

    /// <summary>
    /// A new board: <see cref="DutyTables.BoardSize"/> DutyMission quests of the race up to the level (uids 1..n, status 1), each with
    /// one weighted candidate per slot of its random reward package; none when no mission fits.
    /// </summary>
    public static DutyBoard? DrawDutyBoard(QuestBook book, int race, int level, Random rng, long now)
    {
        var pool = book.Quests.Values
            .Where(q => q.Type == "DutyMission" && !q.Unavailable && q.Level <= level && QuestBook.RaceMatches(q, race) && q.Rewards?.Random is not null)
            .OrderBy(q => q.Id).ToList();
        if (pool.Count == 0) return null;
        var tables = DutyTables.Embedded;
        var missions = new List<DutyMission>();
        for (var uid = 1; uid <= tables.BoardSize && pool.Count > 0; uid++)
        {
            var quest = pool[rng.Next(pool.Count)];
            pool.Remove(quest);
            var rewards = new List<DutyReward>();
            foreach (var (slot, prob) in tables.Package(quest.Rewards!.Random!))
            {
                var candidates = tables.Slot(slot);
                var total = candidates.Sum(c => c.Weight);
                if (total <= 0) continue;
                var roll = rng.NextInt64(total);
                var pick = candidates.First(c => (roll -= c.Weight) < 0);
                rewards.Add(new DutyReward(pick.Item, pick.Min >= pick.Max ? pick.Min : rng.NextInt64(pick.Min, pick.Max + 1), pick.Enchant, prob));
            }
            missions.Add(new DutyMission(uid, quest.Id, 0, 1, [.. rewards]));
        }
        var expire = ContentResetSchedule.NextBoundaryMs(2, now);
        return new DutyBoard(0, 0, [.. missions], expire, expire);
    }

    /// <summary>Before the entry lists: a character without a board gets one, stored.</summary>
    async Task EnsureDutyBoardAsync(long now, CancellationToken ct)
    {
        if (dutyBoard is not null || questLog is null) return;
        dutyBoard = DrawDutyBoard(questLog.Book, profileRace, level, rng, now);
        if (dutyBoard is not null) await SaveSettingAsync(DutyBoardSetting, dutyBoard, ct);
    }

    /// <summary>The board at the entry, as stored (OBSERVED: an old board is sent unchanged); nothing without one.</summary>
    byte[]? DutyBoardAtEntry() => dutyBoard is { } board ? DutyBoardNotice(board) : null;
}
