using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Aion.Core.Security;
using Npgsql;

namespace Aion.Infrastructure.Postgres;

/// <summary>
/// The realm's custom NPCs (game.custom_npcs, migration 102). Staff list them; admins create, change and remove them (audited
/// as custom_npc_saved / custom_npc_removed). The catalogue read is cached for a few seconds: world servers read it over the
/// realm link.
/// </summary>
public sealed class PostgresCustomNpcs(NpgsqlDataSource source) : ICustomNpcAdmin
{
    static readonly TimeSpan Fresh = TimeSpan.FromSeconds(5);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    (IReadOnlyList<CustomNpc> Npcs, DateTimeOffset At)? cache;
    readonly SemaphoreSlim gate = new(1, 1);

    const string Columns = "template_id,slug,base_template_id,name::text,placements::text,nodes::text,enabled,updated_at";

    public async Task<IReadOnlyList<CustomNpc>> NpcsAsync(CancellationToken ct = default)
    {
        if (cache is { } c && DateTimeOffset.UtcNow - c.At < Fresh) return c.Npcs;
        await gate.WaitAsync(ct);
        try
        {
            if (cache is { } again && DateTimeOffset.UtcNow - again.At < Fresh) return again.Npcs;
            await using var conn = await source.OpenConnectionAsync(ct);
            var npcs = await ReadAsync(conn, null, $"SELECT {Columns} FROM game.custom_npcs WHERE enabled ORDER BY template_id", ct);
            cache = (npcs, DateTimeOffset.UtcNow);
            return npcs;
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<CustomNpc>> ListAsync(VerifiedAccount actor, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        await RequireStaff(c, t, actor, StaffPermission.ViewCustomNpcs, ct);
        var npcs = await ReadAsync(c, t, $"SELECT {Columns} FROM game.custom_npcs ORDER BY template_id", ct);
        await t.CommitAsync(ct);
        return npcs;
    }

    public async Task<CustomNpc> SaveAsync(VerifiedAccount actor, CustomNpc npc, CancellationToken ct = default)
    {
        if (CustomNpcRules.Validate(npc) is { } invalid) throw new GameplayException(invalid);
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        await RequireStaff(c, t, actor, StaffPermission.EditCustomNpcs, ct);
        if (await Scalar(c, t, "SELECT template_id FROM game.custom_npcs WHERE lower(slug)=lower($1)", ct, npc.Slug) is int owner && owner != npc.TemplateId)
            throw new GameplayException("slug_taken");
        var id = npc.TemplateId;
        if (id == 0)
            id = (int)(await Scalar(c, t, $"SELECT COALESCE(max(template_id)+1,{CustomNpcRules.FirstTemplateId}) FROM game.custom_npcs", ct) as int? ?? CustomNpcRules.FirstTemplateId);
        if (!CustomNpcRules.IsCustom(id)) throw new GameplayException("npcs_full");
        var saved = await ReadAsync(c, t, $"""
            INSERT INTO game.custom_npcs(template_id,slug,base_template_id,name,placements,nodes,enabled,updated_at,updated_by)
            VALUES($1,$2,$3,$4::jsonb,$5::jsonb,$6::jsonb,$7,now(),$8)
            ON CONFLICT(template_id) DO UPDATE SET slug=EXCLUDED.slug,base_template_id=EXCLUDED.base_template_id,name=EXCLUDED.name,
                placements=EXCLUDED.placements,nodes=EXCLUDED.nodes,enabled=EXCLUDED.enabled,updated_at=now(),updated_by=EXCLUDED.updated_by
            RETURNING {Columns}
            """, ct, id, npc.Slug, npc.BaseTemplateId, JsonSerializer.Serialize(npc.Name, Json), JsonSerializer.Serialize(npc.Placements, Json),
            JsonSerializer.Serialize(npc.Nodes, Json), npc.Enabled, actor.AccountId);
        await Audit(c, t, actor, "custom_npc_saved", new { template_id = id, npc.Slug, npc.BaseTemplateId, npc.Enabled, created = npc.TemplateId == 0 }, ct);
        await t.CommitAsync(ct);
        cache = null;
        return saved[0];
    }

    public async Task<bool> DeleteAsync(VerifiedAccount actor, int templateId, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        await RequireStaff(c, t, actor, StaffPermission.EditCustomNpcs, ct);
        await using var q = Query(c, t, "DELETE FROM game.custom_npcs WHERE template_id=$1", templateId);
        var removed = await q.ExecuteNonQueryAsync(ct) > 0;
        if (removed) await Audit(c, t, actor, "custom_npc_removed", new { template_id = templateId }, ct);
        await t.CommitAsync(ct);
        cache = null;
        return removed;
    }

    static async Task<IReadOnlyList<CustomNpc>> ReadAsync(NpgsqlConnection c, NpgsqlTransaction? t, string sql, CancellationToken ct, params object?[] args)
    {
        await using var q = Query(c, t, sql, args);
        await using var r = await q.ExecuteReaderAsync(ct);
        var list = new List<CustomNpc>();
        while (await r.ReadAsync(ct))
            list.Add(new CustomNpc(r.GetInt32(0), r.GetString(1), r.GetInt32(2),
                JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(3), Json) ?? [],
                JsonSerializer.Deserialize<List<CustomNpcPlacement>>(r.GetString(4), Json) ?? [],
                JsonSerializer.Deserialize<List<CustomNpcNode>>(r.GetString(5), Json) ?? [],
                r.GetBoolean(6), new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(7), DateTimeKind.Utc))));
        return list;
    }

    static NpgsqlCommand Query(NpgsqlConnection c, NpgsqlTransaction? t, string sql, params object?[] args)
    {
        var q = new NpgsqlCommand(sql, c, t);
        foreach (var arg in args) q.Parameters.Add(new NpgsqlParameter { Value = arg ?? DBNull.Value });
        return q;
    }

    static async Task<object?> Scalar(NpgsqlConnection c, NpgsqlTransaction t, string sql, CancellationToken ct, params object?[] args)
    {
        await using var q = Query(c, t, sql, args);
        var value = await q.ExecuteScalarAsync(ct);
        return value is DBNull ? null : value;
    }

    static Task RequireStaff(NpgsqlConnection c, NpgsqlTransaction t, VerifiedAccount actor, StaffPermission permission, CancellationToken ct) =>
        PostgresCustomShop.RequireStaff(c, t, actor, permission, ct);

    static async Task Audit(NpgsqlConnection c, NpgsqlTransaction t, VerifiedAccount actor, string action, object details, CancellationToken ct)
    {
        await using var q = Query(c, t, "INSERT INTO audit.events(action,actor_id,details) VALUES($1,$2,$3::jsonb)", action, actor.AccountId, JsonSerializer.Serialize(details));
        await q.ExecuteNonQueryAsync(ct);
    }
}
