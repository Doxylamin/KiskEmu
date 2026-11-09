using Aion.Core.Accounts;

namespace Aion.Application.Authentication;

/// <summary>A game-port session; Race is the login row's race the player picked (multi-world routing), if recorded.</summary>
public sealed record WorldSession(string Token, Guid AccountId, int WorldId, DateTimeOffset ExpiresAt, int? Race = null)
{
    public override string ToString() => "WorldSession [token redacted]";
}
public sealed record WorldIdentity(VerifiedAccount Account, int WorldId);
public interface IWorldSessions
{
    Task<WorldSession?> OpenAsync(string ticket, long wireAccountId, string grant, int worldId, CancellationToken ct=default);
    Task<WorldIdentity?> ValidateAsync(string token, CancellationToken ct=default);
    Task CloseAsync(string token, CancellationToken ct=default);
}
