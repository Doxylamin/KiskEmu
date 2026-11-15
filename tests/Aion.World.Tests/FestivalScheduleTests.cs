using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>The Shugo festival schedule: FieldEventInfo_NT 0x6000 and FieldEventChanged_NT 0x6001 (official R1104 / R1115).</summary>
public sealed class FestivalScheduleTests
{
    /// <summary>2026-11-15 11:00:00 UTC, a full hour.</summary>
    const long Hour = 1_794_740_400_000;

    static GameSession.FestivalEvent Event(int race, int id) => GameSession.FestivalEvents(race).Single(e => e.Id == id);

    static long I64(string hex) => BitConverter.ToInt64(Convert.FromHexString(hex));

    [Fact]
    public void Changes_match_the_official_bytes()
    {
        var jump = Event(1, 110101);
        long open = I64("8537dddea1010000"), start = I64("fef8dfdea1010000");
        // R1115 i=11191: Prepare (open, open, open + 180 s); i=22495: Progress (open, start, start + 175 s); i=36543: gone.
        Assert.Equal("016003" + "95dc06" + "01" + "8537dddea1010000" + "8537dddea1010000" + "a5f6dfdea1010000",
            Convert.ToHexString(GameSession.FieldEventChanged(jump, (GameSession.FieldEventPrepare, open, open + GameSession.FieldEventReadyMs, open + GameSession.FieldEventReadyMs + jump.ProgressMs))).ToLowerInvariant());
        Assert.Equal("01600395dc06028537dddea1010000fef8dfdea101000096a4e2dea1010000",
            Convert.ToHexString(GameSession.FieldEventChanged(jump, (GameSession.FieldEventProgress, open, start, start + jump.ProgressMs))).ToLowerInvariant());
        Assert.Equal("01600095dc06008075d52abb030000",
            Convert.ToHexString(GameSession.FieldEventChanged(jump, (GameSession.FieldEventNone, 0, 0, 0))).ToLowerInvariant());
        // R1115 i=22494: Racing runs 312 s.
        var racing = Event(1, 110301);
        Assert.Equal("016003dddd06028537dddea1010000fef8dfdea1010000bebbe4dea1010000",
            Convert.ToHexString(GameSession.FieldEventChanged(racing, (GameSession.FieldEventProgress, open, start, start + racing.ProgressMs))).ToLowerInvariant());
    }

    [Fact]
    public void The_list_holds_the_running_events_in_the_official_order()
    {
        Assert.Equal("006000", Convert.ToHexString(GameSession.FieldEventInfo(1, Hour + 1_800_000)).ToLowerInvariant());   // R1112 i=854
        var list = GameSession.FieldEventInfo(1, Hour + 100_000);
        var r = new PacketReader(list) { Offset = 2 };
        Assert.Equal(9u, r.Varint());
        var ids = new List<uint>();
        for (var i = 0; i < 9; i++)
        {
            Assert.Equal(3, r.U8());
            ids.Add(r.Varint());
            Assert.Equal(GameSession.FieldEventPrepare, r.U8());
            Assert.Equal(Hour, r.I64());
            Assert.Equal(Hour, r.I64());
            Assert.Equal(Hour + 180_000, r.I64());
        }
        Assert.Equal([110301u, 110101, 110501, 110201, 110401, 110601, 110701, 110901, 110801], ids);
        // At 11:06:40 Racing, the three that run 300 s and TreasureHunter (230 s) are still on.
        var later = GameSession.FieldEventInfo(1, Hour + 400_000);
        Assert.Equal(5, later[2]);
    }

    [Fact]
    public async Task The_tick_sends_prepare_progress_and_the_ends()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Hour - 2_000);
        Assert.Empty(await Changes(session, Hour - 1_000));
        var prepare = await Changes(session, Hour);
        Assert.Equal(9, prepare.Count);
        Assert.All(prepare, p => Assert.Equal(3, p[2]));
        Assert.Equal(GameSession.FieldEventChanged(Event(1, 110301), (GameSession.FieldEventPrepare, Hour, Hour + 180_000, Hour + 492_000)), prepare[0]);
        Assert.Empty(await Changes(session, Hour + 1_000));
        var progress = await Changes(session, Hour + 180_000);
        Assert.Equal(9, progress.Count);
        Assert.Equal(GameSession.FieldEventChanged(Event(1, 110101), (GameSession.FieldEventProgress, Hour, Hour + 180_000, Hour + 355_000)), progress[1]);
        // JumpJump ends first (175 s), alone; the three 300 s events and Racing last.
        var first = await Changes(session, Hour + 355_000);
        Assert.Equal("01600095dc06008075d52abb030000", Convert.ToHexString(Assert.Single(first)).ToLowerInvariant());
        Assert.Equal(4, (await Changes(session, Hour + 410_000)).Count);   // TrapBoard 195, OdBubble and Masquerade 210, TreasureHunter 230
        Assert.Equal(3, (await Changes(session, Hour + 480_000)).Count);
        Assert.Single(await Changes(session, Hour + 492_000));
        Assert.Empty(await Changes(session, Hour + 600_000));
    }

    static async Task<List<byte[]>> Changes(GameSession session, long now) =>
        (await session.TickAsync(now)).Where(p => GameSessionTestHelpers.Op(p) == GameSession.FieldEventChangedNt).ToList();
}
