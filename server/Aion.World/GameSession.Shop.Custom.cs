using Aion.Application.Gameplay;

namespace Aion.World;

// Custom shop goods (staff dashboard: Shop; realm table game.custom_shop_goods). The in-game shop's catalogue is the
// server's (goods/v2.0/goods): the auth host lists these goods there next to the official ones, and the client buys them
// like any game-money goods, with NPShopPurchase_RQ {goods id, quantity} on the buyer's world (ids 9000000-9999999).
// OURS: the price is paid from the bag (Kina, or Quna repriced by Aion:Shop:PremiumMode); the limit is kept with the NPC
// shop limits (game.shop_purchases) and reported by NPShopGameMoneyLimit; every purchase is a "custom" ledger line.
public sealed partial class GameSession
{
    static ICustomShopCatalog? customShop;

    /// <summary>The custom shop goods this world sells (the realm's, over the realm link); null for none.</summary>
    public static void ConfigureCustomShop(ICustomShopCatalog? catalog) => customShop = catalog;

    async Task<IReadOnlyList<CustomShopGoods>> CustomGoodsAsync(CancellationToken ct)
    {
        if (customShop is null) return [];
        try { return await customShop.GoodsAsync(ct); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            DebugShop($"Custom shop goods unavailable: {error.Message}");
            return [];
        }
    }

    /// <summary>The currency item and total of a custom goods line, null when it is not for sale (PremiumMode Disabled for Quna).</summary>
    (int Currency, long Total)? CustomPrice(CustomShopGoods goods, int quantity)
    {
        (int, long)? unit = goods.Currency switch
        {
            "kina" => (KinaItemId, goods.Price),
            "quna" => ApplyPremiumPrice(PlatformGoodsTables.Embedded.QunaItemId, goods.Price),
            _ => (KinaItemId, 0L),
        };
        if (unit is not { } u || !PacketSecurity.TryMultiply(u.Item2, quantity, out var total)) return null;
        return (u.Item1, total);
    }

    async Task<List<byte[]>> BuyCustomGoodsAsync(int goodsId, int quantity, long now, Func<int, byte[]> reply, CancellationToken ct)
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(now);
        var goods = (await CustomGoodsAsync(ct)).FirstOrDefault(g => g.GoodsId == goodsId);
        if (goods is null || !goods.OnSale(at))
        {
            DebugShop($"Custom goods {goodsId} refused: {(goods is null ? "unknown" : "not on sale")}");
            return [reply(1)];
        }
        if (quantity < 1 || quantity > PacketSecurity.MaxShopLineCount || level < goods.MinLevel || goods.Race != 0 && goods.Race != profileRace)
            return [reply(1)];
        if (Store is not { } store || CharacterId is not { } character || CustomPrice(goods, quantity) is not { } price) return [reply(1)];
        var rewards = new List<(int, long)>();
        foreach (var item in goods.Items)
        {
            if (!PacketSecurity.TryMultiply(item.Quantity, quantity, out var count)) return [reply(1)];
            rewards.Add((item.TemplateId, count));
        }
        List<(int, int, string, int, int)> limits = goods.LimitCount > 0
            ? [(goods.LimitTarget, goodsId, ShopTables.Period(goods.LimitPeriod, at), quantity, goods.LimitCount)]
            : [];
        if (PurchaseCosts([(price.Currency, price.Total)]) is not { } paid) return [reply(2)];
        var order = new ShopOrderLine("custom", goodsId, quantity, price.Currency, price.Total);
        var pay = await PayQunaAsync(paid.Quna, new { kind = "custom", goods = goodsId, quantity },
            () => StoredOk(() => store.ShopBuyAsync(character, paid.Costs, rewards, limits, [order], ct)), ct);
        if (pay == QunaPay.NotEnough) return [reply(2)];
        if (pay != QunaPay.Done)
        {
            DebugShop($"Custom goods {goodsId} x{quantity} failed{(limits.Count > 0 ? " (limit?)" : "")}");
            return [reply(limits.Count > 0 ? 3 : 1)];
        }
        DebugShop($"Custom goods {goodsId} x{quantity} bought for {price.Total} of {price.Currency}");
        // The inventory changes carry the Quna balance refresh when Quna was spent.
        return [reply(0), .. await InventoryChangesAsync(ct, IncreaseShopBuy, DecreaseShopBuy)];
    }
}
