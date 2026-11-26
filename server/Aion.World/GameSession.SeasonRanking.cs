using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// The Abyss season ranking at the world entry: RetrieveMyLatestSeasonRanking_RS 0x8d57 (the client asks 0x8d56 {Abyss 1, class 0}
// at every entry), MyPrevSeasonRanking_NT 0x8d58 (`588d00`, GameSession.EntryConstants.cs) and the Abyss tier buff 20100.
// Layout from zaraza87/A2Core (via A2Core-ChanAge db5199e); the grade rule ported from zaraza87's fork, server/Aion.World/RankTables.cs.
// OBSERVED:
//   - 0x8d57 (R1115 i=193025, 134 B): result u16 0 | type | class | SeasonRankingData {rank i32, dbid i64, name, legion name, legion
//     emblem u16, level i32, conqueror level i32, class u8, point i64, extra JSON {kill_count, death_count, assist_count}, rank change
//     i32, previous season rank i32, grade i32, grade display i32} | ranked total i32 | next update i64 (rank 3797 of 6234, 9165
//     points, grade 18); nobody ranked: `403e` (SeasonRanking_NoRanker), type, class, 54 zero bytes, the time (R1104 i=1726).
//   - 0x8d58 is always `588d00`.
//   - the tier buff: AbnormalChange_NT 0x382b right after 0xe25c at the world entry (R1112 i=574, R1115 i=192623), not 0x382a:
//     mask 0x11 (caster, range), abnormal 20100, duration = the time to the season's end, expire = 2026-12-16 07:00:00.05 UTC,
//     caster = the player, level 21 - grade (grade 18 -> 3; grade 19 has none), show_msg set, the player's position.
// CANDIDATE: the grades from the client's SeasonRankingGrade rows (ranking_grade_abyss_S2 for the list, ranking_grade_class_abyss_S1
// for the class list and the display, season 1 shows the class grade), MinRankingPoint 1500, one list per race of this world, the
// next update an hour on, the buff's grade 1 (level 20). OURS: the points are character_abyss's ranking points as last stored (kills;
// not reset per season), kill/death/assist 0, rank change and previous season rank 0, the buff only shown (its PvP stats are not
// applied).
public sealed partial class GameSession
{
    public const byte SeasonContentsAbyss = 1;
    public const int AbyssTierBuff = 20100;
    /// <summary>SeasonRanking.dat MinRankingPoint of the Abyss (CANDIDATE, zaraza87's export).</summary>
    public const int AbyssMinRankingPoint = 1500;
    /// <summary>Season 1 (Season_Main_Live) ends 2026-12-16 07:00 UTC (OBSERVED as the tier buff's expiry, 50 ms later).</summary>
    public const long SeasonEndMs = 1_797_404_400_000;

    /// <summary>SeasonRankingGrade rows {grade, rank max (0 any), points needed}: the first the rank and the points reach.</summary>
    static readonly (int Grade, int RankMax, long Need)[] AbyssGrades =
    [
        (1, 1, 25_000_000), (2, 3, 15_000_000), (3, 10, 10_000_000), (4, 30, 7_000_000), (5, 50, 4_200_000), (6, 70, 3_500_000),
        (7, 100, 2_800_000), (8, 150, 2_100_000), (9, 200, 1_500_000), (10, 300, 1_000_000), (11, 400, 750_000), (12, 500, 550_000),
        (13, 600, 350_000), (14, 700, 180_000), (15, 800, 75_000), (16, 900, 35_000), (17, 1000, 12_000), (18, 10_000, 3000), (19, 0, 0),
    ];

    static readonly (int Grade, int RankMax, long Need)[] AbyssClassGrades =
    [
        (1, 1, 20_000_000), (2, 2, 13_000_000), (3, 4, 9_000_000), (4, 6, 6_000_000), (5, 20, 4_200_000), (6, 40, 3_500_000),
        (7, 70, 2_800_000), (8, 100, 2_100_000), (9, 150, 1_500_000), (10, 200, 1_000_000), (11, 250, 750_000), (12, 300, 550_000),
        (13, 400, 350_000), (14, 500, 180_000), (15, 600, 75_000), (16, 700, 35_000), (17, 800, 12_000), (18, 1000, 3000), (19, 0, 0),
    ];

    static int GradeOf((int Grade, int RankMax, long Need)[] grades, int rank, long point) =>
        grades.FirstOrDefault(g => (g.RankMax == 0 || rank <= g.RankMax) && point >= g.Need).Grade;

    /// <summary>The character's place: the list's rank and grade, the class list's rank and grade (the display), both totals.</summary>
    public sealed record AbyssRank(AbyssRankRow Row, int Rank, int Total, int ClassRank, int ClassTotal, int Grade, int ClassGrade);

    /// <summary>Where <paramref name="me"/> stands in a race's list (best first); null when unranked.</summary>
    public static AbyssRank? RankOf(IReadOnlyList<AbyssRankRow> list, Guid me)
    {
        var index = list.ToList().FindIndex(r => r.CharacterId == me);
        if (index < 0) return null;
        var row = list[index];
        var sameClass = list.Where(r => r.ClassId == row.ClassId).ToList();
        var classRank = sameClass.FindIndex(r => r.CharacterId == me) + 1;
        return new AbyssRank(row, index + 1, list.Count, classRank, sameClass.Count,
            GradeOf(AbyssGrades, index + 1, row.Point), GradeOf(AbyssClassGrades, classRank, row.Point));
    }

    /// <summary>The tier buff's level: 21 - grade, none from grade 19 (OBSERVED: grade 18 -> 3).</summary>
    public static int AbyssTierLevel(int grade) => grade is >= 1 and <= 18 ? 21 - grade : 0;

    AbyssRank? abyssRank;

    /// <summary>Before the entry lists: the race's Abyss list as stored, this character's place in it.</summary>
    async Task LoadAbyssRankAsync(CancellationToken ct)
    {
        abyssRank = null;
        if (Store is not { } store || CharacterId is not { } me) return;
        var list = await StoredValue(() => store.AbyssRankingAsync(profileRace, AbyssMinRankingPoint, ct), (IReadOnlyList<AbyssRankRow>)[]);
        abyssRank = RankOf(list, me);
    }

    /// <summary>One SeasonRankingData in the observed layout.</summary>
    public static PacketWriter WriteSeasonEntry(PacketWriter w, int rank, AbyssRankRow row, string extra, int rankChange, int prevSeasonRank, int grade, int display) =>
        w.I32(rank).I64(row.WireId).String(row.Name).String(row.Guild).U16((ushort)row.GuildEmblem).I32(row.Level).I32(0).U8((byte)row.ClassId)
            .I64(row.Point).String(extra).I32(rankChange).I32(prevSeasonRank).I32(grade).I32(display);

    /// <summary>The Abyss extra JSON (OURS: no kill, death or assist counts are kept).</summary>
    const string AbyssRankExtra = """{"kill_count":0,"death_count":0,"assist_count":0}""";

    /// <summary>RetrieveMyLatestSeasonRanking_RS of the character's rank (class 0: the whole list, else its class list).</summary>
    public static byte[] MySeasonRanking(byte type, byte cls, AbyssRank me, long nextUpdate, string extra = AbyssRankExtra)
    {
        var w = PacketWriter.Opcode(MySeasonRankingRs).U16(0).U8(type).U8(cls);
        var whole = cls == 0;
        WriteSeasonEntry(w, whole ? me.Rank : me.ClassRank, me.Row, extra, 0, 0, whole ? me.Grade : me.ClassGrade, me.ClassGrade);
        return w.I32(whole ? me.Total : me.ClassTotal).I64(nextUpdate).ToArray();
    }

    /// <summary>RetrieveMyLatestSeasonRanking_RQ {type, class}: the Abyss rank when ranked (in its class list for that class), else the empty form.</summary>
    byte[] MySeasonRankingReply(byte type, byte cls, long now)
    {
        var next = now + 3_600_000;
        if (type == SeasonContentsAbyss && abyssRank is { } me && (cls == 0 || cls == me.Row.ClassId) && now < SeasonEndMs)
            return MySeasonRanking(type, cls, me, next);
        var body = (byte[])SeasonRankingNone.Clone();
        body[2] = type;
        body[3] = cls;
        return PacketWriter.Opcode(MySeasonRankingRs).Raw(body).I64(next).ToArray();
    }

    /// <summary>The tier buff at the world entry: 0x382b {mask 0x11, 20100, the time to the season's end, level 21 - grade, show_msg}.</summary>
    byte[]? AbyssTierBuffAtEntry(long now)
    {
        if (abyssRank is not { } me || now >= SeasonEndMs) return null;
        var level = AbyssTierLevel(me.ClassGrade);
        if (level <= 0) return null;
        return PlayerPackets.AbnormalChange(playerKey, new AbnormalWire(++buffUid, AbyssTierBuff, SeasonEndMs - now, SeasonEndMs, CasterKey: playerKey,
            Level: (byte)level, RangeBase: position ?? Vector3d.Zero), showMsg: true);
    }
}
