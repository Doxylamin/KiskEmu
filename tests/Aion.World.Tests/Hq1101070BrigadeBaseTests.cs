using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;
using Xunit.Abstractions;

namespace Aion.World.Tests;

/// <summary>
/// HQ1101070 "Brigade Base Neutralization" (layer 100002, World_L_Starter_InstanceLayer_2_MapEvent). The instance table names
/// DrakanElite_01 (2701128) the layer's boss. It was up from the entry (the open-ClearMapEvent respawn ignored that the script starts
/// it with Multi_Cliff_Drakan_Module_2), and its kill ended the layer before step 6's last waves, step 7 and the scripted clear.
/// </summary>
public sealed class Hq1101070BrigadeBaseTests(ITestOutputHelper output)
{
    const long Now = 1_760_090_100_000;
    const string Event = "World_L_Starter_InstanceLayer_2_MapEvent";
    const int Elite = 2701128;

    static byte[] Use(int key) => new PacketWriter().U16(0x3636).U8(1).Varint(key).I64(0).I32(0).ToArray();

    static async Task<(GameSession Session, MapEventRun Run)> EnterAsync()
    {
        var store = new FakeCharacterStore();
        store.Quests.Add(new QuestRecord("HQ1101040", 1101040, 100, QuestLog.Rewarded, 1, new int[6]));
        store.Quests.Add(new QuestRecord("HQ1101050", 1101050, 101, QuestLog.Rewarded, 1, new int[6]));
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 9, trainingDummy: false);
        var kalid = session.Targets!.Values.First(n => n.NpcId == 2931030);
        await MapEventScenarioHelpers.SyncPositionAsync(session, kalid.Position with { X = kalid.Position.X + 100 }, Now + 5);
        await session.HandleAsync(new PacketWriter().U16(0x8d0a).I32(1101070).I32(kalid.Key).ToArray(), Now + 8);
        Assert.Equal(QuestLog.Incomplete, session.Quests!.Get("HQ1101070")!.Status);
        session.NoCool = true;
        var inst = InstanceTables.Embedded.Of(100002)!;
        var center = new Vector3d(inst.Entry.Center[0], inst.Entry.Center[1], inst.Entry.Center[2]);
        var outside = center with { X = center.X - 3000 };
        session.Teleport(1000, outside, Now + 10);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 15);
        var t = Now + 110;
        for (var step = 0; step <= 30 && session.MapId == 1000; step++, t += 500)
            await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(outside with { X = outside.X + step * 100 }), t);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t + 10);
        Assert.Equal(100002, session.MapId);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, Event, Now + 20_000, attempts: 24);
        Assert.NotNull(run);
        return (session, run!);
    }

    [Fact]
    public async Task The_elite_waits_for_its_spawner_instead_of_standing_there_from_the_entry()
    {
        var (session, run) = await EnterAsync();
        Assert.Equal(1, run.StepOrder);
        Assert.All(session.Targets!.Values.Where(t => t.NpcId == Elite), t => Assert.True(t.Dead, $"{t.Name} is up at step 1"));
    }

    [Fact]
    public async Task The_script_plays_through_its_waves_to_DungeonClear_and_the_quest_completes()
    {
        var (session, run) = await EnterAsync();
        var now = Now + 30_000;
        var objects = WorldObjects.ObjectsOn(100002);
        var boxes = objects.Select((o, i) => (o, i)).Where(x => x.o.Env == 1400823).ToList();
        foreach (var tower in objects.Select((o, i) => (o, i)).Where(x => x.o.Env == 1400824).ToList())
        {
            now += 20_000;
            var box = boxes.OrderBy(b => b.o.At.DistanceSquared(tower.o.At)).First();
            await MapEventScenarioHelpers.SyncPositionAsync(session, box.o.At, now);
            await session.HandleAsync(Use(WorldObjects.FirstKey + box.i), now + 100);
            await session.TickAsync(now + 100 + GameSession.CarryPickupMs);
            await MapEventScenarioHelpers.SyncPositionAsync(session, tower.o.At, now + 1_000);
            await session.HandleAsync(Use(WorldObjects.FirstKey + tower.i), now + 1_100);
            await session.TickAsync(now + 1_500);
        }
        Assert.True(run.StepOrder > 1, $"towers did not clear step 1 (goals {string.Join(',', run.Goals)})");

        for (var i = 0; i < 300 && !run.Finished; i++, now += 1000)
        {
            await MapEventScenarioHelpers.PlayMapEventScriptAsync(session, run, now, maxSeconds: 1);
            // The boss kill alone must not take the player out of the layer while the script still runs.
            if (!run.Finished && run.StepOrder < 7) Assert.Equal(100002, session.MapId);
        }
        foreach (var note in session.MapEventNotes) output.WriteLine(note);
        Assert.True(run.Finished, $"stuck at step {run.StepOrder} goals={string.Join(',', run.Goals)} open={string.Join(" | ", run.Open.Select(o => o.Objective.Type + ":" + o.Objective.Value(0)))}");
        for (var i = 0; i < 20; i++) await session.TickAsync(now + i * 500);
        Assert.True(session.Quests!.Get("HQ1101070")!.Status >= QuestLog.Complete);
    }
}
