using System.Security.Cryptography;
using System.Text;
using Aion.Application.Authentication;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Aion.Host.Native;
using Aion.World;

namespace Aion.Host.Worlds;

/// <summary>
/// Aion:WorldProcess of a world server (Aion:Role=world): set by the auth host for a managed one, by its operator for a
/// remote one (ListenAddress and ControlHost; its control token is made at start and reported to the realm link).
/// </summary>
public sealed record WorldProcessOptions
{
    /// <summary>Required on the control endpoints (/internal/world/...): the auth host derives it per world.</summary>
    public string ControlToken { get; init; } = "";
    /// <summary>Address of the status and control port (Aion:LocalPort): loopback, or the address the auth host reaches.</summary>
    public string ListenAddress { get; init; } = "127.0.0.1";
    public string Name { get; init; } = "";
    /// <summary>Obsolete alias of Aion:Realm:Url (the realm link's base address).</summary>
    public string? RealmUrl { get; init; }
    /// <summary>The address the auth host reaches this world's control port on (reported when it signs in); default: ListenAddress.</summary>
    public string? ControlHost { get; init; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>The options when this host runs in world role, else null.</summary>
    public static WorldProcessOptions? From(IConfiguration configuration)
    {
        if (!string.Equals(configuration["Aion:Role"], "world", StringComparison.OrdinalIgnoreCase)) return null;
        var options = configuration.GetSection("Aion:WorldProcess").Get<WorldProcessOptions>() ?? new WorldProcessOptions();
        if (options.ControlToken.Length == 0)
            // A remote world server makes its own control token each start and reports it when it signs in to the realm.
            options = options with
            {
                ControlToken = ControlKey(configuration) is { } key && configuration.GetValue<int?>("Aion:Native:WorldId") is { } worldId
                    ? Token(key, worldId) : Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            };
        return options;
    }

    /// <summary>The control key the auth host and its world servers share: derived from Aion:BridgeSecret, else null.</summary>
    public static byte[]? ControlKey(IConfiguration configuration) => configuration["Aion:BridgeSecret"] is { Length: >= 32 } secret
        ? SHA256.HashData(Encoding.UTF8.GetBytes("aion-world-control:" + secret)) : null;

    public static string Token(byte[] key, int worldId) => Convert.ToHexString(HMACSHA256.HashData(key, BitConverter.GetBytes(worldId)));

    /// <summary>A managed world server's realm-link token (the auth host derives it; remote ones are issued theirs).</summary>
    public static string LinkToken(byte[] key, int worldId) => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("realm-link:" + worldId)));
}

/// <summary>
/// Multi-world HTTP: the staff dashboard's World Management (/api/admin/worlds: list, add, change, start, stop, restart,
/// remove; changes need the admin role, the store checks and audits them) and a world server's status and control
/// endpoints for the auth host (/internal/world/...).
/// </summary>
public static class WorldEndpoints
{
    sealed record EnabledRequest(bool Enabled);

    public static void MapWorldEndpoints(WebApplication app)
    {
        // World names for the account page and the dashboard (what the game's server list shows; no addresses).
        app.MapGet("/api/worlds", async (IServiceProvider services, CancellationToken ct) =>
        {
            if (services.GetService<IWorldRegistry>() is not { } registry) return Results.Ok(Array.Empty<object>());
            var worlds = await registry.ListAsync(ct);
            return Results.Ok(worlds.Select(w => new { w.WorldId, w.Name }));
        });
        var staff = app.MapGroup("/api/admin/worlds").RequireRateLimiting("admin");
        staff.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (GameplayException error)
            { return error.Code == "forbidden" ? Results.StatusCode(403) : Results.BadRequest(new { error = error.Code }); }
        });

        staff.MapGet("", async (HttpContext ctx, IAccountAuthentication auth, IServiceProvider services, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } account) return Results.Unauthorized();
            if (services.GetService<IWorldRegistry>() is not { } registry) return Results.NotFound();
            var counts = await registry.CharacterCountsAsync(account, ct);
            var roles = services.GetService<IStaffTools>() is { } tools ? await tools.RolesAsync(account, ct) : [];
            var native = services.GetService<NativeOptions>();
            var supervisor = services.GetService<WorldSupervisor>();
            var worlds = await registry.ListAsync(ct);
            var listed = native is null ? [] : LoginWorldList.Directory(worlds, native, supervisor is null ? _ => WorldRuntime.None : supervisor.Runtime);
            return Results.Ok(new
            {
                canManage = roles.Contains("admin"),
                supervised = supervisor is not null,
                publicAddress = native?.Network.PublicAddress,
                worlds = worlds.Select(w =>
                {
                    var runtime = supervisor?.Runtime(w.WorldId) ?? WorldRuntime.None;
                    return new
                    {
                        w.WorldId, w.Name, w.LightServerId, w.DarkServerId, w.Kind, w.Host, w.GamePort,
                        w.InternalPort, w.Enabled, w.LevelCap, w.CreatedAt, w.UpdatedAt, w.ControlHost, w.LastSeenAt, w.Version, w.HasToken,
                        Characters = counts.GetValueOrDefault(w.WorldId),
                        Listed = listed.Any(l => l.WorldId == w.WorldId),
                        Runtime = runtime,
                    };
                }),
            });
        });
        staff.MapPost("", async (WorldInput input, HttpContext ctx, IAccountAuthentication auth, IWorldRegistry registry, IServiceProvider services, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } account) return Results.Unauthorized();
            var created = await registry.CreateAsync(account, input, ct);
            services.GetService<WorldSupervisor>()?.Nudge();
            return Results.Ok(created);
        });
        staff.MapPut("/{id:int}", async (int id, WorldInput input, HttpContext ctx, IAccountAuthentication auth, IWorldRegistry registry, IServiceProvider services, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } account) return Results.Unauthorized();
            var updated = await registry.UpdateAsync(account, id, input, ct);
            services.GetService<WorldSupervisor>()?.Nudge();
            return Results.Ok(updated);
        });
        staff.MapPost("/{id:int}/start", (int id, HttpContext ctx, IAccountAuthentication auth, IWorldRegistry registry, IServiceProvider services, CancellationToken ct) =>
            SetEnabled(id, true, ctx, auth, registry, services, ct));
        staff.MapPost("/{id:int}/stop", (int id, HttpContext ctx, IAccountAuthentication auth, IWorldRegistry registry, IServiceProvider services, CancellationToken ct) =>
            SetEnabled(id, false, ctx, auth, registry, services, ct));
        staff.MapPost("/{id:int}/restart", async (int id, HttpContext ctx, IAccountAuthentication auth, IWorldRegistry registry, IServiceProvider services, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } account) return Results.Unauthorized();
            // The admin check and the audit entry: re-enable (a no-op for a running world).
            var world = await registry.SetEnabledAsync(account, id, true, ct);
            if (!world.IsManaged) return Results.BadRequest(new { error = "not_managed" });
            if (services.GetService<WorldSupervisor>() is not { } supervisor) return Results.BadRequest(new { error = "not_supervised" });
            await supervisor.RestartAsync(id, ct);
            return Results.Ok(world);
        });
        // A remote world's token (shown once): its operator sets it as Aion:Realm:Token; a new one replaces the old.
        staff.MapPost("/{id:int}/token", async (int id, HttpContext ctx, IAccountAuthentication auth, IWorldRegistry registry, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } account) return Results.Unauthorized();
            return Results.Ok(new { token = await registry.IssueTokenAsync(account, id, ct) });
        });
        staff.MapDelete("/{id:int}", async (int id, HttpContext ctx, IAccountAuthentication auth, IWorldRegistry registry, IServiceProvider services, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } account) return Results.Unauthorized();
            await registry.DeleteAsync(account, id, ct);
            services.GetService<WorldSupervisor>()?.Nudge();
            return Results.Ok(new { removed = id });
        });
    }

    static async Task<IResult> SetEnabled(int id, bool enabled, HttpContext ctx, IAccountAuthentication auth, IWorldRegistry registry, IServiceProvider services, CancellationToken ct)
    {
        if (await Signed(ctx, auth, ct) is not { } account) return Results.Unauthorized();
        var world = await registry.SetEnabledAsync(account, id, enabled, ct);
        services.GetService<WorldSupervisor>()?.Nudge();
        return Results.Ok(world);
    }

    static bool Authorized(HttpContext ctx, string token)
    {
        var given = Encoding.UTF8.GetBytes(ctx.Request.Headers[WorldLinks.ControlHeader].ToString());
        var expected = Encoding.UTF8.GetBytes(token);
        return expected.Length > 0 && CryptographicOperations.FixedTimeEquals(given, expected);
    }

    /// <summary>
    /// World role, on the loopback HTTP port: status (open), and for the realm host with this world's control token a
    /// graceful stop, the characters in the world (the realm's chat), chat commands of this world's players, notices and
    /// sanction changes.
    /// </summary>
    public static void MapWorldProcessEndpoints(WebApplication app, WorldProcessOptions options, int worldId)
    {
        app.MapGet("/internal/world/status", (IServiceProvider services) =>
        {
            var presence = services.GetService<WorldPresence>();
            return Results.Ok(new WorldProcessStatus(worldId, presence?.All.Count ?? 0, presence?.Connections ?? 0, options.StartedAt.ToUnixTimeMilliseconds()));
        });
        var control = app.MapGroup("/internal/world");
        control.AddEndpointFilter(async (context, next) =>
            Authorized(context.HttpContext, options.ControlToken) ? await next(context) : Results.StatusCode(403));
        control.MapPost("/stop", (IHostApplicationLifetime lifetime) =>
        {
            app.Logger.LogInformation("World {World} stopping on request of the realm host", worldId);
            lifetime.StopApplication();
            return Results.Ok(new { stopping = true });
        });
        control.MapGet("/presence", (IServiceProvider services) =>
        {
            var entries = new List<WorldPresenceEntry>();
            if (services.GetService<WorldPresence>() is { } presence)
                foreach (var entry in presence.All)
                    if (entry is { AccountId: { } account, Session: { InWorld: true, CharacterId: { } id } session }
                        && Chat.WorldChatDirectory.Describe(id, session) is { } character)
                        entries.Add(new WorldPresenceEntry(id, account, character.Name, character.DisplayName, character.GuildId, character.Race));
            return Results.Ok(entries);
        });
        control.MapPost("/command", async (WorldCommandRequest input, IServiceProvider services, CancellationToken ct) =>
        {
            if (services.GetService<Aion.Application.Chat.IChatCommandHandler>() is not { } commands || !commands.Owns(input.Content ?? ""))
                return Results.Ok(new { answer = "Commands are not available here." });
            return Results.Ok(new { answer = await commands.HandleAsync(input.Sender, input.UserName ?? "", input.Content ?? "", ct) });
        });
        control.MapPost("/notice", (WorldText input, IServiceProvider services) =>
        {
            var text = (input.Text ?? "").Trim();
            if (text.Length is 0 or > 200) return Results.BadRequest();
            var shown = 0;
            if (services.GetService<WorldPresence>() is { } presence)
            {
                var packet = GameSession.Notice(text, Math.Clamp(input.Size ?? 20, 10, 60), input.Color is { Length: 6 } color && color.All(char.IsAsciiHexDigit) ? color : "FFFFFF");
                foreach (var entry in presence.All)
                {
                    _ = entry.Session.InvokeAsync(_ => (true, new List<byte[]> { packet }));
                    shown++;
                }
            }
            return Results.Ok(new { value = shown });
        });
        control.MapPost("/sanctions", async (WorldSanctionChange input, IServiceProvider services, CancellationToken ct) =>
        {
            if (services.GetService<Authentication.ServerControl>() is not { } serverControl) return Results.Ok(new { value = 0 });
            await serverControl.RefreshAsync(ct);
            // A disabled account or changed roles reach this world's account mirror too.
            if (input.KickAccount is { } named && services.GetService<Realm.AccountMirrorService>() is { } mirror) await mirror.RefreshAsync(named, ct);
            var kicked = input.KickAccount is { } account ? serverControl.KickAccount(account, input.Reason is { Length: > 0 } reason ? reason : "Banned") : 0;
            return Results.Ok(new { value = kicked });
        });
        control.MapGet("/blocked", async (Guid from, Guid to, IGameplayStore game, CancellationToken ct) =>
            Results.Ok(new { value = await game.AnyBlockBetweenAsync(from, to, ct) }));
    }

    static async Task<VerifiedAccount?> Signed(HttpContext ctx, IAccountAuthentication auth, CancellationToken ct)
    {
        var h = ctx.Request.Headers.Authorization.ToString();
        return await auth.AuthenticateAsync(h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..] : "", ct);
    }
}
