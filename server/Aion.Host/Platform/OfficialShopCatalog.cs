using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aion.Application.Gameplay;

namespace Aion.Host.Platform;

/// <summary>
/// Official BM shop catalog (tools/data/official_shop.json, built by tools/shop_official_import.py from a local
/// capture, account data stripped). Served verbatim in the official goods/v2.0 shape: all goods with their
/// goods_data (catl/catd/do/gi/thm...), display names in every language, display policies, prices and items.
/// Expired effective/display windows are pushed to 2123 so the captured shop stays open.
/// </summary>
public static class OfficialShopCatalog
{
    // Official bodies are raw UTF-8 with a literal "+" in dates; the client parser does not handle \uXXXX escapes.
    static readonly JsonSerializerOptions Raw = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly System.Text.RegularExpressions.Regex Escaped = new(@"(?<=(?:^|[^\\])(?:\\\\)*)\\u([0-9A-Fa-f]{4})", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Serializes and then un-escapes \uXXXX (except control chars, quote and backslash) so the bytes match the official raw UTF-8.</summary>
    static byte[] Serialize(JsonNode node)
    {
        var s = Escaped.Replace(JsonSerializer.Serialize(node, Raw), m =>
        {
            var c = (char)Convert.ToInt32(m.Groups[1].Value, 16);
            return c < 0x20 || c == '"' || c == '\\' ? m.Value : c.ToString();
        });
        return System.Text.Encoding.UTF8.GetBytes(s);
    }
    const string Far = "2123-12-31T05:00:00+09:00";
    static readonly Lazy<JsonObject> Data = new(() =>
    {
        using var s = typeof(OfficialShopCatalog).Assembly.GetManifestResourceStream("official_shop.json")
            ?? throw new InvalidOperationException("Missing embedded official_shop.json");
        return JsonNode.Parse(s)!.AsObject();
    });

    static string Revision => Data.Value["revision_id"]?.GetValue<string>() ?? "1";
    public static int GoodsCount => Data.Value["goods"]!.AsArray().Count;

    static JsonObject Fresh(JsonNode g, DateTimeOffset now)
    {
        var o = g.DeepClone().AsObject();
        Extend(o, now);
        if (o["display_policies"] is JsonArray dp)
            foreach (var p in dp) if (p is JsonObject po) Extend(po, now);
        return o;
    }

    static void Extend(JsonObject o, DateTimeOffset now)
    {
        if (o["effective_to"]?.GetValue<string>() is { } to && DateTimeOffset.TryParse(to, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) && t < now)
            o["effective_to"] = Far;
    }

    static byte[] Page(IEnumerable<JsonNode> rows, int pageIndex, int pageSize, DateTimeOffset now, string? revision = null)
    {
        var all = rows.ToList();
        var page = new JsonArray(all.Skip((pageIndex - 1) * pageSize).Take(pageSize).Select(g => (JsonNode)Fresh(g, now)).ToArray());
        var root = new JsonObject
        {
            ["revision_id"] = revision ?? Revision, ["total_count"] = all.Count.ToString(), ["page_index"] = pageIndex.ToString(), ["page"] = page,
        };
        return Serialize(root);
    }

    /// <summary>GET goods/v2.0/goods: the official goods, then the custom goods on sale now (staff dashboard: Shop).</summary>
    public static byte[] Goods(int pageIndex, int pageSize, DateTimeOffset now, IReadOnlyList<CustomShopGoods>? custom = null)
    {
        var onSale = (custom ?? []).Where(g => g.OnSale(now)).ToList();
        var rows = Data.Value["goods"]!.AsArray()!.Select(n => n!).Concat(onSale.Select(g => (JsonNode)CustomGoods(g)));
        return Page(rows, Math.Max(1, pageIndex), pageSize is < 1 or > 500 ? 400 : pageSize, now, CustomRevision(onSale));
    }

    /// <summary>Official revision, with a suffix that changes with the custom goods (so a client cache is not reused).</summary>
    static string CustomRevision(IReadOnlyList<CustomShopGoods> custom)
    {
        if (custom.Count == 0) return Revision;
        var hash = 17L;
        foreach (var g in custom) hash = unchecked(hash * 31 + g.GoodsId * 7 + g.UpdatedAt.ToUnixTimeMilliseconds());
        return Revision + (Math.Abs(hash % 1_000_000)).ToString("D6", CultureInfo.InvariantCulture);
    }

    static readonly string[] Languages = ["de-DE", "en-US", "es-ES", "fr-FR", "ja-JP", "ko-KR", "pt-BR", "ru-RU"];

    /// <summary>Currency group of a custom goods' price: 145 GameCash (Quna), else 159 game money (Kina; 0 when free).</summary>
    public static string CurrencyGroup(CustomShopGoods g) => g.Currency == "quna" ? "145" : "159";

    static string Iso(DateTimeOffset at) => at.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>
    /// A custom goods in the official goods shape (as goods 1169: one tab per race in goods_data catl/catd, "None" hides it
    /// from the other race; gi the shop icon; items by game_item_key; one basic price in its currency group).
    /// </summary>
    public static JsonObject CustomGoods(CustomShopGoods g)
    {
        var id = g.GoodsId.ToString(CultureInfo.InvariantCulture);
        var light = g.Race == 2 ? "None" : g.TabLight;
        var dark = g.Race == 1 ? "None" : g.TabDark;
        var items = new JsonArray(g.Items.Select(i => (JsonNode)new JsonObject
        {
            ["item_id"] = i.TemplateId.ToString(CultureInfo.InvariantCulture), ["item_quantity"] = i.Quantity.ToString(CultureInfo.InvariantCulture),
            ["item_receive_expiration_type"] = "1", ["item_name"] = g.Name, ["item_type"] = "103", ["item_description"] = "",
            ["game_item_key"] = i.TemplateId.ToString(CultureInfo.InvariantCulture),
        }).ToArray());
        return new JsonObject
        {
            ["goods_id"] = id, ["app_group_code"] = "aion2global", ["goods_name"] = g.Name, ["sale_status"] = "2",
            ["effective_from"] = g.SaleFrom is { } from ? Iso(from) : "2025-01-01T05:00:00+09:00",
            ["effective_to"] = g.SaleTo is { } to ? Iso(to) : Far,
            ["goods_data"] = $"catl:{light},catd:{dark},do:{g.SortOrder},sset:0,ssd:0,sen:0,gi:{g.Icon},l:,os:1,cl:1,thm:,thf:,orp:0,sc:1,rg:,cp:0",
            ["purchase_type"] = "1", ["is_subscribable"] = "0", ["is_delivery_confirmation"] = "0", ["refund_unit_type"] = "1", ["inventory_type"] = "0",
            ["goods_type"] = "1", ["goods_delivery_type"] = "0", ["is_single"] = "0", ["items"] = items, ["pgs"] = new JsonArray(),
            ["display_names"] = new JsonArray(Languages.Select(l => (JsonNode)new JsonObject { ["language_code"] = l, ["goods_display_name"] = g.Name }).ToArray()),
            ["display_policies"] = new JsonArray(),
            ["goods_basic_price_policies"] = new JsonArray(new JsonObject
            {
                ["currency_group_id"] = CurrencyGroup(g), ["goods_basic_price"] = g.Price.ToString(CultureInfo.InvariantCulture) + ".0000",
            }),
            ["goods_basic_currency_price_policies"] = new JsonArray(), ["goods_missions"] = new JsonArray(),
        };
    }

    /// <summary>The shop tabs (catl, catd pairs) and icons (gi) of the official goods, for the staff dashboard's choices.</summary>
    public static (IReadOnlyList<(string Light, string Dark, int Goods)> Tabs, IReadOnlyList<string> Icons) Choices()
    {
        var tabs = new Dictionary<(string, string), int>();
        var icons = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var g in Data.Value["goods"]!.AsArray().Concat(Data.Value["me_goods"]!.AsArray()))
        {
            var data = (g?["goods_data"]?.GetValue<string>() ?? "").Split(',').Select(kv => kv.Split(':', 2)).Where(kv => kv.Length == 2)
                .GroupBy(kv => kv[0]).ToDictionary(x => x.Key, x => x.First()[1]);
            if (data.GetValueOrDefault("catl") is { Length: > 0 } l && l != "None" && data.GetValueOrDefault("catd") is { Length: > 0 } d && d != "None")
                tabs[(l, d)] = tabs.GetValueOrDefault((l, d)) + 1;
            if (data.GetValueOrDefault("gi") is { Length: > 0 } icon) icons.Add(icon);
        }
        return (tabs.OrderByDescending(t => t.Value).Select(t => (t.Key.Item1, t.Key.Item2, t.Value)).ToList(), icons.ToList());
    }

    static HashSet<string> Csv(string? v) =>
        (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);

    static bool HasPgKey(JsonNode g, HashSet<string> keys) =>
        g["pgs"] is JsonArray pgs && pgs.Any(p => p?["pg_goods_key"]?.GetValue<string>() is { } k && keys.Contains(k));

    /// <summary>GET goods/v2.0/me/goods (cash goods by pg_goods_key; all when the key list is empty).</summary>
    public static byte[] MeGoods(string? pgGoodsKeys, DateTimeOffset now)
    {
        var keys = Csv(pgGoodsKeys);
        var rows = Data.Value["me_goods"]!.AsArray().Select(n => n!).Where(g => keys.Count == 0 || HasPgKey(g, keys));
        return Page(rows, 1, 500, now);
    }

    /// <summary>GET goods/v2.0/me/goods/policy: captured quotas (purchase counters zeroed), or a plain purchasable quota.</summary>
    public static byte[] Policy(string? goodsIds, string? pgGoodsKeys, string? currencyGroupId, DateTimeOffset now, IReadOnlyList<CustomShopGoods>? custom = null)
    {
        var ids = Csv(goodsIds);
        var keys = Csv(pgGoodsKeys);
        var quotas = Data.Value["quotas"]!.AsArray().Select(n => n!.AsObject())
            .ToDictionary(q => q["goods_id"]!.GetValue<string>(), StringComparer.Ordinal);
        var outList = new JsonArray();
        if (ids.Count > 0)
        {
            var goods = Data.Value["goods"]!.AsArray().Concat(Data.Value["me_goods"]!.AsArray()).Select(n => n!)
                .Concat((custom ?? []).Select(g => (JsonNode)CustomGoods(g)))
                .GroupBy(g => g["goods_id"]!.GetValue<string>()).ToDictionary(x => x.Key, x => x.First());
            foreach (var id in ids)
            {
                if (quotas.TryGetValue(id, out var q)) outList.Add(Quota(q, now));
                else outList.Add(DefaultQuota(id, goods.GetValueOrDefault(id), currencyGroupId));
            }
        }
        else if (keys.Count > 0)
        {
            foreach (var q in quotas.Values)
                if (q["pg_goods_key"]?.GetValue<string>() is { } k && keys.Contains(k)) outList.Add(Quota(q, now));
        }
        return Serialize(new JsonObject { ["quotas"] = outList });
    }

    static JsonObject Quota(JsonObject q, DateTimeOffset now)
    {
        var o = q.DeepClone().AsObject();
        if (o["limit_list"] is JsonArray limits)
            for (var i = 0; i < limits.Count; i++)
            {
                if (limits[i] is not JsonObject l || l["next_reset_time"]?.GetValue<string>() is not { } n
                    || !DateTimeOffset.TryParse(n, CultureInfo.InvariantCulture, DateTimeStyles.None, out var next)) continue;
                // Official field order: remaining_seconds sits right before current_reset_time.
                var remaining = Math.Max(0, (long)(next - now).TotalSeconds).ToString(CultureInfo.InvariantCulture);
                var copy = new JsonObject();
                foreach (var (k, v) in l.ToList())
                {
                    if (k == "current_reset_time") copy["remaining_seconds"] = remaining;
                    copy[k] = v?.DeepClone();
                }
                if (!copy.ContainsKey("remaining_seconds")) copy["remaining_seconds"] = remaining;
                limits[i] = copy;
            }
        return o;
    }

    static JsonObject DefaultQuota(string id, JsonNode? g, string? currencyGroupId)
    {
        var price = g?["goods_basic_price_policies"] is JsonArray pp && pp.Count > 0 ? pp[0] : null;
        return new JsonObject
        {
            ["goods_id"] = id, ["is_purchasable"] = "1", ["is_price_change"] = "0",
            ["currency_group_id"] = price?["currency_group_id"]?.GetValue<string>() ?? currencyGroupId ?? "145",
            ["current_payment_amount"] = price?["goods_basic_price"]?.GetValue<string>() ?? "0.0000",
            ["mileage_currency_id"] = "0", ["mileage_amount"] = "0.000000", ["limit_list"] = new JsonArray(),
        };
    }
}