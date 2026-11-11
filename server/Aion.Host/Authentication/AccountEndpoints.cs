using Aion.Application.Authentication;
using Aion.Application.Characters;
using Aion.Infrastructure.Postgres;

namespace Aion.Host.Authentication;

public static class AccountEndpoints
{
    private sealed record Credentials(string? Username, string? Password, bool? Remember = null);
    private sealed record TicketRequest(int WorldId);
    private sealed record PreferencesInput(bool? ShowPlaying);

    /// <param name="registration">Self-registration on the account page (Aion:Accounts:Registration); when off,
    /// only admins create accounts (POST /api/admin/accounts).</param>
    public static void MapAccountEndpoints(this WebApplication app, bool registration = true)
    {
        app.MapPost("/api/client-launches", async (HttpContext context, IClientHandoff handoff, IAccountAuthentication auth, CancellationToken ct) =>
        {
            var token = Bearer(context);
            var launch = await handoff.AuthorizeLaunchAsync(token, 1204, ct);
            if (launch is null) return Results.Unauthorized();
            // A client started without a launch code redeems the launch its own address asked for.
            context.RequestServices.GetService<Aion.Host.Platform.LaunchPairing>()?.Register(context.Connection.RemoteIpAddress, launch.Authorization, launch.ExpiresAt);
            if (await auth.AuthenticateAsync(token, ct) is { } account && context.RequestServices.GetService<IPlatformSecondaryAuth>() is { } platform)
                await platform.RememberClientAsync(context.Connection.RemoteIpAddress, account.AccountId, ct);
            return Results.Ok(launch);
        }).RequireRateLimiting("account-entry");
        app.MapPost("/api/accounts/register", async (Credentials input, IAccountAuthentication auth, CancellationToken ct) =>
        {
            if (!registration) return Results.Json(new { error = "Registration is closed. Ask an admin for an account." }, statusCode: 403);
            if (!PostgresAccountAuthentication.ValidUsername(input.Username) || !PostgresAccountAuthentication.ValidPassword(input.Password))
                return Results.BadRequest(new { error = "Use a 3–32 character username (letters, numbers, underscore) and a 12–256 character password." });
            return await auth.RegisterAsync(input.Username!, input.Password!, ct)
                ? Results.Json(new { registered = true }, statusCode: 201)
                : Results.Conflict(new { error = "That username is unavailable." });
        }).RequireRateLimiting("account-entry");
        // Admins create accounts (the way in when registration is closed). Role-checked like the staff API.
        app.MapPost("/api/admin/accounts", async (Credentials input, HttpContext context, IAccountAuthentication auth,
            Aion.Application.Gameplay.IGameplayStore game, ILoggerFactory logs, CancellationToken ct) =>
        {
            var actor = await auth.AuthenticateAsync(Bearer(context), ct);
            if (actor is null) return Results.Unauthorized();
            try
            {
                if (!Aion.Core.Security.StaffAccess.Can((await game.AdminProfileAsync(actor, ct)).Roles, Aion.Core.Security.StaffPermission.ManageAccounts))
                    return Results.StatusCode(403);
            }
            catch (Aion.Application.Gameplay.GameplayException) { return Results.StatusCode(403); }
            if (!PostgresAccountAuthentication.ValidUsername(input.Username) || !PostgresAccountAuthentication.ValidPassword(input.Password))
                return Results.BadRequest(new { error = "invalid_account" });
            if (!await auth.RegisterAsync(input.Username!, input.Password!, ct)) return Results.Conflict(new { error = "username_taken" });
            logs.CreateLogger("Aion.Accounts").LogInformation("Account {Username} created by admin account {Admin}", input.Username, actor.AccountId);
            return Results.Json(new { created = true }, statusCode: 201);
        }).RequireRateLimiting("admin");

        app.MapPost("/api/accounts/login", async (Credentials input, IAccountAuthentication auth, ITotpAuthentication totp, CancellationToken ct) =>
        {
            if (input.Username is null || input.Password is null) return Results.Unauthorized();
            // "Keep me signed in": a 30-day session instead of the standard 8 hours (kept through the TOTP step).
            TimeSpan? lifetime = input.Remember == true ? TimeSpan.FromDays(30) : null;
            var session = lifetime is { } days
                ? await auth.SignInAsync(input.Username, input.Password, days, ct)
                : await auth.SignInAsync(input.Username, input.Password, ct);
            if (session is null) return Results.Unauthorized();
            if (!await totp.IsEnabledAsync(session.AccountId, ct)) return Results.Ok(session);
            await auth.SignOutAsync(session.Token, ct);
            var challenge = await totp.BeginLoginAsync(session.AccountId, lifetime, ct);
            return challenge is null ? Results.Unauthorized() : Results.Ok(new
            {
                requiresTotp = true,
                challengeToken = challenge.ChallengeToken,
                expiresAt = challenge.ExpiresAt,
            });
        }).RequireRateLimiting("account-entry");

        app.MapPost("/api/accounts/logout", async (HttpContext context, IAccountAuthentication auth, CancellationToken ct) =>
        {
            await auth.SignOutAsync(Bearer(context), ct);
            return Results.NoContent();
        });
        // The account page's overview: name, since when, two-step verification and the Quna balance (where this host
        // keeps the accounts; a world server answers the id only).
        app.MapGet("/api/accounts/me", async (HttpContext context, IAccountAuthentication auth, IServiceProvider services, CancellationToken ct) =>
        {
            var account = await auth.AuthenticateAsync(Bearer(context), ct);
            if (account is null) return Results.Unauthorized();
            if (services.GetService<IAccountDirectory>() is not { } directory || await directory.ProfileAsync(account.AccountId, ct) is not { } profile)
                return Results.Ok(new { accountId = account.AccountId });
            var totp = services.GetService<ITotpAuthentication>() is { } t && await t.IsEnabledAsync(account.AccountId, ct);
            var showPlaying = services.GetService<IAccountPresence>() is not { } shows || await shows.ShowsPlayingAsync(account.AccountId, ct);
            long? quna = null;
            if (services.GetService<Aion.Application.Gameplay.IQunaWallet>() is { } wallet)
            {
                try { quna = (await wallet.BalanceAsync(account.AccountId, ct)).Balance; }
                catch (System.Data.Common.DbException) { quna = null; }
            }
            return Results.Ok(new { accountId = account.AccountId, username = profile.Username, createdAt = profile.CreatedAt, totpEnabled = totp, quna, showPlaying });
        });
        // The account page's preferences. showPlaying (default on): whether others see the character the account plays
        // (the launcher's Discord status leaves it out when off). The account's own page shows it either way.
        app.MapPut("/api/accounts/me/preferences", async (PreferencesInput input, HttpContext context, IAccountAuthentication auth, IServiceProvider services, CancellationToken ct) =>
        {
            var account = await auth.AuthenticateAsync(Bearer(context), ct);
            if (account is null) return Results.Unauthorized();
            if (services.GetService<IAccountPresence>() is not { } presence) return Results.NotFound();
            if (input.ShowPlaying is { } show) await presence.SetShowsPlayingAsync(account.AccountId, show, ct);
            return Results.Ok(new { showPlaying = await presence.ShowsPlayingAsync(account.AccountId, ct) });
        });
        // The character the signed-in account plays right now, by its account session (bearer), never by address: players
        // who share one address each see their own. The launcher's Discord status and the account page's "In game" read it.
        // inGame false: no game connection of the account reported a character in the last 3 minutes.
        app.MapGet("/api/characters/current", async (HttpContext context, IAccountAuthentication auth, IServiceProvider services, CancellationToken ct) =>
        {
            var account = await auth.AuthenticateAsync(Bearer(context), ct);
            if (account is null) return Results.Unauthorized();
            if (services.GetService<IAccountPresence>() is not { } presence) return Results.NotFound();
            var showPlaying = await presence.ShowsPlayingAsync(account.AccountId, ct);
            if (await presence.PlayingAsync(account.AccountId, ct) is not { } playing) return Results.Ok(new { inGame = false, showPlaying });
            string? worldName = null;
            if (services.GetService<Aion.Application.Gameplay.IWorldRegistry>() is { } registry)
            {
                try { worldName = (await registry.ListAsync(ct)).FirstOrDefault(w => w.WorldId == playing.WorldId)?.Name; }
                catch (System.Data.Common.DbException) { }
            }
            var c = playing.Character;
            return Results.Ok(new
            {
                inGame = true, showPlaying, worldId = playing.WorldId, worldName, since = playing.Since,
                character = new { characterId = c.CharacterId, name = c.Name, level = c.Level, className = c.ClassName, race = c.Race, mapId = c.MapId },
            });
        });
        app.MapGet("/api/characters", async (HttpContext context, IAccountAuthentication auth, CharacterService characters, CancellationToken ct) =>
        {
            var account = await auth.AuthenticateAsync(Bearer(context), ct);
            return account is null ? Results.Unauthorized() : Results.Ok(await characters.ListAsync(account, ct));
        });
        app.MapCharacterPageEndpoints();
        app.MapGet("/api/characters/{id:guid}", async (Guid id, HttpContext context, IAccountAuthentication auth, CharacterPersistenceService characters, CancellationToken ct) =>
        {
            var account = await auth.AuthenticateAsync(Bearer(context), ct);
            if (account is null) return Results.Unauthorized();
            var character = await characters.LoadAsync(account, id, ct);
            return character is null ? Results.NotFound() : Results.Ok(character);
        });
        app.MapPost("/api/game-tickets", async (TicketRequest input, HttpContext context, IAccountAuthentication auth, CancellationToken ct) =>
        {
            // The current development directory has this single world. Do not
            // accept an arbitrary world ID merely because a caller supplied it.
            if (input.WorldId != 1204) return Results.BadRequest(new { error = "Unknown world." });
            var ticket = await auth.IssueGameTicketAsync(Bearer(context), input.WorldId, ct);
            return ticket is null ? Results.Unauthorized() : Results.Ok(ticket);
        }).RequireRateLimiting("account-entry");
    }

    /// <summary>
    /// The account site's character page: one of the signed-in account's own characters (another account's is 404, as a
    /// missing one), with what its world server knows (stored and live) and, when that world does not answer, the realm's
    /// character index and presence report.
    /// </summary>
    public static void MapCharacterPageEndpoints(this WebApplication app) =>
        app.MapGet("/api/characters/{id:guid}/details", async (Guid id, HttpContext context, IAccountAuthentication auth, CharacterService characters,
            IServiceProvider services, CancellationToken ct) =>
        {
            var account = await auth.AuthenticateAsync(Bearer(context), ct);
            if (account is null) return Results.Unauthorized();
            if (await characters.FindAsync(account, id, ct) is not { } summary) return Results.NotFound();
            return Results.Ok(await CharacterPage(services, account, summary, ct));
        });

    /// <summary>
    /// The character page for an owned character (the caller checked ownership): its world server's details on an auth
    /// host with world servers, this host's own store otherwise, then the realm's presence report for what they lack.
    /// </summary>
    static async Task<object> CharacterPage(IServiceProvider services, Aion.Core.Accounts.VerifiedAccount account,
        Aion.Core.Characters.CharacterSummary summary, CancellationToken ct)
    {
        CharacterDetails? details = null;
        try
        {
            if (services.GetService<Aion.Host.Worlds.WorldLinks>() is { } links)
            {
                if (await links.RunningAsync(summary.WorldId, ct) is { } world)
                    details = await links.CharacterAsync(world, account.AccountId, summary.CharacterId, ct);
            }
            else details = await CharacterDetailsReader.LocalAsync(services, account, summary.CharacterId, ct);
        }
        catch (System.Data.Common.DbException) { details = null; }
        // A world's answer about another character than the one asked for is not used.
        if (details is not null && details.CharacterId != summary.CharacterId) details = null;

        CurrentlyPlaying? playing = null;
        if (services.GetService<IAccountPresence>() is { } presence)
        {
            try
            {
                if (await presence.PlayingAsync(account.AccountId, ct) is { } now && now.Character.CharacterId == summary.CharacterId && now.WorldId == summary.WorldId)
                    playing = now;
            }
            catch (System.Data.Common.DbException) { }
        }

        string? worldName = null;
        if (services.GetService<Aion.Application.Gameplay.IWorldRegistry>() is { } registry)
        {
            try { worldName = (await registry.ListAsync(ct)).FirstOrDefault(w => w.WorldId == summary.WorldId)?.Name; }
            catch (System.Data.Common.DbException) { }
        }

        var online = details?.Live == true || playing is not null;
        var location = details?.Location;
        // The world did not answer: the map its last presence report named (no position).
        if (location is null && playing?.Character.MapId is { } reportedMap)
            location = new CharacterLocation(reportedMap, Aion.World.MapNames.Of(reportedMap), null, null, null, null);
        var lastPlayed = summary.LastPlayedUnixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(summary.LastPlayedUnixMs) : (DateTimeOffset?)null;
        if (details is not null && (details.LastLoginAt ?? details.LastLogoutAt) is not null)
            lastPlayed = new[] { details.LastLoginAt, details.LastLogoutAt }.Max();
        return new
        {
            characterId = summary.CharacterId,
            name = details?.Name ?? summary.Name,
            worldId = summary.WorldId,
            worldName,
            level = details?.Live == true ? details.Level : playing?.Character.Level ?? details?.Level ?? summary.Level,
            race = details?.Race ?? summary.Race,
            gender = details?.Gender,
            className = details?.Live == true ? details.ClassName : playing?.Character.ClassName ?? details?.ClassName,
            ascension = details?.Ascension,
            experience = details?.Experience,
            online,
            inWorldSince = details?.Live == true ? details.InWorldSince : playing?.Since,
            combatPower = details?.CombatPower,
            highestCombatPower = details?.HighestCombatPower,
            location,
            // Live: where the character is now; else where it was last saved (or last reported).
            locationLive = location is not null && online && (details?.Live == true || details?.Location is null),
            legion = details?.Legion,
            createdAt = details?.CreatedAt,
            lastPlayedAt = lastPlayed,
            // "world": the world server's own data; "index": the realm's character index only (the world did not answer).
            source = details is null ? "index" : "world",
        };
    }

    private static string Bearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : "";
    }
}
