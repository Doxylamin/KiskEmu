using Aion.Protocol.Native;

namespace Aion.World;

// Toggle skills (Chanter mantras, the Cleric's auras; ToggleSkills, #79). OBSERVED official 2026-11-15 (game version 3533), Sprint
// Mantra 18160000 on (i=286204-286222): the cast accepted with the toggle (0x3801 `01 0000 80191501 01`), its 0x3806 at once,
// the skill's self 0x3804 (effect skill x 100 + 11), then 0x382a with the toggle's abnormal 181600001 (duration -1, the passive
// expire time, skill 18160000). About 50 ms later and then every 1000 ms the System skill 18160001 pulses: 0x3802 (self form),
// 0x3806, 0x3804, and its abnormal 181600011 for 2400 ms, added (0x382a) or refreshed (0x382b), with 0x364a stat 205 +1370.
// OURS: the pulse reaches only the caster (the official also put it on party members in range, filter 180000101); toggling off
// (the same cast with change toggle 0, not recorded) removes both abnormals (0x382c) and stops the pulse; a death ends it.
public sealed partial class GameSession
{
    sealed class ActiveToggle
    {
        public required ToggleSkills.Toggle Toggle;
        public required long NextPulseAt;
    }

    /// <summary>The toggles switched on, by skill.</summary>
    readonly Dictionary<int, ActiveToggle> activeToggles = [];
    /// <summary>The first pulse after the toggle (OBSERVED 49 ms).</summary>
    const int FirstPulseDelayMs = 50;

    public IReadOnlyCollection<int> ActiveToggleSkills => activeToggles.Keys;

    /// <summary>An accepted toggle cast: the self effect, the toggle's abnormal and the pulse scheduled (its 0x3806 is the cast's own).</summary>
    List<byte[]> ToggleOnReplies(int skill, int castUid, long now)
    {
        if (ToggleSkills.For(skill) is not { } toggle) return [];
        var replies = new List<byte[]> { GamePackets.ItemSkillEffect(playerKey, skill, castUid) };
        AddBuff(toggle.Abnormal, -1, skill, now, replies, path: SelfAbnormalPath.Toggle);
        activeToggles[skill] = new ActiveToggle { Toggle = toggle, NextPulseAt = now + FirstPulseDelayMs };
        return replies;
    }

    /// <summary>A toggle switched off (or ended by a death): both abnormals removed and the pulse stopped.</summary>
    List<byte[]> ToggleOff(int skill, byte reason = ExpireEtc)
    {
        if (!activeToggles.Remove(skill, out var active)) return [];
        var gone = buffs.Where(b => b.AbnormalId == active.Toggle.Abnormal || b.AbnormalId == active.Toggle.PulseAbnormal).ToList();
        var speedBefore = BuffStat("MoveSpeed");
        return [.. RemoveBuffs(gone, reason), .. BuffStatsChanged(speedBefore)];
    }

    /// <summary>The cast of a switched-on toggle asking to switch it off: accepted at once, outside the cooldown.</summary>
    List<byte[]>? ToggleOffCast(CastRequest request)
    {
        if (!request.HasChangeToggle || request.ChangeToggle is not (0 or 2) || !activeToggles.ContainsKey(request.SkillId)) return null;
        return [CastReplyFor(request, true), .. ToggleOff(request.SkillId)];
    }

    /// <summary>The tick's pulses: each switched-on toggle's System skill and its abnormal, every PulseEveryMs.</summary>
    List<byte[]> TogglePulses(long now)
    {
        if (activeToggles.Count == 0 || resources is null) return [];
        if (dead)
        {
            foreach (var skill in activeToggles.Keys.ToList()) activeToggles.Remove(skill);
            return [];
        }
        var replies = new List<byte[]>();
        foreach (var (skill, active) in activeToggles)
        {
            if (now < active.NextPulseAt) continue;
            active.NextPulseAt = Math.Max(active.NextPulseAt + active.Toggle.PulseEveryMs, now + 1);
            var pulse = active.Toggle.PulseSkill;
            actionUid = actionUid % 255 + 1;
            replies.Add(GamePackets.ItemSkillStart(playerKey, pulse, actionUid, worldPlayer?.Heading ?? 0));
            replies.Add(GamePackets.SkillEnd(playerKey, pulse, actionUid));
            replies.Add(GamePackets.ItemSkillEffect(playerKey, pulse, actionUid));
            var speedBefore = BuffStat("MoveSpeed");
            AddBuff(active.Toggle.PulseAbnormal, active.Toggle.PulseDurationMs, pulse, now, replies, path: SelfAbnormalPath.Toggle);
            replies.AddRange(BuffStatsChanged(speedBefore));
        }
        return replies;
    }
}
