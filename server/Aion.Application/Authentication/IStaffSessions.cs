using Aion.Core.Accounts;

namespace Aion.Application.Authentication;

/// <summary>How a staff sign-in's password step ended.</summary>
public enum StaffSignInStep
{
    /// <summary>Wrong username or password, a locked or disabled account.</summary>
    Failed,
    /// <summary>The password was right but the account holds no staff role.</summary>
    NotStaff,
    /// <summary>The account has an authenticator: the challenge takes its code (or a recovery code).</summary>
    Code,
    /// <summary>No authenticator yet: the challenge sets one up first (staff must use two-step verification).</summary>
    Enroll,
}

public sealed record StaffSignIn(StaffSignInStep Step, string? ChallengeToken = null, DateTimeOffset? ExpiresAt = null)
{
    public override string ToString() => $"StaffSignIn {Step} [challenge redacted]";
}

/// <summary>
/// Staff dashboard sessions, apart from player sessions (auth.sessions.kind = 'staff', migration 098). A staff session
/// needs the password, a staff role and an authenticator code; an account without an authenticator sets one up during
/// the sign-in. Every /api/admin request needs a staff session of an account that is still staff.
/// </summary>
public interface IStaffSessions
{
    /// <summary>Standard staff sessions last 12 hours; "keep me signed in" 7 days.</summary>
    static readonly TimeSpan Lifetime = TimeSpan.FromHours(12), RememberedLifetime = TimeSpan.FromDays(7);

    Task<StaffSignIn> BeginAsync(string username, string password, bool remember, CancellationToken ct);
    /// <summary>The code step: a staff session, or null (wrong code, expired challenge, too many tries, no longer staff).</summary>
    Task<AccountSession?> CompleteAsync(string challengeToken, string? code, string? recoveryCode, CancellationToken ct);
    /// <summary>The enrolment step: a new authenticator secret for the challenge's account (null when the challenge is not one).</summary>
    Task<TotpEnrollment?> BeginEnrollmentAsync(string challengeToken, CancellationToken ct);
    /// <summary>Confirms the new authenticator with a code; then the staff session and the recovery codes (shown once).</summary>
    Task<(AccountSession Session, string[] RecoveryCodes)?> ConfirmEnrollmentAsync(string challengeToken, string code, CancellationToken ct);
    /// <summary>The account of a live staff session whose account is enabled and still holds a staff role; else null.</summary>
    Task<VerifiedAccount?> AuthenticateAsync(string token, CancellationToken ct);
}
