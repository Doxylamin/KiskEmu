using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Npgsql;
using NpgsqlTypes;

namespace Aion.Infrastructure.Postgres;

/// <summary>World registry (migration 090) on the gameplay role. Staff changes need the admin role and are audited.</summary>
public sealed class PostgresWorldRegistry(NpgsqlDataSource source) : IWorldRegistry
{
    const string Columns = "world_id,name,light_server_id,dark_server_id,kind,host,game_port,internal_port,enabled,level_cap,created_at,updated_at";

    static WorldRecord Read(NpgsqlDataReader r) => new(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetInt32(6), r.IsDBNull(7) ? null : r.GetInt32(7), r.GetBoolean(8),
        r.IsDBNull(9) ? null : r.GetInt32(9), Utc(r.GetDateTime(10)), Utc(r.GetDateTime(11)));
    static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    static NpgsqlParameter Int(int? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer };
    static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    static async Task<List<WorldRecord>> All(NpgsqlConnection c, NpgsqlTransaction? t, CancellationToken ct, bool forUpdate = false)
    {
        await using var q = new NpgsqlCommand($"SELECT {Columns} FROM game.worlds ORDER BY world_id" + (forUpdate ? " FOR UPDATE" : ""), c, t);
        await using var r = await q.ExecuteReaderAsync(ct);
        var list = new List<WorldRecord>();
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    public async Task<IReadOnlyList<WorldRecord>> ListAsync(CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        return await All(c, null, ct);
    }


    async Task<T> AsAdmin<T>(VerifiedAccount actor, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        await using (var roles = new NpgsqlCommand("""
            SELECT 1 FROM auth.accounts a JOIN auth.account_roles r USING(account_id)
            WHERE a.account_id=$1 AND a.disabled_at IS NULL AND r.role='admin'
            """, c, t))
        {
            roles.Parameters.AddWithValue(actor.AccountId);
            if (await roles.ExecuteScalarAsync(ct) is null) throw new GameplayException("forbidden");
        }
        var result = await work(c, t);
        await t.CommitAsync(ct);
        return result;
    }

    static async Task Audit(NpgsqlConnection c, NpgsqlTransaction t, VerifiedAccount actor, string action, object details, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand("INSERT INTO audit.events(action,actor_id,details) VALUES($1,$2,$3::jsonb)", c, t);
        q.Parameters.AddWithValue(action); q.Parameters.AddWithValue(actor.AccountId); q.Parameters.AddWithValue(JsonSerializer.Serialize(details));
        await q.ExecuteNonQueryAsync(ct);
    }

    static async Task<WorldRecord> Save(NpgsqlConnection c, NpgsqlTransaction t, WorldRecord w, bool insert, CancellationToken ct)
    {
        var sql = insert
            ? $"""
               INSERT INTO game.worlds(world_id,name,light_server_id,dark_server_id,kind,host,game_port,internal_port,enabled,level_cap)
               VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10) RETURNING {Columns}
               """
            : $"""
               UPDATE game.worlds SET name=$2,light_server_id=$3,dark_server_id=$4,kind=$5,host=$6,game_port=$7,internal_port=$8,
                   enabled=$9,level_cap=$10,updated_at=now() WHERE world_id=$1 RETURNING {Columns}
               """;
        await using var q = new NpgsqlCommand(sql, c, t);
        q.Parameters.Add(Int(w.WorldId)); q.Parameters.Add(Text(w.Name)); q.Parameters.Add(Int(w.LightServerId)); q.Parameters.Add(Int(w.DarkServerId));
        q.Parameters.Add(Text(w.Kind)); q.Parameters.Add(Text(w.Host)); q.Parameters.Add(Int(w.GamePort)); q.Parameters.Add(Int(w.InternalPort));
        q.Parameters.AddWithValue(w.Enabled); q.Parameters.Add(Int(w.LevelCap));
        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new GameplayException("unknown_world");
        return Read(r);
    }

    static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    public Task<WorldRecord> CreateAsync(VerifiedAccount actor, WorldInput input, CancellationToken ct = default) => AsAdmin(actor, async (c, t) =>
    {
        var all = await All(c, t, ct, forUpdate: true);
        var kind = Clean(input.Kind) ?? WorldRecord.Managed;
        // Defaults: the next free world id and login rows, the next free ports above the highest in use.
        var id = input.WorldId ?? (all.Count == 0 ? 1204 : Math.Max(1203, all.Max(w => w.WorldId)) + 1);
        if (all.Any(w => w.WorldId == id)) throw new GameplayException("world_id_in_use");
        // Login rows: the next ids after the highest Light (1361, 1362, ...) and Dark (2361, 2362, ...) rows.
        var light = input.LightServerId ?? Math.Max(1360, all.Select(w => w.LightServerId).DefaultIfEmpty(0).Max()) + 1;
        var dark = input.DarkServerId ?? Math.Max(2360, all.Select(w => w.DarkServerId).DefaultIfEmpty(0).Max()) + 1;
        int? gamePort = input.GamePort ?? (kind == WorldRecord.Managed ? Math.Max(13328, all.Select(w => w.GamePort ?? 0).DefaultIfEmpty(0).Max() + 1) : null);
        int? internalPort = input.InternalPort ?? (kind == WorldRecord.Managed ? Math.Max(18781, all.Select(w => w.InternalPort ?? 0).DefaultIfEmpty(0).Max() + 1) : null);
        var now = DateTimeOffset.UtcNow;
        var world = new WorldRecord(id, Clean(input.Name) ?? "", light, dark, kind, Clean(input.Host), gamePort, internalPort, false, input.LevelCap, now, now);
        if (WorldInput.Problem(world, all) is { } problem) throw new GameplayException(problem);
        var saved = await Save(c, t, world, insert: true, ct);
        await Audit(c, t, actor, "world.create", saved, ct);
        return saved;
    }, ct);

    public Task<WorldRecord> UpdateAsync(VerifiedAccount actor, int worldId, WorldInput input, CancellationToken ct = default) => AsAdmin(actor, async (c, t) =>
    {
        var all = await All(c, t, ct, forUpdate: true);
        var current = all.FirstOrDefault(w => w.WorldId == worldId) ?? throw new GameplayException("unknown_world");
        var kind = Clean(input.Kind) ?? current.Kind;
        if (input.WorldId is { } newId && newId != worldId) throw new GameplayException("invalid_world_id");
        // The login rows and the ports of a running world change only while it is stopped (clients hold them).
        var moved = input.LightServerId is { } l && l != current.LightServerId || input.DarkServerId is { } d && d != current.DarkServerId
            || input.GamePort is { } g && g != current.GamePort || input.InternalPort is { } i && i != current.InternalPort
            || Clean(input.Host) != current.Host && input.Host is not null || kind != current.Kind;
        if (moved && current.Enabled) throw new GameplayException("world_enabled");
        var world = current with
        {
            Name = Clean(input.Name) ?? current.Name,
            LightServerId = input.LightServerId ?? current.LightServerId,
            DarkServerId = input.DarkServerId ?? current.DarkServerId,
            Kind = kind,
            // An empty host clears it (the auth host's public address).
            Host = input.Host is null ? current.Host : Clean(input.Host),
            GamePort = input.GamePort ?? current.GamePort,
            InternalPort = input.InternalPort ?? current.InternalPort,
            LevelCap = input.LevelCap is 0 ? null : input.LevelCap ?? current.LevelCap,
        };
        if (WorldInput.Problem(world, all) is { } problem) throw new GameplayException(problem);
        var saved = await Save(c, t, world, insert: false, ct);
        await Audit(c, t, actor, "world.update", new { before = current, after = saved }, ct);
        return saved;
    }, ct);

    public Task<WorldRecord> SetEnabledAsync(VerifiedAccount actor, int worldId, bool enabled, CancellationToken ct = default) => AsAdmin(actor, async (c, t) =>
    {
        var all = await All(c, t, ct, forUpdate: true);
        var current = all.FirstOrDefault(w => w.WorldId == worldId) ?? throw new GameplayException("unknown_world");
        var saved = await Save(c, t, current with { Enabled = enabled }, insert: false, ct);
        await Audit(c, t, actor, enabled ? "world.start" : "world.stop", new { worldId, saved.Name }, ct);
        return saved;
    }, ct);

    public Task DeleteAsync(VerifiedAccount actor, int worldId, CancellationToken ct = default) => AsAdmin(actor, async (c, t) =>
    {
        var all = await All(c, t, ct, forUpdate: true);
        var current = all.FirstOrDefault(w => w.WorldId == worldId) ?? throw new GameplayException("unknown_world");
        if (current.Enabled) throw new GameplayException("world_enabled");
        await using (var count = new NpgsqlCommand("SELECT count(*) FROM game.characters WHERE world_id=$1", c, t))
        {
            count.Parameters.Add(Int(worldId));
            if ((long)(await count.ExecuteScalarAsync(ct))! > 0) throw new GameplayException("world_has_characters");
        }
        await using (var delete = new NpgsqlCommand("DELETE FROM game.worlds WHERE world_id=$1", c, t))
        {
            delete.Parameters.Add(Int(worldId));
            await delete.ExecuteNonQueryAsync(ct);
        }
        await Audit(c, t, actor, "world.delete", current, ct);
        return true;
    }, ct);

    public async Task<IReadOnlyDictionary<int, int>> CharacterCountsAsync(VerifiedAccount actor, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using (var roles = new NpgsqlCommand("""
            SELECT 1 FROM auth.accounts a JOIN auth.account_roles r USING(account_id)
            WHERE a.account_id=$1 AND a.disabled_at IS NULL AND r.role IN ('gamemaster','admin')
            """, c))
        {
            roles.Parameters.AddWithValue(actor.AccountId);
            if (await roles.ExecuteScalarAsync(ct) is null) throw new GameplayException("forbidden");
        }
        await using var q = new NpgsqlCommand("SELECT world_id,count(*)::int FROM game.characters GROUP BY world_id", c);
        await using var r = await q.ExecuteReaderAsync(ct);
        var counts = new Dictionary<int, int>();
        while (await r.ReadAsync(ct)) counts[r.GetInt32(0)] = r.GetInt32(1);
        return counts;
    }
}
