using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;
using static Aion.World.Tests.GameSessionTestHelpers;

namespace Aion.World.Tests;

/// <summary>Custom shop goods bought through NPShopPurchase, the purchase ledger, the stored membership and the pass price.</summary>
public sealed class CustomShopTests
{
    const long Now = 1792000000000L;   // 2026-10-14
    const int Spiritstone = 532760001, PaidPass = 100151;
    static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeMilliseconds(Now);

    static CustomShopGoods Goods(int id, string currency = "kina", long price = 500, int limit = 0, int race = 0, int minLevel = 0,
        bool enabled = true, DateTimeOffset? saleTo = null, int target = 1, int period = 0) =>
        new(id, $"Goods {id}", "", "Shop_Usable", "D_Shop_Usable", "", [new CustomShopItem(Spiritstone, 3)], currency, price, limit, target, period,
            race, minLevel, null, saleTo, enabled, 0, At);

    sealed class Catalog(params CustomShopGoods[] goods) : ICustomShopCatalog
    {
        public Task<IReadOnlyList<CustomShopGoods>> GoodsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CustomShopGoods>>(goods);
    }

    static readonly Catalog Shop = new(
        Goods(9_000_001),
        Goods(9_000_002, "free", 0, limit: 1),
        Goods(9_000_003, race: 2),
        Goods(9_000_004, minLevel: 60),
        Goods(9_000_005, enabled: false),
        Goods(9_000_006, saleTo: At.AddDays(-1)),
        Goods(9_000_007, "quna", 300),
        Goods(9_000_008, limit: 2, target: 2, period: 1));

    static async Task<(GameSession Session, FakeCharacterStore Store)> EnterAsync(FakeCharacterStore store, ShopOptions? shop = null)
    {
        GameSession.ConfigureCustomShop(Shop);
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 45, WorldId = store.WorldId, Shop = shop ?? new ShopOptions() },
            new Random(5)) { Store = store };
        await session.HandleAsync(Login(), Now);
        await session.HandleAsync(LightCreate(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return (session, store);
    }

    static FakeCharacterStore WithKina(long kina, long quna = 0)
    {
        var store = new FakeCharacterStore(Guid.NewGuid());
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, kina, 4000));
        if (quna > 0) store.Quna.Seed(store.AccountId, quna);
        return store;
    }

    static async Task<int> BuyAsync(GameSession session, int goods, int quantity = 1, long now = Now)
    {
        var replies = await session.HandleAsync(new PacketWriter().U16(GameSession.NPShopPurchaseRq).I32(goods).I32(quantity).ToArray(), now);
        Assert.Equal(GameSession.NPShopPurchaseRs, Op(replies[0]));
        Assert.Equal(goods, BitConverter.ToInt32(replies[0], 4));
        return BitConverter.ToUInt16(replies[0], 2);
    }

    // Quna is the account wallet's (the stacks seeded as items are swept into it at entry).
    static long Held(FakeCharacterStore store, int item) => item == PlatformGoodsTables.Embedded.QunaItemId
        ? store.QunaBalance + store.Items.Where(i => i.TemplateId == item).Sum(i => i.Quantity)
        : store.Items.Where(i => i.TemplateId == item && i.Location == "bag").Sum(i => i.Quantity);

    [Fact]
    public void Custom_goods_ids_sit_above_the_official_ones()
    {
        Assert.True(CustomShopRules.IsCustom(9_000_000));
        Assert.False(CustomShopRules.IsCustom(1169));
        Assert.Null(CustomShopRules.Validate(Goods(0)));
        Assert.Equal("invalid_price", CustomShopRules.Validate(Goods(0, "free", 10)));
        Assert.Equal("invalid_price", CustomShopRules.Validate(Goods(0, "kina", 0)));
        Assert.Equal("invalid_currency", CustomShopRules.Validate(Goods(0, "gold")));
        Assert.Equal("invalid_goods_id", CustomShopRules.Validate(Goods(1169)));
        Assert.Equal("invalid_limit", CustomShopRules.Validate(Goods(0, target: 3)));
        Assert.Equal("invalid_items", CustomShopRules.Validate(Goods(0) with { Items = [] }));
        Assert.Equal("quna_item", CustomShopRules.Validate(Goods(0) with { Items = [new CustomShopItem(QunaWalletRules.ItemId, 100)] }));
        Assert.Equal("invalid_sale_window", CustomShopRules.Validate(Goods(0) with { SaleFrom = At, SaleTo = At }));
    }

    [Fact]
    public async Task Kina_goods_are_paid_granted_and_recorded()
    {
        var (session, store) = await EnterAsync(WithKina(10_000));

        Assert.Equal(0, await BuyAsync(session, 9_000_001, 2));

        Assert.Equal(10_000 - 1_000, Held(store, GameSession.KinaItemId));
        Assert.Equal(6, Held(store, Spiritstone));
        var order = Assert.Single(store.Orders);
        Assert.Equal(new ShopOrderLine("custom", 9_000_001, 2, GameSession.KinaItemId, 1_000), order);
    }

    [Fact]
    public async Task Refused_goods_cost_nothing()
    {
        var (session, store) = await EnterAsync(WithKina(300));

        Assert.Equal(2, await BuyAsync(session, 9_000_001));   // 500 Kina, 300 held
        Assert.Equal(1, await BuyAsync(session, 9_000_003));   // Dark only
        Assert.Equal(1, await BuyAsync(session, 9_000_004));   // level 60
        Assert.Equal(1, await BuyAsync(session, 9_000_005));   // hidden
        Assert.Equal(1, await BuyAsync(session, 9_000_006));   // sale ended
        Assert.Equal(1, await BuyAsync(session, 9_999_999));   // unknown
        Assert.Equal(300, Held(store, GameSession.KinaItemId));
        Assert.Empty(store.Orders);
    }

    [Fact]
    public async Task Limits_are_kept_and_reported()
    {
        var (session, store) = await EnterAsync(WithKina(10_000));

        Assert.Equal(0, await BuyAsync(session, 9_000_002));
        Assert.Equal(3, await BuyAsync(session, 9_000_002));
        Assert.Equal(new ShopOrderLine("custom", 9_000_002, 1, GameSession.KinaItemId, 0), Assert.Single(store.Orders));

        Assert.Equal(0, await BuyAsync(session, 9_000_008, 2));
        Assert.Equal(3, await BuyAsync(session, 9_000_008));
        // Per account and day: the next game day sells again.
        Assert.Equal(0, await BuyAsync(session, 9_000_008, 1, Now + 86_400_000));

        var limit = await session.HandleAsync(new PacketWriter().U16(GameSession.NPShopGameMoneyLimitRq).Varint(2).I32(9_000_002).I32(9_000_001).ToArray(), Now);
        var r = new PacketReader(limit[0]) { Offset = 2 };
        Assert.Equal(0, r.U16());
        Assert.Equal(1u, r.Varint());
        Assert.Equal(9_000_002, r.I32());
        Assert.Equal(1, r.I32());
        Assert.Equal(0L, r.I64());   // never resets
    }

    [Fact]
    public async Task Quna_goods_follow_the_premium_mode()
    {
        var (quna, questStore) = await EnterAsync(WithKina(1_000, quna: 1_000));
        Assert.Equal(0, await BuyAsync(quna, 9_000_007));
        Assert.Equal(700, Held(questStore, PlatformGoodsTables.Embedded.QunaItemId));
        Assert.Equal(PlatformGoodsTables.Embedded.QunaItemId, questStore.Orders.Single().CurrencyItemId);

        var (kina, kinaStore) = await EnterAsync(WithKina(1_000, quna: 1_000), new ShopOptions { PremiumMode = ShopPremiumMode.Kina });
        Assert.Equal(0, await BuyAsync(kina, 9_000_007));
        Assert.Equal(700, Held(kinaStore, GameSession.KinaItemId));
        Assert.Equal(1_000, Held(kinaStore, PlatformGoodsTables.Embedded.QunaItemId));

        var (disabled, _) = await EnterAsync(WithKina(1_000, quna: 1_000), new ShopOptions { PremiumMode = ShopPremiumMode.Disabled });
        Assert.Equal(1, await BuyAsync(disabled, 9_000_007));
    }

    [Fact]
    public async Task Membership_purchases_and_gm_changes_are_stored_and_read_back()
    {
        var (session, store) = await EnterAsync(WithKina(100_000), new ShopOptions { PremiumMode = ShopPremiumMode.Kina });
        Assert.Equal(0, await BuyAsync(session, 100024));
        Assert.NotNull(store.Membership);
        Assert.False(store.Membership!.Revoked);
        Assert.Equal(session.CurrentMembership(Now)!.EndMs, store.Membership.EndsAt.ToUnixTimeMilliseconds());
        Assert.Equal("membership", store.Orders.Single().Kind);

        // A revoked membership stored for another account is read at its entry.
        var other = WithKina(0);
        other.Membership = new StoredMembership(At, At, Revoked: true);
        var (revoked, _) = await EnterAsync(other);
        Assert.Null(revoked.CurrentMembership(Now));
    }

    [Fact]
    public async Task Pass_unlock_costs_the_configured_price_and_is_recorded()
    {
        var (session, store) = await EnterAsync(WithKina(0, quna: 1_000), new ShopOptions { PassUnlockPrice = 700 });
        var bought = await session.HandleAsync(new PacketWriter().U16(GameSession.BattlePassPurchaseRq).I32(PaidPass).ToArray(), Now);
        Assert.Equal(GameSession.BattlePassPurchaseRs, Op(bought[0]));
        Assert.Equal(0, BitConverter.ToUInt16(bought[0], 2));
        Assert.Equal(300, Held(store, PlatformGoodsTables.Embedded.QunaItemId));
        Assert.Equal(new ShopOrderLine("pass", PaidPass, 1, PlatformGoodsTables.Embedded.QunaItemId, 700), store.Orders.Single());

        var (poor, poorStore) = await EnterAsync(WithKina(0, quna: 100), new ShopOptions { PassUnlockPrice = 700 });
        var refused = await poor.HandleAsync(new PacketWriter().U16(GameSession.BattlePassPurchaseRq).I32(PaidPass).ToArray(), Now);
        Assert.Equal(2, BitConverter.ToUInt16(refused[0], 2));
        Assert.Empty(poorStore.Orders);
    }
}
