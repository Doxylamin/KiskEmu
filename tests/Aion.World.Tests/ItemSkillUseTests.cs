using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Item skills (scrolls, food, potions) against the official sequences of 2026-11-12 r2 and 2026-11-15 (#13).</summary>
public sealed class ItemSkillUseTests
{
    const long Now = 1_760_100_000_000;
    const int SpeedScroll = 511060015, LifePotion = GameSession.DefaultAutoPotion;
    static ushort Op(byte[] p) => GameSessionTestHelpers.Op(p);

    [Fact]
    public void Builders_match_the_official_speed_scroll_bytes()
    {
        // 2026-11-12 r2 i=19270, 19275, 19278: player key 1117, Superior Speed Scroll skill 2210104, action uid 0x7f.
        var yaw = BitConverter.ToSingle(Convert.FromHexString("035f4042"));
        Assert.Equal(Convert.FromHexString("0238dd080038b921007f00dd08035f4042904e0100"), GamePackets.ItemSkillStart(1117, 2210104, 0x7f, yaw));
        Assert.Equal(Convert.FromHexString("0438dd080000dd0838b921007f02eb592c0d01000000904e0100"), GamePackets.ItemSkillEffect(1117, 2210104, 0x7f));
        Assert.Equal(Convert.FromHexString("0638dd0838b921007f00"), GamePackets.SkillEnd(1117, 2210104, 0x7f));
        Assert.Equal(Convert.FromHexString("4a36dd0801cd001c0700000000000000000000"), GamePackets.CharStatsUpdate(1117, [(205, 1820)], 0));
        // 2026-11-15 i=569967: the auto-potion's empty stat update.
        Assert.Equal(Convert.FromHexString("4a36b234000000000000000000"), GamePackets.CharStatsUpdate(0x1a32, 0));
    }

    [Fact]
    public async Task Speed_scroll_follows_the_official_order_without_0x5616_or_0x364b()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        store.Items.Add(FakeCharacterStore.BagItem(SpeedScroll, 2, 900));
        await session.InventoryChangesAsync(default);

        var used = await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(900).ToArray(), Now + 1000);
        var ops = used.Select(Op).ToList();
        Assert.Equal([0x3802, RidePackets.ItemUseRs, 0x3806, 0x3804, 0x561b], ops.Take(5).Select(o => (int)o).ToList());
        Assert.Equal(GameSession.DecreaseItemUseReason, used[4][^2]);
        Assert.Contains((ushort)0x382a, ops);
        Assert.True(ops.IndexOf(0x364a) > ops.IndexOf(0x382a), "the speed in 0x364a stat 205 after the abnormal");
        Assert.DoesNotContain((ushort)0x5616, ops);
        Assert.DoesNotContain((ushort)0x364b, ops);
    }

    [Fact]
    public async Task Auto_potion_sends_0x5616_after_the_item_change_and_no_reply()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        store.Items.Add(FakeCharacterStore.BagItem(LifePotion, 5, 901));
        await session.InventoryChangesAsync(default);
        session.SetResource('H', 10);

        var tick = await session.TickAsync(Now + 2000);
        var ops = tick.Select(Op).ToList();
        Assert.DoesNotContain(RidePackets.ItemUseRs, ops);
        var change = ops.IndexOf(0x561b);
        Assert.True(change > 0 && ops.IndexOf(0x3802) < change && ops.IndexOf(0x3806) < change);
        Assert.Equal(GameSession.DecreaseItemAutoUseReason, tick[change][^2]);
        Assert.Equal(RidePackets.ItemUseNt, ops[change + 1]);
        Assert.Equal(GamePackets.CharStatsUpdate(session.PlayerKey, 0), tick[change + 2]);
        Assert.Equal((ushort)0x3804, ops[change + 3]);
    }
}
