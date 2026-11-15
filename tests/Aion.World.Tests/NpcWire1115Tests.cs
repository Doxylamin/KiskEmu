using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>NPC wire corrections from the official recordings of 2026-11-04 and 2026-11-15 (#81).</summary>
public sealed class NpcWire1115Tests
{
    [Theory]
    [InlineData("1d378fbd0122f7d07cd07c01", 24207, -9, 0, 175.517578125)]   // 2026-11-04 i=7272: dy left out
    [InlineData("1d37949701240c9c3e9c3e01", 19348, 0, 12, 88.04443359375)]  // i=2303: dx left out
    [InlineData("1d37e1d7012014ed14ed01", 27617, 0, 0, 333.39111328125)]     // i=392741: neither
    public void Move_delta_leaves_out_zero_dx_and_dy(string hex, int key, int dx, int dy, double heading) =>
        Assert.Equal(Convert.FromHexString(hex), GamePackets.NpcMoveDelta(key, dx, dy, 0, heading, run: false));

    [Fact]
    public void Move_delta_with_both_deltas_keeps_both_bytes() =>
        Assert.Equal(Convert.FromHexString("1d37" + "b410" + "26" + "ff" + "1f" + "6650" + "6650" + "01"),
            GamePackets.NpcMoveDelta(2100, -1, 31, 0, 0x5066 * 360.0 / 65536, run: false));
}
