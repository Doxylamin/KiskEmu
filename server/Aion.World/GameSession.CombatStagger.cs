namespace Aion.World;

public sealed partial class GameSession
{
    readonly List<Target> brokenGuards = [];

    /// <summary>
    /// A hit's drain of a guarded NPC's groggy guard (Stagger.cs): per drain row the 0x3804 switch 00 with its effect id and hit
    /// seq, then 0xe005 with the energy left, or the broken state at 0 (order OBSERVED in the fork's recording 20261019-231752
    /// #31013-#31016: after the damage hit and before the NPC's HP). A broken guard refills GroggyTime later (GuardRefills).
    /// </summary>
    List<byte[]> DrainGuard(int skill, Target target, byte uid, long now, IReadOnlyList<Stagger.DrainRow>? rows = null)
    {
        var replies = Stagger.Drain(target, skill, playerKey, uid, now, rows);
        if (target.GroggyEnergy == 0 && !brokenGuards.Contains(target)) brokenGuards.Add(target);
        return replies;
    }

    /// <summary>The guards this session broke that refill now (Stagger.Refill), for everyone nearby.</summary>
    List<byte[]> GuardRefills(long now)
    {
        if (brokenGuards.Count == 0) return [];
        var replies = new List<byte[]>();
        foreach (var t in brokenGuards.ToList())
        {
            if (t.GroggyEnergy != 0 || t.Dead) { brokenGuards.Remove(t); continue; }
            if (Stagger.Refill(t, now) is { } refilled) { replies.Add(refilled); brokenGuards.Remove(t); }
        }
        return replies;
    }

    bool NpcTargetHasAbnormalEffect(int targetKey, string effectType, long now)
    {
        if (targets?.TryGetValue(targetKey, out var victim) == true && victim.OwnerKey == 0
            && effectType.Equals("Stagger", StringComparison.OrdinalIgnoreCase) && victim.StaggerUntil > now)
            return true;
        if (!npcBuffs.TryGetValue(targetKey, out var list)) return false;
        return list.Any(b => AbnormalSpecs.Types(b.AbnormalId).Any(t => t.Equals(effectType, StringComparison.OrdinalIgnoreCase)));
    }
}
