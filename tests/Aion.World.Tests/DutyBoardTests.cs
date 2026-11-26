using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>The duty mission board DutyMissionQuestJournalStatus_NT 0x8d1c at the world entry (official R1115 i=355).</summary>
public sealed class DutyBoardTests
{
    const long Now = 1_794_740_400_000;

    /// <summary>R1115 i=355 (the same bytes in R1112 i=96319 and R1112b i=364): an old board of 15 missions, 1419 bytes.</summary>
    const string Recorded = "1c8d00000000000000000f0100000000de1300000000000000000001056526b41fc8010000e1f5050000000037337037e8070080f0fa020000000037337037e80700408af7010000000023c59a25010040787d0100000000fa489b06010080969800000000000200000061de13000000000000000000010521337037a01f0000e1f505000000006826b41fc8010080f0fa02000000006826b41fc80100408af701000000006526b41fc8010040787d010000000021337037a08d060080969800000000000300000068de1300000000000000000001056726b41fc8010000e1f5050000000037337037e8070080f0fa020000000037337037e80700408af7010000000037337037f4030040787d01000000007052652401008096980000000000040000007bde1300000000000000000001056426b41fc8010000e1f5050000000037337037e8070080f0fa020000000037337037e80700408af701000000006826b41fc8010040787d01000000006726b41fc801008096980000000000050000005fde13000000000000000000010521337037d00f0000e1f505000000006426b41fc8010080f0fa02000000006826b41fc80100408af7010000000037337037e8070040787d0100000000c3ee6524010080969800000000000600000003de1300000000000000000001056426b41fc8010000e1f505000000006426b41fc8010080f0fa020000000021337037e0da0100408af70100000000bdee6524010040787d010000000037337037a01f008096980000000000070000007dde13000000000000000000010537337037640000e1f505000000006626b41fc8010080f0fa02000000004186db060100408af7010000000037337037dc0b0040787d01000000002133703780f1040080969800000000000800000077de1300000000000000000001056826b41fc8010000e1f5050000000037337037f4030080f0fa020000000037337037e80700408af7010000000037337037d00f0040787d01000000006526b41fc80100809698000000000009000000a3de1300000000000000000001056526b41fc8010000e1f5050000000040da051e010080f0fa02000000006129880c0100408af7010000000021337037d089020040787d010000000037337037b8170080969800000000000a00000079de1300000000000000000001056726b41fc8010000e1f5050000000037337037f4030080f0fa020000000037337037dc0b00408af701000000002133703780fa010040787d010000000037337037a01f0080969800000000000b00000007de13000000000000000000010521337037a01f0000e1f505000000006426b41fc8010080f0fa02000000006426b41fc80100408af7010000000042da051e010040787d0100000000c67b860c010080969800000000000c00000062de13000000000000000000010521337037a01f0000e1f5050000000037337037e8070080f0fa020000000021337037e0da0100408af7010000000037337037d00f0040787d010000000037337037a01f0080969800000000000d00000089de130000000000000000000105b9ee6524010000e1f505000000006626b41fc8010080f0fa020000000037337037f40300408af7010000000021337037f0a8020040787d01000000006426b41fc8010080969800000000000e00000063de13000000000000000000010541da051e010000e1f5050000000021337037d08c010080f0fa0200000000825595060100408af701000000002133703780fa010040787d01000000002133703780f1040080969800000000000f00000070de1300000000000000000001056726b41fc8010000e1f5050000000043da051e010080f0fa02000000005d02880c0100408af7010000000021337037b0ea010040787d01000000002133703780f10400809698000000000080ad8243a10100000080ad8243a1010000";

    static GameSession.DutyBoard Parse(byte[] body)
    {
        var r = new PacketReader(body) { Offset = 2 };
        int refreshed = r.I32(), rewarded = r.I32();
        var missions = new List<GameSession.DutyMission>();
        for (var n = r.Varint(); n > 0; n--)
        {
            int uid = r.I32(), quest = r.I32(), questUid = r.I32();
            Assert.Equal(0, r.I32());
            var status = r.U8();
            var rewards = new List<GameSession.DutyReward>();
            for (var k = r.Varint(); k > 0; k--) rewards.Add(new(r.I32(), r.Varint(), r.U8(), r.I64()));
            missions.Add(new(uid, quest, questUid, status, [.. rewards]));
        }
        var expire = r.I64();
        var deleted = new List<int>();
        for (var d = r.Varint(); d > 0; d--) deleted.Add(r.I32());
        var weekly = r.I64();
        Assert.True(r.End);
        return new(refreshed, rewarded, [.. missions], expire, weekly, [.. deleted]);
    }

    [Fact]
    public void The_board_matches_the_official_bytes()
    {
        var recorded = Convert.FromHexString(Recorded);
        Assert.Equal(1419, recorded.Length);
        var board = Parse(recorded);
        Assert.Equal(15, board.Missions.Length);
        // Mission 1: UQ1302016, open (1), its five slots' picks with the slot probs 100M .. 10M.
        var first = board.Missions[0];
        Assert.Equal((1, 1302016, 0, (byte)1), (first.Uid, first.QuestId, first.QuestUid, first.Status));
        Assert.Equal([new GameSession.DutyReward(531900005, 200, 0, 100_000_000), new(930100023, 1000, 0, 50_000_000), new(930100023, 1000, 0, 33_000_000),
            new(630900003, 1, 0, 25_000_000), new(110840058, 1, 0, 10_000_000)], first.Rewards);
        Assert.Equal(Recorded, Convert.ToHexString(GameSession.DutyBoardNotice(board)).ToLowerInvariant());
        // Every recorded reward is a candidate of its package's slot, with that slot's prob.
        var tables = DutyTables.Embedded;
        foreach (var mission in board.Missions)
        {
            var slots = tables.Package(QuestBook.Embedded.Find(QuestBook.Embedded.ById[mission.QuestId])!.Rewards!.Random!);
            Assert.Equal(5, slots.Count);
            for (var i = 0; i < 5; i++)
            {
                var reward = mission.Rewards[i];
                Assert.Equal(slots[i].Prob, reward.Prob);
                Assert.Contains(tables.Slot(slots[i].Slot), c => c.Item == reward.Item && c.Min <= reward.Count && reward.Count <= c.Max);
            }
        }
    }

    [Fact]
    public void A_drawn_board_has_fifteen_missions_of_the_race()
    {
        var board = GameSession.DrawDutyBoard(QuestBook.Embedded, 2, 45, new Random(7), Now)!;
        Assert.Equal(15, board.Missions.Length);
        Assert.Equal(Enumerable.Range(1, 15), board.Missions.Select(m => m.Uid));
        Assert.Equal(15, board.Missions.Select(m => m.QuestId).Distinct().Count());
        foreach (var m in board.Missions)
        {
            var quest = QuestBook.Embedded.Find(QuestBook.Embedded.ById[m.QuestId])!;
            Assert.Equal("DutyMission", quest.Type);
            Assert.True(QuestBook.RaceMatches(quest, 2) && quest.Level <= 45);
            Assert.Equal(1, m.Status);
            Assert.Equal([100_000_000L, 50_000_000, 33_000_000, 25_000_000, 10_000_000], m.Rewards.Select(r => r.Prob));
        }
        Assert.Equal(board.Expire, board.WeeklyExpire);
        Assert.True(board.Expire > Now);
        Assert.Null(GameSession.DrawDutyBoard(QuestBook.Embedded, 1, 10, new Random(7), Now));
    }

    [Fact]
    public async Task A_level_one_character_gets_no_board()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        Assert.Null(session.DutyBoardState);
        Assert.False(store.Settings.ContainsKey("duty_board"));
    }

    [Fact]
    public async Task The_entry_sends_the_stored_board_between_the_recipes_and_the_gather_skills()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var first = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        Assert.True(store.Settings.ContainsKey("duty_board"));
        var board = Assert.IsType<GameSession.DutyBoard>(first.DutyBoardState);
        var again = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 55, WorldId = store.WorldId }, new Random(99)) { Store = store };
        await again.HandleAsync(GameSessionTestHelpers.Login(), Now + 60_000);
        await again.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now + 60_000);
        var entry = await again.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 60_000);
        var ops = entry.Select(GameSessionTestHelpers.Op).ToList();
        var at = ops.IndexOf(GameSession.DutyBoardNt);
        Assert.True(at > 0);
        Assert.Equal(0x8d7e, ops[at - 1]);
        Assert.Equal(0x8d37, ops[at + 1]);
        Assert.Equal(GameSession.DutyBoardNotice(board), entry[at]);   // the stored board, unchanged
    }
}
