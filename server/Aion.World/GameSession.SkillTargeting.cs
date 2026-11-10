using Aion.Protocol.Native;

namespace Aion.World;

// Cast range check (Skill.dat NeedSkillUseRange, tools/data/skill-cast-ranges.json). From A2Core-ChanAge (Artezianec),
// with the table's ranges instead of guessed ones. Only the cast is checked: the targets an area then hits are the
// area's (a splash may reach past the use range).
partial class GameSession
{
    /// <summary>Extra reach beyond NeedSkillUseRange for latency and position drift (ours, not Skill.dat).</summary>
    public const double CastRangeToleranceCm = 150;

    /// <summary>Max vertical separation for a valid skill target (2D combat; ours).</summary>
    public const double CastHeightToleranceCm = 500;

    internal bool CastTargetReachValidForTests(SkillDefinition definition, int skillId, CastRequest request) =>
        CastTargetReachValid(definition, skillId, request);

    internal void SetPositionForTests(Vector3d at) => position = at;

    /// <summary>Whether a cast may resolve on its target or ground point: within the skill's use range (+ tolerance and
    /// the target's collision radius), and not too far above or below. Skills without a range are not checked.</summary>
    bool CastTargetReachValid(SkillDefinition definition, int skillId, CastRequest request)
    {
        var range = data.SkillUseRangeCm(skillId);
        if (range <= 0) return true;
        var max = range + CastRangeToleranceCm;
        var caster = position ?? request.Location;
        var ground = SkillAreas.For(skillId) is { IsGround: true } ga ? ga : CastArea(skillId) is { IsGround: true } gb ? gb : null;
        if (ground is not null)
        {
            var at = ground.Ground == "Location" ? request.Location : caster;
            return HorizontalDistance(caster, at) <= max && Math.Abs(caster.Z - at.Z) <= CastHeightToleranceCm;
        }
        if (request.TargetKey == playerKey) return true;
        // A known NPC (by key: the NPC map is the authority, keys are not split into NPC and player ranges everywhere),
        // else another player on the map. An unknown target is left to the cast's own target checks.
        if (targets?.TryGetValue(request.TargetKey, out var npc) == true && npc is not null)
            return HorizontalDistance(caster, npc.Position) <= max + npc.Radius
                && Math.Abs(caster.Z - npc.Position.Z) <= CastHeightToleranceCm;
        if (worldPlayer?.Map?.Players.GetValueOrDefault(request.TargetKey) is { } victim)
            return HorizontalDistance(caster, victim.Position) <= max
                && Math.Abs(caster.Z - victim.Position.Z) <= CastHeightToleranceCm;
        return true;
    }

    static double HorizontalDistance(Vector3d a, Vector3d b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
