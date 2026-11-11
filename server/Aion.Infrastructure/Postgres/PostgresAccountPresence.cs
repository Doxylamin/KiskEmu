using Aion.Application.Authentication;

namespace Aion.Infrastructure.Postgres;

/// <summary>Whether an account is in the game now (its world sessions' reports) and its show-playing preference.</summary>
public sealed partial class PostgresAccountAuthentication : IAccountPresence
{
    /// <summary>A world reports about every minute; a session quiet for longer than this (a crashed world) is not playing.</summary>
    public static readonly TimeSpan PlayingFreshness = TimeSpan.FromMinutes(3);

    public async Task<CurrentlyPlaying?> PlayingAsync(Guid accountId, CancellationToken ct = default)
    {
        await using var q = source.CreateCommand("""
            SELECT w.world_id, w.character_id, w.character_name, w.character_level, w.character_class, w.character_race, w.map_id, w.playing_since
            FROM auth.world_sessions w JOIN auth.sessions s ON s.token_hash = w.session_hash
            WHERE s.account_id = $1 AND s.expires_at > now() AND w.expires_at > now()
              AND w.character_id IS NOT NULL AND w.seen_at > now() - make_interval(secs => $2)
            ORDER BY w.seen_at DESC LIMIT 1
            """);
        q.Parameters.AddWithValue(accountId);
        q.Parameters.AddWithValue(PlayingFreshness.TotalSeconds);
        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var character = new PlayingCharacter(r.GetGuid(1), r.IsDBNull(2) ? "" : r.GetString(2), r.IsDBNull(3) ? 0 : r.GetInt32(3),
            r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? 0 : r.GetInt16(5), r.IsDBNull(6) ? null : r.GetInt32(6));
        var since = r.IsDBNull(7) ? DateTimeOffset.UtcNow : new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(7), DateTimeKind.Utc));
        return new CurrentlyPlaying(r.GetInt32(0), character, since);
    }

    public async Task<bool> ShowsPlayingAsync(Guid accountId, CancellationToken ct = default)
    {
        await using var q = source.CreateCommand("SELECT show_playing FROM auth.account_preferences WHERE account_id = $1");
        q.Parameters.AddWithValue(accountId);
        return await q.ExecuteScalarAsync(ct) is not bool show || show;
    }

    public async Task SetShowsPlayingAsync(Guid accountId, bool show, CancellationToken ct = default)
    {
        await using var q = source.CreateCommand("""
            INSERT INTO auth.account_preferences(account_id, show_playing) VALUES($1, $2)
            ON CONFLICT (account_id) DO UPDATE SET show_playing = EXCLUDED.show_playing, updated_at = now()
            """);
        q.Parameters.AddWithValue(accountId);
        q.Parameters.AddWithValue(show);
        await q.ExecuteNonQueryAsync(ct);
    }
}
