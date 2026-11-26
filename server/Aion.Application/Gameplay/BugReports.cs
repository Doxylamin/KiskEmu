using System.Text.Json;
using Aion.Core.Accounts;

namespace Aion.Application.Gameplay;

// In-game bug reports (migration 106, docs/BUG_REPORTS.md): "/bug <text>" in chat stores the text with what the world
// server saw at that moment (the context JSON), so a report can be reproduced without asking the player. The realm keeps
// them; world servers send theirs over the realm link. Staff list, read and answer them on the dashboard (HandleTickets).

public static class BugReportRules
{
    public const string Open = "open", InProgress = "in_progress", Fixed = "fixed", WontFix = "wont_fix";
    public static readonly IReadOnlyList<string> Statuses = [Open, InProgress, Fixed, WontFix];
    public const int MinText = 3, MaxText = 2000, MaxNote = 2000;
    /// <summary>The context JSON's largest size: the packet ring is bounded well below this.</summary>
    public const int MaxContextBytes = 1024 * 1024;
    /// <summary>Open reports one account may have at a time (spam guard; the chat command also waits between reports).</summary>
    public const int OpenPerAccount = 30;
    public const int MaxScreenshotBytes = 8 * 1024 * 1024;
    /// <summary>A screenshot attaches to the character's latest report made at most this long ago.</summary>
    public static readonly TimeSpan ScreenshotWindow = TimeSpan.FromMinutes(30);
    public static readonly IReadOnlyList<string> ScreenshotTypes = ["image/png", "image/jpeg", "image/webp"];
}

/// <summary>A report as the world server made it (the realm sets the world of a linked world server from its sign-in).</summary>
public sealed record NewBugReport(Guid? AccountId, Guid? CharacterId, string? CharacterName, int WorldId, int? MapId, string? MapName,
    IReadOnlyList<string> Quests, string Text, string? Commit, string ContextJson);

public sealed record BugReportFilter(string? Status = null, int? MapId = null, string? Quest = null, int? WorldId = null, long? Before = null, int Limit = 100);

public sealed record BugReportSummary(long Id, DateTimeOffset CreatedAt, Guid? AccountId, string? AccountName, Guid? CharacterId, string? CharacterName,
    int WorldId, int? MapId, string? MapName, IReadOnlyList<string> Quests, string Text, string Status, string StaffNote, string? HandledBy,
    DateTimeOffset? HandledAt, string? Commit, string? ScreenshotType);

public sealed record BugReport(BugReportSummary Summary, JsonElement Context);

public sealed record BugScreenshot(string ContentType, byte[] Data);

public interface IBugReports
{
    /// <summary>Stores a report; its id. Throws GameplayException invalid_bug_report or too_many_bug_reports.</summary>
    Task<long> CreateAsync(NewBugReport report, CancellationToken ct = default);
    /// <summary>Newest first (HandleTickets).</summary>
    Task<IReadOnlyList<BugReportSummary>> ListAsync(VerifiedAccount actor, BugReportFilter filter, CancellationToken ct = default);
    Task<BugReport?> GetAsync(VerifiedAccount actor, long id, CancellationToken ct = default);
    /// <summary>Status (one of <see cref="BugReportRules.Statuses"/>) and staff note; audited.</summary>
    Task<bool> UpdateAsync(VerifiedAccount actor, long id, string status, string note, CancellationToken ct = default);
    /// <summary>
    /// The player's screenshot for their character's latest report (made within <see cref="BugReportRules.ScreenshotWindow"/>);
    /// the report's id, or null when there is no such report. Replaces an earlier screenshot of that report.
    /// </summary>
    Task<long?> AttachScreenshotAsync(Guid account, Guid character, BugScreenshot screenshot, CancellationToken ct = default);
    Task<BugScreenshot?> ScreenshotAsync(VerifiedAccount actor, long id, CancellationToken ct = default);
}
