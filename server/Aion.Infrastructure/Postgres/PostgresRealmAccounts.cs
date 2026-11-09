using Aion.Application.Authentication;
using Npgsql;
using NpgsqlTypes;

namespace Aion.Infrastructure.Postgres;

/// <summary>Realm database (migration 092): account snapshots for the worlds' mirrors, and the character index.</summary>
public sealed class PostgresRealmAccounts(NpgsqlDataSource source) : IRealmAccounts
{
    public async Task<AccountSnapshot?> SnapshotAsync(Guid accountId, CancellationToken ct = default)
    {
        await using var q = source.CreateCommand("""
            SELECT a.provider,a.subject,a.created_at,a.disabled_at,a.extended_character_slots,
                   ARRAY(SELECT r.role FROM auth.account_roles r WHERE r.account_id=a.account_id ORDER BY r.role)
            FROM auth.accounts a WHERE a.account_id=$1
            """);
        q.Parameters.AddWithValue(accountId);
        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new AccountSnapshot(accountId, r.GetString(0), r.GetString(1), Utc(r.GetDateTime(2)),
            r.IsDBNull(3) ? null : Utc(r.GetDateTime(3)), r.GetInt16(4), r.GetFieldValue<string[]>(5));
    }

    public async Task SyncCharactersAsync(int worldId, bool full, IReadOnlyList<IndexedCharacter> upserts, IReadOnlyList<Guid> removed, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        foreach (var batch in upserts.Chunk(500))
        {
            // Accounts the realm does not know (deleted since) are skipped: the index references auth.accounts.
            await using var q = new NpgsqlCommand("""
                INSERT INTO game.character_index(character_id,account_id,world_id,wire_id,name,level,race,created_at,last_login_at,last_logout_at,synced_at)
                SELECT u.id,u.account,$1,u.wire,u.name,u.level,u.race,u.created,u.login,u.logout,now()
                FROM unnest($2::uuid[],$3::uuid[],$4::int[],$5::text[],$6::int[],$7::int[],$8::timestamptz[],$9::timestamptz[],$10::timestamptz[])
                     AS u(id,account,wire,name,level,race,created,login,logout)
                WHERE EXISTS(SELECT FROM auth.accounts a WHERE a.account_id=u.account)
                ON CONFLICT (character_id) DO UPDATE SET account_id=EXCLUDED.account_id,world_id=EXCLUDED.world_id,wire_id=EXCLUDED.wire_id,
                    name=EXCLUDED.name,level=EXCLUDED.level,race=EXCLUDED.race,created_at=EXCLUDED.created_at,
                    last_login_at=EXCLUDED.last_login_at,last_logout_at=EXCLUDED.last_logout_at,synced_at=now()
                """, c, t);
            q.Parameters.AddWithValue(worldId);
            q.Parameters.AddWithValue(batch.Select(x => x.CharacterId).ToArray());
            q.Parameters.AddWithValue(batch.Select(x => x.AccountId).ToArray());
            q.Parameters.AddWithValue(batch.Select(x => x.WireId).ToArray());
            q.Parameters.AddWithValue(batch.Select(x => x.Name).ToArray());
            q.Parameters.AddWithValue(batch.Select(x => x.Level).ToArray());
            q.Parameters.AddWithValue(batch.Select(x => x.Race).ToArray());
            q.Parameters.Add(Times(batch.Select(x => (DateTimeOffset?)x.CreatedAt)));
            q.Parameters.Add(Times(batch.Select(x => x.LastLoginAt)));
            q.Parameters.Add(Times(batch.Select(x => x.LastLogoutAt)));
            await q.ExecuteNonQueryAsync(ct);
        }
        await using (var q = full
            ? new NpgsqlCommand("DELETE FROM game.character_index WHERE world_id=$1 AND NOT (character_id = ANY($2))", c, t)
            : new NpgsqlCommand("DELETE FROM game.character_index WHERE world_id=$1 AND character_id = ANY($2)", c, t))
        {
            q.Parameters.AddWithValue(worldId);
            q.Parameters.AddWithValue((full ? upserts.Select(x => x.CharacterId) : removed).ToArray());
            await q.ExecuteNonQueryAsync(ct);
        }
        await t.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<(Guid CharacterId, int WireId)>> WireIdsAsync(Guid accountId, int worldId, CancellationToken ct = default)
    {
        await using var q = source.CreateCommand("SELECT character_id,wire_id FROM game.character_index WHERE account_id=$1 AND world_id=$2");
        q.Parameters.AddWithValue(accountId);
        q.Parameters.AddWithValue(worldId);
        await using var r = await q.ExecuteReaderAsync(ct);
        var list = new List<(Guid, int)>();
        while (await r.ReadAsync(ct)) list.Add((r.GetGuid(0), r.GetInt32(1)));
        return list;
    }

    static NpgsqlParameter Times(IEnumerable<DateTimeOffset?> values) => new()
    {
        Value = values.Select(v => v is { } x ? (object)x.UtcDateTime : DBNull.Value).ToArray(),
        NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz,
    };

    static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

/// <summary>
/// World database (migration 092): the mirror of the accounts that play in this world (auth.accounts and
/// auth.account_roles as the realm has them), and this world's characters for the realm's index.
/// </summary>
public sealed class PostgresAccountMirror(NpgsqlDataSource source) : IAccountMirror
{
    public async Task UpsertAsync(AccountSnapshot account, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        await using (var q = new NpgsqlCommand("""
            INSERT INTO auth.accounts(account_id,provider,subject,created_at,disabled_at,extended_character_slots,mirrored_at)
            VALUES($1,$2,$3,$4,$5,$6,now())
            ON CONFLICT (account_id) DO UPDATE SET provider=EXCLUDED.provider,subject=EXCLUDED.subject,disabled_at=EXCLUDED.disabled_at,
                extended_character_slots=EXCLUDED.extended_character_slots,mirrored_at=now()
            """, c, t))
        {
            q.Parameters.AddWithValue(account.AccountId);
            q.Parameters.AddWithValue(account.Provider);
            q.Parameters.AddWithValue(account.Subject);
            q.Parameters.AddWithValue(account.CreatedAt.UtcDateTime);
            q.Parameters.Add(new NpgsqlParameter { Value = account.DisabledAt is { } d ? d.UtcDateTime : DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            q.Parameters.AddWithValue((short)account.ExtendedCharacterSlots);
            await q.ExecuteNonQueryAsync(ct);
        }
        await using (var q = new NpgsqlCommand("""
            DELETE FROM auth.account_roles WHERE account_id=$1 AND NOT (role = ANY($2));
            """, c, t))
        {
            q.Parameters.AddWithValue(account.AccountId);
            q.Parameters.AddWithValue(account.Roles.ToArray());
            await q.ExecuteNonQueryAsync(ct);
        }
        await using (var q = new NpgsqlCommand("""
            INSERT INTO auth.account_roles(account_id,role) SELECT $1,unnest($2::text[]) ON CONFLICT DO NOTHING
            """, c, t))
        {
            q.Parameters.AddWithValue(account.AccountId);
            q.Parameters.AddWithValue(account.Roles.ToArray());
            await q.ExecuteNonQueryAsync(ct);
        }
        await t.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<IndexedCharacter>> CharactersAsync(CancellationToken ct = default)
    {
        await using var q = source.CreateCommand(
            "SELECT character_id,account_id,COALESCE(wire_id,0),name,level,race,created_at,last_login_at,last_logout_at FROM game.characters");
        await using var r = await q.ExecuteReaderAsync(ct);
        var list = new List<IndexedCharacter>();
        while (await r.ReadAsync(ct))
            list.Add(new IndexedCharacter(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5),
                Utc(r.GetDateTime(6)), r.IsDBNull(7) ? null : Utc(r.GetDateTime(7)), r.IsDBNull(8) ? null : Utc(r.GetDateTime(8))));
        return list;
    }

    static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
