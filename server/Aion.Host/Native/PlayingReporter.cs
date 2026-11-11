using Aion.Application.Authentication;
using Aion.World;

namespace Aion.Host.Native;

/// <summary>
/// Reports what one game connection plays to its world session (the realm keeps it: the launcher's Discord status and the
/// account page's "In game"). It sends when the character, level, class or map changes, and at least every
/// <see cref="Heartbeat"/> so the realm knows the connection is still there (it drops a session quiet for 3 minutes).
/// Presence belongs to the session's account, never to an address. One report at a time, off the game loop: a slow realm
/// never holds up the connection, and a failed report is retried on the next tick.
/// </summary>
public sealed class PlayingReporter(IWorldSessions sessions, string token, ILogger logger)
{
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(60);

    PlayingCharacter? sent;
    long sentAt = long.MinValue;
    bool everSent;
    Task? inFlight;

    /// <summary>What the session plays now: the character in the world, else null (the lobby, character select).</summary>
    public static PlayingCharacter? Of(GameSession session) =>
        session.InWorld && session.CharacterId is { } id && session.CharacterName is { Length: > 0 } name
            ? new PlayingCharacter(id, name, session.Level, session.ClassName, session.CharacterRace, session.MapId)
            : null;

    /// <summary>Whether a report is due at <paramref name="nowMs"/> for <paramref name="current"/>.</summary>
    public bool Due(PlayingCharacter? current, long nowMs) =>
        !everSent || current != sent || nowMs - sentAt >= (long)Heartbeat.TotalMilliseconds;

    /// <summary>Called on the connection's tick: starts a report when one is due and none is in flight.</summary>
    public void Tick(GameSession session, long nowMs, CancellationToken ct)
    {
        if (inFlight is { IsCompleted: false }) return;
        var current = Of(session);
        if (!Due(current, nowMs)) return;
        inFlight = SendAsync(current, nowMs, ct);
    }

    async Task SendAsync(PlayingCharacter? current, long nowMs, CancellationToken ct)
    {
        try
        {
            await sessions.ReportPlayingAsync(token, current, ct);
            sent = current;
            sentAt = nowMs;
            everSent = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            // Retried on a later tick; the account just shows as not playing until a report gets through.
            sentAt = nowMs;
            logger.LogDebug("Playing report not sent: {Error}", error.Message);
        }
    }
}
