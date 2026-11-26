using Aion.Protocol.Native;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Dungeon room records on the live 0x97xx run (#74), against the official 0x9701 room lists of 2026-11-04.</summary>
public sealed class DungeonRoomWireTests
{
    [Fact]
    public void Empty_my_room_matches_the_official_prefix()
    {
        // OBSERVED: result 0, dungeon 600002, then my room of a player in none: 20 zero bytes, the bool byte (public = bit 0), join 2,
        // state 0, no members, no affixes.
        var w = PacketWriter.Opcode(GameSession.DungeonRoomListRs).U16(0).I32(600002);
        GameSession.WriteEmptyDungeonRoomInfo(w);
        Assert.Equal(Convert.FromHexString("01970000c2270900" + new string('0', 40) + "0102000000"), w.ToArray());
    }

    [Fact]
    public void Empty_member_slot_matches_the_official_record()
    {
        // OBSERVED: an empty slot is mask 0, its number, zero dbid/name/pc/level/item level, the login bool (false, packed),
        // role 4 (All), zero combat power, no tickets, mentoring 0, latency 0.
        var w = new PacketWriter();
        GameSession.WriteEmptyDungeonRoomMember(w, 3);
        Assert.Equal(Convert.FromHexString("0003" + "0000000000000000" + "00" + "00000000" + "00000000" + "00000000" + "00" + "04"
            + "0000000000000000" + "00" + "0000"), w.ToArray());
    }

    [Fact]
    public void Room_run_is_the_live_numbers()
    {
        Assert.Equal(0x9700, GameSession.DungeonRoomListRq);
        Assert.Equal(0x9702, GameSession.DungeonRoomInfoNt);
        Assert.Equal(0x970b, GameSession.DungeonRoomAddMemberNt);
        Assert.Equal(0x9330, GameSession.BookmarkedContentListNt);
        Assert.Equal(0xffab, GameSession.AbyssPointUpdateNt);
    }
}
