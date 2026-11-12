using Aion.Protocol.Native;

namespace Aion.World;

// NP (billing) shop game-money path: purchase 0x37cd/0x37ce and limit list 0x37cf–0x37d1 (usmap). The client catalog
// is local; the server validates goods from tools/data/shops.json and answers limit state for in-game currencies.
// Real-money (Quna) SKUs are omitted from shops.json at export time; purchases of unknown goods fail with result 1.
public sealed partial class GameSession
{
    // Wire opcodes. The usmap dev indices 0x37cd-0x37d1 sit in the same run as TakeAchievementReward (dev 0x37c7 = wire
    // 0x5685), so wire = dev + 0x1ebe. OBSERVED: opening the BM shop the client sends 0x568d with the ten game-money
    // (currency group 159) goods ids it then asks goods/v2.0/me/goods/policy about; the dev 0x37cf case never matched.
    public const ushort NPShopPurchaseRq = 0x568b, NPShopPurchaseRs = 0x568c,
        NPShopGameMoneyLimitRq = 0x568d, NPShopGameMoneyLimitRs = 0x568e, NPShopGameMoneyLimitNt = 0x568f;

    // TrustLimitInfo (dev 0x3b65-0x3b67, same run as BMShopTutorialInfo_NT dev 0x3b56 = wire 0xe251, so wire = dev + 0xa6fb).
    // OBSERVED: the client sends the empty 0xe260 TrustLimitInfo_RQ when the BM shop opens and got no answer.
    public const ushort TrustLimitInfoRq = 0xe260, TrustLimitInfoRs = 0xe261, TrustLimitInfoNt = 0xe262;

    /// <summary>
    /// 0xe260 TrustLimitInfo_RQ: TrustLimitInfo_NT then TrustLimitInfo_RS result 0.
    /// </summary>
    static List<byte[]> TrustLimitInfo(long now) => [TrustLimitInfoNotice(now), PacketWriter.Opcode(TrustLimitInfoRs).U16(0).ToArray()];

    /// <summary>The trust step the official account had (2026-11-12); with step 0 the client locked the enhancement window
    /// ("This content is currently unavailable due to account restrictions.", live 2026-11-12).</summary>
    public const int TrustStep = 5;

    /// <summary>
    /// 0xe262 TrustLimitInfo_NT, sent at world entry and on 0xe260. OBSERVED official (2026-11-12, 120 B, identical at entry and
    /// on every request): packed bools byte `07`, limit_info map varint 4 × {key u8, type u8 (= key), usage i64, limit i64,
    /// reset time i64} for types 2..5, update_time i64, trust_step i32 5. The official type 2 limit is 1,953,125; ours are all
    /// -1 (no limit, as the official 3..5).
    /// </summary>
    static byte[] TrustLimitInfoNotice(long now)
    {
        var w = PacketWriter.Opcode(TrustLimitInfoNt).Bool(true).Bool(true).Bool(true).Varint(4);
        for (var type = 2; type <= 5; type++) w.U8(type).U8(type).I64(0).I64(-1).I64(now);
        return w.I64(now).I32(TrustStep).ToArray();
    }

    async Task<List<byte[]>> NPShopGameMoneyLimitAsync(byte[] plain, long now, CancellationToken ct)
    {
        var goodsIds = new List<int>();
        try
        {
            var r = new PacketReader(plain) { Offset = 2 };
            var n = r.Varint();
            for (var i = 0; i < n && i < 256; i++) goodsIds.Add(r.I32());
        }
        catch (ProtocolException) { return []; }

        DebugShop($"NPShopGameMoneyLimit_RQ goods=[{string.Join(',', goodsIds)}]");
        var limits = new List<(int GoodsId, int Count, long Expire)>();
        if (Store is { } store && CharacterId is { } character)
        {
            var rows = await store.ShopPurchasesAsync(character, ct);
            var at = DateTimeOffset.FromUnixTimeMilliseconds(now);
            var custom = goodsIds.Any(Aion.Application.Gameplay.CustomShopRules.IsCustom) ? await CustomGoodsAsync(ct) : [];
            foreach (var goodsId in goodsIds)
            {
                (int Target, int Period)? limit = Shops.RowByGoodsId(goodsId) is { Limit: > 0 } row ? (row.LimitTarget, row.LimitPeriod)
                    : custom.FirstOrDefault(g => g.GoodsId == goodsId) is { LimitCount: > 0 } goods ? (goods.LimitTarget, goods.LimitPeriod) : null;
                if (limit is not { } l) continue;
                var period = ShopTables.Period(l.Period, at);
                var bought = rows.FirstOrDefault(r => r.GoodsId == goodsId && r.Target == l.Target && r.Period == period).Count;
                if (bought <= 0) continue;
                var expire = l.Period > 0 ? ContentResetSchedule.NextBoundaryMs(l.Period, now) : 0L;
                limits.Add((goodsId, bought, expire));
            }
        }

        byte[] LimitReply(int result)
        {
            var w = PacketWriter.Opcode(NPShopGameMoneyLimitRs).U16(result).Varint(limits.Count);
            foreach (var (goodsId, count, expire) in limits) w.I32(goodsId).I32(count).I64(expire);
            return w.ToArray();
        }

        var replies = new List<byte[]> { LimitReply(0) };
        if (limits.Count > 0)
        {
            var w = PacketWriter.Opcode(NPShopGameMoneyLimitNt).Varint(limits.Count);
            foreach (var (goodsId, count, expire) in limits) w.I32(goodsId).I32(count).I64(expire);
            replies.Add(w.ToArray());
        }
        DebugShop($"NPShopGameMoneyLimit_RS count={limits.Count}");
        return replies;
    }

    async Task<List<byte[]>> NPShopPurchaseAsync(byte[] plain, long now, CancellationToken ct)
    {
        if (plain.Length < 10) return [];
        var goodsId = BitConverter.ToInt32(plain, 2);
        var quantity = BitConverter.ToInt32(plain, 6);
        byte[] Reply(int result) => PacketWriter.Opcode(NPShopPurchaseRs).U16(result).I32(goodsId).I32(quantity).ToArray();

        DebugShop($"NPShopPurchase_RQ goods={goodsId} qty={quantity} (platform/catalog id)");
        if (IsMembershipGoods(goodsId)) return await BuyMembershipAsync(goodsId, quantity, now, Reply, ct);
        if (Aion.Application.Gameplay.CustomShopRules.IsCustom(goodsId)) return await BuyCustomGoodsAsync(goodsId, quantity, now, Reply, ct);
        if (ResolveExportedGoods(goodsId) is not { } row)
        {
            DebugShop($"NPShopPurchase rejected: goods {goodsId} not in dat export (billing or omitted SKU)");
            return [Reply(1)];
        }
        if (quantity < 1 || quantity > PacketSecurity.MaxShopLineCount || level < row.Level)
            return [Reply(1)];
        if (Store is not { } store || CharacterId is not { } character)
            return [Reply(1)];

        var gameGoodsId = PlatformGoodsTables.Embedded.ResolveGameGoodsId(goodsId) ?? goodsId;
        var entries = new List<(int Goods, int Count)> { (gameGoodsId, quantity) };
        var replies = await BuyCatalogGoodsAsync("bm", entries, now, ct, Reply, store, character);
        DebugShop($"NPShopPurchase_RS result={(replies.Count > 0 ? replies[0][2] : -1)}");
        return replies;
    }
}
