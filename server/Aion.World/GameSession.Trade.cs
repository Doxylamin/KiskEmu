using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Trade server / broker (TradeServerInfo 0xe263, Trade* 0xe264-0xe272). Listings persist in Postgres (051+) or FakeCharacterStore in tests.
/// Live opcodes: the usmap's 0x3b69-0x3b77 + 0xa6fb (#74); OBSERVED in our 2026-11-12 r2 capture (0xe267 cancel, 0xe269 renew,
/// 0xe26b expire, 0xe26d purchase replies) and in the A2Core fork's recordings (zaraza87/A2Core GameSession.Market.cs: 0xe265 sell,
/// 0xe26e sold, 0xe270 delivery). Replies: u16 result, the sale id as a 36-character upper-case GUID string, zero fields, the trade
/// server id as u16 (the usmap says int; every observed reply is two bytes). Requests: usmap field types (CANDIDATE widths: client
/// requests are encrypted in recordings); the trade server id is read as u16 when exactly two bytes are left, else i32.
/// The browse side of the window (search, price lists, my sales, earnings) is an HTTPS web API on the official server, not
/// served here, so its lists stay empty.
/// </summary>
public sealed partial class GameSession
{
    public const ushort TradeSellRq = 0xe264, TradeSellRs = 0xe265, TradeCancelSaleRq = 0xe266, TradeCancelSaleRs = 0xe267,
        TradeRenewSaleRq = 0xe268, TradeRenewSaleRs = 0xe269, TradeExpireSaleRq = 0xe26a, TradeExpireSaleRs = 0xe26b,
        TradePurchaseRq = 0xe26c, TradePurchaseRs = 0xe26d, TradeSoldNt = 0xe26e, TradeDeliveryCompleteRq = 0xe26f,
        TradeDeliveryCompleteRs = 0xe270, TradeGetSummaryRq = 0xe271, TradeGetSummaryRs = 0xe272;

    static TradeTables TradeConfig => TradeTables.Embedded;

    async Task<List<byte[]>> TradeRequestAsync(byte[] plain, long now, CancellationToken ct)
    {
        var op = Opcode(plain);
        try
        {
            return op switch
            {
                TradeSellRq => await TradeSellAsync(plain, now, ct),
                TradeCancelSaleRq or TradeExpireSaleRq => await TradeCancelAsync(plain, op == TradeExpireSaleRq ? TradeExpireSaleRs : TradeCancelSaleRs, ct),
                TradeRenewSaleRq => TradeRenew(plain),
                TradePurchaseRq => await TradePurchaseAsync(plain, ct),
                TradeDeliveryCompleteRq => await TradeDeliveryCompleteAsync(plain, ct),
                TradeGetSummaryRq => await TradeSummaryAsync(plain, ct),
                _ => [],
            };
        }
        catch (ProtocolException) { return []; }
    }

    /// <summary>A stored sale or delivery id as the client knows ids: a GUID string (OURS: the id in its last twelve hex digits).</summary>
    public static string TradeIdText(long id) => $"00000000-0000-4000-8000-{id & 0xFFFF_FFFF_FFFF:X12}";

    /// <summary>The stored id back from <see cref="TradeIdText"/> (a plain decimal is accepted too); 0 when neither.</summary>
    public static long ParseSaleId(string text) =>
        Guid.TryParse(text, out _) && text.Length == 36 && long.TryParse(text[24..], System.Globalization.NumberStyles.HexNumber, null, out var hex) ? hex
        : long.TryParse(text, out var id) ? id : 0;

    /// <summary>The trade server id that ends a request: u16 when exactly two bytes are left (as in every observed reply), else i32.</summary>
    static int TradeServerOf(PacketReader r) => r.Remaining == 2 ? r.U16() : r.I32();

    /// <summary>An observed reply: result, the id string, <paramref name="zeros"/> zero bytes, the trade server u16.</summary>
    static byte[] TradeReply(ushort opcode, int result, string id, int zeros, int tradeServer) =>
        PacketWriter.Opcode(opcode).U16(result).String(id).Zeros(zeros).U16(tradeServer).ToArray();

    async Task<List<byte[]>> TradeSellAsync(byte[] plain, long nowMs, CancellationToken ct)
    {
        // usmap: item key i64, count i64, ECurrencyCategory u8, price i64, fee rate i64, fee min i64, trade server.
        var r = new PacketReader(plain) { Offset = 2 };
        var itemKey = r.I64();
        var count = r.I64();
        r.U8();          // currency (Kina); the price is always taken as Kina
        var price = r.I64();
        r.I64(); r.I64(); // fee rate/min from the client: the server uses trade.json
        var tradeServer = TradeServerOf(r);
        // OBSERVED (zaraza87/A2Core, 5 sales): 0000 | GUID | 41 zero bytes | server u16.
        byte[] Reply(int result, TradeListingRecord? listing) => TradeReply(TradeSellRs, result, listing is null ? "" : TradeIdText(listing.SaleId), 41, tradeServer);
        if (Store is not { } store || CharacterId is not { } character || !PacketSecurity.IsValidStackCount(count) || price < 1 || price > PacketSecurity.MaxTradeUnitPrice
            || !PacketSecurity.TryMultiply(price, count, out _))
            return [Reply(1, null)];
        var (result, listing) = await StoredValue(() => store.TradeListItemAsync(character, itemKey, count, price, tradeServer, nowMs, ct), ("failed", null));
        if (listing is null) return [Reply(result == "ok" ? 0 : 1, null), .. await InventoryChangesAsync(ct, 0, 0)];
        return [Reply(0, listing), .. await InventoryChangesAsync(ct, 0, 0)];
    }

    /// <summary>TradeCancelSale_RQ / TradeExpireSale_RQ {sale id, item i32, count i64, server}: the listing comes back to the bag.</summary>
    async Task<List<byte[]>> TradeCancelAsync(byte[] plain, ushort replyOpcode, CancellationToken ct)
    {
        var r = new PacketReader(plain) { Offset = 2 };
        var text = r.String();
        var saleId = ParseSaleId(text);
        var template = r.I32();
        var count = r.I64();
        var tradeServer = TradeServerOf(r);
        // OBSERVED r2 (0xe267 x1, 0xe26b x3): 0000 | GUID | 20 zero bytes | server u16.
        byte[] Reply(int result) => TradeReply(replyOpcode, result, text, 20, tradeServer);
        if (Store is not { } store || CharacterId is not { } character || !PacketSecurity.IsValidStackCount(count)) return [Reply(1)];
        var (result, _) = await StoredValue(() => store.TradeCancelListingAsync(character, saleId, template, count, tradeServer, ct), ("failed", null));
        return [Reply(result == "ok" ? 0 : 1), .. result == "ok" ? await InventoryChangesAsync(ct, 0, 0) : []];
    }

    /// <summary>TradeRenewSale_RQ {sale id, item i32, server, fee rate i64, fee min i64, price i64}: not supported by our store (refused).</summary>
    static List<byte[]> TradeRenew(byte[] plain)
    {
        var r = new PacketReader(plain) { Offset = 2 };
        var text = r.String();
        r.I32();
        var tradeServer = r.Remaining == 2 + 24 ? r.U16() : r.I32();
        // OBSERVED r2 (0xe269): 0000 | GUID | server u16.
        return [TradeReply(TradeRenewSaleRs, 1, text, 0, tradeServer)];
    }

    async Task<List<byte[]>> TradePurchaseAsync(byte[] plain, CancellationToken ct)
    {
        var r = new PacketReader(plain) { Offset = 2 };
        var text = r.String();
        var saleId = ParseSaleId(text);
        var template = r.I32();
        var count = r.I64();
        var tradeServer = TradeServerOf(r);
        // OBSERVED r2 (0xe26d): 0000 | GUID | 12 zero bytes (the usmap's item i32 + count i64, left 0) | server u16.
        byte[] Reply(int result) => TradeReply(TradePurchaseRs, result, text, 12, tradeServer);
        if (Store is not { } store || CharacterId is not { } character || !PacketSecurity.IsValidStackCount(count)) return [Reply(1)];
        var (result, deliveryId) = await StoredValue(() => store.TradePurchaseAsync(character, saleId, template, count, tradeServer, ct), ("failed", 0L));
        var replies = new List<byte[]> { Reply(result == "ok" ? 0 : 1) };
        if (result == "ok") replies.AddRange(await InventoryChangesAsync(ct, 0, 0));
        // OBSERVED (zaraza87/A2Core): GUID | item i32 | count i64 | server u16. Ours goes to the buyer's session (OURS: the seller may be offline).
        if (deliveryId > 0) replies.Add(PacketWriter.Opcode(TradeSoldNt).String(text).I32(template).I64(count).U16(tradeServer).ToArray());
        return replies;
    }

    async Task<List<byte[]>> TradeDeliveryCompleteAsync(byte[] plain, CancellationToken ct)
    {
        var r = new PacketReader(plain) { Offset = 2 };
        var text = r.String();
        var deliveryId = ParseSaleId(text);
        r.I32();
        var tradeServer = TradeServerOf(r);
        // OBSERVED (zaraza87/A2Core, 3 deliveries): 0000 | GUID | server u16.
        byte[] Reply(int result) => TradeReply(TradeDeliveryCompleteRs, result, text, 0, tradeServer);
        if (Store is not { } store || CharacterId is not { } character) return [Reply(1)];
        var ok = await StoredValue(() => store.TradeCompleteDeliveryAsync(character, deliveryId, tradeServer, ct), false);
        return [Reply(ok ? 0 : 1), .. ok ? await InventoryChangesAsync(ct, 0, 0) : []];
    }

    /// <summary>TradeGetSummary_RS (usmap, CANDIDATE): result, sales, world sales, group sales map, earnings, world earnings, group earnings map, server u16.</summary>
    async Task<List<byte[]>> TradeSummaryAsync(byte[] plain, CancellationToken ct)
    {
        var tradeServer = TradeServerOf(new PacketReader(plain) { Offset = 2 });
        byte[] Reply(int result, int sales, int earnings) => PacketWriter.Opcode(TradeGetSummaryRs).U16(result).I32(sales).I32(sales).Varint(0)
            .I32(earnings).I32(earnings).Varint(0).U16(tradeServer).ToArray();
        if (Store is not { } store || CharacterId is not { } character) return [Reply(1, 0, 0)];
        var summary = await StoredValue(() => store.TradeSummaryAsync(character, tradeServer, ct), new TradeSummaryRecord(0, 0, 0));
        return [Reply(0, summary.ActiveSales, summary.PendingEarningsKina)];
    }
}
