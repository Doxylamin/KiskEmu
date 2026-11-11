using Aion.Application.Authentication;

namespace Aion.Infrastructure.Postgres;

public sealed partial class PostgresAccountAuthentication : IWorldSessions
{
    public async Task<WorldSession?> OpenAsync(string ticket,long wireAccountId,string grant,int worldId,CancellationToken ct=default)
    {
        if(!ValidToken(ticket) || !Guid.TryParseExact(grant,"D",out var parsed)) return null;
        var token=Token();
        await using var q=source.CreateCommand("""
            WITH consumed AS (
              DELETE FROM auth.client_handoffs h USING auth.sessions s,auth.accounts a
              WHERE h.nonce_hash=$1 AND h.ticket_hash=$2 AND h.wire_id=$3 AND h.world_id=$4
                AND h.expires_at>now() AND s.token_hash=h.session_hash AND s.expires_at>now()
                AND a.account_id=s.account_id AND a.disabled_at IS NULL
              RETURNING s.token_hash,s.expires_at,h.race
            ), inserted AS (
              INSERT INTO auth.world_sessions(token_hash,session_hash,world_id,expires_at)
              SELECT $5,token_hash,$4,expires_at FROM consumed RETURNING session_hash,expires_at
            ) SELECT s.account_id,i.expires_at,(SELECT race FROM consumed) FROM inserted i JOIN auth.sessions s ON s.token_hash=i.session_hash
            """);
        q.Parameters.AddWithValue(Hash(parsed.ToString())); q.Parameters.AddWithValue(Hash(ticket));
        q.Parameters.AddWithValue(wireAccountId); q.Parameters.AddWithValue(worldId); q.Parameters.AddWithValue(Hash(token));
        await using var r=await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new(token,r.GetGuid(0),worldId,new DateTimeOffset(r.GetDateTime(1)),r.IsDBNull(2) ? null : r.GetInt16(2)) : null;
    }
    public async Task<WorldIdentity?> ValidateAsync(string token,CancellationToken ct=default)
    {
        if(!ValidToken(token)) return null;
        await using var q=source.CreateCommand("""
            SELECT s.account_id,w.world_id FROM auth.world_sessions w
            JOIN auth.sessions s ON s.token_hash=w.session_hash JOIN auth.accounts a USING(account_id)
            WHERE w.token_hash=$1 AND w.expires_at>now() AND s.expires_at>now() AND a.disabled_at IS NULL
            """);
        q.Parameters.AddWithValue(Hash(token)); await using var r=await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new(new(r.GetGuid(0)),r.GetInt32(1)) : null;
    }
    public async Task CloseAsync(string token,CancellationToken ct=default)
    {
        if(!ValidToken(token)) return;
        await using var q=source.CreateCommand("DELETE FROM auth.world_sessions WHERE token_hash=$1");
        q.Parameters.AddWithValue(Hash(token)); await q.ExecuteNonQueryAsync(ct);
    }
    public async Task<bool> ReportPlayingAsync(string token,PlayingCharacter? character,CancellationToken ct=default)
    {
        if(!ValidToken(token)) return false;
        // playing_since starts when the character changes (SET reads the row's old values) and stays while it does not.
        await using var q=source.CreateCommand("""
            UPDATE auth.world_sessions SET
              playing_since=CASE WHEN $2::uuid IS NULL THEN NULL WHEN character_id IS DISTINCT FROM $2::uuid OR playing_since IS NULL THEN now() ELSE playing_since END,
              character_id=$2,character_name=$3,character_level=$4,character_class=$5,character_race=$6,map_id=$7,seen_at=now()
            WHERE token_hash=$1 AND expires_at>now() RETURNING 1
            """);
        q.Parameters.AddWithValue(Hash(token));
        q.Parameters.Add(new Npgsql.NpgsqlParameter{Value=(object?)character?.CharacterId??DBNull.Value,NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Uuid});
        q.Parameters.Add(new Npgsql.NpgsqlParameter{Value=(object?)Clip(character?.Name,64)??DBNull.Value,NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Text});
        q.Parameters.Add(new Npgsql.NpgsqlParameter{Value=character is null ? DBNull.Value : Math.Clamp(character.Level,0,1000),NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Integer});
        q.Parameters.Add(new Npgsql.NpgsqlParameter{Value=(object?)Clip(character?.ClassName,32)??DBNull.Value,NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Text});
        q.Parameters.Add(new Npgsql.NpgsqlParameter{Value=character is null ? DBNull.Value : (short)Math.Clamp(character.Race,short.MinValue,short.MaxValue),NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Smallint});
        q.Parameters.Add(new Npgsql.NpgsqlParameter{Value=(object?)character?.MapId??DBNull.Value,NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Integer});
        return await q.ExecuteScalarAsync(ct) is not null;
    }
    static string? Clip(string? text,int max)=>string.IsNullOrEmpty(text) ? null : text.Length<=max ? text : text[..max];
}
