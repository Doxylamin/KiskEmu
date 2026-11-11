using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Aion.Application.Authentication;
using Aion.Application.Characters;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Aion.Core.Characters;
using Aion.Host.Authentication;
using Aion.Protocol.Native;
using Aion.World;
using Aion.World.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using static Aion.World.Tests.GameSessionTestHelpers;

namespace Aion.Host.Tests;

/// <summary>The account site's character page (GET /api/characters/{id}/details): own characters only, stored and live.</summary>
public sealed class CharacterPageTests : IAsyncDisposable
{
    const long Now = 1792000000000L;
    static readonly Guid Mine = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid Theirs = Guid.Parse("22222222-2222-4222-8222-222222222222");
    WebApplication? app;

    /// <summary>Bearer "mine" and "theirs" sign in as two accounts.</summary>
    sealed class TwoAccounts : IAccountAuthentication
    {
        public Task<VerifiedAccount?> AuthenticateAsync(string token, CancellationToken ct) => Task.FromResult(token switch
        {
            "mine" => new VerifiedAccount(Mine),
            "theirs" => new VerifiedAccount(Theirs),
            _ => null,
        });
        public Task<bool> RegisterAsync(string username, string password, CancellationToken ct) => Task.FromResult(false);
        public Task<AccountSession?> SignInAsync(string username, string password, CancellationToken ct) => Task.FromResult<AccountSession?>(null);
        public Task SignOutAsync(string token, CancellationToken ct) => Task.CompletedTask;
        public Task<GameTicket?> IssueGameTicketAsync(string sessionToken, int worldId, CancellationToken ct) => Task.FromResult<GameTicket?>(null);
    }

    /// <summary>The stored characters; every query filters by account, as the database's does.</summary>
    sealed class Characters(IEnumerable<CharacterState> states, bool stored = true) : ICharacterRepository, ICharacterStateRepository
    {
        readonly List<CharacterState> all = [.. states];
        public Task<IReadOnlyList<CharacterSummary>> ListOwnedAsync(Guid accountId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CharacterSummary>>([.. all.Where(s => s.Summary.AccountId == accountId).Select(s => s.Summary)]);
        public Task<CharacterSummary?> FindOwnedAsync(Guid accountId, Guid characterId, CancellationToken ct) =>
            Task.FromResult(all.FirstOrDefault(s => s.Summary.AccountId == accountId && s.Summary.CharacterId == characterId)?.Summary);
        public Task<CharacterState?> LoadOwnedAsync(Guid accountId, Guid characterId, CancellationToken ct) =>
            Task.FromResult(stored ? all.FirstOrDefault(s => s.Summary.AccountId == accountId && s.Summary.CharacterId == characterId) : null);
        public Task<CharacterState?> CreateOwnedAsync(Guid accountId, NewCharacter character, CancellationToken ct) => throw new NotSupportedException();
        public Task<long?> SaveOwnedAsync(Guid accountId, Guid characterId, long expectedRevision, CharacterProgress progress, CancellationToken ct) => throw new NotSupportedException();
        public Task<long?> SavePositionOwnedAsync(Guid accountId, Guid characterId, long expectedRevision, int mapId, JsonElement position, CancellationToken ct) => throw new NotSupportedException();
        public Task<long?> SaveInstanceReturnOwnedAsync(Guid accountId, Guid characterId, long expectedRevision, int? returnMap, double? returnX, double? returnY, double? returnZ, CancellationToken ct) => throw new NotSupportedException();
    }

    sealed class Presence(CurrentlyPlaying? playing) : IAccountPresence
    {
        public Task<CurrentlyPlaying?> PlayingAsync(Guid accountId, CancellationToken ct) => Task.FromResult(accountId == Mine ? playing : null);
        public Task<bool> ShowsPlayingAsync(Guid accountId, CancellationToken ct) => Task.FromResult(true);
        public Task SetShowsPlayingAsync(Guid accountId, bool show, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>A gameplay store that knows one legion (GuildOfAsync) and nothing else.</summary>
    public class LegionStore : DispatchProxy
    {
        public GuildView? Guild;
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method?.Name == nameof(IGameplayStore.GuildOfAsync) ? Task.FromResult(Guild) : throw new NotSupportedException(method?.Name);
        public static IGameplayStore With(GuildView guild)
        {
            var store = Create<IGameplayStore, LegionStore>();
            ((LegionStore)(object)store).Guild = guild;
            return store;
        }
    }

    static CharacterState Stored(Guid account, string name, int mapId = 1010) => new(
        new CharacterSummary(Guid.NewGuid(), account, 1204, name, 12, Race: 1, LastPlayedUnixMs: Now - 3_600_000),
        CreationId: 1, Race: 1, Gender: 1, Appearance: [],
        new CharacterProgress(12, 3400, mapId, JsonSerializer.SerializeToElement(new { x = 1234.4, y = -5678.6, z = 90.0 }),
            JsonSerializer.SerializeToElement(new { })),
        Revision: 1, RegisteredUnixMs: Now - 86_400_000, LastLoginUnixMs: Now - 7_200_000, LastLogoutUnixMs: Now - 3_600_000);

    async Task<HttpClient> StartAsync(Action<IServiceCollection> services)
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(2));
        builder.Services.AddSingleton<IAccountAuthentication, TwoAccounts>();
        builder.Services.AddSingleton<CharacterService>();
        services(builder.Services);
        app = builder.Build();
        app.MapCharacterPageEndpoints();
        await app.StartAsync();
        return new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    static void Store(IServiceCollection services, Characters characters)
    {
        services.AddSingleton<ICharacterRepository>(characters);
        services.AddSingleton<ICharacterStateRepository>(characters);
    }

    static async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(HttpClient client, string token, Guid id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/characters/{id:D}/details");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    [Fact]
    public async Task Another_accounts_character_is_refused_like_a_missing_one()
    {
        var mine = Stored(Mine, "Ayla");
        var theirs = Stored(Theirs, "Borin");
        var client = await StartAsync(services => Store(services, new Characters([mine, theirs])));

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "mine", mine.Summary.CharacterId)).Status);
        var refused = await GetAsync(client, "mine", theirs.Summary.CharacterId);
        Assert.Equal(HttpStatusCode.NotFound, refused.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(client, "mine", Guid.NewGuid())).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, "nobody", mine.Summary.CharacterId)).Status);
        // The owner still sees it.
        Assert.Equal("Borin", (await GetAsync(client, "theirs", theirs.Summary.CharacterId)).Body.GetProperty("name").GetString());
    }

    [Fact]
    public async Task An_offline_character_shows_its_stored_class_legion_and_last_saved_place()
    {
        var mine = Stored(Mine, "Ayla");
        var guild = new GuildView(7, "Dawnguard", 3, 0, 0, 0, "", "", "", DateTimeOffset.UnixEpoch,
            [new GuildMember(mine.Summary.CharacterId, "Ayla", 12, 0, "", 1, DateTimeOffset.UnixEpoch), new GuildMember(Guid.NewGuid(), "Cid", 10, 2, "", 2, DateTimeOffset.UnixEpoch)]);
        var client = await StartAsync(services =>
        {
            Store(services, new Characters([mine]));
            services.AddSingleton(LegionStore.With(guild));
        });

        var (status, body) = await GetAsync(client, "mine", mine.Summary.CharacterId);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("Ayla", 12, "Gladiator", 1204), (body.GetProperty("name").GetString(), body.GetProperty("level").GetInt32(),
            body.GetProperty("className").GetString(), body.GetProperty("worldId").GetInt32()));
        Assert.False(body.GetProperty("online").GetBoolean());
        Assert.Equal("world", body.GetProperty("source").GetString());
        var location = body.GetProperty("location");
        Assert.Equal((1010, "Verteron", 1234.0, -5679.0), (location.GetProperty("mapId").GetInt32(), location.GetProperty("mapName").GetString(),
            location.GetProperty("x").GetDouble(), location.GetProperty("y").GetDouble()));
        Assert.False(body.GetProperty("locationLive").GetBoolean());
        var legion = body.GetProperty("legion");
        Assert.Equal(("Dawnguard", 2, true), (legion.GetProperty("name").GetString(), legion.GetProperty("members").GetInt32(), legion.GetProperty("leader").GetBoolean()));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(Now - 3_600_000), body.GetProperty("lastPlayedAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("combatPower").ValueKind);
    }

    [Fact]
    public async Task Without_the_worlds_data_the_realms_index_and_presence_report_are_shown()
    {
        var mine = Stored(Mine, "Ayla");
        var playing = new CurrentlyPlaying(1204, new PlayingCharacter(mine.Summary.CharacterId, "Ayla", 14, "Gladiator", 1, 1110),
            DateTimeOffset.FromUnixTimeMilliseconds(Now));
        var client = await StartAsync(services =>
        {
            Store(services, new Characters([mine], stored: false));
            services.AddSingleton<IAccountPresence>(new Presence(playing));
        });

        var (status, body) = await GetAsync(client, "mine", mine.Summary.CharacterId);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("index", body.GetProperty("source").GetString());
        Assert.True(body.GetProperty("online").GetBoolean());
        Assert.Equal((14, "Gladiator"), (body.GetProperty("level").GetInt32(), body.GetProperty("className").GetString()));
        Assert.Equal("Altgard", body.GetProperty("location").GetProperty("mapName").GetString());
        Assert.True(body.GetProperty("locationLive").GetBoolean());
    }

    [Fact]
    public async Task A_character_in_the_world_shows_its_live_level_combat_power_and_place()
    {
        var store = new FakeCharacterStore(Mine);
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 45, WorldId = 1204 }, new Random(3)) { Store = store };
        await session.HandleAsync(Login(), Now);
        await session.HandleAsync(LightCreate(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        Assert.True(session.InWorld);
        var presence = new WorldPresence();
        presence.Enter(session.CharacterName!, session, Mine);
        var state = store.States[0];
        var client = await StartAsync(services =>
        {
            Store(services, new Characters([state]));
            services.AddSingleton(presence);
        });

        var (status, body) = await GetAsync(client, "mine", state.Summary.CharacterId);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("online").GetBoolean());
        Assert.Equal(session.Level, body.GetProperty("level").GetInt32());
        Assert.Equal(session.CombatPower, body.GetProperty("combatPower").GetInt64());
        Assert.Equal(session.MapId, body.GetProperty("location").GetProperty("mapId").GetInt32());
        Assert.Equal(MapNames.Of(session.MapId!.Value), body.GetProperty("location").GetProperty("mapName").GetString());
        Assert.True(body.GetProperty("locationLive").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("inWorldSince").ValueKind);
        // Another account's session on the same character id is not shown live to this account.
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(client, "theirs", state.Summary.CharacterId)).Status);
    }

    [Theory]
    [InlineData("L_A_VerteronFortressRuin_Subzone", "Verteron Fortress Ruin")]
    [InlineData("Battlefield_A_1_BlueRespawn_Subzone", "Battlefield A 1 Blue Respawn")]
    [InlineData("D_Starter_Region_03_Subzone", "Starter Region 03")]
    public void Area_names_read_as_words(string subzone, string readable) => Assert.Equal(readable, MapNames.Readable(subzone));

    [Fact]
    public void Layers_take_their_base_maps_region_and_unknown_maps_their_id()
    {
        Assert.Equal("Poeta", MapNames.Of(100001)); // a Poeta layer (map-bases.json: 100001 → 1000)
        Assert.Equal("Map 987654", MapNames.Of(987654));
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null) await app.DisposeAsync();
    }
}
