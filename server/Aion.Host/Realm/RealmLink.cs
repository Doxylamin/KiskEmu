using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Aion.Application.Authentication;
using Aion.Application.Chat;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Aion.Host.Worlds;
using Aion.Infrastructure.Postgres;

namespace Aion.Host.Realm;

// The realm link (server/DATABASES.md): a world server has its own database and no access to the realm's. Everything it
// needs from the realm (game logins, staff sign-ins, the account mirror, bans, maintenance, scheduled events, player
// reports, the character index, chat announcements) goes over HTTP to the auth host, authenticated with its world
// token: X-World-Id and "Authorization: Bearer <token>". A managed world's token is derived by the auth host and handed
// to it at start; a remote world's is issued on the staff dashboard (only a hash is kept) and set in its configuration.

/// <summary>Aion:Realm of a world server: the auth host's address (http://127.0.0.1:8091 for a managed world, the
/// account site https://host:8780 for a remote one), its world token, and optionally the SHA-256 of the auth host's
/// certificate when that is self-signed.</summary>
public sealed record RealmLinkOptions
{
    public string Url { get; init; } = "";
    public string Token { get; init; } = "";
    public string? CertificateSha256 { get; init; }

    public static RealmLinkOptions? From(IConfiguration configuration, WorldProcessOptions? process)
    {
        if (process is null) return null;
        var options = configuration.GetSection("Aion:Realm").Get<RealmLinkOptions>() ?? new RealmLinkOptions();
        if (options.Url.Length == 0 && process.RealmUrl is { Length: > 0 } legacy) options = options with { Url = legacy };
        return options.Url.Length > 0 && options.Token.Length > 0 ? options : null;
    }
}

public sealed record LinkRegister(string? GameHost, int GamePort, string? ControlHost, int ControlPort, string ControlToken, string? Version);
public sealed record LinkWorld(int WorldId, string Name, int LightServerId, int DarkServerId, int? LevelCap, bool Enabled);
public sealed record LinkSessionOpen(string Ticket, long WireAccountId, string Grant);
public sealed record LinkSession(string Token, Guid AccountId, int WorldId, DateTimeOffset ExpiresAt, int? Race, AccountSnapshot Account);
public sealed record LinkToken(string Token);
public sealed record LinkIdentity(Guid AccountId, int WorldId);
public sealed record LinkRealmState(MaintenanceState Maintenance, IReadOnlyList<Sanction> Sanctions, IReadOnlyList<ScheduledEvent> Events);
public sealed record LinkTicket(Guid? Account, string? CharacterName, string Category, string Content);
public sealed record LinkMaintenance(Guid Actor, bool Enabled, string Message);
public sealed record LinkCharacters(bool Full, IReadOnlyList<IndexedCharacter> Upserts, IReadOnlyList<Guid> Removed);
public sealed record LinkText(string Text);
public sealed record LinkCount(int Value);

/// <summary>The auth host's side of the realm link (/internal/realm/...).</summary>
public static class RealmLinkEndpoints
{
    public const string WorldHeader = "X-World-Id";
    const string WorldKey = "realm-link-world";

    public static void Map(WebApplication app)
    {
        var link = app.MapGroup("/internal/realm");
        link.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (await Caller(http) is not { } world) return Results.StatusCode(403);
            http.Items[WorldKey] = world;
            try { return await next(context); }
            catch (GameplayException error)
            { return error.Code == "forbidden" ? Results.StatusCode(403) : Results.BadRequest(new { error = error.Code }); }
        });

        link.MapPost("/register", async (LinkRegister input, HttpContext ctx, IWorldRegistry registry, WorldSupervisor supervisor, ILoggerFactory logs, CancellationToken ct) =>
        {
            var world = World(ctx);
            var log = logs.CreateLogger("Aion.Host.Realm.RealmLink");
            if (input.GamePort is < 1 or > 65535 || input.ControlPort is < 1 or > 65535) return Results.BadRequest(new { error = "invalid_port" });
            if (Address(input.GameHost) is false || Address(input.ControlHost) is false) return Results.BadRequest(new { error = "invalid_host" });
            // A remote world that names no control address is reached where it connected from.
            var controlHost = input.ControlHost ?? (world.IsManaged ? null : ctx.Connection.RemoteIpAddress?.MapToIPv4().ToString());
            var gameHost = input.GameHost ?? (world.IsManaged ? null : controlHost);
            var saved = await registry.RegisterAsync(world.WorldId, new WorldRegistration(gameHost, input.GamePort, controlHost, input.ControlPort, input.Version), ct);
            if (saved is null) return Results.NotFound();
            supervisor.Registered(saved, input.ControlToken);
            if (world.LastSeenAt is null || DateTimeOffset.UtcNow - world.LastSeenAt > TimeSpan.FromMinutes(3))
                log.LogInformation("World {World} ({Name}) signed in: game {GameHost}:{GamePort}, control {ControlHost}:{ControlPort}, {Version}",
                    saved.WorldId, saved.Name, saved.Host ?? "(auth host)", saved.GamePort, saved.ControlHost ?? saved.Host ?? "loopback", saved.InternalPort, saved.Version);
            supervisor.Nudge();
            return Results.Ok(new LinkWorld(saved.WorldId, saved.Name, saved.LightServerId, saved.DarkServerId, saved.LevelCap, saved.Enabled));
        });

        link.MapPost("/sessions/open", async (LinkSessionOpen input, HttpContext ctx, IWorldSessions sessions, IRealmAccounts accounts, CancellationToken ct) =>
        {
            var world = World(ctx);
            var opened = await sessions.OpenAsync(input.Ticket, input.WireAccountId, input.Grant, world.WorldId, ct);
            if (opened is null || opened.WorldId != world.WorldId) return Results.Unauthorized();
            if (await accounts.SnapshotAsync(opened.AccountId, ct) is not { } account) return Results.Unauthorized();
            return Results.Ok(new LinkSession(opened.Token, opened.AccountId, opened.WorldId, opened.ExpiresAt, opened.Race, account));
        });
        link.MapPost("/sessions/validate", async (LinkToken input, HttpContext ctx, IWorldSessions sessions, CancellationToken ct) =>
            await sessions.ValidateAsync(input.Token, ct) is { } who && who.WorldId == World(ctx).WorldId
                ? Results.Ok(new LinkIdentity(who.Account.AccountId, who.WorldId)) : Results.Unauthorized());
        link.MapPost("/sessions/close", async (LinkToken input, HttpContext ctx, IWorldSessions sessions, CancellationToken ct) =>
        {
            if (await sessions.ValidateAsync(input.Token, ct) is { } who && who.WorldId != World(ctx).WorldId) return Results.StatusCode(403);
            await sessions.CloseAsync(input.Token, ct);
            return Results.NoContent();
        });

        // A staff (or player) sign-in on a request the auth host forwarded to this world: the account, for the mirror.
        link.MapPost("/authenticate", async (LinkToken input, IAccountAuthentication auth, IRealmAccounts accounts, CancellationToken ct) =>
            await auth.AuthenticateAsync(input.Token, ct) is { } account && await accounts.SnapshotAsync(account.AccountId, ct) is { } snapshot
                ? Results.Ok(snapshot) : Results.Unauthorized());
        link.MapGet("/accounts/{id:guid}", async (Guid id, IRealmAccounts accounts, CancellationToken ct) =>
            await accounts.SnapshotAsync(id, ct) is { } snapshot ? Results.Ok(snapshot) : Results.NotFound());

        link.MapGet("/state", async (HttpContext ctx, IStaffTools tools, CancellationToken ct) =>
            Results.Ok(new LinkRealmState(await tools.MaintenanceAsync(World(ctx).WorldId, ct), await tools.ActiveSanctionsAsync(ct), await tools.LiveEventsAsync(ct))));
        link.MapPost("/maintenance", async (LinkMaintenance input, HttpContext ctx, IStaffTools tools, CancellationToken ct) =>
            Results.Ok(await tools.SetMaintenanceAsync(new VerifiedAccount(input.Actor), World(ctx).WorldId, input.Enabled, input.Message ?? "", ct)));
        link.MapPost("/tickets", async (LinkTicket input, IStaffTools tools, CancellationToken ct) =>
            Results.Ok(new LinkCount((int)await tools.CreateTicketAsync(input.Account, input.CharacterName, input.Category, input.Content, ct))));
        link.MapPost("/characters", async (LinkCharacters input, HttpContext ctx, IRealmAccounts accounts, CancellationToken ct) =>
        {
            if (input.Upserts.Count > 100_000 || input.Removed.Count > 100_000) return Results.BadRequest(new { error = "too_many" });
            await accounts.SyncCharactersAsync(World(ctx).WorldId, input.Full, input.Upserts, input.Removed, ct);
            return Results.NoContent();
        });
        // The custom shop goods this world sells (game.custom_shop_goods, staff dashboard: Shop).
        link.MapGet("/shop/goods", async (IServiceProvider services, CancellationToken ct) =>
            services.GetService<ICustomShopCatalog>() is { } shop ? Results.Ok(await shop.GoodsAsync(ct)) : Results.Ok(Array.Empty<CustomShopGoods>()));
        // This world's chat announcement (scheduled restart, staff announcement on that world) to its chat sessions.
        link.MapPost("/chat", async (LinkText input, HttpContext ctx, IServiceProvider services) =>
        {
            var text = (input.Text ?? "").Trim();
            if (text.Length is 0 or > 200) return Results.BadRequest();
            var worldKey = World(ctx).WorldId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Ok(new LinkCount(await Authentication.StaffEndpoints.ChatAnnounceAsync(services, text, worldKey)));
        });
    }

    static WorldRecord World(HttpContext context) => (WorldRecord)context.Items[WorldKey]!;

    static bool? Address(string? text) => text is null ? null : System.Net.IPAddress.TryParse(text, out var a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

    /// <summary>The world the request signs in as: a registered world whose token matches, else null.</summary>
    static async Task<WorldRecord?> Caller(HttpContext http)
    {
        if (!int.TryParse(http.Request.Headers[WorldHeader].ToString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var worldId))
            return null;
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = header[7..].Trim();
        var services = http.RequestServices;
        if (services.GetService<IWorldRegistry>() is not { } registry || services.GetService<WorldSupervisor>() is not { } supervisor) return null;
        var world = (await registry.ListAsync(http.RequestAborted)).FirstOrDefault(w => w.WorldId == worldId);
        if (world is null) return null;
        var ok = world.IsManaged
            ? CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(supervisor.LinkToken(worldId)))
            : await registry.TokenMatchesAsync(worldId, token, http.RequestAborted);
        return ok ? world : null;
    }
}

/// <summary>A world server's client of the realm link.</summary>
public sealed class RealmLinkClient
{
    readonly HttpClient http;
    readonly ILogger<RealmLinkClient> logger;
    public int WorldId { get; }

    public RealmLinkClient(RealmLinkOptions options, int worldId, ILogger<RealmLinkClient> logger)
    {
        this.logger = logger;
        WorldId = worldId;
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        if (options.CertificateSha256 is { Length: > 0 } pin)
        {
            // A self-signed auth host: its certificate is accepted when its SHA-256 matches (hex, colons allowed).
            var expected = Convert.FromHexString(pin.Replace(":", "", StringComparison.Ordinal).Trim());
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None
                || certificate is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), expected);
        }
        http = new HttpClient(handler) { BaseAddress = new Uri(options.Url.TrimEnd('/') + "/internal/realm/"), Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Add(RealmLinkEndpoints.WorldHeader, worldId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.Token);
    }

    async Task<T?> Send<T>(HttpMethod method, string path, object? body, CancellationToken ct, bool quiet = false)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.NotFound) return default;
        if (!response.IsSuccessStatusCode)
        {
            if (!quiet) logger.LogWarning("Realm link {Path}: {Status}", path, (int)response.StatusCode);
            if ((int)response.StatusCode == 403) throw new RealmLinkException("The realm refused this world's token (Aion:Realm:Token).");
            if ((int)response.StatusCode == 400 && await response.Content.ReadFromJsonAsync<Error>(ct) is { Code: { } code }) throw new GameplayException(code);
            throw new RealmLinkException($"The realm answered {(int)response.StatusCode} on {path}.");
        }
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent || typeof(T) == typeof(bool)) return (T)(object)true;
        return await response.Content.ReadFromJsonAsync<T>(ct);
    }

    sealed record Error([property: System.Text.Json.Serialization.JsonPropertyName("error")] string? Code);

    public Task<LinkWorld?> RegisterAsync(LinkRegister input, CancellationToken ct) => Send<LinkWorld>(HttpMethod.Post, "register", input, ct, quiet: true);
    public Task<LinkSession?> OpenSessionAsync(LinkSessionOpen input, CancellationToken ct) => Send<LinkSession>(HttpMethod.Post, "sessions/open", input, ct);
    public Task<LinkIdentity?> ValidateSessionAsync(string token, CancellationToken ct) => Send<LinkIdentity>(HttpMethod.Post, "sessions/validate", new LinkToken(token), ct);
    public Task CloseSessionAsync(string token, CancellationToken ct) => Send<bool>(HttpMethod.Post, "sessions/close", new LinkToken(token), ct);
    public Task<AccountSnapshot?> AuthenticateAsync(string token, CancellationToken ct) => Send<AccountSnapshot>(HttpMethod.Post, "authenticate", new LinkToken(token), ct);
    public Task<AccountSnapshot?> AccountAsync(Guid account, CancellationToken ct) => Send<AccountSnapshot>(HttpMethod.Get, $"accounts/{account:D}", null, ct);
    public Task<LinkRealmState?> StateAsync(CancellationToken ct) => Send<LinkRealmState>(HttpMethod.Get, "state", null, ct);
    public Task<MaintenanceState?> SetMaintenanceAsync(LinkMaintenance input, CancellationToken ct) => Send<MaintenanceState>(HttpMethod.Post, "maintenance", input, ct);
    public Task<LinkCount?> TicketAsync(LinkTicket input, CancellationToken ct) => Send<LinkCount>(HttpMethod.Post, "tickets", input, ct);
    public Task SyncCharactersAsync(LinkCharacters input, CancellationToken ct) => Send<bool>(HttpMethod.Post, "characters", input, ct);
    public async Task<int> ChatAsync(string text, CancellationToken ct) => (await Send<LinkCount>(HttpMethod.Post, "chat", new LinkText(text), ct))?.Value ?? 0;
    public async Task<IReadOnlyList<CustomShopGoods>> ShopGoodsAsync(CancellationToken ct) =>
        await Send<List<CustomShopGoods>>(HttpMethod.Get, "shop/goods", null, ct) ?? throw new RealmLinkException("The realm sent no shop goods.");
}

/// <summary>
/// A world server's custom shop goods: the realm's, read over the realm link at most every 30 seconds; the last list
/// stays in use while the realm cannot be reached.
/// </summary>
public sealed class LinkedCustomShopCatalog(RealmLinkClient link, ILogger<LinkedCustomShopCatalog> logger) : ICustomShopCatalog
{
    static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30), Retry = TimeSpan.FromSeconds(5);
    readonly SemaphoreSlim gate = new(1, 1);
    IReadOnlyList<CustomShopGoods> goods = [];
    DateTimeOffset nextRead = DateTimeOffset.MinValue;

    public async Task<IReadOnlyList<CustomShopGoods>> GoodsAsync(CancellationToken ct = default)
    {
        if (DateTimeOffset.UtcNow < nextRead) return goods;
        await gate.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow < nextRead) return goods;
            try
            {
                goods = await link.ShopGoodsAsync(ct);
                nextRead = DateTimeOffset.UtcNow + Fresh;
            }
            catch (Exception error) when (error is HttpRequestException or RealmLinkException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning("Custom shop goods from the realm: {Error}", error.Message);
                nextRead = DateTimeOffset.UtcNow + Retry;
            }
            return goods;
        }
        finally { gate.Release(); }
    }
}

public sealed class RealmLinkException(string message) : Exception(message);

/// <summary>
/// The account mirror of this world's database, kept current from the realm: on every game login and staff sign-in
/// (at most every 30 seconds per account), and for an account a sanction change names.
/// </summary>
public sealed class AccountMirrorService(IAccountMirror mirror, RealmLinkClient link, ILogger<AccountMirrorService> logger)
{
    static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);
    readonly ConcurrentDictionary<Guid, DateTimeOffset> mirrored = new();

    public async Task KeepAsync(AccountSnapshot account, CancellationToken ct)
    {
        if (mirrored.TryGetValue(account.AccountId, out var at) && DateTimeOffset.UtcNow - at < Fresh) return;
        await mirror.UpsertAsync(account, ct);
        mirrored[account.AccountId] = DateTimeOffset.UtcNow;
    }

    /// <summary>Reads the account from the realm again (a ban or role change).</summary>
    public async Task RefreshAsync(Guid accountId, CancellationToken ct)
    {
        mirrored.TryRemove(accountId, out _);
        try { if (await link.AccountAsync(accountId, ct) is { } account) await KeepAsync(account, ct); }
        catch (Exception error) when (error is HttpRequestException or RealmLinkException or TaskCanceledException)
        { logger.LogWarning("Account mirror refresh failed: {Error}", error.Message); }
    }
}

/// <summary>Game-port sessions of a world server: opened, checked and closed by the realm (its world sessions).</summary>
public sealed class LinkedWorldSessions(RealmLinkClient link, AccountMirrorService mirror) : IWorldSessions
{
    // A session stays valid for this long between checks (the store checks it on every save).
    static readonly TimeSpan Recheck = TimeSpan.FromSeconds(15);
    readonly ConcurrentDictionary<string, (WorldIdentity Who, DateTimeOffset At)> valid = new(StringComparer.Ordinal);

    public async Task<WorldSession?> OpenAsync(string ticket, long wireAccountId, string grant, int worldId, CancellationToken ct = default)
    {
        if (worldId != link.WorldId) return null;
        if (await link.OpenSessionAsync(new LinkSessionOpen(ticket, wireAccountId, grant), ct) is not { } opened) return null;
        // The account (and its staff roles) as the realm has it, before any character query of this login.
        await mirror.KeepAsync(opened.Account, ct);
        valid[opened.Token] = (new WorldIdentity(new VerifiedAccount(opened.AccountId), opened.WorldId), DateTimeOffset.UtcNow);
        return new WorldSession(opened.Token, opened.AccountId, opened.WorldId, opened.ExpiresAt, opened.Race);
    }

    public async Task<WorldIdentity?> ValidateAsync(string token, CancellationToken ct = default)
    {
        if (valid.TryGetValue(token, out var cached) && DateTimeOffset.UtcNow - cached.At < Recheck) return cached.Who;
        if (await link.ValidateSessionAsync(token, ct) is not { } who)
        {
            valid.TryRemove(token, out _);
            return null;
        }
        var identity = new WorldIdentity(new VerifiedAccount(who.AccountId), who.WorldId);
        valid[token] = (identity, DateTimeOffset.UtcNow);
        return identity;
    }

    public async Task CloseAsync(string token, CancellationToken ct = default)
    {
        valid.TryRemove(token, out _);
        await link.CloseSessionAsync(token, ct);
    }
}

/// <summary>
/// Sign-ins on a world server: only the staff dashboard's requests the auth host forwards (?world=…) reach it; the
/// bearer token is checked by the realm and the account mirrored. Accounts are not created or signed in here.
/// </summary>
public sealed class LinkedAccountAuthentication(RealmLinkClient link, AccountMirrorService mirror) : IAccountAuthentication
{
    static readonly TimeSpan Recheck = TimeSpan.FromSeconds(30);
    readonly ConcurrentDictionary<string, (VerifiedAccount Account, DateTimeOffset At)> signed = new(StringComparer.Ordinal);

    public async Task<VerifiedAccount?> AuthenticateAsync(string token, CancellationToken cancellationToken)
    {
        if (token.Length is 0 or > 512) return null;
        if (signed.TryGetValue(token, out var cached) && DateTimeOffset.UtcNow - cached.At < Recheck) return cached.Account;
        if (await link.AuthenticateAsync(token, cancellationToken) is not { } account)
        {
            signed.TryRemove(token, out _);
            return null;
        }
        await mirror.KeepAsync(account, cancellationToken);
        var verified = new VerifiedAccount(account.AccountId);
        if (signed.Count > 10_000) signed.Clear();
        signed[token] = (verified, DateTimeOffset.UtcNow);
        return verified;
    }

    public Task<bool> RegisterAsync(string username, string password, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<AccountSession?> SignInAsync(string username, string password, CancellationToken cancellationToken) => Task.FromResult<AccountSession?>(null);
    public Task SignOutAsync(string token, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<GameTicket?> IssueGameTicketAsync(string sessionToken, int worldId, CancellationToken cancellationToken) => Task.FromResult<GameTicket?>(null);
}

/// <summary>
/// A world server's staff tools: staff roles, the economy and bulk-reward targets from this world's database (the
/// mirror holds the roles); its maintenance, the realm's sanctions and scheduled events, and player reports over the
/// realm link. The realm-wide dashboard tools (sanctions, tickets, chat log, events, population) are the auth host's.
/// </summary>
public sealed class LinkedStaffTools(PostgresStaffTools local, RealmLinkClient link) : IStaffTools
{
    LinkRealmState? state;
    DateTimeOffset stateAt = DateTimeOffset.MinValue;

    async Task<LinkRealmState> StateAsync(CancellationToken ct)
    {
        if (state is { } cached && DateTimeOffset.UtcNow - stateAt < TimeSpan.FromSeconds(2)) return cached;
        state = await link.StateAsync(ct) ?? throw new RealmLinkException("The realm sent no state.");
        stateAt = DateTimeOffset.UtcNow;
        return state;
    }

    static NotSupportedException RealmOnly() => new("A realm tool: the auth host answers it.");

    public Task<IReadOnlyList<string>> RolesAsync(VerifiedAccount actor, CancellationToken ct = default) => local.RolesAsync(actor, ct);
    public Task<bool> IsStaffAsync(Guid account, CancellationToken ct = default) => local.IsStaffAsync(account, ct);
    public async Task<MaintenanceState> MaintenanceAsync(int worldId, CancellationToken ct = default) =>
        worldId == link.WorldId ? (await StateAsync(ct)).Maintenance : throw RealmOnly();
    public async Task<MaintenanceState> SetMaintenanceAsync(VerifiedAccount actor, int worldId, bool enabled, string message, CancellationToken ct = default)
    {
        if (worldId != link.WorldId) throw RealmOnly();
        var changed = await link.SetMaintenanceAsync(new LinkMaintenance(actor.AccountId, enabled, message), ct) ?? throw new GameplayException("forbidden");
        stateAt = DateTimeOffset.MinValue;
        return changed;
    }
    public async Task<IReadOnlyList<Sanction>> ActiveSanctionsAsync(CancellationToken ct = default) => (await StateAsync(ct)).Sanctions;
    public async Task<IReadOnlyList<ScheduledEvent>> LiveEventsAsync(CancellationToken ct = default) => (await StateAsync(ct)).Events;
    public async Task<long> CreateTicketAsync(Guid? account, string? characterName, string category, string content, CancellationToken ct = default) =>
        (await link.TicketAsync(new LinkTicket(account, characterName, category, content), ct))?.Value ?? throw new GameplayException("ticket_failed");
    public Task<EconomyReport> EconomyAsync(VerifiedAccount actor, CancellationToken ct = default) => local.EconomyAsync(actor, ct);
    public Task<IReadOnlyList<Guid>> AllCharactersAsync(VerifiedAccount actor, CancellationToken ct = default) => local.AllCharactersAsync(actor, ct);
    public Task<AccountRef?> FindAccountAsync(VerifiedAccount actor, string characterOrAccount, CancellationToken ct = default) =>
        local.FindAccountAsync(actor, characterOrAccount, ct);

    // A world server runs no chat server, scheduler, population sampler or cleanup.
    public Task LogChatAsync(IReadOnlyList<ChatLogEntry> lines, CancellationToken ct = default) => Task.CompletedTask;
    public Task MarkEventRunAsync(long id, DateTimeOffset at, CancellationToken ct = default) => Task.CompletedTask;
    public Task SamplePopulationAsync(int players, int connections, CancellationToken ct = default) => Task.CompletedTask;
    public Task CleanupAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<Sanction>> SanctionsAsync(VerifiedAccount actor, bool includeEnded, CancellationToken ct = default) => throw RealmOnly();
    public Task<Sanction> SanctionAsync(VerifiedAccount actor, string characterOrAccount, string kind, string reason, TimeSpan? duration, CancellationToken ct = default) => throw RealmOnly();
    public Task<bool> RevokeSanctionAsync(VerifiedAccount actor, long id, CancellationToken ct = default) => throw RealmOnly();
    public Task<IReadOnlyList<ChatLine>> ChatLogAsync(VerifiedAccount actor, string? search, string? sender, long? before, int limit, CancellationToken ct = default) => throw RealmOnly();
    public Task<IReadOnlyList<Ticket>> TicketsAsync(VerifiedAccount actor, string status, CancellationToken ct = default) => throw RealmOnly();
    public Task<bool> UpdateTicketAsync(VerifiedAccount actor, long id, string status, string note, CancellationToken ct = default) => throw RealmOnly();
    public Task<IReadOnlyList<Ticket>> MyTicketsAsync(Guid account, CancellationToken ct = default) => throw RealmOnly();
    public Task<IReadOnlyList<ScheduledEvent>> EventsAsync(VerifiedAccount actor, CancellationToken ct = default) => throw RealmOnly();
    public Task<ScheduledEvent> CreateEventAsync(VerifiedAccount actor, NewScheduledEvent input, CancellationToken ct = default) => throw RealmOnly();
    public Task<bool> CancelEventAsync(VerifiedAccount actor, long id, CancellationToken ct = default) => throw RealmOnly();
    public Task<IReadOnlyList<PopulationPoint>> PopulationAsync(VerifiedAccount actor, TimeSpan window, CancellationToken ct = default) => throw RealmOnly();
}

/// <summary>
/// Signs this world server in to the realm at start and again every minute (its addresses, ports and control token;
/// the realm lists it while it answers), and keeps the realm's character index current (every 15 seconds, changes only).
/// </summary>
public sealed class RealmLinkService(RealmLinkClient link, IAccountMirror mirror, WorldProcessOptions process, Native.NativeOptions native,
    IConfiguration configuration, ILogger<RealmLinkService> logger) : BackgroundService
{
    Dictionary<Guid, IndexedCharacter>? sent;
    DateTimeOffset registeredAt = DateTimeOffset.MinValue, syncedAt = DateTimeOffset.MinValue;
    bool signedIn;

    public LinkWorld? World { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            var now = DateTimeOffset.UtcNow;
            try
            {
                if (now - registeredAt >= (signedIn ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(5))) await RegisterAsync(stoppingToken);
                if (signedIn && now - syncedAt >= TimeSpan.FromSeconds(15)) await SyncAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is HttpRequestException or RealmLinkException or TaskCanceledException or Npgsql.NpgsqlException or GameplayException)
            {
                logger.LogWarning("Realm link: {Error}", error.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    async Task RegisterAsync(CancellationToken ct)
    {
        registeredAt = DateTimeOffset.UtcNow;
        var listen = process.ListenAddress;
        var controlHost = process.ControlHost ?? (listen is "0.0.0.0" || System.Net.IPAddress.TryParse(listen, out var a) && System.Net.IPAddress.IsLoopback(a) ? null : listen);
        var gameHost = configuration["Aion:WorldProcess:GameHost"] ?? (native.Network.LoopbackOnly ? null : native.Network.PublicAddress);
        var version = typeof(RealmLinkService).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        var reply = await link.RegisterAsync(new LinkRegister(gameHost, native.GamePort ?? 0, controlHost,
            configuration.GetValue<int?>("Aion:LocalPort") ?? 0, process.ControlToken, version), ct);
        if (reply is null)
        {
            if (signedIn || World is null) logger.LogWarning("Realm link: the realm does not know world {World} (or its token)", link.WorldId);
            signedIn = false;
            return;
        }
        if (!signedIn) logger.LogInformation("Signed in to the realm as world {World} ({Name})", reply.WorldId, reply.Name);
        World = reply;
        signedIn = true;
    }

    /// <summary>Sends the characters that changed since the last sync (all of them the first time).</summary>
    public async Task SyncAsync(CancellationToken ct)
    {
        syncedAt = DateTimeOffset.UtcNow;
        var now = (await mirror.CharactersAsync(ct)).ToDictionary(c => c.CharacterId);
        if (sent is null)
        {
            await link.SyncCharactersAsync(new LinkCharacters(true, [.. now.Values], []), ct);
            sent = now;
            return;
        }
        var changed = now.Values.Where(c => !sent.TryGetValue(c.CharacterId, out var before) || before != c).ToList();
        var removed = sent.Keys.Where(id => !now.ContainsKey(id)).ToList();
        if (changed.Count == 0 && removed.Count == 0) return;
        await link.SyncCharactersAsync(new LinkCharacters(false, changed, removed), ct);
        sent = now;
    }
}
