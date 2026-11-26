using System.Text;
using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Aion.Core.Security;
using Npgsql;
using NpgsqlTypes;

namespace Aion.Infrastructure.Postgres;

/// <summary>
/// The realm's bug reports (game.bug_reports, migration 106). Players' reports come from the world servers ("/bug" in chat);
/// staff with HandleTickets list, read and answer them (audited as staff_bug_report); a player adds a screenshot to their
/// character's latest report.
/// </summary>
public sealed class PostgresBugReports(NpgsqlDataSource source) : IBugReports
{
    const string Fields = "b.report_id,b.created_at,b.account_id,a.subject,b.character_id,b.character_name,b.world_id,b.map_id,b.map_name,b.quests,"
        + "b.content,b.status,b.staff_note,h.subject,b.handled_at,b.server_commit,s.content_type";
    const string From = "FROM game.bug_reports b LEFT JOIN auth.accounts a ON a.account_id=b.account_id "
        + "LEFT JOIN auth.accounts h ON h.account_id=b.handled_by LEFT JOIN game.bug_report_screenshots s ON s.report_id=b.report_id";

    public async Task<long> CreateAsync(NewBugReport report, CancellationToken ct = default)
    {
        var text = (report.Text ?? "").Trim();
        if (text.Length is < BugReportRules.MinText or > BugReportRules.MaxText || (report.CharacterName?.Length ?? 0) > 64
            || Encoding.UTF8.GetByteCount(report.ContextJson ?? "") > BugReportRules.MaxContextBytes)
            throw new GameplayException("invalid_bug_report");
        await using var c = await source.OpenConnectionAsync(ct);
        if (report.AccountId is { } account)
        {
            await using var count = Query(c, null, "SELECT count(*) FROM game.bug_reports WHERE account_id=$1 AND status='open'", account);
            if ((long)(await count.ExecuteScalarAsync(ct))! >= BugReportRules.OpenPerAccount) throw new GameplayException("too_many_bug_reports");
        }
        await using var q = Query(c, null, """
            INSERT INTO game.bug_reports(account_id,character_id,character_name,world_id,map_id,map_name,quests,content,server_commit,context)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10::jsonb) RETURNING report_id
            """,
            Typed(report.AccountId, NpgsqlDbType.Uuid), Typed(report.CharacterId, NpgsqlDbType.Uuid), Typed(report.CharacterName, NpgsqlDbType.Text),
            report.WorldId, Typed(report.MapId, NpgsqlDbType.Integer), Typed(Clip(report.MapName, 200), NpgsqlDbType.Text),
            report.Quests.Where(q => q.Length is > 0 and <= 64).Distinct().Take(50).ToArray(), text, Typed(Clip(report.Commit, 64), NpgsqlDbType.Text),
            report.ContextJson ?? "{}");
        return (long)(await q.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<BugReportSummary>> ListAsync(VerifiedAccount actor, BugReportFilter filter, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        await PostgresCustomShop.RequireStaff(c, t, actor, StaffPermission.HandleTickets, ct);
        var status = filter.Status is { } s && BugReportRules.Statuses.Contains(s) ? s : null;
        await using var q = Query(c, t, $"""
            SELECT {Fields} {From}
            WHERE ($1::text IS NULL OR b.status=$1) AND ($2::integer IS NULL OR b.map_id=$2) AND ($3::text IS NULL OR $3=ANY(b.quests))
              AND ($4::integer IS NULL OR b.world_id=$4) AND ($5::bigint IS NULL OR b.report_id<$5)
            ORDER BY b.report_id DESC LIMIT $6
            """,
            Typed(status, NpgsqlDbType.Text), Typed(filter.MapId, NpgsqlDbType.Integer), Typed(string.IsNullOrWhiteSpace(filter.Quest) ? null : filter.Quest.Trim(), NpgsqlDbType.Text),
            Typed(filter.WorldId, NpgsqlDbType.Integer), Typed(filter.Before, NpgsqlDbType.Bigint), Math.Clamp(filter.Limit, 1, 500));
        var list = new List<BugReportSummary>();
        await using (var r = await q.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) list.Add(Read(r));
        await t.CommitAsync(ct);
        return list;
    }

    public async Task<BugReport?> GetAsync(VerifiedAccount actor, long id, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        await PostgresCustomShop.RequireStaff(c, t, actor, StaffPermission.HandleTickets, ct);
        await using var q = Query(c, t, $"SELECT {Fields},b.context::text {From} WHERE b.report_id=$1", id);
        BugReport? report = null;
        await using (var r = await q.ExecuteReaderAsync(ct))
            if (await r.ReadAsync(ct))
            {
                using var context = JsonDocument.Parse(r.GetString(17));
                report = new BugReport(Read(r), context.RootElement.Clone());
            }
        await t.CommitAsync(ct);
        return report;
    }

    public async Task<bool> UpdateAsync(VerifiedAccount actor, long id, string status, string note, CancellationToken ct = default)
    {
        note = (note ?? "").Trim();
        if (!BugReportRules.Statuses.Contains(status) || note.Length > BugReportRules.MaxNote) throw new GameplayException("invalid_bug_report");
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        await PostgresCustomShop.RequireStaff(c, t, actor, StaffPermission.HandleTickets, ct);
        await using var q = Query(c, t, "UPDATE game.bug_reports SET status=$2,staff_note=$3,handled_by=$4,handled_at=now() WHERE report_id=$1", id, status, note, actor.AccountId);
        var changed = await q.ExecuteNonQueryAsync(ct) == 1;
        if (changed)
        {
            await using var audit = Query(c, t, "INSERT INTO audit.events(action,actor_id,details) VALUES($1,$2,$3::jsonb)", "staff_bug_report", actor.AccountId,
                JsonSerializer.Serialize(new { report = id, status, note }));
            await audit.ExecuteNonQueryAsync(ct);
        }
        await t.CommitAsync(ct);
        return changed;
    }

    public async Task<long?> AttachScreenshotAsync(Guid account, Guid character, BugScreenshot screenshot, CancellationToken ct = default)
    {
        if (!BugReportRules.ScreenshotTypes.Contains(screenshot.ContentType) || screenshot.Data.Length is 0 or > BugReportRules.MaxScreenshotBytes)
            throw new GameplayException("invalid_screenshot");
        await using var c = await source.OpenConnectionAsync(ct);
        await using var q = Query(c, null, """
            WITH latest AS (
                SELECT report_id FROM game.bug_reports
                WHERE account_id=$1 AND character_id=$2 AND created_at > now() - make_interval(secs => $3)
                ORDER BY report_id DESC LIMIT 1)
            INSERT INTO game.bug_report_screenshots(report_id,content_type,data)
            SELECT report_id,$4,$5 FROM latest
            ON CONFLICT (report_id) DO UPDATE SET content_type=EXCLUDED.content_type,data=EXCLUDED.data,created_at=now()
            RETURNING report_id
            """, account, character, BugReportRules.ScreenshotWindow.TotalSeconds, screenshot.ContentType, screenshot.Data);
        return await q.ExecuteScalarAsync(ct) is long id ? id : null;
    }

    public async Task<BugScreenshot?> ScreenshotAsync(VerifiedAccount actor, long id, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        await PostgresCustomShop.RequireStaff(c, t, actor, StaffPermission.HandleTickets, ct);
        await using var q = Query(c, t, "SELECT content_type,data FROM game.bug_report_screenshots WHERE report_id=$1", id);
        BugScreenshot? shot = null;
        await using (var r = await q.ExecuteReaderAsync(ct))
            if (await r.ReadAsync(ct)) shot = new BugScreenshot(r.GetString(0), (byte[])r[1]);
        await t.CommitAsync(ct);
        return shot;
    }

    static BugReportSummary Read(NpgsqlDataReader r) => new(
        r.GetInt64(0), At(r.GetDateTime(1)), r.IsDBNull(2) ? null : r.GetGuid(2), Text(r, 3), r.IsDBNull(4) ? null : r.GetGuid(4), Text(r, 5),
        r.GetInt32(6), r.IsDBNull(7) ? null : r.GetInt32(7), Text(r, 8), r.IsDBNull(9) ? [] : (string[])r[9], r.GetString(10),
        r.GetString(11), r.GetString(12), Text(r, 13), r.IsDBNull(14) ? null : At(r.GetDateTime(14)), Text(r, 15), Text(r, 16));

    static DateTimeOffset At(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    static string? Text(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    static string? Clip(string? text, int length) => text is null ? null : text.Length <= length ? text : text[..length];

    sealed record TypedValue(object? Value, NpgsqlDbType Type);
    static TypedValue Typed(object? value, NpgsqlDbType type) => new(value, type);

    static NpgsqlCommand Query(NpgsqlConnection c, NpgsqlTransaction? t, string sql, params object?[] args)
    {
        var q = new NpgsqlCommand(sql, c, t);
        foreach (var arg in args)
            q.Parameters.Add(arg switch
            {
                TypedValue typed => new NpgsqlParameter { Value = typed.Value ?? DBNull.Value, NpgsqlDbType = typed.Type },
                null => new NpgsqlParameter { Value = DBNull.Value },
                _ => new NpgsqlParameter { Value = arg },
            });
        return q;
    }
}
