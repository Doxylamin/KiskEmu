using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using Aion.Host.Realm;
using Aion.Host.Worlds;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aion.Host.Tests;

/// <summary>The realm socket (server/REALM_LINK.md) between a test realm and a test world server, both on loopback.</summary>
public sealed class RealmSocketTests
{
    const int WorldId = 7;

    static readonly LinkTiming Fast = new()
    {
        Heartbeat = TimeSpan.FromMilliseconds(200), Silence = TimeSpan.FromSeconds(3), Request = TimeSpan.FromSeconds(5),
        Connect = TimeSpan.FromSeconds(3), ReconnectBase = TimeSpan.FromMilliseconds(50), ReconnectMax = TimeSpan.FromMilliseconds(200),
        PresenceEvery = TimeSpan.FromMilliseconds(50), StatusEvery = TimeSpan.FromMilliseconds(100),
    };

    sealed record Echo(string Via, string World);

    /// <summary>A realm (hub and its /internal/realm routes) and a world server (its control routes) with the world's socket client.</summary>
    sealed class Rig : IAsyncDisposable
    {
        public required RealmSocketHub Hub;
        public required WebApplication Realm, World;
        public required Uri RealmBase, WorldBase;
        public readonly LinkSlot Slot = new();
        public RealmSocketClient? Client;
        public int Resyncs;
        public readonly List<LinkFrame> Events = [];
        public readonly TaskCompletionSource SlowStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SlowCalls;

        public static async Task<Rig> StartAsync(string token = "good", bool connect = true, LinkTiming? timing = null)
        {
            var hub = new RealmSocketHub(NullLogger<RealmSocketHub>.Instance, timing ?? Fast);
            Rig? rig = null;
            var realm = await AppAsync(app =>
            {
                app.UseWebSockets();
                app.Map(RealmSocketHub.Path, (HttpContext ctx) => hub.AcceptAsync(ctx, Authenticate));
                app.MapGet("/internal/realm/echo", (HttpContext ctx) => Results.Ok(Seen(ctx)));
                app.MapGet("/api/status", () => Results.Ok(new { open = true }));
            });
            var world = await AppAsync(app =>
            {
                app.MapGet("/internal/world/echo", (HttpContext ctx) => Results.Ok(Seen(ctx)));
                // The first call waits until the test lets it go (a call in flight when the socket drops).
                app.MapMethods("/internal/world/slow", ["GET", "POST"], async (HttpContext ctx) =>
                {
                    if (Interlocked.Increment(ref rig!.SlowCalls) == 1)
                    {
                        rig.SlowStarted.TrySetResult();
                        await rig.Gate.Task;
                    }
                    return Results.Ok(Seen(ctx));
                });
            });
            rig = new Rig { Hub = hub, Realm = realm, World = world, RealmBase = Address(realm), WorldBase = Address(world) };
            if (connect)
            {
                rig.Client = new RealmSocketClient(new RealmLinkOptions { Url = rig.RealmBase.ToString(), Token = token }, WorldId, rig.WorldBase, rig.Slot,
                    new ServiceCollection().BuildServiceProvider(), NullLogger<RealmSocketClient>.Instance, timing ?? Fast);
                rig.Client.Resynced += () => Interlocked.Increment(ref rig.Resyncs);
                rig.Client.EventReceived += frame => { lock (rig.Events) rig.Events.Add(frame); };
                await rig.Client.StartAsync(CancellationToken.None);
            }
            return rig;
        }

        /// <summary>World 7 with the token "good" (the realm checks the registry and the world token).</summary>
        static Task<int?> Authenticate(HttpContext http) => Task.FromResult<int?>(
            http.Request.Headers.Authorization == "Bearer good" && http.Request.Headers[RealmLinkEndpoints.WorldHeader] == "7" ? WorldId : null);

        static Echo Seen(HttpContext ctx) => new(ctx.Request.Headers[LinkDispatcher.Marker].ToString(), ctx.Request.Headers[RealmLinkEndpoints.WorldHeader].ToString());

        static async Task<WebApplication> AppAsync(Action<WebApplication> map)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Logging.ClearProviders();
            builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(2));
            var app = builder.Build();
            map(app);
            await app.StartAsync();
            return app;
        }

        static Uri Address(WebApplication app) =>
            new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/') + "/");

        /// <summary>A client of the world server as the realm's WorldLinks sends: over the socket when open, else HTTP.</summary>
        public HttpClient ToWorld() => new(Hub.Handler(new SocketsHttpHandler())) { Timeout = TimeSpan.FromSeconds(10) };

        public HttpRequestMessage WorldRequest(HttpMethod method, string path, Uri? at = null, bool repeatable = false)
        {
            var request = new HttpRequestMessage(method, new Uri(at ?? WorldBase, path));
            request.Options.Set(LinkTunnelHandler.World, WorldId);
            if (repeatable) request.Options.Set(LinkTunnelHandler.RetrySafe, true);
            return request;
        }

        /// <summary>A client of the realm as the world's RealmLinkClient sends.</summary>
        public HttpClient ToRealm() => new(new LinkTunnelHandler(new SocketsHttpHandler(), _ => Slot.Current, Slot.Counters, NullLogger.Instance))
        { BaseAddress = RealmBase, Timeout = TimeSpan.FromSeconds(10) };

        public async ValueTask DisposeAsync()
        {
            Gate.TrySetResult();
            if (Client is not null) await Client.StopAsync(CancellationToken.None);
            await Realm.StopAsync();
            await World.StopAsync();
            await Realm.DisposeAsync();
            await World.DisposeAsync();
        }
    }

    static async Task Until(Func<bool> condition, string what, int milliseconds = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Waited in vain for: " + what);
            await Task.Delay(20);
        }
    }

    static async Task Connected(Rig rig) => await Until(() => rig.Slot.Current is not null && rig.Hub.Connected(WorldId), "the socket to connect");

    [Fact]
    public void Socket_address_follows_the_realm_link_url()
    {
        Assert.Equal("ws://127.0.0.1:8091/internal/realm/socket", RealmSocketClient.SocketAddress("http://127.0.0.1:8091").ToString());
        Assert.Equal("wss://203.0.113.5:8780/internal/realm/socket", RealmSocketClient.SocketAddress("https://203.0.113.5:8780/").ToString());
        Assert.Equal("http://127.0.0.1:18780/", RealmSocketClient.ControlAddress("0.0.0.0", 18780).ToString());
        Assert.Equal("http://10.0.0.9:18780/", RealmSocketClient.ControlAddress("10.0.0.9", 18780).ToString());
    }

    [Fact]
    public async Task Tunnel_carries_requests_both_ways()
    {
        await using var rig = await Rig.StartAsync();
        await Connected(rig);

        // World → realm: the realm answers on its own route and stamps the socket's world id over the one the world sent.
        using var realm = rig.ToRealm();
        using var spoofed = new HttpRequestMessage(HttpMethod.Get, "internal/realm/echo");
        spoofed.Headers.Add(RealmLinkEndpoints.WorldHeader, "99");
        using var answer = await realm.SendAsync(spoofed);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal(new Echo("socket", "7"), await answer.Content.ReadFromJsonAsync<Echo>());
        Assert.Equal(1, rig.Slot.Counters.Tunnelled);
        // Only the realm link's routes are reachable over the socket.
        using var other = await realm.GetAsync("api/status");
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);

        // Realm → world, even without a control port of the world (as for a remote world without an inbound port).
        using var world = rig.ToWorld();
        using var noPort = rig.WorldRequest(HttpMethod.Get, "/internal/world/echo", new Uri("http://world-7.invalid/"));
        using var reply = await world.SendAsync(noPort);
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Equal("socket", (await reply.Content.ReadFromJsonAsync<Echo>())!.Via);
        Assert.Equal(1, rig.Hub.Counters.Tunnelled);
        Assert.Equal(0, rig.Hub.Counters.Fallbacks);
    }

    [Fact]
    public async Task Bad_token_is_refused()
    {
        await using var rig = await Rig.StartAsync(token: "wrong");
        await Until(() => rig.Slot.Counters.Refused >= 1 && rig.Hub.Counters.Refused >= 1, "the realm to refuse the token");
        Assert.False(rig.Hub.Connected(WorldId));
        Assert.Null(rig.Slot.Current);
        Assert.Equal(0, rig.Hub.Counters.Connects);
    }

    [Fact]
    public async Task Calls_go_over_http_without_a_socket()
    {
        await using var rig = await Rig.StartAsync(connect: false);
        using var world = rig.ToWorld();
        using var request = rig.WorldRequest(HttpMethod.Get, "/internal/world/echo");
        using var reply = await world.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Equal("", (await reply.Content.ReadFromJsonAsync<Echo>())!.Via);
        Assert.Equal(1, rig.Hub.Counters.Fallbacks);
        Assert.Equal(0, rig.Hub.Counters.Tunnelled);
    }

    [Fact]
    public async Task A_call_in_flight_fails_fast_when_the_socket_drops_and_goes_over_http()
    {
        await using var rig = await Rig.StartAsync();
        await Connected(rig);
        using var world = rig.ToWorld();

        // A GET (safe to repeat) waiting on the socket: sent again over HTTP at once when the socket closes.
        using var get = rig.WorldRequest(HttpMethod.Get, "/internal/world/slow");
        var started = DateTime.UtcNow;
        var pending = world.SendAsync(get);
        await rig.SlowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        rig.Hub.Disconnect(WorldId, "test");
        using var reply = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(4));
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Equal("", (await reply.Content.ReadFromJsonAsync<Echo>())!.Via);
        Assert.Equal(1, rig.Hub.Counters.Fallbacks);

        // A POST already on the wire (it may have run) fails like an unreachable world instead of running twice.
        await Connected(rig);
        Interlocked.Exchange(ref rig.SlowCalls, 0);
        using var post = rig.WorldRequest(HttpMethod.Post, "/internal/world/slow");
        post.Content = JsonContent.Create(new { });
        var sending = world.SendAsync(post);
        await Until(() => Volatile.Read(ref rig.SlowCalls) == 1, "the POST to arrive");
        rig.Hub.Disconnect(WorldId, "test");
        await Assert.ThrowsAsync<HttpRequestException>(() => sending.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, rig.Hub.Counters.Fallbacks);
    }

    [Fact]
    public async Task Reconnects_and_resyncs_after_the_socket_drops()
    {
        await using var rig = await Rig.StartAsync();
        await Connected(rig);
        await Until(() => Volatile.Read(ref rig.Resyncs) == 1, "the first resync");
        var first = rig.Slot.Current;

        rig.Hub.Disconnect(WorldId, "test");
        await Until(() => rig.Slot.Counters.Reconnects == 1 && rig.Slot.Current is { } now && now != first && rig.Hub.Connected(WorldId), "a reconnect");
        await Until(() => Volatile.Read(ref rig.Resyncs) == 2, "the resync after the reconnect");
        Assert.Equal(1, rig.Hub.Counters.Reconnects);
        Assert.Equal(2, rig.Hub.Counters.Connects);
        Assert.True(rig.Slot.Counters.Disconnects >= 1);

        // The tunnel works again on the new socket.
        using var realm = rig.ToRealm();
        Assert.Equal("socket", (await realm.GetFromJsonAsync<Echo>("internal/realm/echo"))!.Via);
    }

    [Fact]
    public async Task Events_reach_the_other_side()
    {
        await using var rig = await Rig.StartAsync();
        await Connected(rig);

        // Realm → world: an account change.
        var account = Guid.NewGuid();
        Assert.Equal(1, await rig.Hub.AccountChangedAsync(account));
        await Until(() => { lock (rig.Events) return rig.Events.Any(e => e.Name == LinkEvents.AccountChanged); }, "the account event");
        lock (rig.Events) Assert.Equal(account, rig.Events.Single(e => e.Name == LinkEvents.AccountChanged).DataAs<LinkAccount>()!.Account);
        Assert.Equal(1, await rig.Hub.StateChangedAsync());
        await Until(() => { lock (rig.Events) return rig.Events.Any(e => e.Name == LinkEvents.StateChanged); }, "the state event");

        // World → realm: the presence snapshot and status after connecting, then characters entering and leaving.
        await Until(() => rig.Hub.Presence.In(WorldId) is not null && rig.Hub.Status(WorldId) is { WorldId: WorldId }, "presence and status");
        var character = new WorldPresenceEntry(Guid.NewGuid(), account, "Aria", "Aria", 0, 1);
        Assert.True(await rig.Slot.Current!.PublishAsync(LinkEvents.PresenceEnter, character));
        await Until(() => rig.Hub.Presence.WorldOf(character.CharacterId) == WorldId, "the character to enter");
        Assert.Equal(character, rig.Hub.Presence.In(WorldId)![character.CharacterId]);
        Assert.True(await rig.Slot.Current!.PublishAsync(LinkEvents.PresenceLeave, new LinkLeft(character.CharacterId)));
        await Until(() => rig.Hub.Presence.WorldOf(character.CharacterId) is null, "the character to leave");

        // When the socket closes the realm forgets the pushed list (and polls the world again until it reconnects).
        Assert.True(await rig.Slot.Current!.PublishAsync(LinkEvents.PresenceEnter, character));
        await Until(() => rig.Hub.Presence.WorldOf(character.CharacterId) == WorldId, "the character to enter again");
        await rig.Client!.StopAsync(CancellationToken.None);
        await Until(() => !rig.Hub.Connected(WorldId) && rig.Hub.Presence.In(WorldId) is null, "the pushed list to go");
    }

    [Fact]
    public async Task A_silent_peer_is_dropped_by_the_heartbeat()
    {
        var timing = Fast with { Heartbeat = TimeSpan.FromMilliseconds(100), Silence = TimeSpan.FromMilliseconds(400) };
        await using var rig = await Rig.StartAsync(connect: false, timing: timing);
        // A world that connects and then never reads or answers.
        using var silent = new ClientWebSocket();
        silent.Options.SetRequestHeader(RealmLinkEndpoints.WorldHeader, "7");
        silent.Options.SetRequestHeader("Authorization", "Bearer good");
        silent.Options.KeepAliveInterval = TimeSpan.Zero;
        await silent.ConnectAsync(RealmSocketClient.SocketAddress(rig.RealmBase.ToString()), CancellationToken.None);
        await Until(() => rig.Hub.Connected(WorldId), "the silent socket to connect");
        await Until(() => rig.Hub.Counters.HeartbeatTimeouts == 1 && !rig.Hub.Connected(WorldId), "the heartbeat to drop it", 5000);
    }
}
