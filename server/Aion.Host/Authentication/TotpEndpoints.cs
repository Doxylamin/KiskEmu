using Aion.Application.Authentication;
using Aion.Infrastructure.Postgres;

namespace Aion.Host.Authentication;

public static class TotpEndpoints
{
    private sealed record TotpCode(string? Code, string? RecoveryCode);
    private sealed record DisableTotp(string Password, string? Code, string? RecoveryCode);
    private sealed record RegenerateRecovery(string Password);
    private sealed record EnrollTotp(string? Password);
    private sealed record CompleteLogin(string ChallengeToken, string? Code, string? RecoveryCode);

    public static void MapTotpEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/accounts/totp").RequireRateLimiting("account-entry");
        api.MapGet("/status", async (HttpContext ctx, IAccountAuthentication auth, ITotpAuthentication totp, CancellationToken ct) =>
        {
            var account = await auth.AuthenticateAsync(Bearer(ctx), ct);
            if (account is null) return Results.Unauthorized();
            var status = await totp.StatusAsync(account.AccountId, ct);
            return Results.Ok(new { enabled = status?.Enabled ?? false, recoveryCodesRemaining = status?.RecoveryCodesRemaining ?? 0 });
        });
        // Setting up an authenticator needs the password too, so a stolen session alone cannot lock the owner out
        // (after addiboii/A2Core 26306c5).
        api.MapPost("/enroll", async (EnrollTotp? input, HttpContext ctx, IAccountAuthentication auth, ITotpAuthentication totp, CancellationToken ct) =>
        {
            var account = await auth.AuthenticateAsync(Bearer(ctx), ct);
            if (account is null) return Results.Unauthorized();
            if (await totp.IsEnabledAsync(account.AccountId, ct)) return Results.Conflict(new { error = "already_enabled" });
            if (input?.Password is not { Length: > 0 } password)
                return Results.Json(new { error = "password_required" }, statusCode: StatusCodes.Status403Forbidden);
            var username = await UsernameAsync(totp, account.AccountId, ct);
            if (username is null) return Results.BadRequest(new { error = "account_unavailable" });
            var enrollment = await totp.BeginEnrollmentAsync(account.AccountId, username, password, ct);
            return enrollment is null
                ? Results.Json(new { error = "wrong_password" }, statusCode: StatusCodes.Status403Forbidden)
                : Results.Ok(enrollment);
        }).RequireRateLimiting("totp-verify");
        api.MapPost("/confirm", async (TotpCode input, HttpContext ctx, IAccountAuthentication auth, ITotpAuthentication totp, CancellationToken ct) =>
        {
            var account = await auth.AuthenticateAsync(Bearer(ctx), ct);
            if (account is null) return Results.Unauthorized();
            var (ok, recovery) = await totp.ConfirmEnrollmentAsync(account.AccountId, input.Code ?? "", ct);
            return ok ? Results.Ok(new { enabled = true, recoveryCodes = recovery }) : Results.Unauthorized();
        }).RequireRateLimiting("totp-verify");
        api.MapPost("/recovery/regenerate", async (RegenerateRecovery input, HttpContext ctx, IAccountAuthentication auth, ITotpAuthentication totp, CancellationToken ct) =>
        {
            var account = await auth.AuthenticateAsync(Bearer(ctx), ct);
            if (account is null) return Results.Unauthorized();
            var (ok, codes) = await totp.RegenerateRecoveryCodesAsync(account.AccountId, input.Password, ct);
            return ok ? Results.Ok(new { recoveryCodes = codes }) : Results.Unauthorized();
        });
        api.MapPost("/disable", async (DisableTotp input, HttpContext ctx, IAccountAuthentication auth, ITotpAuthentication totp, CancellationToken ct) =>
        {
            var account = await auth.AuthenticateAsync(Bearer(ctx), ct);
            if (account is null) return Results.Unauthorized();
            var ok = await totp.DisableAsync(account.AccountId, input.Password, input.Code, input.RecoveryCode, ct);
            return ok ? Results.NoContent() : Results.Unauthorized();
        }).RequireRateLimiting("totp-verify");
        app.MapPost("/api/accounts/login/totp", async (CompleteLogin input, ITotpAuthentication totp, CancellationToken ct) =>
        {
            var session = await totp.CompleteLoginAsync(input.ChallengeToken, input.Code, input.RecoveryCode, ct);
            return session is null ? Results.Unauthorized() : Results.Ok(session);
        }).RequireRateLimiting("totp-verify");
    }

    static async Task<string?> UsernameAsync(ITotpAuthentication totp, Guid accountId, CancellationToken ct)
    {
        if (totp is PostgresAccountAuthentication postgres) return await postgres.LookupUsernameAsync(accountId, ct);
        return accountId.ToString("N")[..8];
    }

    static string Bearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : "";
    }
}
