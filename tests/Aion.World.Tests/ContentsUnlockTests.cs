using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class ContentsUnlockTests
{
    [Fact]
    public void Gather_unlocks_at_45_not_10_after_october_patch()
    {
        Assert.DoesNotContain((byte)11, GamePackets.UnlockedContents(44));
        Assert.Contains((byte)11, GamePackets.UnlockedContents(45));
    }

    [Fact]
    public void World_entry_includes_chat_and_customizing_when_quests_unlocked()
    {
        var quest = new byte[] { 43, 45 };
        var list = GamePackets.WorldEntryContentsUnlocks(16, quest, []);
        Assert.Contains((byte)43, list);
        Assert.Contains((byte)45, list);
    }

    [Fact]
    public void World_entry_carries_chat_without_a_later_unlock()
    {
        // OBSERVED (official 2026-11-12/13): every 0xe257 at an entry carries chat (45); 0xe258 is never sent.
        var list = GamePackets.WorldEntryContentsUnlocks(16, [], []);
        Assert.Contains((byte)45, list);
        Assert.Contains((byte)43, list);
    }
}
