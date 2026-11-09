using System.Collections.Concurrent;
using System.Net.Http.Json;
using Aion.Application.Characters;
using Aion.Application.Chat;
using Aion.Application.Gameplay;
using Aion.Host.Chat;

namespace Aion.Host.Worlds;

/// <summary>A character in a world process, as that process reports it to the primary host (/internal/world/presence).</summary>
public sealed record WorldPresenceEntry(Guid CharacterId, Guid AccountId, string Name, string DisplayName, long GuildId, int Race);

/// <summary>A chat command forwarded to the world the sender plays on (/internal/world/command).</summary>
public sealed record WorldCommandRequest(ChatIdentity Sender, string UserName, string Content);

/// <summary>A text for a world: a centre-screen notice (/internal/world/notice) or a chat announcement (/internal/world/chat).</summary>
public sealed record WorldText(int WorldId, string Text, string? Color = null, int? Size = null);

/// <summary>After a sanction change: reload the sanctions, and disconnect the account when it was banned (/internal/world/sanctions).</summary>
public sealed record WorldSanctionChange(Guid? KickAccount, string? Reason);

/// <summary>
/// The auth host's links to the world servers: each one's status and control port (loopback for managed ones, the host of a
/// remote one), called with the world's control token. The realm's chat server, the staff dashboard's world tools and
/// realm-wide actions (bans, scheduled announcements) reach the worlds through it. A world counts while it answers.
/// </summary>
public sealed class WorldLinks(LoginWorldList worlds, WorldSupervisor supervisor, ILogger<WorldLinks> logger)
{
    public const string ControlHeader = "X-World-Control";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>The world servers that answer on their status port now.</summary>
    public async Task<IReadOnlyList<WorldRecord>> RunningAsync(CancellationToken ct) =>
        (await worlds.WorldsAsync(ct)).Where(w => w is { Enabled: true, InternalPort: not null }
            && supervisor.Runtime(w.WorldId).State is WorldRuntime.Running or WorldRuntime.Online).ToList();

    public async Task<WorldRecord?> RunningAsync(int worldId, CancellationToken ct) =>
        (await RunningAsync(ct)).FirstOrDefault(w => w.WorldId == worldId);

    /// <summary>Whether <paramref name="worldId"/> is one of the realm's registered worlds (running or not).</summary>
    public async Task<bool> KnownAsync(int worldId, CancellationToken ct) => (await worlds.WorldsAsync(ct)).Any(w => w.WorldId == worldId);

    public string ControlToken(int worldId) => supervisor.ControlToken(worldId);

    HttpRequestMessage Request(WorldRecord world, HttpMethod method, string pathAndQuery)
    {
        var request = new HttpRequestMessage(method, $"http://{WorldSupervisor.StatusHost(world)}:{world.InternalPort}{pathAndQuery}");
        request.Headers.Add(ControlHeader, supervisor.ControlToken(world.WorldId));
        return request;
    }

    async Task<T?> SendAsync<T>(WorldRecord world, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = Request(world, method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        try
        {
            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("World {World} answered {Status} on {Path}", world.WorldId, (int)response.StatusCode, path);
                return default;
            }
            return await response.Content.ReadFromJsonAsync<T>(ct);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("World {World} unreachable on {Path}: {Error}", world.WorldId, path, error.Message);
            return default;
        }
    }

    public async Task<IReadOnlyList<WorldPresenceEntry>?> PresenceAsync(WorldRecord world, CancellationToken ct) =>
        await SendAsync<List<WorldPresenceEntry>>(world, HttpMethod.Get, "/internal/world/presence", null, ct);

    /// <summary>The answer of the sender's world to a chat command, or null when that world is not reachable.</summary>
    public async Task<string?> CommandAsync(int worldId, WorldCommandRequest command, CancellationToken ct) =>
        await RunningAsync(worldId, ct) is { } world
            ? (await SendAsync<CommandAnswer>(world, HttpMethod.Post, "/internal/world/command", command, ct))?.Answer : null;

    sealed record CommandAnswer(string Answer);
    sealed record Count(int Value);

    /// <summary>A centre-screen notice on every running world process; the number of players shown.</summary>
    public async Task<int> NoticeAllAsync(string text, string color, CancellationToken ct, int size = 20)
    {
        var shown = 0;
        foreach (var world in await RunningAsync(ct))
            shown += (await SendAsync<Count>(world, HttpMethod.Post, "/internal/world/notice", new WorldText(world.WorldId, text, color, size), ct))?.Value ?? 0;
        return shown;
    }

    /// <summary>Players in the world on every running world server (their last reported status).</summary>
    public async Task<(int Players, int Connections)> PopulationAsync(CancellationToken ct)
    {
        var (players, connections) = (0, 0);
        foreach (var world in await RunningAsync(ct))
        {
            var runtime = supervisor.Runtime(world.WorldId);
            players += runtime.Players ?? 0;
            connections += runtime.Connections ?? 0;
        }
        return (players, connections);
    }

    /// <summary>Every running world reloads its sanctions; a banned account is disconnected there. The number kicked.</summary>
    public async Task<int> SanctionsChangedAsync(Guid? kickAccount, string? reason, CancellationToken ct)
    {
        var kicked = 0;
        foreach (var world in await RunningAsync(ct))
            kicked += (await SendAsync<Count>(world, HttpMethod.Post, "/internal/world/sanctions", new WorldSanctionChange(kickAccount, reason), ct))?.Value ?? 0;
        return kicked;
    }

    /// <summary>
    /// The staff dashboard's world-scoped request (?world=…) handled by that world's process: same path, query, body and
    /// sign-in (the world process runs the same staff endpoints and checks the same staff roles).
    /// </summary>
    public async Task ProxyAsync(HttpContext context, int worldId)
    {
        var ct = context.RequestAborted;
        if (await RunningAsync(worldId, ct) is not { } world)
        {
            context.Response.StatusCode = (await KnownAsync(worldId, ct)) ? 503 : 404;
            await context.Response.WriteAsJsonAsync(new { error = "world_offline" }, ct);
            return;
        }
        var method = new HttpMethod(context.Request.Method);
        using var request = Request(world, method, context.Request.Path + context.Request.QueryString);
        if (context.Request.Headers.Authorization is { Count: > 0 } authorization)
            request.Headers.TryAddWithoutValidation("Authorization", authorization.ToString());
        if (method != HttpMethod.Get && method != HttpMethod.Head)
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, ct);
            request.Content = new ByteArrayContent(body.ToArray());
            if (context.Request.ContentType is { Length: > 0 } type)
                request.Content.Headers.TryAddWithoutValidation("Content-Type", type);
        }
        try
        {
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            context.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is { } contentType) context.Response.ContentType = contentType.ToString();
            if (response.Content.Headers.ContentDisposition is { } disposition) context.Response.Headers.ContentDisposition = disposition.ToString();
            await response.Content.CopyToAsync(context.Response.Body, ct);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Staff request for world {World} failed: {Error}", worldId, error.Message);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 502;
                await context.Response.WriteAsJsonAsync(new { error = "world_offline" }, ct);
            }
        }
    }
}

/// <summary>
/// The characters in the world on each running world process, polled every 2 s (and on demand at a chat login) for the
/// realm's chat server, which runs on the primary host. Reads trusted world state over the control link only.
/// </summary>
public sealed class WorldPresenceMirror(WorldLinks links, ILogger<WorldPresenceMirror> logger) : BackgroundService
{
    static readonly TimeSpan Every = TimeSpan.FromSeconds(2);
    readonly ConcurrentDictionary<int, IReadOnlyDictionary<Guid, WorldPresenceEntry>> worlds = new();
    readonly ConcurrentDictionary<int, DateTimeOffset> polledAt = new();

    public WorldPresenceEntry? Find(Guid character)
    {
        foreach (var world in worlds.Values)
            if (world.TryGetValue(character, out var entry)) return entry;
        return null;
    }

    public IEnumerable<WorldPresenceEntry> OfAccount(Guid account) => worlds.Values.SelectMany(w => w.Values).Where(e => e.AccountId == account);

    public IReadOnlyDictionary<Guid, WorldPresenceEntry> In(int worldId) =>
        worlds.TryGetValue(worldId, out var world) ? world : new Dictionary<Guid, WorldPresenceEntry>();

    /// <summary>Reads one world now (at most twice a second); its characters are empty when it is not running.</summary>
    public async Task RefreshAsync(int worldId, CancellationToken ct)
    {
        if (polledAt.TryGetValue(worldId, out var at) && DateTimeOffset.UtcNow - at < TimeSpan.FromMilliseconds(500)) return;
        polledAt[worldId] = DateTimeOffset.UtcNow;
        if (await links.RunningAsync(worldId, ct) is not { } world) { worlds.TryRemove(worldId, out _); return; }
        if (await links.PresenceAsync(world, ct) is { } entries) worlds[worldId] = entries.ToDictionary(e => e.CharacterId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var running = await links.RunningAsync(stoppingToken);
                foreach (var gone in worlds.Keys.Except(running.Select(w => w.WorldId))) worlds.TryRemove(gone, out _);
                foreach (var world in running)
                {
                    polledAt[world.WorldId] = DateTimeOffset.UtcNow;
                    if (await links.PresenceAsync(world, stoppingToken) is { } entries) worlds[world.WorldId] = entries.ToDictionary(e => e.CharacterId);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is Npgsql.NpgsqlException or InvalidOperationException or TimeoutException)
            {
                logger.LogWarning("World presence poll failed: {Error}", error.Message);
            }
        }
    }
}

/// <summary>
/// Chat's view of every world of the realm: the characters the world servers report (and a local world when this host
/// runs one) (<see cref="WorldPresenceMirror"/>). Character ids are unique across worlds.
/// </summary>
public sealed class RealmChatDirectory(WorldChatDirectory? local, WorldPresenceMirror mirror, IWireCharacterRepository characters) : IChatDirectory
{
    public async Task<IReadOnlyList<(ChatCharacter Character, string WireId)>> InWorldAsync(Guid account, int worldId, CancellationToken cancellationToken)
    {
        if (local is not null && worldId == local.WorldId) return await local.InWorldAsync(account, worldId, cancellationToken);
        await mirror.RefreshAsync(worldId, cancellationToken);
        var present = mirror.In(worldId);
        var stored = await characters.ListWorldAsync(account, worldId, cancellationToken);
        var found = new List<(ChatCharacter, string)>();
        foreach (var state in stored)
            if (present.TryGetValue(state.Summary.CharacterId, out var entry) && entry.AccountId == account)
                found.Add((Character(entry), state.WireId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return found;
    }

    public ChatCharacter? Current(Guid character, Guid account) =>
        local?.Current(character, account)
        ?? (mirror.Find(character) is { } entry && entry.AccountId == account ? Character(entry) : null);

    public ChatCharacter? CurrentOfAccount(Guid account)
    {
        var here = local?.CurrentOfAccount(account);
        var elsewhere = mirror.OfAccount(account).ToList();
        return (here, elsewhere) switch
        {
            ({ } only, []) => only,
            (null, [var one]) => Character(one),
            _ => null,
        };
    }

    static ChatCharacter Character(WorldPresenceEntry entry) => new(entry.CharacterId, entry.Name, entry.DisplayName, entry.GuildId, entry.Race);
}

/// <summary>
/// Chat commands ("//…") of the realm's chat server: answered by the world the sender plays on (its players, its
/// builder commands), over its control link (a local world, when this host runs one, answers directly).
/// </summary>
public sealed class RealmChatCommands(IChatCommandHandler? local, WorldLinks links, string localServerKey) : IChatCommandHandler
{
    public bool Owns(string content) => content.TrimStart().StartsWith('/');

    public async Task<string> HandleAsync(ChatIdentity sender, string userName, string content, CancellationToken cancellationToken)
    {
        if (local is not null && sender.ServerKey == localServerKey) return await local.HandleAsync(sender, userName, content, cancellationToken);
        if (!int.TryParse(sender.ServerKey, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var worldId))
            return "Commands are not available here.";
        return await links.CommandAsync(worldId, new WorldCommandRequest(sender, userName, content), cancellationToken)
            ?? "Commands are not available right now. Please try again.";
    }
}
