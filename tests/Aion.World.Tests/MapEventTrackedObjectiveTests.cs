using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Map-event objectives the server now tracks instead of finishing them by the 30 s fallback (#72, zaraza87's fork): "collect
/// points" scores.
/// </summary>
public sealed class MapEventTrackedObjectiveTests
{
    sealed class Host : IMapEventHost
    {
        public List<string> Calls { get; } = [];
        public HashSet<string> Alive { get; } = new(StringComparer.OrdinalIgnoreCase);
        public void Spawn(string spawner, int delayMs) { Calls.Add($"Spawn {spawner}"); Alive.Add(spawner); }
        public void Despawn(string spawner, int delayMs, bool kill) { Calls.Add($"Despawn {spawner}"); Alive.Remove(spawner); }
        public void SummonAround(string target, string spawner, int count, double radius) { }
        public void MoveNpc(string spawner, string route) { }
        public void FollowPc(string spawner, bool follow) { }
        public void RotateNpc(string spawner, string target) { }
        public void Talk(string talk) { }
        public void ChatBubble(string spawner, string talk) { }
        public void SocialAction(string spawner, int action, bool stop) { }
        public void SetAggroPc(string spawner) { }
        public void SetAggroNpc(string spawner, string target) { }
        public void SetRelationship(string spawner, string relationship) { }
        public void TeleportPc(string point, string effect, string before, string after) { }
        public void MapExit(string point, string mode) { }
        public void DungeonClear(MapEventRun run) { }
        public void DungeonFail(MapEventRun run) { }
        public void MapEnvState(int state) { }
        public void MapEnvSetting(int group, int state) { }
        public void Cutscene(string name) { }
        public void ShowGuide(string guide) { }
        public void FocusCamera(string camera, string talk) { }
        public void ShowNote(string note) { }
        public void Message(string key, string argument) { }
        public void Abnormal(int id, int durationMs, string target, bool remove) { }
        public void EnvObjState(string spawner, string state) { }
        public void EnvObjChangePhase(string spawner, int phase) { }
        public void SetEnvObjCanInteract(string spawner, bool canInteract) { }
        public void DynamicTerrain(string spawner, bool block) { }
        public void Progress(MapEventRun run) { }
        public void Unsupported(MapEventRun run, string what) => Calls.Add($"Unsupported {what}");
        public bool QuestStepActive(int questId, int step) => true;
        public bool MapEventStartAllowed(MapEventDefinition definition) => true;
        public bool NpcInCombat(string spawner) => false;
        public bool SpawnerAlive(string spawner) => Alive.Contains(spawner);
        public bool SpawnerExists(string spawner) => true;
        public bool PlayerDead => false;
        public double? DistanceTo(string spawner) => null;
    }

    static MapEventObjective O(int id, string type, params string[] values) => new(id, type, values);

    /// <summary>A one-step event: the step under test, then a second step that never ends (so the first one's end shows).</summary>
    static MapEventRun Run(Host host, MapEventStep step)
    {
        var last = new MapEventStep(99, [], [O(1, "EnterVolumePC", "Trigger_Never", "1")], []);
        var definition = new MapEventDefinition(100010, [], [], [new MapEventStage(1, "", [step, last])]) { Name = "Tracked_Test" };
        var run = new MapEventRun(definition, 1, host, 0);
        run.Start(0);
        return run;
    }

    static void TickTo(MapEventRun run, long from, long until)
    {
        for (var t = from; t <= until; t += 500) run.Tick(t);
    }

    [Fact]
    public void Score_step_counts_the_unified_kills_points()
    {
        var host = new Host();
        var step = new MapEventStep(1, [], [O(1, "AchieveUnifiedObjectivesScore", "3"), O(2, "AchieveUnifiedObjectivesScore", "5")], [],
            Unified: [O(1, "KillNpc", "M_Test_Normal", "1"), O(2, "KillNpcSubtype", "EliteMonster", "2")], Score: 5);
        var run = Run(host, step);
        // No kill: the score waits (no 30 s fallback).
        TickTo(run, 500, 40_000);
        Assert.Equal([0, 0], run.Goals);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("Unsupported", StringComparison.Ordinal));
        run.Killed(1, "M_Test_Normal", true, false, 41_000, "NormalMonster");
        Assert.Equal([1, 1], run.Goals);
        // A normal monster that is not on the list scores nothing; an elite gives 2.
        run.Killed(2, "M_Test_Other", true, false, 41_100, "NormalMonster");
        Assert.Equal([1, 1], run.Goals);
        run.Killed(3, "M_Test_Elite", true, false, 41_200, "EliteMonster");
        Assert.Equal([3, 3], run.Goals);
        run.Killed(4, "M_Test_Elite", true, false, 41_300, "EliteMonster");
        TickTo(run, 41_500, 42_000);
        Assert.Equal(99, run.StepOrder);
    }

    [Fact]
    public void Score_without_its_list_keeps_the_fallback()
    {
        var host = new Host();
        var run = Run(host, new MapEventStep(1, [], [O(1, "AchieveUnifiedObjectivesScore", "3")], []));
        TickTo(run, 500, 31_000);
        Assert.Equal(99, run.StepOrder);
    }

    const string Wave = "DungeonWave_0001_Wave_L_B_03";

    [Fact]
    public void Wave_spawns_its_orders_in_turn_and_clears()
    {
        var orders = MapEventTables.Embedded.Wave(Wave)!;
        Assert.Equal(10, orders.Length);
        var host = new Host();
        var step = new MapEventStep(1, [new MapEventDirective(1, "StartWave", [Wave])], [O(1, "ClearWave", Wave)], []);
        var run = Run(host, step);
        Assert.Equal([$"Spawn {orders[0][0]}"], host.Calls);
        // The first order alive: nothing more spawns, and no fallback ends the wave.
        TickTo(run, 500, 40_000);
        Assert.Single(host.Calls);
        Assert.Equal(1, run.StepOrder);
        long t = 40_000;
        for (var i = 0; i < orders.Length; i++)
        {
            Assert.Equal(orders[i], run.WaveSpawners(Wave));
            host.Alive.ExceptWith(orders[i]);
            run.Tick(t += 500);
            if (i + 1 < orders.Length) Assert.Contains($"Spawn {orders[i + 1][0]}", host.Calls);
        }
        TickTo(run, t, t + 2_000);
        Assert.Equal(99, run.StepOrder);
    }

    [Fact]
    public void Clear_wave_nobody_starts_runs_from_its_step()
    {
        var host = new Host();
        var run = Run(host, new MapEventStep(1, [], [O(1, "ClearWave", Wave)], []));
        Assert.Equal([$"Spawn {MapEventTables.Embedded.Wave(Wave)![0][0]}"], host.Calls);
        Assert.Equal(1, run.StepOrder);
    }

    [Fact]
    public void Unknown_wave_keeps_the_fallback()
    {
        var host = new Host();
        var run = Run(host, new MapEventStep(1, [], [O(1, "ClearWave", "ReforgedAbyss_Hard_01_Wave_01")], []));
        TickTo(run, 500, 31_000);
        Assert.Equal(99, run.StepOrder);
    }

    [Fact]
    public void Score_steps_carry_their_unified_lists()
    {
        var awaken = MapEventTables.Embedded.Event("Dungeon_Awaken_001_01_MapEvent")!;
        var step = awaken.Stages[0].Steps[0];
        Assert.Equal(11, step.Score);
        Assert.Equal(2, step.Unified!.Length);
        Assert.Equal([3, 6, 9], step.Objectives.Where(o => o.Type == "AchieveUnifiedObjectivesScore").Select(o => o.Count));
        Assert.Equal([2701761, 2701758, 2701759], step.Unified[1].Npcs ?? []);
    }
}
