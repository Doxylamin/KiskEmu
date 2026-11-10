using System.Net;
using Aion.Application.Gameplay;
using Aion.Host.Realm;
using Aion.Host.Worlds;
using Aion.Protocol.Native;
using Aion.World;
using Aion.World.Tests;
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
using static Aion.World.Tests.GameSessionTestHelpers;

namespace Aion.Host.Tests;

/// <summary>
/// The account Quna wallet over the realm link (server/DATABASES.md): a world server spends through the realm socket and
/// the realm pushes balance changes back to the world the account plays on.
/// </summary>
public sealed class QunaLinkTests
{
    const int WorldId = 7;
    const long Now = 1792000000000L;
    const int PaidPass = 100151;

    static readonly LinkTiming Fast = new()
    {
        Heartbeat = TimeSpan.FromMilliseconds(200), Silence = TimeSpan.FromSeconds(3), Request = TimeSpan.FromSeconds(5),
        Connect = TimeSpan.FromSeconds(3), ReconnectBase = TimeSpan.FromMilliseconds(50), ReconnectMax = TimeSpan.FromMilliseconds(200),
        PresenceEvery = TimeSpan.FromMilliseconds(50), StatusEvery = TimeSpan.FromMilliseconds(100),
    };

    /// <summary>A realm with the wallet routes and a world server (its Quna push route) joined by the world's socket.</summary>
    sealed class Rig : IAsyncDisposable
    {
        public required WebApplication Realm, World;
        public required RealmSocketClient Client;
        public required LinkSlot Slot;
        public required MemoryQunaWallet Ledger;
        public required PushingQunaWallet RealmWallet;
        public required WorldPresence Presence;
        public required LinkedQunaWallet WorldWallet;
        public required Dictionary<Guid, int> Playing;

        public static async Task<Rig> StartAsync()
        {
            var hub = new RealmSocketHub(NullLogger<RealmSocketHub>.Instance, Fast);
            var ledger = new MemoryQunaWallet();
            var presence = new WorldPresence();
            var playing = new Dictionary<Guid, int>();
            var toWorld = new HttpClient(hub.Handler(new SocketsHttpHandler())) { Timeout = TimeSpan.FromSeconds(10) };
            Uri? worldBase = null;
            // The realm's push goes over the world's socket (WorldLinks does the same with its tunnel handler).
            var pushing = new PushingQunaWallet(ledger, account => playing.TryGetValue(account, out var w) ? w : null, async (world, change) =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(worldBase!, "/internal/world/quna/balance"))
                { Content = System.Net.Http.Json.JsonContent.Create(change) };
                request.Options.Set(LinkTunnelHandler.World, world);
                using var response = await toWorld.SendAsync(request);
                return response.IsSuccessStatusCode;
            }, NullLogger<PushingQunaWallet>.Instance);
            var realm = await AppAsync(services => services.AddSingleton<IQunaWallet>(pushing), app =>
            {
                app.UseWebSockets();
                app.Map(RealmSocketHub.Path, (HttpContext ctx) => hub.AcceptAsync(ctx, Authenticate));
                RealmLinkEndpoints.MapQuna(app.MapGroup("/internal/realm"), ctx => int.Parse(ctx.Request.Headers[RealmLinkEndpoints.WorldHeader].ToString()));
            });
            var world = await AppAsync(_ => { }, app =>
                app.MapPost("/internal/world/quna/balance", (QunaBalancePush input) => Results.Ok(new { value = WorldEndpoints.QunaPushed(presence, input) })));
            worldBase = Address(world);
            var options = new RealmLinkOptions { Url = Address(realm).ToString(), Token = "good" };
            var slot = new LinkSlot();
            var client = new RealmSocketClient(options, WorldId, worldBase, slot, new ServiceCollection().BuildServiceProvider(),
                NullLogger<RealmSocketClient>.Instance, Fast);
            await client.StartAsync(CancellationToken.None);
            var link = new RealmLinkClient(options, WorldId, NullLogger<RealmLinkClient>.Instance, slot);
            await Until(() => slot.Current is not null && hub.Connected(WorldId), "the realm socket");
            return new Rig
            {
                Realm = realm, World = world, Client = client, Slot = slot, Ledger = ledger, RealmWallet = pushing, Presence = presence,
                WorldWallet = new LinkedQunaWallet(link, NullLogger<LinkedQunaWallet>.Instance), Playing = playing,
            };
        }

        /// <summary>World 7 with the token "good".</summary>
        static Task<int?> Authenticate(HttpContext http) => Task.FromResult<int?>(
            http.Request.Headers.Authorization == "Bearer good" && http.Request.Headers[RealmLinkEndpoints.WorldHeader] == "7" ? WorldId : null);

        static async Task<WebApplication> AppAsync(Action<IServiceCollection> services, Action<WebApplication> map)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Logging.ClearProviders();
            builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(2));
            services(builder.Services);
            var app = builder.Build();
            map(app);
            await app.StartAsync();
            return app;
        }

        static Uri Address(WebApplication app) =>
            new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/') + "/");

        public async ValueTask DisposeAsync()
        {
            await Client.StopAsync(CancellationToken.None);
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

    /// <summary>A character of <paramref name="store"/>'s account in the world, its wallet the realm's over the link.</summary>
    static async Task<GameSession> EnterAsync(FakeCharacterStore store)
    {
        GameSession.ConfigureShopOptions(new ShopOptions { PremiumMode = ShopPremiumMode.Quna });
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 45, WorldId = WorldId }, new Random(3)) { Store = store };
        await session.HandleAsync(Login(), Now);
        await session.HandleAsync(LightCreate(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    static long RefreshedBalance(IEnumerable<byte[]> packets)
    {
        var r = new PacketReader(packets.Last(p => Op(p) == GameSession.RefreshCurrencyNt)) { Offset = 6 };
        r.Varint();
        r.I32();
        return r.I64();
    }

    [Fact]
    public async Task A_world_spends_through_the_realm_socket_and_the_realm_records_the_world()
    {
        await using var rig = await Rig.StartAsync();
        var store = new FakeCharacterStore(Guid.NewGuid()) { WalletOverride = rig.WorldWallet };
        rig.Ledger.Seed(store.AccountId, 5_000);
        var session = await EnterAsync(store);
        Assert.Equal(5_000, session.QunaBalance());
        var tunnelled = Interlocked.Read(ref rig.Slot.Counters.Tunnelled);

        var bought = await session.HandleAsync(new PacketWriter().U16(GameSession.BattlePassPurchaseRq).I32(PaidPass).ToArray(), Now + 5);
        Assert.Equal(0, BitConverter.ToUInt16(bought[0], 2));
        Assert.Equal(5_000 - GameSession.PaidPassUnlockQunaCost, RefreshedBalance(bought));
        var spend = rig.Ledger.Ledger[^1];
        Assert.Equal((QunaWalletRules.Purchase, -GameSession.PaidPassUnlockQunaCost, (int?)WorldId), (spend.Reason, spend.Delta, spend.WorldId));
        Assert.Equal(store.States[0].Summary.CharacterId, spend.CharacterId);
        Assert.True(Interlocked.Read(ref rig.Slot.Counters.Tunnelled) > tunnelled, "the spend rode the realm socket");

        // A repeat of the same request over the link returns the first result.
        var request = new QunaRequest(store.AccountId, spend.RequestId, GameSession.PaidPassUnlockQunaCost, QunaWalletRules.Purchase);
        var again = await rig.WorldWallet.SpendAsync(request);
        Assert.True(again.Repeated);
        Assert.Equal(spend.EntryId, again.EntryId);

        // Too little Quna: refused by the realm (an answer, not an error).
        var refused = await rig.WorldWallet.SpendAsync(request with { RequestId = "too-much", Amount = 1_000_000 });
        Assert.Equal(QunaWalletRules.Insufficient, refused.Error);
        Assert.Equal(5_000 - GameSession.PaidPassUnlockQunaCost, refused.Balance);
    }

    [Fact]
    public async Task A_world_cannot_add_quna_over_the_link()
    {
        await using var rig = await Rig.StartAsync();
        var account = Guid.NewGuid();
        rig.Ledger.Seed(account, 10);
        var entries = rig.Ledger.Ledger.Count;
        foreach (var reason in new[] { QunaWalletRules.Grant, QunaWalletRules.Staff, QunaWalletRules.Migration })
        {
            var granted = await rig.WorldWallet.GrantAsync(new QunaRequest(account, "mint:" + reason, 1_000_000, reason, WorldId));
            Assert.False(granted.Ok);
            Assert.Equal(QunaWalletRules.RealmOnly, granted.Error);
        }
        Assert.Equal(entries, rig.Ledger.Ledger.Count);
        Assert.Equal(10, (await rig.WorldWallet.BalanceAsync(account)).Balance);
    }

    [Fact]
    public async Task The_realm_pushes_a_balance_change_to_the_world_the_account_plays_on()
    {
        await using var rig = await Rig.StartAsync();
        var store = new FakeCharacterStore(Guid.NewGuid()) { WalletOverride = rig.WorldWallet };
        rig.Ledger.Seed(store.AccountId, 1_000);
        var session = await EnterAsync(store);
        rig.Presence.Enter("Buyer", session, store.AccountId);
        rig.Playing[store.AccountId] = WorldId;

        // A staff grant on the realm (the dashboard) or a purchase on another world.
        var granted = await rig.RealmWallet.GrantAsync(new QunaRequest(store.AccountId, "staff:1", 4_000, QunaWalletRules.Staff));
        Assert.Equal(5_000, granted.Balance);
        await rig.RealmWallet.LastPush;
        List<byte[]> shown = [];
        await Until(() =>
        {
            shown.AddRange(session.TickAsync(Now + 10).GetAwaiter().GetResult());
            return shown.Any(p => Op(p) == GameSession.RefreshCurrencyNt);
        }, "the pushed balance");
        Assert.Equal(5_000, RefreshedBalance(shown));
        Assert.Equal(5_000, session.QunaBalance());
        Assert.Contains(shown, p => Op(p) == 0x561b);

        // An account that plays nowhere gets no push.
        var other = Guid.NewGuid();
        rig.Ledger.Seed(other, 10);
        await rig.RealmWallet.GrantAsync(new QunaRequest(other, "staff:2", 5, QunaWalletRules.Staff));
        Assert.Equal(5_000, session.QunaBalance());
    }
}
