using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Npgsql;
using NpgsqlTypes;

namespace Aion.Infrastructure.Postgres;

/// <summary>
/// The realm's custom shop goods (game.custom_shop_goods, migration 094). Staff list them; admins create, change and remove
/// them (audited as shop_goods_saved / shop_goods_removed). The catalogue read is cached for a few seconds: the auth host
/// serves it on every shop open and world servers read it over the realm link.
/// </summary>
public sealed class PostgresCustomShop(NpgsqlDataSource source) : ICustomShopAdmin
{
    static readonly TimeSpan Fresh = TimeSpan.FromSeconds(5);
    (IReadOnlyList<CustomShopGoods> Goods, DateTimeOffset At)? cache;
    readonly SemaphoreSlim gate = new(1, 1);

    const string Columns = "goods_id,name,description,tab_light,tab_dark,icon,items::text,currency,price,limit_count,limit_target,limit_period,race,min_level,sale_from,sale_to,enabled,sort_order,updated_at";

    public async Task<IReadOnlyList<CustomShopGoods>> GoodsAsync(CancellationToken ct = default)
    {
        if (cache is { } c && DateTimeOffset.UtcNow - c.At < Fresh) return c.Goods;
        await gate.WaitAsync(ct);
        try
        {
            if (cache is { } again && DateTimeOffset.UtcNow - again.At < Fresh) return again.Goods;
            await using var conn = await source.OpenConnectionAsync(ct);
            var goods = await ReadAsync(conn, null, $"SELECT {Columns} FROM game.custom_shop_goods WHERE enabled ORDER BY sort_order, goods_id", ct);
            cache = (goods, DateTimeOffset.UtcNow);
            return goods;
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<CustomShopGoods>> ListAsync(VerifiedAccount actor, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        await RequireStaff(c, t, actor, admin: false, ct);
        var goods = await ReadAsync(c, t, $"SELECT {Columns} FROM game.custom_shop_goods ORDER BY sort_order, goods_id", ct);
        await t.CommitAsync(ct);
        return goods;
    }

    public async Task<CustomShopGoods> SaveAsync(VerifiedAccount actor, CustomShopGoods goods, CancellationToken ct = default)
    {
        if (CustomShopRules.Validate(goods) is { } invalid) throw new GameplayException(invalid);
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        await RequireStaff(c, t, actor, admin: true, ct);
        var build = await Scalar(c, t, "SELECT build_id FROM catalog.builds ORDER BY imported_at DESC LIMIT 1", ct) as string
            ?? throw new GameplayException("no_catalogue");
        foreach (var item in goods.Items)
            if (await Scalar(c, t, "SELECT 1 FROM catalog.items WHERE build_id=$1 AND template_id=$2", ct, build, item.TemplateId) is null)
                throw new GameplayException("unknown_item");
        var id = goods.GoodsId;
        if (id == 0)
            id = (int)(await Scalar(c, t, $"SELECT COALESCE(max(goods_id)+1,{CustomShopRules.FirstGoodsId}) FROM game.custom_shop_goods", ct) as int? ?? CustomShopRules.FirstGoodsId);
        if (!CustomShopRules.IsCustom(id)) throw new GameplayException("shop_full");
        var items = JsonSerializer.Serialize(goods.Items.Select(i => new[] { i.TemplateId, i.Quantity }));
        var saved = await ReadAsync(c, t, $"""
            INSERT INTO game.custom_shop_goods(goods_id,name,description,tab_light,tab_dark,icon,items,currency,price,limit_count,limit_target,limit_period,
                race,min_level,sale_from,sale_to,enabled,sort_order,updated_at,updated_by)
            VALUES($1,$2,$3,$4,$5,$6,$7::jsonb,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,now(),$19)
            ON CONFLICT(goods_id) DO UPDATE SET name=EXCLUDED.name,description=EXCLUDED.description,tab_light=EXCLUDED.tab_light,tab_dark=EXCLUDED.tab_dark,
                icon=EXCLUDED.icon,items=EXCLUDED.items,currency=EXCLUDED.currency,price=EXCLUDED.price,limit_count=EXCLUDED.limit_count,
                limit_target=EXCLUDED.limit_target,limit_period=EXCLUDED.limit_period,race=EXCLUDED.race,min_level=EXCLUDED.min_level,
                sale_from=EXCLUDED.sale_from,sale_to=EXCLUDED.sale_to,enabled=EXCLUDED.enabled,sort_order=EXCLUDED.sort_order,updated_at=now(),updated_by=EXCLUDED.updated_by
            RETURNING {Columns}
            """, ct, id, goods.Name.Trim(), goods.Description.Trim(), goods.TabLight.Trim(), goods.TabDark.Trim(), goods.Icon.Trim(), items, goods.Currency,
            goods.Price, goods.LimitCount, (short)goods.LimitTarget, (short)goods.LimitPeriod, (short)goods.Race, (short)goods.MinLevel,
            new Typed(goods.SaleFrom?.UtcDateTime, NpgsqlDbType.TimestampTz), new Typed(goods.SaleTo?.UtcDateTime, NpgsqlDbType.TimestampTz),
            goods.Enabled, goods.SortOrder, actor.AccountId);
        await Audit(c, t, actor, "shop_goods_saved", new { goods_id = id, goods.Name, goods.Currency, goods.Price, goods.Enabled, created = goods.GoodsId == 0 }, ct);
        await t.CommitAsync(ct);
        cache = null;
        return saved[0];
    }

    public async Task<bool> DeleteAsync(VerifiedAccount actor, int goodsId, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        await RequireStaff(c, t, actor, admin: true, ct);
        await using var q = Query(c, t, "DELETE FROM game.custom_shop_goods WHERE goods_id=$1", goodsId);
        var removed = await q.ExecuteNonQueryAsync(ct) > 0;
        if (removed) await Audit(c, t, actor, "shop_goods_removed", new { goods_id = goodsId }, ct);
        await t.CommitAsync(ct);
        cache = null;
        return removed;
    }

    static async Task<IReadOnlyList<CustomShopGoods>> ReadAsync(NpgsqlConnection c, NpgsqlTransaction? t, string sql, CancellationToken ct, params object?[] args)
    {
        await using var q = Query(c, t, sql, args);
        await using var r = await q.ExecuteReaderAsync(ct);
        var list = new List<CustomShopGoods>();
        while (await r.ReadAsync(ct))
        {
            var items = JsonSerializer.Deserialize<long[][]>(r.GetString(6))?.Where(i => i.Length >= 2).Select(i => new CustomShopItem((int)i[0], i[1])).ToList() ?? [];
            DateTimeOffset? At(int i) => r.IsDBNull(i) ? null : new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc));
            list.Add(new CustomShopGoods(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), items,
                r.GetString(7), r.GetInt64(8), r.GetInt32(9), r.GetInt16(10), r.GetInt16(11), r.GetInt16(12), r.GetInt16(13), At(14), At(15),
                r.GetBoolean(16), r.GetInt32(17), At(18)!.Value));
        }
        return list;
    }

    sealed record Typed(object? Value, NpgsqlDbType Type);

    static NpgsqlCommand Query(NpgsqlConnection c, NpgsqlTransaction? t, string sql, params object?[] args)
    {
        var q = new NpgsqlCommand(sql, c, t);
        foreach (var arg in args)
            q.Parameters.Add(arg switch
            {
                null => new NpgsqlParameter { Value = DBNull.Value },
                Typed typed => new NpgsqlParameter { Value = typed.Value ?? DBNull.Value, NpgsqlDbType = typed.Type },
                _ => new NpgsqlParameter { Value = arg },
            });
        return q;
    }

    static async Task<object?> Scalar(NpgsqlConnection c, NpgsqlTransaction t, string sql, CancellationToken ct, params object?[] args)
    {
        await using var q = Query(c, t, sql, args);
        var value = await q.ExecuteScalarAsync(ct);
        return value is DBNull ? null : value;
    }

    internal static async Task RequireStaff(NpgsqlConnection c, NpgsqlTransaction t, VerifiedAccount actor, bool admin, CancellationToken ct)
    {
        await using var q = Query(c, t, "SELECT array_agg(r.role) FROM auth.accounts a JOIN auth.account_roles r USING(account_id) WHERE a.account_id=$1 AND a.disabled_at IS NULL AND r.role IN ('gamemaster','admin')", actor.AccountId);
        var roles = await q.ExecuteScalarAsync(ct) as string[] ?? [];
        if (roles.Length == 0 || admin && !roles.Contains("admin")) throw new GameplayException("forbidden");
    }

    static async Task Audit(NpgsqlConnection c, NpgsqlTransaction t, VerifiedAccount actor, string action, object details, CancellationToken ct)
    {
        await using var q = Query(c, t, "INSERT INTO audit.events(action,actor_id,details) VALUES($1,$2,$3::jsonb)", action, actor.AccountId, JsonSerializer.Serialize(details));
        await q.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>A world's purchase ledger (game.shop_orders, migration 094) for staff.</summary>
public sealed class PostgresShopLedger(NpgsqlDataSource source) : IShopLedger
{
    public async Task<IReadOnlyList<ShopOrderRecord>> OrdersAsync(VerifiedAccount actor, string? character, string? kind, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        await PostgresCustomShop.RequireStaff(c, t, actor, admin: false, ct);
        var who = (character ?? "").Trim();
        Guid? id = Guid.TryParse(who, out var parsed) ? parsed : null;
        await using var q = new NpgsqlCommand("""
            SELECT o.order_id,o.ordered_at,o.character_id,COALESCE(ch.name,''),o.account_id,o.kind,o.goods_id,o.quantity,o.currency_item,o.price
            FROM game.shop_orders o LEFT JOIN game.characters ch USING(character_id)
            WHERE ($1='' OR o.character_id=$2 OR lower(ch.name)=lower($1)) AND ($3='' OR o.kind=$3)
            ORDER BY o.ordered_at DESC, o.order_id DESC LIMIT $4
            """, c, t);
        q.Parameters.Add(new NpgsqlParameter { Value = who, NpgsqlDbType = NpgsqlDbType.Text });
        q.Parameters.Add(new NpgsqlParameter { Value = (object?)id ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        q.Parameters.Add(new NpgsqlParameter { Value = (kind ?? "").Trim(), NpgsqlDbType = NpgsqlDbType.Text });
        q.Parameters.Add(new NpgsqlParameter { Value = limit });
        var list = new List<ShopOrderRecord>();
        await using (var r = await q.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                list.Add(new ShopOrderRecord(r.GetInt64(0), new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Utc)), r.GetGuid(2), r.GetString(3),
                    r.GetGuid(4), r.GetString(5), r.GetInt32(6), r.GetInt32(7), r.GetInt32(8), r.GetInt64(9)));
        await t.CommitAsync(ct);
        return list;
    }
}
