using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Host.Platform;
using Xunit;

namespace Aion.Host.Tests;

public sealed class OfficialShopCatalogTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Goods_page_has_full_official_catalog()
    {
        using var doc = JsonDocument.Parse(OfficialShopCatalog.Goods(1, 400, Now));
        var root = doc.RootElement;
        Assert.Equal("222", root.GetProperty("total_count").GetString());
        var page = root.GetProperty("page");
        Assert.Equal(222, page.GetArrayLength());
        var cats = page.EnumerateArray().Select(g => g.TryGetProperty("goods_data", out var d) ? d.GetString() ?? "" : "").ToList();
        Assert.Contains(cats, c => c.StartsWith("catl:SkinShop_SkinSet,", StringComparison.Ordinal));
        Assert.Contains(cats, c => c.StartsWith("catl:SkinShop_Weapon,", StringComparison.Ordinal));
        Assert.Contains(page.EnumerateArray().SelectMany(g => g.GetProperty("display_names").EnumerateArray()),
            n => n.GetProperty("language_code").GetString() == "ru-RU");
        Assert.All(page.EnumerateArray(), g => Assert.Equal("", g.TryGetProperty("restricted_game_server_ids", out var r) ? r.GetString() : ""));
        Assert.All(page.EnumerateArray(), g => Assert.True(DateTimeOffset.Parse(g.GetProperty("effective_to").GetString()!) >= Now));
    }

    [Fact]
    public void Policy_returns_quota_per_requested_goods_with_zero_purchases()
    {
        using var doc = JsonDocument.Parse(OfficialShopCatalog.Policy("1242,1213,999999", null, "145", Now));
        var q = doc.RootElement.GetProperty("quotas");
        Assert.Equal(3, q.GetArrayLength());
        Assert.All(q.EnumerateArray(), x => Assert.Equal("1", x.GetProperty("is_purchasable").GetString()));
        Assert.All(q.EnumerateArray().SelectMany(x => x.GetProperty("limit_list").EnumerateArray()),
            l => Assert.Equal("0", l.GetProperty("purchased_quantity").GetString()));
    }

    [Fact]
    public void Me_goods_lists_cash_goods()
    {
        using var doc = JsonDocument.Parse(OfficialShopCatalog.MeGoods(null, Now));
        Assert.Equal(22, doc.RootElement.GetProperty("page").GetArrayLength());
    }

    [Fact]
    public void Gate_always_serves_official_catalog_without_switch()
    {
        var q = new Microsoft.AspNetCore.Http.QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["page_index"] = "1", ["page_size"] = "400" });
        Assert.True(PlatformApiGateResponses.TryReply("GET", "/local-api-gate/goods/v2.0/goods", q, null, out var body));
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("222", doc.RootElement.GetProperty("total_count").GetString());
        Assert.True(PlatformApiGateResponses.TryReply("GET", "/local-api-gate/goods/v2.0/me/goods", q, null, out var me));
        using var meDoc = JsonDocument.Parse(me);
        Assert.Equal(22, meDoc.RootElement.GetProperty("page").GetArrayLength());
    }

    [Fact]
    public void Bodies_are_raw_utf8_like_official()
    {
        foreach (var body in new[] { OfficialShopCatalog.Goods(1, 400, Now), OfficialShopCatalog.Policy("1213", null, "158", Now) })
        {
            var s = System.Text.Encoding.UTF8.GetString(body);
            Assert.DoesNotContain("\\u", s);
            Assert.Contains("+09:00", s);
        }
        var q = System.Text.Encoding.UTF8.GetString(OfficialShopCatalog.Policy("1213", null, "158", Now));
        Assert.True(q.IndexOf("\"remaining_seconds\"", StringComparison.Ordinal) < q.IndexOf("\"current_reset_time\"", StringComparison.Ordinal));
    }
    static CustomShopGoods Custom(int id, string currency = "kina", long price = 2500, int race = 0, bool enabled = true, DateTimeOffset? saleTo = null) =>
        new(id, "Starter bundle", "", "Shop_Usable", "D_Shop_Usable", "Icon_BMShop_Test", [new CustomShopItem(532760001, 2), new CustomShopItem(930100001, 1000)],
            currency, price, 0, 1, 0, race, 0, null, saleTo, enabled, 5, Now.AddDays(-1));

    [Fact]
    public void Goods_page_appends_the_custom_goods_on_sale()
    {
        var custom = new[] { Custom(9_000_001), Custom(9_000_002, "quna", 300, race: 2), Custom(9_000_003, enabled: false), Custom(9_000_004, saleTo: Now.AddHours(-1)) };
        using var doc = JsonDocument.Parse(OfficialShopCatalog.Goods(1, 400, Now, custom));
        var root = doc.RootElement;
        Assert.Equal("224", root.GetProperty("total_count").GetString());
        Assert.StartsWith("8484", root.GetProperty("revision_id").GetString());
        Assert.NotEqual("8484", root.GetProperty("revision_id").GetString());
        var page = root.GetProperty("page").EnumerateArray().ToList();
        var kina = page.Single(g => g.GetProperty("goods_id").GetString() == "9000001");
        Assert.StartsWith("catl:Shop_Usable,catd:D_Shop_Usable,do:5,", kina.GetProperty("goods_data").GetString());
        Assert.Contains("gi:Icon_BMShop_Test,", kina.GetProperty("goods_data").GetString());
        var price = kina.GetProperty("goods_basic_price_policies")[0];
        Assert.Equal("159", price.GetProperty("currency_group_id").GetString());
        Assert.Equal("2500.0000", price.GetProperty("goods_basic_price").GetString());
        Assert.Equal(new[] { "532760001", "930100001" }, kina.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("game_item_key").GetString() ?? "").ToArray());
        Assert.Equal(8, kina.GetProperty("display_names").GetArrayLength());
        var dark = page.Single(g => g.GetProperty("goods_id").GetString() == "9000002");
        Assert.StartsWith("catl:None,catd:D_Shop_Usable,", dark.GetProperty("goods_data").GetString());
        Assert.Equal("145", dark.GetProperty("goods_basic_price_policies")[0].GetProperty("currency_group_id").GetString());
        Assert.DoesNotContain(page, g => g.GetProperty("goods_id").GetString() is "9000003" or "9000004");

        using var plain = JsonDocument.Parse(OfficialShopCatalog.Goods(1, 400, Now, []));
        Assert.Equal("8484", plain.RootElement.GetProperty("revision_id").GetString());
    }

    [Fact]
    public void Policy_prices_custom_goods()
    {
        using var doc = JsonDocument.Parse(OfficialShopCatalog.Policy("9000001", null, "159", Now, [Custom(9_000_001)]));
        var quota = doc.RootElement.GetProperty("quotas")[0];
        Assert.Equal("9000001", quota.GetProperty("goods_id").GetString());
        Assert.Equal("2500.0000", quota.GetProperty("current_payment_amount").GetString());
        Assert.Equal("159", quota.GetProperty("currency_group_id").GetString());
    }

    [Fact]
    public void Dashboard_choices_list_the_official_tabs_and_icons()
    {
        var (tabs, icons) = OfficialShopCatalog.Choices();
        Assert.Contains(tabs, t => t.Light == "Shop_Usable" && t.Dark == "D_Shop_Usable");
        Assert.DoesNotContain(tabs, t => t.Light == "None" || t.Dark == "None");
        Assert.Contains("Icon_BMShop_Currency_SpiritStone_Resurrect_other", icons);
    }
}
