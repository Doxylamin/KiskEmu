using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Scripted NPCs finish their talk (the NpcTalk lines' PlayTime) and their fight before the next directive walks them on
/// (live: the cutscene NPCs ran to their next point mid-sentence and mid-fight).
/// </summary>
public sealed class MapEventCutsceneWaitTests
{
    sealed class TimelineHost : IMapEventHost
    {
        public long Now { get; set; }
        public List<(long At, string Call)> Calls { get; } = [];
        public HashSet<string> Fighting { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> TalkTimes { get; } = new(StringComparer.OrdinalIgnoreCase);
        void Call(string text) => Calls.Add((Now, text));
        public long? At(string call) => Calls.Where(c => c.Call == call).Select(c => (long?)c.At).FirstOrDefault();

        public int TalkDurationMs(string talk) => TalkTimes.TryGetValue(talk, out var ms) ? ms : NpcTalks.DurationMs(talk);
        public void Spawn(string spawner, int delayMs) => Call($"Spawn {spawner}");
        public void Despawn(string spawner, int delayMs, bool kill) => Call($"Despawn {spawner}");
        public void SummonAround(string target, string spawner, int count, double radius) { }
        public void MoveNpc(string spawner, string route) => Call($"Move {spawner} {route}");
        public void FollowPc(string spawner, bool follow) { }
        public void RotateNpc(string spawner, string target) { }
        public void Talk(string talk) => Call($"Talk {talk}");
        public void ChatBubble(string spawner, string talk) => Call($"Bubble {talk}");
        public void SocialAction(string spawner, int action, bool stop) { }
        public void SetAggroPc(string spawner) { }
        public void SetAggroNpc(string spawner, string target) => Call($"AggroNpc {spawner} {target}");
        public void SetRelationship(string spawner, string relationship) => Call($"Relationship {spawner} {relationship}");
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
        public void Unsupported(MapEventRun run, string what) { }
        public bool QuestStepActive(int questId, int step) => true;
        public bool MapEventStartAllowed(MapEventDefinition definition) => true;
        public bool NpcInCombat(string spawner) => Fighting.Contains(spawner);
        public bool SpawnerAlive(string spawner) => true;
        public bool SpawnerExists(string spawner) => true;
        public bool PlayerDead => false;
        public double? DistanceTo(string spawner) => null;
    }

    const string Mard = "N_D_Starter_HQ001_Mard_01";

    static MapEventDirective D(int id, string type, params string[] values) => new(id, type, values);

    /// <summary>One step whose pre-directives are the script under test, and an objective the test never meets.</summary>
    static (MapEventRun Run, TimelineHost Host) Start(TimelineHost host, params MapEventDirective[] script)
    {
        var step = new MapEventStep(1, script, [new MapEventObjective(1, "EnterVolumePC", ["Trigger_Never", "100010"])], []);
        var definition = new MapEventDefinition(100010, [], [], [new MapEventStage(1, "", [step])]) { Name = "CutsceneWait_Test" };
        var run = new MapEventRun(definition, 1, host, 0);
        run.Start(0);
        return (run, host);
    }

    static void TickTo(MapEventRun run, TimelineHost host, long until, long stepMs = 100)
    {
        for (var t = host.Now + stepMs; t <= until; t += stepMs)
        {
            host.Now = t;
            run.Tick(t);
        }
    }

    [Fact]
    public void Talk_times_come_from_the_client_NpcTalk_table()
    {
        Assert.Equal(3000, NpcTalks.DurationMs("D_Starter_HQ001_Caption_06"));
        Assert.Equal(5000, NpcTalks.DurationMs("HQ001_MapEvent_Caption_01"));
        Assert.Equal(0, NpcTalks.DurationMs("No_Such_Talk"));
    }

    [Fact]
    public void Move_after_a_talk_starts_no_earlier_than_the_talk_ends()
    {
        // HQ001 (World_D_Starter_HQ001), the second firewood: Mard's caption, then his walk to the next camp.
        var (run, host) = Start(new TimelineHost(),
            D(1, "PlayNpcTalk", "D_Starter_HQ001_Caption_06", ""),
            D(2, "NpcMoveToWayPoint", Mard, "N_D_Starter_HQ001_Mard_01_Waypoints5", "True"));
        var talkMs = NpcTalks.DurationMs("D_Starter_HQ001_Caption_06");
        Assert.True(talkMs > 0);
        Assert.Equal(0, host.At("Talk D_Starter_HQ001_Caption_06"));
        TickTo(run, host, talkMs - 100);
        Assert.Null(host.At($"Move {Mard} N_D_Starter_HQ001_Mard_01_Waypoints5"));
        TickTo(run, host, talkMs + 500);
        var moveAt = host.At($"Move {Mard} N_D_Starter_HQ001_Mard_01_Waypoints5");
        Assert.NotNull(moveAt);
        Assert.True(moveAt >= host.At("Talk D_Starter_HQ001_Caption_06") + talkMs, $"move at {moveAt}, talk ends at {talkMs}");
    }

    [Fact]
    public void Talk_lines_add_up_and_a_talk_without_a_row_takes_the_default()
    {
        var host = new TimelineHost();
        host.TalkTimes["Two_Lines"] = 7000;
        var (run, _) = Start(host,
            D(1, "PlayNpcTalk", "Two_Lines", ""),
            D(2, "PlayNpcTalk", "No_Such_Talk", ""),
            D(3, "NpcMoveToWayPoint", Mard, "Route", "True"));
        TickTo(run, host, 12_000);
        Assert.Equal(7000, host.At("Talk No_Such_Talk"));
        Assert.Equal(7000 + MapEventRun.DefaultTalkMs, host.At($"Move {Mard} Route"));
    }

    [Fact]
    public void A_bubble_and_a_trailing_talk_do_not_hold_anything()
    {
        var host = new TimelineHost();
        var (run, _) = Start(host,
            D(1, "PlayChatbubble", Mard, "D_Starter_HQ001_Bubble_02"),
            D(2, "NpcMoveToWayPoint", Mard, "Route", "True"),
            D(3, "PlayNpcTalk", "D_Starter_HQ001_Caption_06", ""));
        Assert.Equal(0, host.At($"Move {Mard} Route"));
        Assert.Equal(0, host.At("Talk D_Starter_HQ001_Caption_06"));
    }

    [Fact]
    public void Move_waits_until_the_NPCs_fight_is_over()
    {
        var host = new TimelineHost();
        host.Fighting.Add(Mard);
        var (run, _) = Start(host,
            D(1, "SetAggroNpc", Mard, "M_D_Starter_HQ001_SkurvWar_01_3"),
            D(2, "NpcMoveToWayPoint", Mard, "N_D_Starter_HQ001_Mard_01_Waypoints2", "True"));
        TickTo(run, host, 8000);
        Assert.Null(host.At($"Move {Mard} N_D_Starter_HQ001_Mard_01_Waypoints2"));
        host.Fighting.Clear();
        TickTo(run, host, 8100);
        Assert.Equal(8100, host.At($"Move {Mard} N_D_Starter_HQ001_Mard_01_Waypoints2"));
    }

    [Fact]
    public void A_relationship_change_before_the_move_ends_the_fight_and_a_fight_never_stalls_the_script()
    {
        // HQ001 at Elvida's 70 % HP: Mard turns NPC_Dark, talks and leaves the fight.
        var host = new TimelineHost();
        host.Fighting.Add(Mard);
        host.Fighting.Add("Other");
        var (run, _) = Start(host,
            D(1, "ChangeRelationshipEntity", Mard, "NPC_Dark"),
            D(2, "PlayNpcTalk", "D_Starter_HQ001_Caption_07", ""),
            D(3, "Delay", "500"),
            D(4, "NpcMoveToWayPoint", Mard, "N_D_Starter_HQ001_Mard_01_Waypoints3", "True"),
            D(5, "NpcMoveToWayPoint", "Other", "Route", "True"));
        var talkMs = NpcTalks.DurationMs("D_Starter_HQ001_Caption_07");
        TickTo(run, host, talkMs + 600);   // the Delay (500) runs alongside the talk: the walk comes when the talk is over
        Assert.Equal(talkMs, host.At($"Move {Mard} N_D_Starter_HQ001_Mard_01_Waypoints3"));
        Assert.Null(host.At("Move Other Route"));
        TickTo(run, host, talkMs + MapEventRun.FightWaitMs + 100);
        Assert.Equal(talkMs + MapEventRun.FightWaitMs, host.At("Move Other Route"));
    }
}
