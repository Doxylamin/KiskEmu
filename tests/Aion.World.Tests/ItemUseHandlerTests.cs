using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class ItemUseHandlerTests
{
    const long Now = 1_760_030_000_000;

    [Fact]
    public async Task Unimplemented_use_effect_replies_with_failure_result()
    {
        const int godstoneItem = 513830001;
        Assert.Equal("GetGodstone", RewardBoxes.Embedded.Use(godstoneItem)!.Effect);
        Assert.False(RewardBoxes.Embedded.ImplementsUseEffect("GetGodstone"));

        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.BagItem(godstoneItem, 1, 880));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);

        var replies = await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(880).ToArray(), Now + 1);
        var rs = replies.Single(p => GameSessionTestHelpers.Op(p) == RidePackets.ItemUseRs);
        Assert.Equal((ushort)1, BitConverter.ToUInt16(rs, 2));
        Assert.Contains(store.Items, i => i.TemplateId == godstoneItem && i.Quantity > 0);
    }

    [Fact]
    public async Task Using_several_kina_chests_by_item_id_grants_kina_for_each()
    {
        const int chest = 516160001;   // GetKina 50000
        Assert.Equal("GetKina", RewardBoxes.Embedded.Use(chest)!.Effect);
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.BagItem(chest, 5, 882));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);
        var before = FakeCharacterStore.KinaIn(store);

        // 0x5614: uid, item id, use count 3 (live 22 bytes).
        var replies = await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseByIdRq).I64(882).I32(chest).I64(3).ToArray(), Now + 1);
        var rs = replies.First(p => GameSessionTestHelpers.Op(p) == RidePackets.ItemUseByIdRs);
        Assert.Equal((ushort)0, BitConverter.ToUInt16(rs, 2));
        Assert.Equal(3L, BitConverter.ToInt64(rs, 16));
        Assert.Equal(before + 150_000, FakeCharacterStore.KinaIn(store));
        Assert.Equal(2, store.Items.Where(i => i.TemplateId == chest).Sum(i => i.Quantity));
    }

    [Fact]
    public async Task Missing_use_data_replies_with_failure_result()
    {
        const int unknownUsable = 999_999_001;
        Assert.Null(RewardBoxes.Embedded.Use(unknownUsable));

        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.BagItem(unknownUsable, 1, 881));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);

        var replies = await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(881).ToArray(), Now + 1);
        var rs = replies.Single(p => GameSessionTestHelpers.Op(p) == RidePackets.ItemUseRs);
        Assert.Equal((ushort)1, BitConverter.ToUInt16(rs, 2));
    }
}
