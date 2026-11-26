using Aion.Protocol.Native;

namespace Aion.World;

// The skill presets (EPresetType Skill 2) as PresetInfo_NT 0xe28c and PresetSkillUpdate_NT 0xe293 carry them.
// Layout from zaraza87/A2Core (via A2Core-ChanAge db5199e); ported from zaraza87's fork, server/Aion.World/GameSession.SkillPresets.cs.
// OBSERVED:
//   - 0xe28c's skill part: the current skill preset no, varint 36, then presets 1-3 x the 12 bar slots 0-7 and 20-23, each record
//     {mask u8 (3: a skill, 2: none), preset no, slot, [skill i32 when mask 3], varint 4, 4 x i32 (the skill and its context rows,
//     zeros when none)}: all 15 official 0xe28c parse (R1104 i=1044 preset 1 slot 0 = 17010000; R1115 i=188132 a level-1 bar in
//     preset 1, presets 2 and 3 empty; slots 20-23 always empty).
//   - 0xe293 {preset no, varint 12, the preset's records}: R1112b i=48688 and i=49554, each right before 0x5608, the reply to a
//     Skill Order edit (0x5607): i=48688 with slot 5 emptied, i=49554 with 18120000 back on it.
// OURS: the current preset is the bar (the class default with the saved slots over it, GameSession.SkillChains.cs); the other two
// presets are not kept and go out empty; the preset switch (PresetSet_RS 0xe290 with its skill updates) stays open.
// CANDIDATE: QuickSlotSet/Reset (0x5601/0x5603) on a preset slot would announce the preset too (no official sample; not sent).
public sealed partial class GameSession
{
    public const ushort PresetSkillUpdateNt = 0xe293;
    const int PresetSkill = 2;

    /// <summary>The bar slots a skill preset holds, in the official order.</summary>
    public static readonly int[] SkillPresetSlots = [0, 1, 2, 3, 4, 5, 6, 7, 20, 21, 22, 23];

    static readonly IReadOnlyDictionary<int, int[]> NoSkillStacks = new Dictionary<int, int[]>();

    /// <summary>One preset's 12 records from the bar's stacks (slot -> [skill, context rows]).</summary>
    public static PacketWriter WriteSkillPresetSlots(PacketWriter w, int no, IReadOnlyDictionary<int, int[]> stacks)
    {
        foreach (var slot in SkillPresetSlots)
        {
            var stack = stacks.GetValueOrDefault(slot) ?? [];
            var skill = stack.Length > 0 ? stack[0] : 0;
            w.U8(skill != 0 ? 3 : 2).U8(no).U8(slot);
            if (skill != 0) w.I32(skill);
            w.Varint(4);
            for (var i = 0; i < 4; i++) w.I32(skill != 0 && i < stack.Length ? stack[i] : 0);
        }
        return w;
    }

    /// <summary>PresetInfo_NT's skill part: the current no, then presets 1-3 (the current one from the bar, the others empty).</summary>
    public static PacketWriter WriteSkillPresets(PacketWriter w, int current, IReadOnlyDictionary<int, int[]> stacks)
    {
        w.U8(current).Varint(PresetCount * SkillPresetSlots.Length);
        for (var no = 1; no <= PresetCount; no++) WriteSkillPresetSlots(w, no, no == current ? stacks : NoSkillStacks);
        return w;
    }

    /// <summary>PresetSkillUpdate_NT 0xe293 {preset no, varint 12, its records} (OBSERVED R1112b i=49554).</summary>
    public static byte[] PresetSkillUpdate(int no, IReadOnlyDictionary<int, int[]> stacks) =>
        WriteSkillPresetSlots(PacketWriter.Opcode(PresetSkillUpdateNt).U8(no).Varint(SkillPresetSlots.Length), no, stacks).ToArray();

    /// <summary>The current skill preset as the bar has it now, announced after a bar edit.</summary>
    byte[] CurrentSkillPresetUpdate() => PresetSkillUpdate(PresetNo(PresetSkill), EffectiveSkillStacks());
}
