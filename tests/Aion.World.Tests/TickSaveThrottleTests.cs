using System.Reflection;
using Aion.Application.Gameplay;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Reward limits and dungeon cooltimes are saved when they change (at most once a second), not on every tick.</summary>
public sealed class TickSaveThrottleTests
{
    const long Now = 4_000_000;

    [Fact]
    public async Task Idle_ticks_make_no_reward_limit_or_cooltime_saves()
    {
        var store = new FakeCharacterStore();
        store.RewardLimits.Add(new RewardLimitRecord(1, "2026-10-16", 3));
        store.DungeonCooltimes.Add(new DungeonCooltimeRecord(600000, Now + 3_600_000));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var (limits, cooltimes) = (store.RewardLimitSaves, store.DungeonCooltimeSaves);

        // Ten seconds of ticks at the connection loop's pace and faster.
        for (var at = Now; at <= Now + 10_000; at += 20) await session.TickAsync(at);

        Assert.Equal(limits, store.RewardLimitSaves);
        Assert.Equal(cooltimes, store.DungeonCooltimeSaves);
        Assert.Single(store.DungeonCooltimes);
        Assert.Single(store.RewardLimits);
    }

    [Fact]
    public async Task A_change_is_saved_once_and_then_at_most_once_a_second()
    {
        var store = new FakeCharacterStore();
        store.DungeonCooltimes.Add(new DungeonCooltimeRecord(600000, Now - 1));       // expired: pruned at entry
        store.DungeonCooltimes.Add(new DungeonCooltimeRecord(600001, Now + 500));
        store.DungeonCooltimes.Add(new DungeonCooltimeRecord(600002, Now + 3_600_000));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var before = store.DungeonCooltimeSaves;
        var limits = store.RewardLimitSaves;

        await session.TickAsync(Now + 10);
        await session.TickAsync(Now + 20);
        Assert.Equal(before + 1, store.DungeonCooltimeSaves);
        Assert.Equal([600001, 600002], store.DungeonCooltimes.Select(r => r.DungeonId).Order());

        // A second change inside the second after that save waits for it.
        session.PruneExpiredDungeonCooltimes(Now + 600);
        await session.TickAsync(Now + 600);
        await session.TickAsync(Now + 900);
        Assert.Equal(before + 1, store.DungeonCooltimeSaves);
        await session.TickAsync(Now + 10 + GameSession.ContentLimitsFlushMs);
        Assert.Equal(before + 2, store.DungeonCooltimeSaves);
        Assert.Equal([600002], store.DungeonCooltimes.Select(r => r.DungeonId));

        // Nothing changed since: no more saves.
        for (var at = Now + 1100; at <= Now + 5000; at += 50) await session.TickAsync(at);
        Assert.Equal(before + 2, store.DungeonCooltimeSaves);
        Assert.Equal(limits, store.RewardLimitSaves);
    }

    [Fact]
    public async Task Logout_saves_a_change_the_throttle_held_back()
    {
        var store = new FakeCharacterStore();
        store.DungeonCooltimes.Add(new DungeonCooltimeRecord(600000, Now - 1));
        store.DungeonCooltimes.Add(new DungeonCooltimeRecord(600001, Now + 500));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        await session.TickAsync(Now + 10);
        var saves = store.DungeonCooltimeSaves;

        session.PruneExpiredDungeonCooltimes(Now + 600);
        await session.TickAsync(Now + 600);
        Assert.Equal(saves, store.DungeonCooltimeSaves);

        await session.FlushDeferredPersistenceAsync(Now + 650, default);
        Assert.Equal(saves + 1, store.DungeonCooltimeSaves);
        Assert.Empty(store.DungeonCooltimes);

        // With nothing left to save, logout writes nothing more.
        await session.FlushDeferredPersistenceAsync(Now + 700, default);
        Assert.Equal(saves + 1, store.DungeonCooltimeSaves);
    }

    [Fact]
    public async Task Turn_store_keeps_the_turn_for_a_finished_call_and_releases_it_for_a_pending_one()
    {
        var world = new SharedWorld(GameData.Embedded);
        var turn = new WorldTurn(world);
        var script = ScriptedStore.Create();
        var store = TurnStore.Wrap(script, turn);
        await turn.EnterMapAsync(world.Map(1000), default);
        var before = world.Lock.Stats.Read();

        // Finished at once: the very task comes back and the turn was never let go.
        var done = Task.FromResult<IReadOnlyList<int>>([7]);
        ScriptedStore.Of(script).Answer = _ => done;
        var finished = store.WingsAsync(Guid.Empty, default);
        Assert.Same(done, finished);
        Assert.True(turn.Held);
        Assert.Equal(0, world.Lock.Stats.Read().Since(before).StoreCallsOutside);

        // A finished save without a result (non-generic Task) likewise.
        ScriptedStore.Of(script).Answer = _ => Task.CompletedTask;
        await store.ApplyDailyLegionResetsAsync(DateTime.UnixEpoch, default);
        Assert.True(turn.Held);
        Assert.Equal(0, world.Lock.Stats.Read().Since(before).StoreCallsOutside);

        // Still pending: the turn is released while it runs (others may enter) and held again after.
        var gate = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedStore.Of(script).Answer = _ => gate.Task;
        var pending = store.WingsAsync(Guid.Empty, default);
        Assert.False(pending.IsCompleted);
        Assert.False(turn.Held);
        var other = new WorldTurn(world);
        await other.EnterWorldAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
        other.Exit();
        gate.SetResult([1, 2]);
        Assert.Equal([1, 2], await pending);
        Assert.True(turn.Held);
        Assert.Equal(1, world.Lock.Stats.Read().Since(before).StoreCallsOutside);

        // Failures still arrive through the task, finished or not, and the turn is held afterwards.
        ScriptedStore.Of(script).Answer = _ => throw new InvalidOperationException("sync");
        var thrown = store.WingsAsync(Guid.Empty, default);
        Assert.Equal("sync", (await Assert.ThrowsAsync<InvalidOperationException>(() => thrown)).Message);
        ScriptedStore.Of(script).Answer = _ => Task.FromException<IReadOnlyList<int>>(new InvalidOperationException("faulted"));
        Assert.Equal("faulted", (await Assert.ThrowsAsync<InvalidOperationException>(() => store.WingsAsync(Guid.Empty, default))).Message);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        ScriptedStore.Of(script).Answer = _ => throw new OperationCanceledException(cancel.Token);
        var cancelled = store.WingsAsync(Guid.Empty, cancel.Token);
        Assert.True(cancelled.IsCanceled);
        var failing = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedStore.Of(script).Answer = _ => failing.Task;
        var later = store.WingsAsync(Guid.Empty, default);
        failing.SetCanceled();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => later);
        Assert.True(later.IsCanceled);
        Assert.True(turn.Held);
        Assert.Equal(2, world.Lock.Stats.Read().Since(before).StoreCallsOutside);
        turn.Exit();
    }
}

/// <summary>A store whose every call answers with <see cref="Answer"/> (the test's script).</summary>
public class ScriptedStore : DispatchProxy
{
    public Func<object?[]?, object?> Answer { get; set; } = _ => null;

    public static INativeCharacterStore Create() => Create<INativeCharacterStore, ScriptedStore>();
    public static ScriptedStore Of(INativeCharacterStore store) => (ScriptedStore)(object)store;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Answer(args);
}
