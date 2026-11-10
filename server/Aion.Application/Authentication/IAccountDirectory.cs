namespace Aion.Application.Authentication;

/// <summary>An account as the account page and the staff Accounts page show it (the realm's auth schema).</summary>
public sealed record AccountSummary(Guid AccountId, string Username, DateTimeOffset CreatedAt, DateTimeOffset? DisabledAt,
    IReadOnlyList<string> Roles, DateTimeOffset? LastSignInAt);

/// <summary>A character in the realm's index of every world's characters (game.character_index).</summary>
public sealed record AccountCharacter(Guid CharacterId, int WorldId, string Name, int Level, int Race, DateTimeOffset? LastLoginAt);

public sealed record AccountDetail(AccountSummary Account, bool TotpEnabled, int ActiveSessions, int ActiveStaffSessions,
    IReadOnlyList<AccountCharacter> Characters);

public sealed record AccountPage(IReadOnlyList<AccountSummary> Accounts, int Total);

/// <summary>Which accounts a staff search lists.</summary>
public enum AccountFilter { All, Staff, Disabled }

/// <summary>
/// Reading accounts (the player's own profile, the staff Accounts page) and the staff account tools that are not roles
/// or disabling (IAccountManagement): ending an account's sessions and a temporary password. Both tools take the actor's
/// staff session and are checked again in the database (migration 099).
/// </summary>
public interface IAccountDirectory
{
    Task<AccountSummary?> ProfileAsync(Guid accountId, CancellationToken ct);
    Task<AccountPage> SearchAsync(string? query, AccountFilter filter, int page, int pageSize, CancellationToken ct);
    Task<AccountDetail?> DetailAsync(Guid accountId, CancellationToken ct);
    /// <summary>Ends every session of the target (moderators: players only; administrators: anyone but themselves).</summary>
    Task<bool> RevokeSessionsAsync(string staffSession, Guid target, CancellationToken ct);
    /// <summary>Administrators: sets a new random password and ends the target's sessions; returns it once, or null.</summary>
    Task<string?> ResetPasswordAsync(string staffSession, Guid target, CancellationToken ct);
}
