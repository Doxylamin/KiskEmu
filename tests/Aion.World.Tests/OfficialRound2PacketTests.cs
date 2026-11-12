using Aion.Protocol.Native;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Packet layouts checked against the official capture of 2026-11-12 (round 2).</summary>
public class OfficialRound2PacketTests
{
    const long Now = 1_762_970_000_000;

    [Fact]
    public void Polymorph_update_carries_the_abnormal_uid_as_i32() =>
        Assert.Equal(Convert.FromHexString("5536dd08f2050000"), RidePackets.PolymorphUpdate(1117, 1522));

    [Fact]
    public void Item_use_replies_carry_kItem_Use_Success()
    {
        // 0x5613 / 0x5615 for Odyle Energy (uid 0x0000a6000009bba8, item 503700031, use count 1).
        Assert.Equal(Convert.FromHexString("13560403a8bb090000a600003fda051e"), RidePackets.ItemUseReply(0, 0x0000a6000009bba8, 503700031));
        Assert.Equal(Convert.FromHexString("15560403a8bb090000a600003fda051e0100000000000000"),
            RidePackets.ItemUseByIdReply(0, 0x0000a6000009bba8, 503700031, 1));
        Assert.Equal(1, BitConverter.ToUInt16(RidePackets.ItemUseReply(1, 1, 1), 2));
    }

    [Fact]
    public void Skill_auto_load_update_is_a_list_of_skill_and_count() =>
        Assert.Equal(Convert.FromHexString("223801c27d140102"), GamePackets.SkillAutoLoadUpdate(18120130, 2));

    [Fact]
    public async Task World_entry_sends_contents_tickets_on_0x610b_with_presence_masks()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var list = session.ContentsTicketsList(Now);
        Assert.Equal(0x610b, GameSessionTestHelpers.Op(list));
        var r = new PacketReader(list) { Offset = 2 };
        var count = (int)r.Varint();
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(0x04, r.U8());   // bit 2: remain_count only
            r.I32();
            r.Varint();
        }
        Assert.True(r.End);
    }
}
