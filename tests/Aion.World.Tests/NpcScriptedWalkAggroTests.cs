using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// HQ004 pairs NpcMoveToWayPoint with SetAggroNpc for the same NPC (Nania: Waypoints_4 then DracoWar_06, Step2_4 then MixSpawn2).
/// The walk must still reach its end; the fight starts there.
/// </summary>
public sealed class NpcScriptedWalkAggroTests
{
    static (NpcAi Ai, Target Walker, Target Prey, Dictionary<int, Target> Map, NpcRoute Route) Setup()
    {
        var walker = new Target
        {
            Key = 9101, Name = "nania", Hp = 1000, HpMax = 1000, Position = Vector3d.Zero,
            WalkSpeed = 126, RunSpeed = 500, Radius = 40, Function = "Companion", Relationship = 13,
        };
        var prey = new Target
        {
            Key = 9102, Name = "draco", Hp = 1000, HpMax = 1000, Position = new Vector3d(300, 0, 0),
            WalkSpeed = 126, RunSpeed = 500, Radius = 40, Relationship = 11,
        };
        var route = new NpcRoute("walk", [[0.0, 0.0, 0.0], [2000.0, 0.0, 0.0]], "OneWay", Run: true);
        var map = new Dictionary<int, Target> { [walker.Key] = walker, [prey.Key] = prey };
        return (new NpcAi(new Random(1)), walker, prey, map, route);
    }

    static void TickUntil(NpcAi ai, Dictionary<int, Target> map, Func<bool> done, ref long now, int maxMs = 10_000)
    {
        for (var end = now + maxMs; now < end && !done(); now += 100)
            ai.Tick(map, [(1, new Vector3d(0, -5000, 0))], now, (_, _) => []);
    }

    [Fact]
    public void Aggro_given_during_a_scripted_walk_waits_for_the_route_end()
    {
        var (ai, walker, prey, map, route) = Setup();
        ai.Walk(walker, route, 0);
        Assert.Empty(ai.ForceAggroNpc(walker, prey, 0));
        var brain = ai.Brains[walker.Key];
        Assert.Equal(NpcAi.Mode.Scripted, brain.Mode);
        Assert.Same(route, brain.Script);

        long now = 100;
        TickUntil(ai, map, () => brain.Mode != NpcAi.Mode.Scripted, ref now);

        Assert.Equal(2000, walker.Position.X, 0);
        Assert.Contains(ai.TakeArrivals(), a => a.Key == walker.Key && a.Route == "walk");
        Assert.Equal(NpcAi.Mode.Chase, brain.Mode);
        Assert.Equal(prey.Key, brain.VictimKey);
        Assert.True(brain.VictimIsNpc);
    }

    [Fact]
    public void A_new_walk_drops_the_aggro_queued_on_the_previous_one()
    {
        var (ai, walker, prey, map, route) = Setup();
        ai.Walk(walker, route, 0);
        ai.ForceAggroNpc(walker, prey, 0);
        var next = new NpcRoute("next", [[0.0, 500.0, 0.0]], "OneWay", Run: true);
        ai.Walk(walker, next, 0);

        long now = 100;
        var brain = ai.Brains[walker.Key];
        TickUntil(ai, map, () => brain.Mode != NpcAi.Mode.Scripted, ref now);

        Assert.Contains(ai.TakeArrivals(), a => a.Key == walker.Key && a.Route == "next");
        Assert.Equal(NpcAi.Mode.Idle, brain.Mode);
        Assert.NotEqual(prey.Key, brain.VictimKey);
    }

    [Fact]
    public void Aggro_on_a_standing_npc_still_starts_the_fight_at_once()
    {
        var (ai, walker, prey, _, _) = Setup();
        Assert.NotEmpty(ai.ForceAggroNpc(walker, prey, 0));
        Assert.Equal(NpcAi.Mode.Chase, ai.Brains[walker.Key].Mode);
    }
}
