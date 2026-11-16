using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public class CastResultFormTests
{
    [Fact]
    public void LiveRangerCastGetsNineByteAccept()
    {
        var reply = GamePackets.CastResult(14020000);
        // OBSERVED official 2026-10-30: 01 38 00 0000 a0edd500 (flags 0, result u16, skill) (#70).
        Assert.Equal(Convert.FromHexString("0138000000A0EDD500"), reply);
        Assert.Equal(0, GameSession.CastResultCode(reply));
        Assert.Equal(1, GameSession.CastResultCode(GamePackets.CastResult(14020000, 1)));
        // The old flags-first accept still reads as accepted by the server's own readers.
        Assert.Equal(0, GameSession.CastResultCode(GamePackets.CastReply(true, 14020000, 0)));
    }

    [Fact]
    public void Dodge_reply_matches_the_official_bytes() =>
        Assert.Equal(Convert.FromHexString("0138000000e4a81201"), GamePackets.DodgeResult(18000100));   // OBSERVED 2026-11-15
}
