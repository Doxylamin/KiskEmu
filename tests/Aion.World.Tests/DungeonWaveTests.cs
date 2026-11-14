using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// The daily wave dungeon (DungeonWave_0001, map 900001) spawns its monsters in waves: the ClearWave objective's steps from
/// WaveStepData.dat (Wave_L_21 .. Wave_L_30), the first with the event join, each next one on a kill (OBSERVED in the official
/// run, capture-vm official-20261112-r2). Before, the whole map spawned at entry (all 30 Wave_L spawners, 600 monsters) and the
/// next order waited for the current one to die completely.
/// </summary>
public sealed class DungeonWaveTests
{
    const int WaveMap = 900001;
    const string Wave = "DungeonWave_0001_Wave_L_B_03";
    const string SubEvent = "DungeonWave_0001_Sub_MapEvent";
    const string MainEvent = "DungeonWave_0001_MapEvent";
    static string Step(int n) => $"S1_DungeonWave_0001_Multi_Monster_Wave_L_{n}";

    /// <summary>A host that keeps each spawner's living count, as the session does for its targets.</summary>
    sealed class WaveHost : IMapEventHost
    {
        public List<string> Calls { get; } = [];
        public Dictionary<string, (int Living, int Total)> Counts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Cleared { get; private set; }
        public int Size { get; init; } = 20;

        public void Spawn(string spawner, int delayMs)
        {
            Counts[spawner] = (Size, Size);
            Calls.Add($"Spawn {spawner} {delayMs}");
        }

        public void Kill(string spawner) => Counts[spawner] = (Counts[spawner].Living - 1, Counts[spawner].Total);
        public void Despawn(string spawner, int delayMs, bool kill) => Calls.Add($"Despawn {spawner}");
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
        public void DungeonClear(MapEventRun run) => Cleared = true;
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
        public bool QuestStepActive(int questId, int step) => false;
        public bool MapEventStartAllowed(MapEventDefinition definition) => true;
        public bool NpcInCombat(string spawner) => false;
        public bool SpawnerAlive(string spawner) => Counts.TryGetValue(spawner, out var c) && c.Living > 0;
        public bool SpawnerExists(string spawner) => Counts.ContainsKey(spawner);
        public bool PlayerDead => false;
        public double? DistanceTo(string spawner) => null;
        public (int Living, int Total) SpawnerCount(string spawner) => Counts.GetValueOrDefault(spawner);
    }

    /// <summary>Kills one of the spawner's monsters and reports it to the run, as the session does after a player kill.</summary>
    static void KillOne(MapEventRun run, WaveHost host, string spawner, long now)
    {
        host.Kill(spawner);
        run.Killed(2701418, spawner, true, host.SpawnerAlive(spawner), now, "NormalMonster");
        run.Tick(now);
    }

    [Fact]
    public void Wave_orders_follow_WaveStepData_and_wait_dormant_at_map_load()
    {
        var tables = MapEventTables.Embedded;
        Assert.Equal(Enumerable.Range(21, 10).Select(Step), tables.Wave(Wave)!.Select(s => Assert.Single(s)));
        var waveSpawners = tables.WaveSpawnersOn(WaveMap).ToHashSet();
        Assert.Equal(Enumerable.Range(21, 10).Select(Step).ToHashSet(), waveSpawners);
        Assert.DoesNotContain("S1_DungeonWave_0001_Multi_Monster_MissileTurret_1", waveSpawners);
    }

    [Fact]
    public void First_step_spawns_with_the_join_and_each_next_one_on_the_kill_that_leaves_few_alive()
    {
        var host = new WaveHost();
        var run = new MapEventRun(MapEventTables.Embedded.Event(SubEvent)!, 1, host, 0);
        run.Start(0);
        Assert.Equal([$"Spawn {Step(21)} 0"], host.Calls);

        var now = 1_000L;
        for (var step = 21; step < 30; step++)
        {
            host.Calls.Clear();
            // 20 placed; the next step comes once at most 30% (6) are alive: the 14th kill, 200 ms later.
            for (var kill = 1; kill <= 13; kill++) KillOne(run, host, Step(step), now += 500);
            Assert.Empty(host.Calls);
            KillOne(run, host, Step(step), now += 500);
            Assert.Equal([$"Spawn {Step(step + 1)} 200"], host.Calls);
            // Kills of the previous step's leftovers do not bring another step.
            host.Calls.Clear();
            KillOne(run, host, Step(step), now += 500);
            Assert.Empty(host.Calls);
        }

        Assert.Equal(0, run.Goals[0]);
        for (var kill = 1; kill < 20; kill++) KillOne(run, host, Step(30), now += 500);
        Assert.Equal(0, run.Goals[0]);
        Assert.False(run.Finished);
        // The last step all dead clears the wave: the step's DespawnAll, the event completes.
        KillOne(run, host, Step(30), now += 500);
        Assert.Equal(MapEventRun.Complete, run.Status);
        Assert.Contains("Despawn all", host.Calls);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("Unsupported", StringComparison.Ordinal));
    }

    [Fact]
    public void Wave_objective_no_longer_finishes_by_the_30_second_fallback()
    {
        var host = new WaveHost();
        var run = new MapEventRun(MapEventTables.Embedded.Event(SubEvent)!, 1, host, 0);
        run.Start(0);
        run.Tick(MapEventRun.FallbackMs + 1_000);
        Assert.Equal(0, run.Goals[0]);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("Unsupported", StringComparison.Ordinal));
    }

    [Fact]
    public void Score_goals_count_kills_and_start_the_point_monsters_at_19_and_38()
    {
        var host = new WaveHost();
        var run = new MapEventRun(MapEventTables.Embedded.Event(MainEvent)!, 2, host, 0);
        run.Start(0);
        host.Counts[Step(21)] = (60, 60);
        for (var kill = 1; kill <= 18; kill++) KillOne(run, host, Step(21), kill * 1_000);
        Assert.DoesNotContain(host.Calls, c => c.Contains("PointMonster", StringComparison.Ordinal));
        // OBSERVED: the 19th kill starts PointMonster_1 (0x922d goals 19,19,19,...), the 38th PointMonster_2.
        KillOne(run, host, Step(21), 19_000);
        Assert.Contains("Spawn S1_DungeonWave_0001_Multi_Monster_PointMonster_1 0", host.Calls);
        Assert.Equal([19, 19, 19, 19, 19], run.Goals.Take(5));
        for (var kill = 20; kill <= 38; kill++) KillOne(run, host, Step(21), kill * 1_000);
        Assert.Contains("Spawn S1_DungeonWave_0001_Multi_Monster_PointMonster_2 0", host.Calls);
        Assert.DoesNotContain("Spawn S1_DungeonWave_0001_Multi_Monster_PointMonster_3 0", host.Calls);
        // Nothing finishes the score goals by time any more (they finished all at once after 30 s before).
        run.Tick(120_000);
        Assert.DoesNotContain("Spawn S1_DungeonWave_0001_Multi_Monster_PointMonster_3 0", host.Calls);
    }

    const long Now = 1_760_012_300_000;

    [Fact]
    public async Task Entering_the_daily_dungeon_spawns_only_the_first_step_then_the_next_on_kills()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };   // MapEntrance: level 30 or more
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 45);
        await MapEventScenarioHelpers.EnterDungeonByIdAsync(session, WaveMap, Now + 200);
        Assert.True(session.MapId == WaveMap, string.Join(" | ", session.MapEventNotes));
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, SubEvent, Now + 300, attempts: 16);
        Assert.NotNull(run);

        int Living(int step) => session.Targets.Values.Count(t => t.Name == Step(step) && !t.Dead);
        Assert.Equal(20, Living(21));
        Assert.All(Enumerable.Range(1, 30).Where(n => n != 21), n => Assert.Equal(0, Living(n)));

        var skill = MapEventScenarioHelpers.BasicAttackSkill(session);
        var now = Now + 2_000;
        foreach (var target in session.Targets.Values.Where(t => t.Name == Step(21) && !t.Dead).Take(14).ToList())
        {
            await MapEventScenarioHelpers.SyncPositionAsync(session, target.Position with { X = target.Position.X + 120 }, now);
            target.Hp = 1;
            await MapEventScenarioHelpers.CombatCastAsync(session, skill, target.Key, target.Position, now += 50);
            now += MapEventScenarioHelpers.SkillHitsLandMs(skill) + 100;
            await session.TickAsync(now);
        }
        Assert.Equal(6, Living(21));
        await session.TickAsync(now + 500);
        Assert.Equal(20, Living(22));
        Assert.Equal(0, Living(23));
    }
}
