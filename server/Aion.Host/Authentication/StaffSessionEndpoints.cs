using Aion.Application.Authentication;

namespace Aion.Host.Authentication;

/// <summary>
/// Staff dashboard sign-in (IStaffSessions, migration 098): the password, then an authenticator code, give a staff
/// session. Staff without an authenticator set one up inside the sign-in, so every staff session has two steps.
/// <see cref="UseStaffSessions"/> makes every other /api/admin request need a staff session.
/// </summary>
public static class StaffSessionEndpoints
{
    public sealed record StaffCredentials(string? Username, string? Password, bool? Remember);
    public sealed record StaffCode(string? ChallengeToken, string? Code, string? RecoveryCode);
    public sealed record StaffChallenge(string? ChallengeToken);

    /// <summary>The routes a request without a staff session may use: the sign-in itself, and dropping the wardrobe pass.</summary>
    public static bool Open(HttpRequest request) =>
        request.Path.StartsWithSegments("/api/admin/session")
        || HttpMethods.IsDelete(request.Method) && request.Path.StartsWithSegments("/api/admin/wardrobe-access");

    public static void MapStaffSessionEndpoints(this WebApplication app)
    {
        var session = app.MapGroup("/api/admin/session");
        session.MapPost("", async (StaffCredentials input, IStaffSessions staff, CancellationToken ct) =>
        {
            if (input.Username is not { Length: > 0 } username || input.Password is not { Length: > 0 } password) return Results.Unauthorized();
            var result = await staff.BeginAsync(username, password, input.Remember == true, ct);
            return result.Step switch
            {
                StaffSignInStep.Code => Results.Ok(new { step = "code", challengeToken = result.ChallengeToken, expiresAt = result.ExpiresAt }),
                StaffSignInStep.Enroll => Results.Ok(new { step = "enroll", challengeToken = result.ChallengeToken, expiresAt = result.ExpiresAt }),
                StaffSignInStep.NotStaff => Results.Json(new { error = "forbidden" }, statusCode: StatusCodes.Status403Forbidden),
                _ => Results.Unauthorized(),
            };
        }).RequireRateLimiting("account-entry");
        session.MapPost("/totp", async (StaffCode input, IStaffSessions staff, CancellationToken ct) =>
            await staff.CompleteAsync(input.ChallengeToken ?? "", input.Code, input.RecoveryCode, ct) is { } signed
                ? Results.Ok(signed) : Results.Unauthorized()).RequireRateLimiting("totp-verify");
        session.MapPost("/enroll", async (StaffChallenge input, IStaffSessions staff, CancellationToken ct) =>
            await staff.BeginEnrollmentAsync(input.ChallengeToken ?? "", ct) is { } enrollment
                ? Results.Ok(new { enrollment.Secret, enrollment.OtpAuthUri }) : Results.Unauthorized()).RequireRateLimiting("totp-verify");
        session.MapPost("/enroll/confirm", async (StaffCode input, IStaffSessions staff, CancellationToken ct) =>
            await staff.ConfirmEnrollmentAsync(input.ChallengeToken ?? "", input.Code ?? "", ct) is { } done
                ? Results.Ok(new { done.Session.Token, done.Session.AccountId, done.Session.ExpiresAt, done.RecoveryCodes })
                : Results.Unauthorized()).RequireRateLimiting("totp-verify");
        session.MapDelete("", async (HttpContext ctx, IAccountAuthentication auth, CancellationToken ct) =>
        {
            await auth.SignOutAsync(Bearer(ctx), ct);
            return Results.NoContent();
        });
    }

    /// <summary>
    /// Every /api/admin request (except the sign-in) needs a live staff session of an account that is still staff:
    /// 401 staff_session_required otherwise. Runs before the world proxy, so a world server only sees staff requests.
    /// </summary>
    public static void UseStaffSessions(this WebApplication app)
    {
        if (app.Services.GetService<IStaffSessions>() is not { } staff) return;
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api/admin") || Open(context.Request)
                || await staff.AuthenticateAsync(Bearer(context), context.RequestAborted) is not null)
            {
                await next();
                return;
            }
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new { error = "staff_session_required" });
        });
    }

    static string Bearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : "";
    }
}
