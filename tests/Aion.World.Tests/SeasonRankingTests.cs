using System.Text;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>The Abyss season ranking at entry: 0x8d57, 0x8d58 and the tier buff 20100 via 0x382b (official R1104 / R1112 / R1115).</summary>
public sealed class SeasonRankingTests
{
    const long Now = 1_794_740_400_000;

    static string Str(string s) => Convert.ToHexString([(byte)Encoding.UTF8.GetByteCount(s), .. Encoding.UTF8.GetBytes(s)]).ToLowerInvariant();

    [Fact]
    public void The_ranking_entry_matches_the_official_bytes()
    {
        // R1115 i=193025 with the character's and legion's names replaced: rank 3797, 9165 points, kills 0 / deaths 2 / assists 0,
        // change -5, grade 18 shown as 18, 6234 ranked.
        var row = new AbyssRankRow(Guid.NewGuid(), 0x0529000000_01c6e1, "Player1", "Legion", 1, 45, 8, 9165);
        const string extra = """{"kill_count":0,"death_count":2,"assist_count":0}""";
        var w = PacketWriter.Opcode(GameSession.MySeasonRankingRs).U16(0).U8(1).U8(0);
        GameSession.WriteSeasonEntry(w, 3797, row, extra, -5, 0, 18, 18);
        var body = w.I32(6234).I64(BitConverter.ToInt64(Convert.FromHexString("2a1b35dfa1010000"))).ToArray();
        Assert.Equal("578d00000100" + "d50e0000" + "e1c6010000002905" + Str("Player1") + Str("Legion") + "0100" + "2d000000" + "00000000" + "08"
            + "cd23000000000000" + "31" + Convert.ToHexString(Encoding.UTF8.GetBytes(extra)).ToLowerInvariant()
            + "fbffffff" + "00000000" + "12000000" + "12000000" + "5a180000" + "2a1b35dfa1010000", Convert.ToHexString(body).ToLowerInvariant());
    }

    [Fact]
    public void Grades_and_the_tier_level()
    {
        AbyssRankRow Row(int i, int cls, long point) => new(new Guid(i, 0, 0, new byte[8]), i, $"P{i}", "", 0, 45, cls, point);
        var list = Enumerable.Range(1, 3800).Select(i => Row(i, i % 2 == 0 ? 8 : 2, 40_000_000 - i * 10)).ToList();
        var top = GameSession.RankOf(list, list[0].CharacterId)!;
        Assert.Equal((1, 1, 1, 1), (top.Rank, top.Grade, top.ClassRank, top.ClassGrade));
        Assert.Equal(20, GameSession.AbyssTierLevel(top.ClassGrade));
        var mid = GameSession.RankOf(list, list[3796].CharacterId)!;   // rank 3797: grade 18 in the list (rank <= 10000, >= 3000 points)
        Assert.Equal((3797, 18, 3800), (mid.Rank, mid.Grade, mid.Total));
        Assert.Equal(19, mid.ClassGrade);   // class rank 1898 is past the class list's 1000
        Assert.Equal(0, GameSession.AbyssTierLevel(19));
        Assert.Equal(3, GameSession.AbyssTierLevel(18));
        Assert.Null(GameSession.RankOf(list, Guid.NewGuid()));
    }

    [Fact]
    public void The_tier_buff_change_matches_the_official_bytes()
    {
        // R1115 i=192623: key 11055, uid 17, 20100, the time to 2026-12-16 07:00:00.049, caster the player, level 3, show_msg, position.
        var buff = PlayerPackets.AbnormalChange(11055, new AbnormalWire(17, GameSession.AbyssTierBuff, 2_661_824_188, 1_797_404_400_049, CasterKey: 11055,
            Level: 3, RangeBase: new Vector3d(-105397.1171875, -99726.765625, 21899.0)), showMsg: true);
        Assert.Equal("2b38af561111844e0000bc36a89e00000000b199a67da2010000af5603028fdacdc762c7c2c70016ab46", Convert.ToHexString(buff).ToLowerInvariant());
    }

    [Fact]
    public async Task The_entry_sends_the_buff_after_0xe25c_and_the_rank_on_request()
    {
        var store = new FakeCharacterStore();
        await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var me = store.States[0];
        store.AbyssRanking.Add((1, new AbyssRankRow(me.Summary.CharacterId, me.WireId, me.Summary.Name, "", 0, 45, 8, 9165)));
        store.AbyssRanking.Add((1, new AbyssRankRow(Guid.NewGuid(), 999, "Other", "", 0, 45, 2, 20_000)));
        store.AbyssRanking.Add((2, new AbyssRankRow(Guid.NewGuid(), 998, "Dark", "", 0, 45, 8, 50_000)));
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 55, WorldId = store.WorldId }, new Random(5)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)me.WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        var entry = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        Assert.Contains(entry, b => b.SequenceEqual(Convert.FromHexString("588d00")));   // MyPrevSeasonRanking_NT
        var at = entry.FindIndex(b => b.SequenceEqual(Convert.FromHexString("5ce2014b01")));
        var buff = entry[at + 1];
        Assert.Equal(0x382b, GameSessionTestHelpers.Op(buff));
        var r = new PacketReader(buff) { Offset = 2 };
        r.Varint();
        Assert.Equal(0x11, r.U8());
        r.Varint();
        Assert.Equal(GameSession.AbyssTierBuff, r.I32());
        Assert.Equal(GameSession.SeasonEndMs - Now, r.I64());
        Assert.Equal(GameSession.SeasonEndMs, r.I64());
        r.Varint();
        Assert.Equal(3, r.U8());   // rank 2 of 2, class rank 1 of 1 at 9165 points: class grade 18 -> level 3
        Assert.Equal(2, r.U8());   // show_msg
        var reply = Assert.Single(await session.HandleAsync([0x56, 0x8d, 1, 0], Now));
        var rs = new PacketReader(reply) { Offset = 2 };
        Assert.Equal((0, 1, 0), (rs.U16(), rs.U8(), rs.U8()));
        Assert.Equal(2, rs.I32());
        Assert.Equal((long)me.WireId, rs.I64());
        // Nobody ranked in another contents type: the official empty form (R1104 i=1726: 403e, type, class, 54 zero bytes, a time).
        var none = Assert.Single(await session.HandleAsync([0x56, 0x8d, 5, 0], Now));
        Assert.Equal("578d403e0500" + new string('0', 108), Convert.ToHexString(none[..^8]).ToLowerInvariant());
    }
}
