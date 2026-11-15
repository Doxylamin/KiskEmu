using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>PresetInfo_NT 0xe28c and PresetSkillUpdate_NT 0xe293 against the official bodies (R1115 i=188132, R1112b i=49554).</summary>
public sealed class PresetInfoTests
{
    const long Now = 1_760_000_200_000;

    /// <summary>R1115 i=188132 (871 bytes): a level-1 character, one worn item, the bar in skill preset 1.</summary>
    const string RecordedInfo = "8ce2010101019b950b0000820000010101010001240301001016e800041016e8000000000000000000000000000301015041e600045041e600000000000000000000000000030102007ee50004007ee5000000000000000000000000000301038027e900048027e900000000000000000000000000030104708fe60004708fe60000000000000000000000000003010520cce5000420cce500000000000000000000000000030106203de80004203de800b02be7000000000000000000030107d008e50004d008e50010a5e500b09ce90000000000020114040000000000000000000000000000000002011504000000000000000000000000000000000201160400000000000000000000000000000000020117040000000000000000000000000000000002020004000000000000000000000000000000000202010400000000000000000000000000000000020202040000000000000000000000000000000002020304000000000000000000000000000000000202040400000000000000000000000000000000020205040000000000000000000000000000000002020604000000000000000000000000000000000202070400000000000000000000000000000000020214040000000000000000000000000000000002021504000000000000000000000000000000000202160400000000000000000000000000000000020217040000000000000000000000000000000002030004000000000000000000000000000000000203010400000000000000000000000000000000020302040000000000000000000000000000000002030304000000000000000000000000000000000203040400000000000000000000000000000000020305040000000000000000000000000000000002030604000000000000000000000000000000000203070400000000000000000000000000000000020314040000000000000000000000000000000002031504000000000000000000000000000000000203160400000000000000000000000000000000020317040000000000000000000000000000000001000100000000000101010111000000000100000000020000000003000000000400000000050000000006000000000700000000080000000009000000000a000000000b000000000c000000000d000000000e000000000f00000000100000000011";

    /// <summary>R1112b i=49554 (280 bytes): preset 1 after a Skill Order edit put 18120000 back on slot 5.</summary>
    const string RecordedUpdate = "93e2010c03010090cf12010490cf1201000000000000000000000000030101501517010450151701202f140190b11701e0921301030102603c170104603c1701000000000000000000000000030103801915010480191501000000000000000000000000030104b08e150104b08e1501000000000000000000000000030105407d140104407d1401000000000000000000000000030106c044130104c044130170f21401000000000000000002010704000000000000000000000000000000000201140400000000000000000000000000000000030115107916010410791601000000000000000000000000030116100814010410081401d0dc150100e11301000000000201170400000000000000000000000000000000";

    /// <summary>The records of one preset back to slot -> [skill, context rows] (the bar's stacks).</summary>
    static Dictionary<int, int[]> ReadSkillRecords(PacketReader r, int count, int no)
    {
        var stacks = new Dictionary<int, int[]>();
        for (var i = 0; i < count; i++)
        {
            var mask = r.U8();
            int preset = r.U8(), slot = r.U8();
            var skill = (mask & 1) != 0 ? r.I32() : 0;
            Assert.Equal(4u, r.Varint());
            var stack = new[] { r.I32(), r.I32(), r.I32(), r.I32() };
            Assert.Equal(GameSession.SkillPresetSlots[i % 12], slot);
            if (preset != no) continue;
            Assert.Equal(skill != 0 ? 3 : 2, mask);
            if (skill != 0)
            {
                Assert.Equal(skill, stack[0]);
                stacks[slot] = stack;
            }
            else Assert.Equal([0, 0, 0, 0], stack);
        }
        return stacks;
    }

    [Fact]
    public void The_preset_info_matches_the_official_bytes()
    {
        var recorded = Convert.FromHexString(RecordedInfo);
        Assert.Equal(871, recorded.Length);
        // Equip preset 1 holds the one worn item; arcana `01 01 01 00`; then the skill part.
        Assert.Equal("8ce201010101" + "9b950b0000820000" + "01" + "01010100" + "0124", Convert.ToHexString(recorded[..21]).ToLowerInvariant());
        var r = new PacketReader(recorded) { Offset = 21 };
        var stacks = ReadSkillRecords(r, 36, 1);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], stacks.Keys.Order());
        Assert.Equal([15010000, 15050000, 15310000, 0], stacks[7]);   // `d008e500 10a5e500 b09ce900 00000000`
        var equip = new SortedDictionary<int, Dictionary<int, long>> { [1] = new() { [1] = 0x0000_8200_000b_959b } };
        Assert.Equal(RecordedInfo, Convert.ToHexString(GameSession.PresetInfoNotice(1, equip, 1, 1, stacks, 1, 1, 1)).ToLowerInvariant());
    }

    [Fact]
    public void The_preset_skill_update_matches_the_official_bytes()
    {
        var recorded = Convert.FromHexString(RecordedUpdate);
        var r = new PacketReader(recorded) { Offset = 2 };
        Assert.Equal((1, 12), ((int)r.U8(), (int)r.Varint()));
        var stacks = ReadSkillRecords(r, 12, 1);
        Assert.True(r.End);
        Assert.Equal([18010000, 0, 0, 0], stacks[0]);
        Assert.Equal(18120000, stacks[5][0]);
        Assert.Equal(RecordedUpdate, Convert.ToHexString(GameSession.PresetSkillUpdate(1, stacks)).ToLowerInvariant());
    }

    static async Task<(GameSession Session, FakeCharacterStore Store, List<byte[]> Entry)> EnterAsync()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 40 }, new Random(42)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        return (session, store, await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now));
    }

    static Dictionary<int, int[]> SkillPresetOf(byte[] info)
    {
        var r = new PacketReader(info) { Offset = 2 };
        r.U8();
        for (var n = r.Varint(); n > 0; n--)
        {
            r.U8();
            for (var k = r.Varint(); k > 0; k--) { r.I64(); r.U8(); }
        }
        Assert.Equal((1, 1, 1, 0), ((int)r.U8(), (int)r.Varint(), (int)r.U8(), (int)r.Varint()));
        Assert.Equal((1, 36), ((int)r.U8(), (int)r.Varint()));
        return ReadSkillRecords(r, 36, 1);
    }

    [Fact]
    public async Task The_entry_carries_the_bar_and_an_edit_announces_the_preset_before_0x5608()
    {
        var (session, store, entry) = await EnterAsync();
        var info = entry.Single(b => GameSessionTestHelpers.Op(b) == GameSession.PresetInfoNt);
        var bar = SkillPresetOf(info);
        Assert.Equal(11_020_000, bar[0][0]);   // the Gladiator's default bar on slot 0

        var edit = await session.HandleAsync(new PacketWriter().U16(GameSession.QuickSlotContextSkillRq).U8(0).U8(3).I32(11_030_000).ToArray(), Now);
        Assert.Equal([GameSession.PresetSkillUpdateNt, GameSession.QuickSlotContextSkillRs, 0x5600], edit.Select(GameSessionTestHelpers.Op));
        var r = new PacketReader(edit[0]) { Offset = 2 };
        Assert.Equal((1, 12), ((int)r.U8(), (int)r.Varint()));
        var update = ReadSkillRecords(r, 12, 1);
        Assert.Equal(11_030_000, update[0][3]);
        Assert.Equal(bar.Keys.Order(), update.Keys.Order());
    }
}
