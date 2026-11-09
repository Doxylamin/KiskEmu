using Aion.Core.Accounts;

namespace Aion.Application.Gameplay;

/// <summary>One item a custom shop goods gives.</summary>
public sealed record CustomShopItem(int TemplateId, long Quantity);

/// <summary>
/// Goods staff add to the in-game shop (realm table game.custom_shop_goods, migration 094). The auth host lists them in the
/// shop catalogue; the buyer's world server sells them (NPShopPurchase). Currency "kina", "quna" or "free"; the limit is
/// per character (target 1) or account (2) over a period (0 ever, 1 day, 2 week, 3 month); race 0 both, 1 Light, 2 Dark.
/// </summary>
public sealed record CustomShopGoods(
    int GoodsId, string Name, string Description, string TabLight, string TabDark, string Icon, IReadOnlyList<CustomShopItem> Items,
    string Currency, long Price, int LimitCount, int LimitTarget, int LimitPeriod, int Race, int MinLevel,
    DateTimeOffset? SaleFrom, DateTimeOffset? SaleTo, bool Enabled, int SortOrder, DateTimeOffset UpdatedAt)
{
    public bool OnSale(DateTimeOffset now) => Enabled && (SaleFrom is null || SaleFrom <= now) && (SaleTo is null || now < SaleTo);
}

/// <summary>The custom shop goods (cached by the implementations; a world server reads them over the realm link).</summary>
public interface ICustomShopCatalog
{
    Task<IReadOnlyList<CustomShopGoods>> GoodsAsync(CancellationToken ct = default);
}

/// <summary>The realm's custom shop: staff list them, admins create, change and remove them (audited).</summary>
public interface ICustomShopAdmin : ICustomShopCatalog
{
    /// <summary>Creates (GoodsId 0: the next free id) or replaces goods; throws GameplayException with a CustomShopRules code.</summary>
    Task<CustomShopGoods> SaveAsync(VerifiedAccount actor, CustomShopGoods goods, CancellationToken ct = default);
    Task<bool> DeleteAsync(VerifiedAccount actor, int goodsId, CancellationToken ct = default);
    /// <summary>All goods, including disabled and expired ones, for staff.</summary>
    Task<IReadOnlyList<CustomShopGoods>> ListAsync(VerifiedAccount actor, CancellationToken ct = default);
}

public static class CustomShopRules
{
    public const int FirstGoodsId = 9_000_000, LastGoodsId = 9_999_999;
    public const long MaxPrice = 10_000_000_000, MaxItemQuantity = 1_000_000;
    public static readonly IReadOnlyList<string> Currencies = ["kina", "quna", "free"];

    public static bool IsCustom(int goodsId) => goodsId is >= FirstGoodsId and <= LastGoodsId;

    /// <summary>The error code of invalid goods (before the catalogue check of the items), or null.</summary>
    public static string? Validate(CustomShopGoods g)
    {
        if (g.GoodsId != 0 && !IsCustom(g.GoodsId)) return "invalid_goods_id";
        if (g.Name.Trim().Length is 0 or > 80 || g.Description.Length > 400) return "invalid_name";
        if (g.TabLight.Trim().Length is 0 or > 64 || g.TabDark.Trim().Length is 0 or > 64 || g.Icon.Length > 128) return "invalid_tab";
        if (g.Items.Count is 0 or > 10 || g.Items.Any(i => i.TemplateId <= 0 || i.Quantity is < 1 or > MaxItemQuantity)) return "invalid_items";
        if (!Currencies.Contains(g.Currency)) return "invalid_currency";
        if (g.Price < 0 || g.Price > MaxPrice || g.Currency == "free" && g.Price != 0 || g.Currency != "free" && g.Price == 0) return "invalid_price";
        if (g.LimitCount is < 0 or > 100_000 || g.LimitTarget is not (1 or 2) || g.LimitPeriod is < 0 or > 3) return "invalid_limit";
        if (g.Race is < 0 or > 2 || g.MinLevel is < 0 or > 100) return "invalid_restriction";
        if (g.SaleFrom is { } from && g.SaleTo is { } to && from >= to) return "invalid_sale_window";
        return null;
    }
}

/// <summary>One line of a shop purchase for the world's ledger (game.shop_orders): kind npc, coin, guild, bm, custom, pass or membership.</summary>
public sealed record ShopOrderLine(string Kind, int GoodsId, int Quantity, int CurrencyItemId, long Price);

/// <summary>A ledger row for staff.</summary>
public sealed record ShopOrderRecord(long OrderId, DateTimeOffset OrderedAt, Guid CharacterId, string CharacterName, Guid AccountId,
    string Kind, int GoodsId, int Quantity, int CurrencyItemId, long Price);

/// <summary>A world's purchase ledger for staff (world database).</summary>
public interface IShopLedger
{
    /// <summary>The latest orders, optionally of one character (name or id) or one kind; staff only.</summary>
    Task<IReadOnlyList<ShopOrderRecord>> OrdersAsync(VerifiedAccount actor, string? character, string? kind, int limit, CancellationToken ct = default);
}

/// <summary>An account's stored membership on a world.</summary>
public sealed record StoredMembership(DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool Revoked);
