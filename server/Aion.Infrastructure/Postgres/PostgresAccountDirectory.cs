using System.Security.Cryptography;
using Aion.Application.Authentication;
using Npgsql;

namespace Aion.Infrastructure.Postgres;

/// <summary>Accounts for the account page and the staff Accounts page (IAccountDirectory; migration 099).</summary>
public sealed partial class PostgresAccountAuthentication : IAccountDirectory
{
    // Roles and the latest session per account; the latest session stands for the last sign-in (sessions are kept until
    // they end, so an account signed out everywhere shows none).
    private const string SummaryColumns = """
        a.account_id, c.username, a.created_at, a.disabled_at,
        COALESCE((SELECT array_agg(r.role ORDER BY r.role) FROM auth.account_roles r WHERE r.account_id=a.account_id), '{}'::text[]),
        (SELECT max(s.created_at) FROM auth.sessions s WHERE s.account_id=a.account_id)
        """;

    private static AccountSummary ReadSummary(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2),
        r.IsDBNull(3) ? null : r.GetFieldValue<DateTimeOffset>(3), r.GetFieldValue<string[]>(4),
        r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5));

    public async Task<AccountSummary?> ProfileAsync(Guid accountId, CancellationToken ct)
    {
        await using var q = source.CreateCommand("SELECT " + SummaryColumns + " FROM auth.accounts a JOIN auth.local_credentials c USING(account_id) WHERE a.account_id=$1");
        q.Parameters.AddWithValue(accountId);
        await using var r = await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadSummary(r) : null;
    }

    /// <summary>Accounts whose name, or one of whose characters' names, contains the query.</summary>
    public async Task<AccountPage> SearchAsync(string? query, AccountFilter filter, int page, int pageSize, CancellationToken ct)
    {
        var text = (query ?? "").Trim().ToLowerInvariant();
        if (text.Length > 64) text = text[..64];
        var pattern = "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(page, 0);
        await using var q = source.CreateCommand("SELECT " + SummaryColumns + ", count(*) OVER() FROM auth.accounts a JOIN auth.local_credentials c USING(account_id) "
            + "WHERE ($1='' OR c.username LIKE $2 OR EXISTS (SELECT 1 FROM game.character_index i WHERE i.account_id=a.account_id AND lower(i.name) LIKE $2)) "
            + "AND ($3<>'staff' OR EXISTS (SELECT 1 FROM auth.account_roles r WHERE r.account_id=a.account_id)) "
            + "AND ($3<>'disabled' OR a.disabled_at IS NOT NULL) "
            + "ORDER BY c.username LIMIT $4 OFFSET $5");
        q.Parameters.AddWithValue(text);
        q.Parameters.AddWithValue(pattern);
        q.Parameters.AddWithValue(filter switch { AccountFilter.Staff => "staff", AccountFilter.Disabled => "disabled", _ => "all" });
        q.Parameters.AddWithValue(pageSize);
        q.Parameters.AddWithValue(page * pageSize);
        var list = new List<AccountSummary>();
        var total = 0;
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) { list.Add(ReadSummary(r)); total = (int)r.GetInt64(6); }
        if (list.Count == 0 && page > 0) total = (await SearchAsync(query, filter, 0, 1, ct)).Total;
        return new AccountPage(list, total);
    }

    public async Task<AccountDetail?> DetailAsync(Guid accountId, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        AccountSummary? summary;
        bool totp; int sessions, staffSessions;
        await using (var q = new NpgsqlCommand("SELECT " + SummaryColumns + """
            , EXISTS (SELECT 1 FROM auth.account_totp t WHERE t.account_id=a.account_id),
              (SELECT count(*) FROM auth.sessions s WHERE s.account_id=a.account_id AND s.expires_at>now() AND s.kind='account'),
              (SELECT count(*) FROM auth.sessions s WHERE s.account_id=a.account_id AND s.expires_at>now() AND s.kind='staff')
            FROM auth.accounts a JOIN auth.local_credentials c USING(account_id) WHERE a.account_id=$1
            """, c))
        {
            q.Parameters.AddWithValue(accountId);
            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            summary = ReadSummary(r); totp = r.GetBoolean(6); sessions = (int)r.GetInt64(7); staffSessions = (int)r.GetInt64(8);
        }
        var characters = new List<AccountCharacter>();
        await using (var q = new NpgsqlCommand("SELECT character_id,world_id,name,level,race,last_login_at FROM game.character_index WHERE account_id=$1 ORDER BY world_id,created_at", c))
        {
            q.Parameters.AddWithValue(accountId);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                characters.Add(new(r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5)));
        }
        return new AccountDetail(summary, totp, sessions, staffSessions, characters);
    }

    public async Task<bool> RevokeSessionsAsync(string staffSession, Guid target, CancellationToken ct)
    {
        if (!ValidToken(staffSession) || target == Guid.Empty) return false;
        await using var q = source.CreateCommand("SELECT auth.staff_revoke_sessions($1,$2)");
        q.Parameters.AddWithValue(Hash(staffSession)); q.Parameters.AddWithValue(target);
        return await q.ExecuteScalarAsync(ct) is true;
    }

    public async Task<string?> ResetPasswordAsync(string staffSession, Guid target, CancellationToken ct)
    {
        if (!ValidToken(staffSession) || target == Guid.Empty) return null;
        var password = TemporaryPassword();
        await using var q = source.CreateCommand("SELECT auth.staff_reset_password($1,$2,$3)");
        q.Parameters.AddWithValue(Hash(staffSession)); q.Parameters.AddWithValue(target); q.Parameters.AddWithValue(hasher.HashPassword(new(target), password));
        return await q.ExecuteScalarAsync(ct) is true ? password : null;
    }

    // Four groups of four from letters and digits that are hard to mix up (no 0/O, 1/l/I): about 80 bits.
    private const string PasswordAlphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public static string TemporaryPassword()
    {
        Span<char> chars = stackalloc char[19];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = i % 5 == 4 ? '-' : PasswordAlphabet[RandomNumberGenerator.GetInt32(PasswordAlphabet.Length)];
        return new string(chars);
    }
}
