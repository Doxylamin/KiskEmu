using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// Toggle skills (Chanter mantras, the Cleric's auras) from tools/data/toggle-skills.json (tools/toggle_skills.py): the abnormal
/// the toggle puts on (Skill.dat ToggleOnAbnormalId) and the System skill it pulses with that skill's abnormal and duration.
/// </summary>
public static class ToggleSkills
{
    public sealed record Toggle(
        [property: JsonPropertyName("abnormal")] int Abnormal,
        [property: JsonPropertyName("pulse_skill")] int PulseSkill = 0,
        [property: JsonPropertyName("pulse_abnormal")] int PulseAbnormal = 0,
        [property: JsonPropertyName("pulse_duration_ms")] int PulseDurationMs = 0,
        [property: JsonPropertyName("pulse_every_ms")] int PulseEveryMs = 0);

    sealed record Document([property: JsonPropertyName("skills")] Dictionary<string, Toggle> Skills);

    static readonly Lazy<IReadOnlyDictionary<int, Toggle>> Table = new(() =>
    {
        using var stream = typeof(ToggleSkills).Assembly.GetManifestResourceStream("toggle-skills.json")
            ?? throw new InvalidOperationException("toggle-skills.json is not embedded");
        var document = JsonSerializer.Deserialize<Document>(stream) ?? throw new InvalidDataException("toggle-skills.json");
        return document.Skills.ToDictionary(p => int.Parse(p.Key), p => p.Value);
    });

    /// <summary>The toggle of a skill with a pulse, or null (a toggle without one is only echoed).</summary>
    public static Toggle? For(int skillId) => Table.Value.TryGetValue(skillId, out var t) && t.PulseSkill > 0 && t.Abnormal > 0 ? t : null;
}
