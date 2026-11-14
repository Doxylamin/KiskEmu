using System.Reflection;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>The dungeon clear messages: 0x6101 with the clear reward, Complete (3) for the hold, End (4) for 600 s, 0x6109's deaths.</summary>
public sealed class DungeonClearTests
{
    const long Now = 1_760_500_000_000;

    static byte[] Strip(byte[] state) => GameSession.DungeonStateTrailingByte ? state[..^1] : state;

    [Fact]
    public void Complete_result_lists_the_clear_reward_by_item_id()
    {
        // Byte-exact to the community fork's 2026-11-06 234912 #44952 (sealed 310027, its RewardLight).
        var seal = MapEventTables.Embedded.Dungeon(310027)!;
        var reward = seal.RewardLight!.Where(r => r.Length >= 2).Select(r => ((int)r[0], r[1])).ToList();
        Assert.Equal(
            "0161000bbb040001076126b41f02000000000000006326b41f02000000000000009b38be1f0100000000000000977db32502000000000000006bcbb325020000000000000021337037983a00000000000037337037e204000000000000",
            Convert.ToHexStringLower(GameSession.DungeonCompleteResult(310027, true, reward)));
        // Without rewards: ours, OBSERVED `00 60000000 01 00`.
        Assert.Equal("016100600000000100", Convert.ToHexStringLower(GameSession.DungeonCompleteResult(96, true)));
    }

    [Fact]
    public void Complete_state_runs_the_table_hold_from_the_clear()
    {
        // Byte-exact to #44953 (10 s hold); the trailing byte is capture #75 item 1.
        var seal = MapEventTables.Embedded.Dungeon(310027)!;
        var state = GameSession.DungeonState(310027, GameSession.DungeonStateComplete, 0x1a1b2de8a3b, 0x1a1b2deb14b);
        Assert.Equal("00610bbb0400033b8adeb2a10100004bb1deb2a1010000", Convert.ToHexStringLower(Strip(state)));
        Assert.Equal(GameSession.DungeonStateTrailingByte ? 24 : 23, state.Length);
        Assert.Equal(0x1a1b2deb14bL - 0x1a1b2de8a3bL, seal.Hold * 1000L);
    }

    [Fact]
    public void Death_limit_carries_the_copy_deaths()
    {
        Assert.Equal("096105000000", Convert.ToHexStringLower(GameSession.DungeonLimitsAtEntry(5).First()));
        Assert.Equal("096100000000", Convert.ToHexStringLower(GameSession.DungeonLimitsAtEntry().First()));
    }

    static async Task<GameSession> EnterKrao()
    {
        var store = new FakeCharacterStore();
        FakeCharacterStore.SeedExpeditionItemLevel(store);
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 55);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now + 10);
        session.EnterDungeonById(600002, Now + 50);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 100);
        Assert.NotNull(session.Copy);
        return session;
    }

    [Fact]
    public async Task Expedition_clear_sends_kill_npc_result_then_complete_for_the_hold_then_end()
    {
        var session = await EnterKrao();
        var room = MapEventTables.Embedded.Dungeon(600002)!;
        ((IMapEventHost)session).DungeonClear(session.MapEventRuns.First());
        var c = session.Copy!;
        Assert.Equal(c.ClearedMs + room.Hold * 1000L, c.CompleteEndsMs);
        var cleared = await session.TickAsync(c.ClearedMs + 1);
        var ops = cleared.Select(GameSessionTestHelpers.Op).ToList();
        int At(ushort op) => ops.IndexOf(op);
        Assert.True(At(GameSession.DungeonClearKillNpcNt) >= 0 && At(GameSession.DungeonClearKillNpcNt) < At(GameSession.DungeonCompleteResultNt));
        Assert.True(At(GameSession.DungeonCompleteResultNt) < At(GameSession.DungeonStateNt));
        Assert.True(At(GameSession.DungeonStateNt) < At(GameSession.PartyDungeonClearNt));
        var states = cleared.Where(b => GameSessionTestHelpers.Op(b) == GameSession.DungeonStateNt).ToList();
        var complete = Assert.Single(states);
        Assert.Equal(GameSession.DungeonStateComplete, complete[6]);
        Assert.Equal(c.ClearedMs, BitConverter.ToInt64(complete, 7));
        Assert.Equal(c.CompleteEndsMs, BitConverter.ToInt64(complete, 15));
        Assert.False(c.EndSent);

        var ended = await session.TickAsync(c.CompleteEndsMs);
        var end = Assert.Single(ended, b => GameSessionTestHelpers.Op(b) == GameSession.DungeonStateNt);
        Assert.Equal(GameSession.DungeonStateEnd, end[6]);
        Assert.Equal(c.CompleteEndsMs, BitConverter.ToInt64(end, 7));
        Assert.Equal(c.CompleteEndsMs + GameSession.DungeonEndStateMs, BitConverter.ToInt64(end, 15));
        Assert.True(c.EndSent);
        var again = await session.TickAsync(c.CompleteEndsMs + 1000);
        Assert.DoesNotContain(again, b => GameSessionTestHelpers.Op(b) == GameSession.DungeonStateNt);
    }

    [Fact]
    public async Task Re_entry_after_the_clear_sees_the_state_it_is_in()
    {
        var session = await EnterKrao();
        ((IMapEventHost)session).DungeonClear(session.MapEventRuns.First());
        var c = session.Copy!;
        var atEntry = (List<byte[]>)typeof(GameSession).GetMethod("DungeonStateAtEntry", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, [c.ClearedMs + 5])!;
        var state = Assert.Single(atEntry);
        Assert.Equal(GameSession.DungeonStateComplete, state[6]);
        Assert.Equal(c.CompleteEndsMs, BitConverter.ToInt64(state, 15));
        c.EndSent = true;
        atEntry = (List<byte[]>)typeof(GameSession).GetMethod("DungeonStateAtEntry", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, [c.CompleteEndsMs + 5])!;
        state = Assert.Single(atEntry);
        Assert.Equal(GameSession.DungeonStateEnd, state[6]);
        Assert.Equal(c.CompleteEndsMs + GameSession.DungeonEndStateMs, BitConverter.ToInt64(state, 15));
    }

    [Fact]
    public async Task Death_sends_0x6109_once_and_entry_carries_the_count()
    {
        var session = await EnterKrao();
        var died = (List<byte[]>)typeof(GameSession).GetMethod("Die", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, [0, Now + 200])!;
        Assert.Single(died, b => GameSessionTestHelpers.Op(b) == GameSession.DungeonDeathLimitNt);
        var tick = await session.TickAsync(Now + 300);
        Assert.DoesNotContain(tick, b => GameSessionTestHelpers.Op(b) == GameSession.DungeonDeathLimitNt);
        var atEntry = (List<byte[]>)typeof(GameSession).GetMethod("DungeonStateAtEntry", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, [Now + 400])!;
        var limit = Assert.Single(atEntry, b => GameSessionTestHelpers.Op(b) == GameSession.DungeonDeathLimitNt);
        Assert.Equal(1, BitConverter.ToInt32(limit, 2));
    }
}
