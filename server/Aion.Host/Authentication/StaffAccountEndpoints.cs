using Aion.Application.Authentication;
using Aion.Application.Gameplay;
using Aion.Core.Security;

namespace Aion.Host.Authentication;

/// <summary>
/// The staff Accounts page (realm only): search, an account's detail with its characters on every world, ending its
/// sessions and a temporary password. Roles, disabling, the authenticator reset and Quna stay where they were
/// (GameplayEndpoints). Every route needs a staff session (StaffSessionEndpoints); the rank is checked here and the two
/// actions again in the database (migration 099).
/// </summary>
public static class StaffAccountEndpoints
{
    public static void MapStaffAccountEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/admin/accounts").RequireRateLimiting("admin");

        admin.MapGet("", async (string? q, string? filter, int? page, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game,
            IAccountDirectory directory, CancellationToken ct) =>
        {
            if (await ActorAsync(ctx, auth, game, StaffPermission.ViewAccounts, ct) is not { } outcome) return Results.Unauthorized();
            if (outcome.Denied) return Results.StatusCode(403);
            var kind = filter switch { "staff" => AccountFilter.Staff, "disabled" => AccountFilter.Disabled, _ => AccountFilter.All };
            const int size = 50;
            var result = await directory.SearchAsync(q, kind, page ?? 0, size, ct);
            return Results.Ok(new { accounts = result.Accounts.Select(Summary), total = result.Total, page = page ?? 0, pageSize = size });
        });

        admin.MapGet("/{id:guid}", async (Guid id, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, IAccountDirectory directory,
            IServiceProvider services, CancellationToken ct) =>
        {
            if (await ActorAsync(ctx, auth, game, StaffPermission.ViewAccounts, ct) is not { } outcome) return Results.Unauthorized();
            if (outcome.Denied) return Results.StatusCode(403);
            if (await directory.DetailAsync(id, ct) is not { } detail) return Results.NotFound(new { error = "unknown_account" });
            long? quna = null;
            if (services.GetService<IQunaWallet>() is { } wallet)
            {
                try { quna = (await wallet.BalanceAsync(id, ct)).Balance; } catch (System.Data.Common.DbException) { }
            }
            IReadOnlyList<Sanction> sanctions = [];
            IReadOnlyList<Ticket> tickets = [];
            if (services.GetService<IStaffTools>() is { } tools)
            {
                sanctions = [.. (await tools.SanctionsAsync(outcome.Actor, true, ct)).Where(s => s.AccountId == id).Take(50)];
                tickets = [.. (await tools.MyTicketsAsync(id, ct)).Take(50)];
            }
            var now = DateTimeOffset.UtcNow;
            return Results.Ok(new
            {
                account = Summary(detail.Account),
                totpEnabled = detail.TotpEnabled,
                sessions = detail.ActiveSessions,
                staffSessions = detail.ActiveStaffSessions,
                quna,
                characters = detail.Characters,
                sanctions = sanctions.Select(s => new { s.Id, s.Kind, s.Reason, s.CreatedAt, s.ExpiresAt, s.CreatedBy, s.RevokedAt, active = s.Active(now) }),
                tickets = tickets.Select(t => new { t.Id, t.CreatedAt, t.Category, t.Content, t.Status, t.StaffNote, t.HandledAt }),
            });
        });

        admin.MapPost("/{id:guid}/sessions/revoke", async (Guid id, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game,
            IAccountDirectory directory, IServiceProvider services, CancellationToken ct) =>
        {
            if (await ActorAsync(ctx, auth, game, StaffPermission.DisableAccounts, ct) is not { } outcome) return Results.Unauthorized();
            if (outcome.Denied || !await directory.RevokeSessionsAsync(Bearer(ctx), id, ct)) return Results.StatusCode(403);
            if (services.GetService<Aion.Host.Realm.RealmSocketHub>() is { } sockets) await sockets.AccountChangedAsync(id);
            return Results.NoContent();
        });

        admin.MapPost("/{id:guid}/password", async (Guid id, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game,
            IAccountDirectory directory, IServiceProvider services, CancellationToken ct) =>
        {
            if (await ActorAsync(ctx, auth, game, StaffPermission.ManageAccounts, ct) is not { } outcome) return Results.Unauthorized();
            if (outcome.Denied || await directory.ResetPasswordAsync(Bearer(ctx), id, ct) is not { } password) return Results.StatusCode(403);
            if (services.GetService<Aion.Host.Realm.RealmSocketHub>() is { } sockets) await sockets.AccountChangedAsync(id);
            return Results.Ok(new { temporaryPassword = password });
        });
    }

    static object Summary(AccountSummary a) => new
    {
        a.AccountId, a.Username, a.CreatedAt, a.DisabledAt, a.LastSignInAt, a.Roles,
        rankLabel = StaffAccess.RankOf(a.Roles) == StaffRank.None ? null : StaffAccess.Label(StaffAccess.RankOf(a.Roles)),
    };

    sealed record Outcome(Aion.Core.Accounts.VerifiedAccount Actor, bool Denied);

    /// <summary>The signed-in staff account and whether its rank lacks the permission; null when not signed in.</summary>
    static async Task<Outcome?> ActorAsync(HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, StaffPermission permission, CancellationToken ct)
    {
        var account = await auth.AuthenticateAsync(Bearer(ctx), ct);
        if (account is null) return null;
        try { return new Outcome(account, !StaffAccess.Can((await game.AdminProfileAsync(account, ct)).Roles, permission)); }
        catch (GameplayException) { return new Outcome(account, true); }
    }

    static string Bearer(HttpContext context) =>
        context.Request.Headers.Authorization.ToString() is var value && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..] : "";
}
