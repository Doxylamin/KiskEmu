using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Merchant sell/buyback, bag dismantling (ItemDecompose), and ItemSort packet flows.</summary>
public sealed class MerchantInventoryScenarioTests
{
    const long Now = 1_760_100_000_000;
    const int LifePotion = 530126003;

    static long KinaTotal(FakeCharacterStore store) =>
        store.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity);

    [Fact]
    public async Task Sell_to_merchant_and_buyback_round_trip()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterMap1010Async(store, Now);
        var grocer = session.Targets.Values.First(n => n.NpcId == 2930067);
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 5000, 42));
        store.Items.Add(FakeCharacterStore.BagItem(LifePotion, 1, 43, 0));
        await session.InventoryChangesAsync(default);

        var kinaBefore = KinaTotal(store);
        var open = await session.HandleAsync(new PacketWriter().U16(GameSession.ShopOpenRq).Varint((uint)grocer.Key).ToArray(), Now);
        Assert.Equal(GameSession.ShopOpenRs, GameSessionTestHelpers.Op(open.Single()));

        var sold = await session.HandleAsync(new PacketWriter().U16(GameSession.ShopSellRq).Varint((uint)grocer.Key).Varint(1).I64(43).I64(1).ToArray(), Now);
        // Official order (OBSERVED 2026-11-15): 0xe342, the bag change, then 0x8c05 with 1552 and the NPC key as an i32.
        Assert.Equal((ushort)0xe342, GameSessionTestHelpers.Op(sold[0]));
        Assert.Equal(GameSession.ShopSellRs, GameSessionTestHelpers.Op(sold[^1]));
        Assert.Equal(GameSession.ShopSellOk, BitConverter.ToUInt16(sold[^1], 2));
        Assert.Equal(grocer.Key, BitConverter.ToInt32(sold[^1], 4));
        Assert.Equal(kinaBefore + 2, KinaTotal(store));
        var buybackRow = store.Items.Single(i => i.Location == "buyback");
        Assert.True(sold.Any(b => b[0] == 0x42 && b[1] == 0xe3), "0xe342 RestorableItemList after sell");

        var restore = await session.HandleAsync(new PacketWriter().U16(GameSession.ItemRestoreRq).Varint(1).I64(buybackRow.WireId).ToArray(), Now);
        Assert.Equal(0, BitConverter.ToUInt16(restore[0], 2));
        Assert.Equal(kinaBefore, KinaTotal(store));
        Assert.Single(store.Items, i => i.TemplateId == LifePotion && i.Location == "bag");
    }

    [Fact]
    public async Task Item_decompose_pays_kina_and_removes_gear()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, rng: new Random(7));
        store.Items.Add(FakeCharacterStore.BagItem(110160001, 1, 940, 9));
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 5000, 941));
        await session.InventoryChangesAsync(default);
        var kinaBefore = KinaTotal(store);

        var reply = await session.HandleAsync(new PacketWriter().U16(GameSession.ItemDecomposeRq).U8(0).Varint(1).I64(940).ToArray(), Now);
        Assert.Equal(GameSession.ItemDecomposeRs, GameSessionTestHelpers.Op(reply[0]));
        Assert.Equal(0, BitConverter.ToInt32(reply[0], 2));
        Assert.Equal(1, BitConverter.ToInt32(reply[0], 7));
        Assert.DoesNotContain(store.Items, i => i.WireId == 940);
        Assert.Equal(kinaBefore - 1000, KinaTotal(store));
    }

    [Fact]
    public async Task Item_sort_puts_currencies_first_then_orders_by_template_id()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        store.Items.RemoveAll(i => i.Location == "bag");
        // OBSERVED R1115: 930100031, Kina 930100001, 930100003/011/017/021/023/019 on slots 0-7, other items after.
        foreach (var (template, wire) in new[] { (530116001, 40L), (930100019, 41L), (LifePotion, 42L), (930100023, 43L), (GameSession.KinaItemId, 44L),
                     (930100021, 45L), (930100031, 46L), (930100017, 47L), (930100011, 48L), (930100003, 49L) })
            store.Items.Add(FakeCharacterStore.BagItem(template, 10, wire, (int)(wire - 40)));
        await session.InventoryChangesAsync(default);

        var sort = await session.HandleAsync(new PacketWriter().U16(GameSession.ItemSortRq).I32(0).U8(1).ToArray(), Now);
        var ordered = store.Items.Where(i => i.Location == "bag").OrderBy(i => i.Slot).Select(i => i.TemplateId).ToList();
        Assert.Equal([930100031, GameSession.KinaItemId, 930100003, 930100011, 930100017, 930100021, 930100023, 930100019, 530116001, LifePotion], ordered);
        // The item changes first, then 0x563f {u16 576, npc, container} (official `3f564002000000000f` for npc 0, container 15).
        Assert.True(sort.Count >= 2);
        Assert.Equal(new PacketWriter().U16(GameSession.ItemSortRs).U16(576).I32(0).U8(1).ToArray(), sort[^1]);
        Assert.Equal(0x561b, GameSessionTestHelpers.Op(sort[0]));
    }

    [Fact]
    public async Task Item_sort_orders_storage_container()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        store.Items.Add(FakeCharacterStore.StorageItem(530116001, 3, 50));
        store.Items.Add(FakeCharacterStore.StorageItem(LifePotion, 1, 51));
        await session.InventoryChangesAsync(default);

        var sort = await session.HandleAsync(new PacketWriter().U16(GameSession.ItemSortRq).I32(0).U8(2).ToArray(), Now);
        Assert.Equal(GameSession.ItemSortDone, BitConverter.ToUInt16(sort[^1], 2));
        var ordered = store.Items.Where(i => i.Location == "storage").OrderBy(i => i.Slot).Select(i => i.TemplateId).ToList();
        Assert.Equal(ordered.Order().ToList(), ordered);
    }

    [Fact]
    public async Task Item_sort_accepts_the_account_warehouse()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        store.Items.Add(FakeCharacterStore.StorageItem(LifePotion, 1, 60, 0) with { Location = "account_storage" });
        store.Items.Add(FakeCharacterStore.StorageItem(530116001, 3, 61, 1) with { Location = "account_storage" });
        await session.InventoryChangesAsync(default);

        var sort = await session.HandleAsync(new PacketWriter().U16(GameSession.ItemSortRq).I32(0).U8(InventoryItem.AccountStorage).ToArray(), Now);
        Assert.Equal(new PacketWriter().U16(GameSession.ItemSortRs).U16(GameSession.ItemSortDone).I32(0).U8(InventoryItem.AccountStorage).ToArray(), sort[^1]);
        var ordered = store.Items.Where(i => i.Location == "account_storage").OrderBy(i => i.Slot).Select(i => i.TemplateId).ToList();
        Assert.Equal([530116001, LifePotion], ordered);
    }

    [Fact]
    public async Task Coin_shop_open_succeeds_for_dat_mapped_id()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var open = await session.HandleAsync(new PacketWriter().U16(GameSession.CoinShopOpenRq).I32(7).ToArray(), Now);
        Assert.Equal(GameSession.CoinShopOpenRs, GameSessionTestHelpers.Op(open[0]));
        Assert.Equal(0, BitConverter.ToUInt16(open[0], 2));
    }

    [Fact]
    public async Task Coin_shop_sells_the_goods_of_its_goods_lists()
    {
        // GameShop.dat: coin_shop_arena (990006) lists shop_L_abyss_usable; goods 401015101 = item 530116008 for 500 Abyss points.
        const int abyssPoint = 930100003, goodsId = 401015101, itemId = 530116008;
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        store.Items.Add(FakeCharacterStore.BagItem(abyssPoint, 1_000, 960));
        await session.InventoryChangesAsync(default);
        var buy = await session.HandleAsync(new PacketWriter().U16(GameSession.CoinShopBuyRq).I32(990006).Varint(1).I32(goodsId).I32(1).ToArray(), Now);
        Assert.Equal(GameSession.CoinShopBuyRs, GameSessionTestHelpers.Op(buy[0]));
        Assert.Equal(0, BitConverter.ToUInt16(buy[0], 2));
        Assert.Contains(store.Items, i => i.TemplateId == itemId);
        Assert.Equal(500, store.Items.Single(i => i.TemplateId == abyssPoint).Quantity);
    }

    [Fact]
    public void Coin_shops_list_goods_from_their_client_goods_lists()
    {
        var shops = ShopTables.Embedded;
        // Arena and battlefield sell the abyss lists (GameShop.dat GoodsList), not lists named after themselves.
        Assert.NotNull(shops.GoodsInShop("coin_shop_arena", 1, 401015101));
        Assert.NotNull(shops.GoodsInShop("coin_shop_battlefield", 1, 401015101));
        Assert.Null(shops.GoodsInShop("coin_shop_L_battlepass", 2, 401015101));
        foreach (var dataId in new[] { 990001, 990003, 990006, 990007, 990008, 990009 })
            foreach (var race in new[] { 1, 2 })
                Assert.True(shops.ShopGoodsCount(shops.CoinShopName(dataId)!, race) > 0, $"{dataId} race {race}");
    }

    [Fact]
    public async Task Decompose_increments_grade_scoped_achievement()
    {
        const int rareDecomposeAch = 343101900;
        const int uniqueDecomposeAch = 343102100;
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, rng: new Random(11));
        store.Items.Add(FakeCharacterStore.BagItem(110150003, 1, 950, 0));
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 10_000, 951));
        await session.InventoryChangesAsync(default);

        var reply = await session.HandleAsync(new PacketWriter().U16(GameSession.ItemDecomposeRq).U8(0).Varint(1).I64(950).ToArray(), Now);
        Assert.Equal(0, BitConverter.ToInt32(reply[0], 2));
        Assert.Equal(1, session.AchievementCount(rareDecomposeAch));
        Assert.Equal(0, session.AchievementCount(uniqueDecomposeAch));
        Assert.Contains(reply, b => GameSessionTestHelpers.Op(b) == GameSession.AchievementCountUpdateNt);
    }
}
