using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Application.Gameplay;

namespace Aion.World;

/// <summary>
/// "/bug &lt;text&gt;" for every player (docs/BUG_REPORTS.md): captures the reporter's context on their game loop
/// (<see cref="GameSession.CaptureBugContext"/>) and stores it with the text. One report per character every
/// <see cref="Cooldown"/>; the store also caps open reports per account.
/// </summary>
public sealed class BugReporter(IBugReports store, string? commit = null, Func<ushort, string?>? opcodeName = null)
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    readonly ConcurrentDictionary<string, DateTimeOffset> lastReport = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The clock (tests move it).</summary>
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;
    public string? Commit => commit;

    public static string Serialize(BugContext context) => JsonSerializer.Serialize(context, Json);

    /// <summary>The reply for the player.</summary>
    public async Task<string> ReportAsync(GameSession session, Guid? accountId, string text, CancellationToken ct)
    {
        text = text.Trim();
        if (text.Length < BugReportRules.MinText) return "Describe the bug: /bug <what happened> (your position, quests and nearby NPCs are added).";
        if (text.Length > BugReportRules.MaxText) text = text[..BugReportRules.MaxText];
        var now = Clock();
        var who = session.CharacterId?.ToString() ?? session.CharacterName ?? "?";
        if (lastReport.TryGetValue(who, out var last) && now - last < Cooldown)
            return $"Please wait {(int)Math.Ceiling((Cooldown - (now - last)).TotalSeconds)} s before the next bug report.";
        lastReport[who] = now;
        if (lastReport.Count > 50_000) foreach (var (key, at) in lastReport) if (now - at > Cooldown) lastReport.TryRemove(key, out _);
        BugContext context;
        try
        {
            context = await session.InvokeAsync(ms => (session.CaptureBugContext(ms, now, accountId ?? session.Store?.AccountId, commit, opcodeName), new List<byte[]>()))
                .WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        catch (Exception error) when (error is TimeoutException or TaskCanceledException)
        {
            lastReport.TryRemove(who, out _);
            return "The game connection did not respond.";
        }
        var report = new NewBugReport(context.Character.AccountId, context.Character.Id, context.Character.Name, context.Server.WorldId,
            context.Location?.MapId, context.Location?.MapName, context.Quests.Codes(), text, commit, Serialize(context));
        try
        {
            var id = await store.CreateAsync(report, ct);
            var where = context.Location is { } l ? $" at {l.MapName} ({l.X:0}, {l.Y:0}, {l.Z:0})" : "";
            return $"Thanks, bug report #{id} was saved{where}.";
        }
        catch (GameplayException error)
        {
            lastReport.TryRemove(who, out _);
            return error.Code == "too_many_bug_reports"
                ? $"You have {BugReportRules.OpenPerAccount} open bug reports. Please wait until the staff look at them."
                : "That bug report could not be saved.";
        }
    }
}
