using System.Text.Json;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Bag and warehouse expansion: ItemContainerSlotExtend 0x8d4f/0x8d50/0x8d51 (ported from the zaraza87 fork, #72).</summary>
public sealed class SlotExtendTests
{
    const long Now = 1_760_100_100_000;

    static byte[] Request(byte container, long uid = 0) =>
        new PacketWriter().U16(GameSession.SlotExtendRq).I32(0).U8(container).I64(uid).U8(0).ToArray();

    static ushort Result(byte[] rs) => BitConverter.ToUInt16(rs, 2);

    [Fact]
    public void Request_is_the_16_bytes_the_live_client_sends()
    {
        Assert.Equal(16, Request(1).Length);
        Assert.Equal(0x398a + 0x53c5, GameSession.SlotExtendRq);
    }

    [Fact]
    public async Task Kina_expansion_adds_a_bag_line_pays_and_persists()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 15_000, 941));
        await session.InventoryChangesAsync(default);
        var before = session.SlotCapacity(InventoryItem.Bag);

        var replies = await session.HandleAsync(Request(InventoryItem.Bag), Now);

        var rs = Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == GameSession.SlotExtendRs);
        Assert.Equal(GameSession.SlotExtendSuccess, Result(rs));
        Assert.Equal(0, rs[^2]);   // by item
        Assert.Equal(1, rs[^1]);   // by Kina
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == GameSession.SlotExtendNt);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x561b);
        Assert.Equal(before + GameSession.SlotsPerLine, session.SlotCapacity(InventoryItem.Bag));
        Assert.Equal(15_000 - GameSession.BagExtendKina[0], store.Items.Single(i => i.TemplateId == GameSession.KinaItemId).Quantity);
        Assert.Equal(new[] { 0, 1, 0, 0, 0, 0 }, store.Settings["slot_extends"].Deserialize<int[]>()!);
    }

    [Fact]
    public async Task Not_enough_Kina_is_refused_and_nothing_changes()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 50, 941));
        await session.InventoryChangesAsync(default);

        var replies = await session.HandleAsync(Request(InventoryItem.Bag), Now);

        Assert.Equal(GameSession.SlotExtendFailNotEnoughKina, Result(Assert.Single(replies)));
        Assert.Equal(50, store.Items.Single(i => i.TemplateId == GameSession.KinaItemId).Quantity);
        Assert.Equal(0, session.SlotExtends[1]);
    }

    [Fact]
    public async Task Ticket_expansion_spends_the_named_bag_item()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        store.Items.Add(FakeCharacterStore.BagItem(188100001, 1, 943));
        await session.InventoryChangesAsync(default);

        var replies = await session.HandleAsync(Request(InventoryItem.Bag, 943), Now);

        Assert.Equal(GameSession.SlotExtendSuccess, Result(replies[0]));
        Assert.Equal(1, session.SlotExtends[0]);
        Assert.DoesNotContain(store.Items, i => i.WireId == 943);
    }

    [Fact]
    public async Task Unknown_ticket_and_unexpandable_container_are_refused()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        Assert.Equal(GameSession.SlotExtendFailInvalidItem, Result((await session.HandleAsync(Request(InventoryItem.Bag, 999), Now))[0]));
        Assert.Equal(GameSession.SlotExtendFail, Result((await session.HandleAsync(Request(9), Now))[0]));
    }

    [Fact]
    public async Task Character_warehouse_line_six_costs_1000_Kina()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 1_000, 941));
        await session.InventoryChangesAsync(default);

        var replies = await session.HandleAsync(Request(15), Now);

        Assert.Equal(GameSession.SlotExtendSuccess, Result(replies[0]));
        Assert.Equal(1, session.SlotExtends[4]);
        Assert.DoesNotContain(store.Items, i => i.TemplateId == GameSession.KinaItemId);
    }

    [Fact]
    public async Task Account_warehouse_expansion_is_shared_by_the_account()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 10_000, 941));
        await session.InventoryChangesAsync(default);

        var replies = await session.HandleAsync(Request(InventoryItem.AccountStorage), Now);

        Assert.Equal(GameSession.SlotExtendSuccess, Result(replies[0]));
        Assert.Equal(new[] { 0, 1 }, store.AccountSettings["account_storage_extends"].Deserialize<int[]>()!);
        Assert.False(store.Settings.ContainsKey("slot_extends"));
        // Another character of the account (a new session on the same account's settings) sees the extra line.
        var other = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        Assert.Equal(1, other.SlotExtends[2]);
        Assert.Equal((GameSession.StorageDefaultLines + 1) * GameSession.SlotsPerLine, other.SlotCapacity(InventoryItem.AccountStorage));
    }

    [Fact]
    public void Containers_follow_the_client_server_storage_tab_is_2()
    {
        Assert.Equal(2, InventoryItem.AccountStorage);
        Assert.Equal(15, InventoryItem.Storage);
    }

    [Fact]
    public void Depot_list_carries_the_six_counts()
    {
        var packet = GamePackets.ItemDepotList([], [1, 2, 3, 4, 5, 6]);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, packet[^6..]);
        Assert.Equal(new byte[6], GamePackets.ItemDepotList([])[^6..]);
    }
}
