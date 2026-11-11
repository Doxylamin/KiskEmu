using Aion.Core.Accounts;

namespace Aion.Application.Authentication;

/// <summary>A game-port session; Race is the login row's race the player picked (multi-world routing), if recorded.</summary>
public sealed record WorldSession(string Token, Guid AccountId, int WorldId, DateTimeOffset ExpiresAt, int? Race = null)
{
    public override string ToString() => "WorldSession [token redacted]";
}
public sealed record WorldIdentity(VerifiedAccount Account, int WorldId);

/// <summary>The character a game connection plays (in the world, after entry). ClassName as the world's class profile names it.</summary>
public sealed record PlayingCharacter(Guid CharacterId, string Name, int Level, string? ClassName, int Race, int? MapId);

public interface IWorldSessions
{
    Task<WorldSession?> OpenAsync(string ticket, long wireAccountId, string grant, int worldId, CancellationToken ct=default);
    Task<WorldIdentity?> ValidateAsync(string token, CancellationToken ct=default);
    Task CloseAsync(string token, CancellationToken ct=default);
    /// <summary>
    /// The character this session plays now (null: in the lobby), and that the connection is still there. False when the
    /// session is gone (closed or expired). Presence is per session, so per account: never by address.
    /// </summary>
    Task<bool> ReportPlayingAsync(string token, PlayingCharacter? character, CancellationToken ct=default);
}

/// <summary>A character in the game now: its world, what it is, and since when.</summary>
public sealed record CurrentlyPlaying(int WorldId, PlayingCharacter Character, DateTimeOffset Since);

/// <summary>Realm side: whether an account is in the game now, and whether it lets others see that.</summary>
public interface IAccountPresence
{
    /// <summary>A world session that reported a character within the last 3 minutes (the latest, when several do), else null.</summary>
    Task<CurrentlyPlaying?> PlayingAsync(Guid accountId, CancellationToken ct=default);
    /// <summary>The account's opt-out: whether others may see the character it plays (default true).</summary>
    Task<bool> ShowsPlayingAsync(Guid accountId, CancellationToken ct=default);
    Task SetShowsPlayingAsync(Guid accountId, bool show, CancellationToken ct=default);
}
