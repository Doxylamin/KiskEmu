using Aion.Protocol.Native;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Monolith messages on the live run (#74), against our official 2026-11-12 capture.</summary>
public sealed class MonolithWireTests
{
    [Fact]
    public void Resonate_reply_matches_the_official_bytes()
    {
        // OBSERVED: six tokens at Monolith 1001, 264 -> 270, no reward items.
        var reply = GamePackets.MonolithResonateReply(0, 1001, 631900001, 6, 264, 270, []);
        Assert.Equal(Convert.FromHexString("0ee30000e90300006107aa2506000000080100000e01000000"), reply);
    }

    [Fact]
    public void List_matches_the_official_bytes()
    {
        var list = GamePackets.MonolithList([(1001, 264), (1002, 0), (3001, 1)]);
        Assert.Equal(Convert.FromHexString("0be303e903000008010000ea03000000000000b90b000001000000"), list);
    }
}
