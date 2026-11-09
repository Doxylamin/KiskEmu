using System.Collections.Concurrent;
using System.Net;
using Aion.Host.Worlds;
using Microsoft.AspNetCore.Http.Features;

namespace Aion.Host.Realm;

/// <summary>Realm socket event names (server/REALM_LINK.md).</summary>
public static class LinkEvents
{
    public const string AccountChanged = "account.changed", StateChanged = "state.changed",
        PresenceSnapshot = "presence.snapshot", PresenceEnter = "presence.enter", PresenceLeave = "presence.leave", Status = "status";
}

public sealed record LinkAccount(Guid Account);
public sealed record LinkLeft(Guid CharacterId);

/// <summary>The characters each world server reports over its realm socket (live: entered and left as it happens).</summary>
public sealed class PushedPresence
{
    readonly ConcurrentDictionary<int, ConcurrentDictionary<Guid, WorldPresenceEntry>> worlds = new();

    /// <summary>The world's live list while its socket is open, else null (then the realm polls it).</summary>
    public IReadOnlyDictionary<Guid, WorldPresenceEntry>? In(int worldId) => worlds.TryGetValue(worldId, out var world) ? world : null;

    public int? WorldOf(Guid character)
    {
        foreach (var (worldId, world) in worlds)
            if (world.ContainsKey(character)) return worldId;
        return null;
    }

    public void Replace(int worldId, IEnumerable<WorldPresenceEntry> entries)
    {
        var world = worlds.GetOrAdd(worldId, _ => new());
        var now = entries.ToDictionary(e => e.CharacterId);
        foreach (var gone in world.Keys.Where(id => !now.ContainsKey(id)).ToList()) world.TryRemove(gone, out _);
        foreach (var (id, entry) in now) world[id] = entry;
    }

    public void Enter(int worldId, WorldPresenceEntry entry) => worlds.GetOrAdd(worldId, _ => new())[entry.CharacterId] = entry;
    public void Leave(int worldId, Guid character) { if (worlds.TryGetValue(worldId, out var world)) world.TryRemove(character, out _); }
    public void Drop(int worldId) => worlds.TryRemove(worldId, out _);
}

/// <summary>
/// The auth host's end of the realm sockets (server/REALM_LINK.md): accepts each world server's socket after checking its
/// world token, answers its tunnelled realm-link requests on this host's own port, keeps its pushed presence and status,
/// and sends events to the worlds. <see cref="WorldLinks"/> sends through <see cref="Handler"/>.
/// </summary>
public sealed class RealmSocketHub(ILogger<RealmSocketHub> logger, LinkTiming? timing = null)
{
    public const string Path = "/internal/realm/socket";
    readonly LinkTiming timing = timing ?? LinkTiming.Default;
    readonly ConcurrentDictionary<int, LinkChannel> channels = new();
    readonly ConcurrentDictionary<int, (WorldProcessStatus Status, DateTimeOffset At)> statuses = new();
    readonly ConcurrentDictionary<int, bool> seen = new();

    public LinkCounters Counters { get; } = new();
    public PushedPresence Presence { get; } = new();

    public LinkChannel? Channel(int worldId) => channels.TryGetValue(worldId, out var channel) && channel.IsOpen ? channel : null;
    public bool Connected(int worldId) => Channel(worldId) is not null;
    public IReadOnlyCollection<int> ConnectedWorlds => channels.Where(c => c.Value.IsOpen).Select(c => c.Key).ToList();

    /// <summary>The world's last status over its socket (players, connections), while the socket is open and it is recent.</summary>
    public WorldProcessStatus? Status(int worldId) =>
        Connected(worldId) && statuses.TryGetValue(worldId, out var last) && DateTimeOffset.UtcNow - last.At < timing.StatusEvery * 3 ? last.Status : null;

    /// <summary>For the dashboard's Worlds page.</summary>
    public object Describe(int worldId) => Channel(worldId) is { } channel
        ? new { connected = true, since = channel.Since }
        : new { connected = false, since = (DateTimeOffset?)null };

    /// <summary>The handler <see cref="WorldLinks"/> sends with: the socket of the request's world when open, else HTTP.</summary>
    public HttpMessageHandler Handler(HttpMessageHandler http) =>
        new LinkTunnelHandler(http, request => request.Options.TryGetValue(LinkTunnelHandler.World, out var worldId) ? Channel(worldId) : null, Counters, logger);

    /// <summary>GET /internal/realm/socket: a world server's socket, signed in like every realm-link call.</summary>
    public async Task AcceptAsync(HttpContext context, Func<HttpContext, Task<int?>> authenticate)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        if (await authenticate(context) is not { } worldId)
        {
            Interlocked.Increment(ref Counters.Refused);
            logger.LogWarning("Realm socket refused from {Address}: unknown world or wrong token (world {World})",
                context.Connection.RemoteIpAddress, context.Request.Headers[RealmLinkEndpoints.WorldHeader].ToString());
            context.Response.StatusCode = 403;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var forced = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [RealmLinkEndpoints.WorldHeader] = worldId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Authorization"] = context.Request.Headers.Authorization.ToString(),
        };
        using var dispatcher = new LinkDispatcher(SelfAddress(context), LinkDispatcher.Under("/internal/realm/"), forced);
        var channel = new LinkChannel(socket, worldId, timing, Counters, logger) { OnRequest = dispatcher.DispatchAsync };
        channel.OnEvent = frame => { Received(worldId, frame); return Task.CompletedTask; };
        if (channels.TryGetValue(worldId, out var old)) old.Abort("replaced by a new socket");
        channels[worldId] = channel;
        Interlocked.Increment(ref Counters.Connects);
        if (!seen.TryAdd(worldId, true)) Interlocked.Increment(ref Counters.Reconnects);
        logger.LogInformation("Realm socket of world {World} connected from {Address}", worldId, context.Connection.RemoteIpAddress);
        var stopping = context.RequestServices.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None;
        using var ends = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
        try { await channel.RunAsync(ends.Token); }
        finally
        {
            if (channels.TryRemove(new KeyValuePair<int, LinkChannel>(worldId, channel)))
            {
                Presence.Drop(worldId);
                statuses.TryRemove(worldId, out _);
            }
            Interlocked.Increment(ref Counters.Disconnects);
            logger.LogInformation("Realm socket of world {World} closed: {Reason}", worldId, channel.CloseReason);
        }
    }

    /// <summary>Closes a world's socket (it reconnects); for tests and a token change.</summary>
    public void Disconnect(int worldId, string reason) { if (channels.TryGetValue(worldId, out var channel)) channel.Abort(reason); }

    void Received(int worldId, LinkFrame frame)
    {
        switch (frame.Name)
        {
            case LinkEvents.PresenceSnapshot:
                Presence.Replace(worldId, frame.DataAs<List<WorldPresenceEntry>>() ?? []);
                break;
            case LinkEvents.PresenceEnter when frame.DataAs<WorldPresenceEntry>() is { } entry:
                Presence.Enter(worldId, entry);
                break;
            case LinkEvents.PresenceLeave when frame.DataAs<LinkLeft>() is { } left:
                Presence.Leave(worldId, left.CharacterId);
                break;
            case LinkEvents.Status when frame.DataAs<WorldProcessStatus>() is { } status && status.WorldId == worldId:
                statuses[worldId] = (status, DateTimeOffset.UtcNow);
                break;
        }
    }

    /// <summary>Sends an event to every connected world (or one); the number it reached.</summary>
    public async Task<int> PublishAsync(string name, object? data, int? worldId = null)
    {
        var reached = 0;
        foreach (var (id, channel) in channels)
            if ((worldId is null || worldId == id) && channel.IsOpen && await channel.PublishAsync(name, data)) reached++;
        return reached;
    }

    /// <summary>An account's roles or state changed: every world reloads it into its mirror now.</summary>
    public Task<int> AccountChangedAsync(Guid account) => PublishAsync(LinkEvents.AccountChanged, new LinkAccount(account));

    /// <summary>Maintenance, sanctions or scheduled events changed: every world reloads them now.</summary>
    public Task<int> StateChangedAsync() => PublishAsync(LinkEvents.StateChanged, null);

    /// <summary>This host's own port the socket came in on (tunnelled requests are replayed there).</summary>
    static Uri SelfAddress(HttpContext context)
    {
        var connection = context.Connection;
        var address = connection.LocalIpAddress ?? IPAddress.Loopback;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        // The connection's own TLS, not X-Forwarded-Proto (behind a TLS proxy the port itself is plain HTTP).
        var scheme = context.Features.Get<ITlsConnectionFeature>() is not null ? "https" : "http";
        return new Uri($"{scheme}://{host}:{connection.LocalPort.ToString(System.Globalization.CultureInfo.InvariantCulture)}/");
    }
}
