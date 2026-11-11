using Aion.Application.Gameplay;
using Npgsql;
using NpgsqlTypes;

namespace Aion.Infrastructure.Postgres;

/// <summary>A world's field boss timers (game.field_bosses, migration 104).</summary>
public sealed class PostgresFieldBosses(NpgsqlDataSource source) : IFieldBossStore
{
    public async Task<IReadOnlyList<FieldBossRecord>> LoadAsync(CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var q = new NpgsqlCommand("SELECT code,killed_at,respawn_at,min_minutes,max_minutes FROM game.field_bosses ORDER BY code", c);
        var list = new List<FieldBossRecord>();
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new FieldBossRecord(r.GetInt32(0), Time(r, 1), Time(r, 2), r.IsDBNull(3) ? null : r.GetInt32(3), r.IsDBNull(4) ? null : r.GetInt32(4)));
        return list;
    }

    public Task SaveStateAsync(int code, DateTimeOffset? killedAt, DateTimeOffset? respawnAt, CancellationToken ct = default) =>
        ExecuteAsync("""
            INSERT INTO game.field_bosses(code,killed_at,respawn_at) VALUES($1,$2,$3)
            ON CONFLICT (code) DO UPDATE SET killed_at=EXCLUDED.killed_at, respawn_at=EXCLUDED.respawn_at, updated_at=now()
            """, ct, NpgsqlDbType.TimestampTz, code, killedAt?.UtcDateTime, respawnAt?.UtcDateTime);

    public Task SaveTimerAsync(int code, int? minMinutes, int? maxMinutes, CancellationToken ct = default) =>
        ExecuteAsync("""
            INSERT INTO game.field_bosses(code,min_minutes,max_minutes) VALUES($1,$2,$3)
            ON CONFLICT (code) DO UPDATE SET min_minutes=EXCLUDED.min_minutes, max_minutes=EXCLUDED.max_minutes, updated_at=now()
            """, ct, NpgsqlDbType.Integer, code, minMinutes, maxMinutes);

    async Task ExecuteAsync(string sql, CancellationToken ct, NpgsqlDbType type, int code, object? a, object? b)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var q = new NpgsqlCommand(sql, c);
        q.Parameters.Add(new NpgsqlParameter { Value = code });
        q.Parameters.Add(new NpgsqlParameter { Value = a ?? DBNull.Value, NpgsqlDbType = type });
        q.Parameters.Add(new NpgsqlParameter { Value = b ?? DBNull.Value, NpgsqlDbType = type });
        await q.ExecuteNonQueryAsync(ct);
    }

    static DateTimeOffset? Time(NpgsqlDataReader r, int i) =>
        r.IsDBNull(i) ? null : new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc));
}
