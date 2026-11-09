using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Aion.Host.Worlds;

namespace Aion.Host.Realm;

/// <summary>A world server's open realm socket, if any (shared by <see cref="RealmLinkClient"/> and <see cref="RealmSocketClient"/>).</summary>
public sealed class LinkSlot
{
    LinkChannel? channel;

    public LinkChannel? Current => channel is { IsOpen: true } open ? open : null;
    public LinkCounters Counters { get; } = new();

    internal void Set(LinkChannel? value) => channel = value;
    internal void Clear(LinkChannel value) => Interlocked.CompareExchange(ref channel, null, value);
}

/// <summary>
/// A world server's end of the realm socket (server/REALM_LINK.md): dials the auth host, answers its tunnelled control
/// requests on this server's own control port, applies its events (account and state changes), pushes presence and
/// status, and after every connect signs in again and reloads what it may have missed. Reconnects with backoff.
/// </summary>
public sealed class RealmSocketClient(RealmLinkOptions options, int worldId, Uri controlBase, LinkSlot slot, IServiceProvider services,
    ILogger<RealmSocketClient> logger, LinkTiming? timing = null) : BackgroundService
{
    readonly LinkTiming timing = timing ?? LinkTiming.Default;

    public LinkCounters Counters => slot.Counters;
    /// <summary>Raised after each connect, once the resync is done (tests).</summary>
    public event Action? Resynced;
    /// <summary>Every event from the realm, after it was applied (tests).</summary>
    public event Action<LinkFrame>? EventReceived;

    /// <summary>ws(s)://…/internal/realm/socket of the realm-link URL.</summary>
    public static Uri SocketAddress(string realmUrl)
    {
        var builder = new UriBuilder(realmUrl.TrimEnd('/') + RealmSocketHub.Path);
        builder.Scheme = builder.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        return builder.Uri;
    }

    /// <summary>The address this server's control port answers on locally.</summary>
    public static Uri ControlAddress(string listenAddress, int port)
    {
        var host = listenAddress is "0.0.0.0" or "" ? "127.0.0.1" : listenAddress is "::" ? "[::1]"
            : IPAddress.TryParse(listenAddress, out var a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{a}]" : listenAddress;
        return new Uri($"http://{host}:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}/");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var address = SocketAddress(options.Url);
        using var dispatcher = new LinkDispatcher(controlBase, LinkDispatcher.Under("/internal/world/", "/api/"), new Dictionary<string, string>());
        var failures = 0;
        var connectedBefore = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader(RealmLinkEndpoints.WorldHeader, worldId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            socket.Options.SetRequestHeader("Authorization", "Bearer " + options.Token);
            socket.Options.KeepAliveInterval = TimeSpan.Zero;   // the channel's own heartbeat
            socket.Options.CollectHttpResponseDetails = true;
            if (options.CertificateSha256 is { Length: > 0 } pin)
            {
                var expected = Convert.FromHexString(pin.Replace(":", "", StringComparison.Ordinal).Trim());
                socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    errors == System.Net.Security.SslPolicyErrors.None
                    || certificate is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), expected);
            }
            try
            {
                using var connecting = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                connecting.CancelAfter(timing.Connect);
                await socket.ConnectAsync(address, connecting.Token);
            }
            catch (Exception error) when (error is WebSocketException or IOException or HttpRequestException
                or OperationCanceledException && !stoppingToken.IsCancellationRequested)
            {
                failures++;
                var refused = socket.HttpStatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
                if (refused) Interlocked.Increment(ref Counters.Refused);
                var wait = refused ? timing.ReconnectMax : timing.Backoff(failures);
                if (refused || failures == 1 || failures % 10 == 0)
                    logger.LogWarning("Realm socket: {Problem}; retrying in {Seconds:0.#} s (attempt {Attempt}, HTTP stays in use)",
                        refused ? "the realm refused this world's token (Aion:Realm:Token)" : $"cannot reach {address} ({error.Message})", wait.TotalSeconds, failures);
                if (!await DelayAsync(wait, stoppingToken)) break;
                continue;
            }
            failures = 0;
            Interlocked.Increment(ref Counters.Connects);
            if (connectedBefore) Interlocked.Increment(ref Counters.Reconnects);
            connectedBefore = true;
            var channel = new LinkChannel(socket, worldId, timing, Counters, logger) { OnRequest = dispatcher.DispatchAsync };
            channel.OnEvent = frame => OnEventAsync(frame, stoppingToken);
            slot.Set(channel);
            logger.LogInformation("Realm socket connected to {Address} as world {World}", address, worldId);
            var run = channel.RunAsync(stoppingToken);
            var resync = ResyncAsync(stoppingToken);
            var pump = PumpAsync(channel, stoppingToken);
            await run;
            slot.Clear(channel);
            Interlocked.Increment(ref Counters.Disconnects);
            await Task.WhenAll(Quiet(resync), Quiet(pump));
            if (stoppingToken.IsCancellationRequested) break;
            logger.LogWarning("Realm socket closed ({Reason}); reconnecting (HTTP in the meantime)", channel.CloseReason);
            if (!await DelayAsync(timing.Backoff(1), stoppingToken)) break;
        }
    }

    static async Task Quiet(Task task) { try { await task; } catch (OperationCanceledException) { } }

    static async Task<bool> DelayAsync(TimeSpan wait, CancellationToken ct)
    {
        try { await Task.Delay(wait, ct); return true; }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>After a connect: sign in again (over the new socket) and reload state and the mirrored accounts.</summary>
    async Task ResyncAsync(CancellationToken ct)
    {
        try
        {
            if (services.GetService<RealmLinkService>() is { } link) await link.SignInAsync(ct);
            await ReloadStateAsync(ct);
            if (services.GetService<AccountMirrorService>() is { } mirror) await mirror.RefreshAllAsync(ct);
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Realm socket: resync after connect failed: {Error}", error.Message);
        }
        Resynced?.Invoke();
    }

    async Task ReloadStateAsync(CancellationToken ct)
    {
        if (services.GetService<Aion.Application.Gameplay.IStaffTools>() is LinkedStaffTools tools) tools.Invalidate();
        if (services.GetService<Authentication.ServerControl>() is { } control) await control.RefreshAsync(ct);
    }

    async Task OnEventAsync(LinkFrame frame, CancellationToken ct)
    {
        switch (frame.Name)
        {
            case LinkEvents.AccountChanged when frame.DataAs<LinkAccount>() is { } changed:
                if (services.GetService<Aion.Application.Authentication.IAccountAuthentication>() is LinkedAccountAuthentication auth) auth.Forget(changed.Account);
                if (services.GetService<AccountMirrorService>() is { } mirror) await mirror.RefreshAsync(changed.Account, ct);
                logger.LogInformation("Realm socket: account {Account} changed on the realm; mirror reloaded", changed.Account);
                break;
            case LinkEvents.StateChanged:
                await ReloadStateAsync(ct);
                break;
        }
        EventReceived?.Invoke(frame);
    }

    /// <summary>Presence changes (each second) and the status (every few seconds) to the realm while the socket is open.</summary>
    async Task PumpAsync(LinkChannel channel, CancellationToken ct)
    {
        var presence = services.GetService<Aion.World.WorldPresence>();
        var startedAt = (services.GetService<WorldProcessOptions>()?.StartedAt ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        Dictionary<Guid, WorldPresenceEntry>? sent = null;
        var statusAt = DateTimeOffset.MinValue;
        while (channel.IsOpen && !ct.IsCancellationRequested)
        {
            var now = new Dictionary<Guid, WorldPresenceEntry>();
            if (presence is not null)
                foreach (var entry in WorldEndpoints.PresenceEntries(presence)) now[entry.CharacterId] = entry;
            if (sent is null)
            {
                if (!await channel.PublishAsync(LinkEvents.PresenceSnapshot, now.Values.ToList(), ct)) return;
            }
            else
            {
                foreach (var entry in now.Values)
                    if ((!sent.TryGetValue(entry.CharacterId, out var before) || before != entry) && !await channel.PublishAsync(LinkEvents.PresenceEnter, entry, ct)) return;
                foreach (var gone in sent.Keys)
                    if (!now.ContainsKey(gone) && !await channel.PublishAsync(LinkEvents.PresenceLeave, new LinkLeft(gone), ct)) return;
            }
            sent = now;
            if (DateTimeOffset.UtcNow - statusAt >= timing.StatusEvery)
            {
                statusAt = DateTimeOffset.UtcNow;
                await channel.PublishAsync(LinkEvents.Status, new WorldProcessStatus(worldId, presence?.All.Count ?? 0, presence?.Connections ?? 0, startedAt), ct);
            }
            await Task.WhenAny(Task.Delay(timing.PresenceEvery, ct), channel.Closed);
        }
    }
}
