using Aion.Application.Authentication;
using Aion.Application.Gameplay;
using Aion.Host.Platform;

namespace Aion.Host.Authentication;

/// <summary>
/// The dashboard's Shop page: the realm's custom shop goods (staff read, admins edit; auth host) and a world's purchase
/// ledger (staff; answered by the world server the dashboard picked, ?world=…).
/// </summary>
public static partial class StaffToolsEndpoints
{
    sealed record ShopItemInput(int TemplateId, long Quantity);
    sealed record ShopGoodsInput(string? Name, string? Description, string? TabLight, string? TabDark, string? Icon, ShopItemInput[]? Items,
        string? Currency, long Price, int LimitCount, int LimitTarget, int LimitPeriod, int Race, int MinLevel,
        DateTimeOffset? SaleFrom, DateTimeOffset? SaleTo, bool Enabled, int SortOrder);

    static CustomShopGoods ToGoods(int id, ShopGoodsInput input) => new(id, input.Name ?? "", input.Description ?? "", input.TabLight ?? "",
        input.TabDark ?? "", input.Icon ?? "", (input.Items ?? []).Select(i => new CustomShopItem(i.TemplateId, i.Quantity)).ToList(),
        (input.Currency ?? "").Trim().ToLowerInvariant(), input.Price, input.LimitCount, input.LimitTarget, input.LimitPeriod, input.Race, input.MinLevel,
        input.SaleFrom, input.SaleTo, input.Enabled, input.SortOrder, DateTimeOffset.UtcNow);

    static void MapShop(RouteGroupBuilder staff)
    {
        staff.MapGet("/shop/goods", async (HttpContext ctx, IAccountAuthentication auth, IServiceProvider services, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            if (services.GetService<ICustomShopAdmin>() is not { } shop) return Results.BadRequest(new { error = "realm_only" });
            var goods = await shop.ListAsync(a, ct);
            var (tabs, icons) = OfficialShopCatalog.Choices();
            return Results.Ok(new
            {
                goods,
                tabs = tabs.Select(t => new { light = t.Light, dark = t.Dark, goods = t.Goods }),
                icons,
                firstId = CustomShopRules.FirstGoodsId,
            });
        });
        staff.MapPost("/shop/goods", (ShopGoodsInput input, HttpContext ctx, IAccountAuthentication auth, IServiceProvider services, CancellationToken ct) =>
            SaveGoods(0, input, ctx, auth, services, ct));
        staff.MapPut("/shop/goods/{id:int}", (int id, ShopGoodsInput input, HttpContext ctx, IAccountAuthentication auth, IServiceProvider services, CancellationToken ct) =>
            CustomShopRules.IsCustom(id) ? SaveGoods(id, input, ctx, auth, services, ct) : Task.FromResult(Results.NotFound()));
        staff.MapDelete("/shop/goods/{id:int}", async (int id, HttpContext ctx, IAccountAuthentication auth, IServiceProvider services, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            if (services.GetService<ICustomShopAdmin>() is not { } shop) return Results.BadRequest(new { error = "realm_only" });
            return await shop.DeleteAsync(a, id, ct) ? Results.NoContent() : Results.NotFound();
        });

        staff.MapGet("/shop/orders", async (string? character, string? kind, int? limit, HttpContext ctx, IAccountAuthentication auth, IServiceProvider services, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            // The ledger is in each world's database: the dashboard asks the picked world (?world=…).
            if (services.GetService<IShopLedger>() is not { } ledger) return Results.BadRequest(new { error = "world_only" });
            return Results.Ok(await ledger.OrdersAsync(a, character, kind, limit ?? 100, ct));
        });
    }

    static async Task<IResult> SaveGoods(int id, ShopGoodsInput input, HttpContext ctx, IAccountAuthentication auth, IServiceProvider services, CancellationToken ct)
    {
        if (await Signed(ctx, auth, ct) is not { } a) return Results.Unauthorized();
        if (services.GetService<ICustomShopAdmin>() is not { } shop) return Results.BadRequest(new { error = "realm_only" });
        var goods = ToGoods(id, input);
        if (CustomShopRules.Validate(goods) is { } invalid) return Results.BadRequest(new { error = invalid });
        return Results.Ok(await shop.SaveAsync(a, goods, ct));
    }
}
