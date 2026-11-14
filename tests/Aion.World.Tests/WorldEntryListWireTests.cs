using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>The world-entry list messages added from the recorded order (GameSession.EntryConstants.cs), pinned to our own
/// 2026-11-12 recording (client 3535, a level-45 character).</summary>
public sealed class WorldEntryListWireTests
{
    // The entry's server time in that recording (`da75fad0a1010000`).
    static readonly long RecordedNow = BitConverter.ToInt64(Convert.FromHexString("da75fad0a1010000"));

    [Fact]
    public void AccountCurrencyLimitStatus_matches_the_recording() =>
        Assert.Equal("aeff" + "0201000000000200000000" + "da75fad0a1010000", Convert.ToHexStringLower(GameSession.AccountCurrencyLimitStatus(RecordedNow)));

    [Theory]
    [InlineData(0xffa5, "a5ff")]
    [InlineData(0xffa7, "a7ff")]
    public void Kina_status_is_nothing_then_the_time(int opcode, string head) =>
        Assert.Equal(head + "0000000000000000" + "da75fad0a1010000", Convert.ToHexStringLower(GameSession.KinaStatus((ushort)opcode, RecordedNow)));

    [Fact]
    public void ArenaMatchingInfo_matches_the_recording() =>
        Assert.Equal("0a40" + "0101022905012b0502", Convert.ToHexStringLower(GameSession.ArenaMatchingInfo(1321, 1323)));
}
