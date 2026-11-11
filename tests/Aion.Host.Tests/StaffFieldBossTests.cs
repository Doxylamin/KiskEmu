using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Threading.RateLimiting;
using Aion.Application.Authentication;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Aion.Host.Authentication;
using Aion.World;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aion.Host.Tests;

/// <summary>
/// The staff field boss routes (Authentication/StaffFieldBossEndpoints.cs) over HTTP: the list, respawn now (ManageMonsters),
/// the respawn range and its reset (EditFieldBossTimers), each audited.
/// </summary>
public sealed class StaffFieldBossTests : IAsyncDisposable
{
    const long Now = 1_793_000_000_000;
    const int Dartan = 101022;
    static readonly Dictionary<string, (Guid Id, string? Role)> Tokens = new()
    {
        ["gm"] = (Guid.NewGuid(), "gamemaster"), ["mod"] = (Guid.NewGuid(), "moderator"), ["admin"] = (Guid.NewGuid(), "admin"), ["player"] = (Guid.NewGuid(), null),
    };

    readonly SharedWorld world = new(Aion.World.GameData.Embedded) { Clock = () => Now };
    readonly List<(string Action, string Details)> audit = [];
    WebApplication? app;

    sealed class Auth : IAccountAuthentication
    {
        public Task<VerifiedAccount?> AuthenticateAsync(string token, CancellationToken ct) =>
            Task.FromResult(Tokens.TryGetValue(token, out var t) ? new VerifiedAccount(t.Id) : null);
        public Task<bool> RegisterAsync(string username, string password, CancellationToken ct) => throw new NotSupportedException();
        public Task<AccountSession?> SignInAsync(string username, string password, CancellationToken ct) => throw new NotSupportedException();
        public Task SignOutAsync(string token, CancellationToken ct) => throw new NotSupportedException();
        public Task<GameTicket?> IssueGameTicketAsync(string sessionToken, int worldId, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>IGameplayStore with only the staff profile and the audit record.</summary>
    public class Store : DispatchProxy
    {
        internal List<(string, string)> Audit = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method?.Name)
            {
                case nameof(IGameplayStore.AdminProfileAsync):
                    var id = ((VerifiedAccount)args![0]!).AccountId;
                    var role = Tokens.Values.First(t => t.Id == id).Role;
                    return Task.FromResult(new AdminProfile(role is null ? [] : [role], null));
                case nameof(IGameplayStore.AdminRecordAsync):
                    Audit.Add(((string)args![1]!, JsonSerializer.Serialize(args[3])));
                    return Task.CompletedTask;
                default: throw new NotSupportedException(method?.Name);
            }
        }
    }

    async Task<HttpClient> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(2));
        var store = DispatchProxy.Create<IGameplayStore, Store>();
        ((Store)(object)store).Audit = audit;
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<IAccountAuthentication>(new Auth());
        builder.Services.AddSingleton(world);
        builder.Services.AddRateLimiter(options => options.AddPolicy("admin", _ => RateLimitPartition.GetNoLimiter("test")));
        app = builder.Build();
        app.UseRateLimiter();
        StaffToolsEndpoints.MapFieldBosses(StaffToolsEndpoints.AdminGroup(app));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new HttpClient { BaseAddress = new Uri(address) };
    }

    static HttpRequestMessage Request(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    static JsonElement Boss(JsonElement list, int code) => list.GetProperty("bosses").EnumerateArray().First(b => b.GetProperty("code").GetInt32() == code);

    [Fact]
    public async Task StaffSeeEveryFieldBossWithItsStateAndTimer()
    {
        var client = await StartAsync();
        var map = world.Map(1010);
        var boss = map.FieldBossTarget(Dartan)!;
        map.ClearArea(boss.Position.X, boss.Position.Y, 100, Now, Aion.World.GameSession.RespawnMs);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/api/admin/field-bosses", "gm"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(85, list.GetProperty("bosses").GetArrayLength());
        var dartan = Boss(list, Dartan);
        Assert.Equal("dead", dartan.GetProperty("status").GetString());
        Assert.Equal(1010, dartan.GetProperty("mapId").GetInt32());
        Assert.Equal(Now, dartan.GetProperty("killedAt").GetInt64());
        Assert.Equal(map.RespawnAt[boss.Key], dartan.GetProperty("respawnAt").GetInt64());
        Assert.Equal((360, 360), (dartan.GetProperty("minMinutes").GetInt32(), dartan.GetProperty("maxMinutes").GetInt32()));
        Assert.False(dartan.GetProperty("customTimer").GetBoolean());
        Assert.Equal("alive", Boss(list, 101001).GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, dartan.GetProperty("schedule").ValueKind);
        Assert.Equal("Sun, Fri 21:00", Boss(list, 2003).GetProperty("schedule").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Request(HttpMethod.Get, "/api/admin/field-bosses", "nobody"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Request(HttpMethod.Get, "/api/admin/field-bosses", "player"))).StatusCode);
    }

    [Fact]
    public async Task ModeratorsRespawnABossNowAndItIsAudited()
    {
        var client = await StartAsync();
        var map = world.Map(1010);
        var boss = map.FieldBossTarget(Dartan)!;
        map.ClearArea(boss.Position.X, boss.Position.Y, 100, Now, Aion.World.GameSession.RespawnMs);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Request(HttpMethod.Post, $"/api/admin/field-bosses/{Dartan}/respawn", "gm"))).StatusCode);
        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/admin/field-bosses/{Dartan}/respawn", "mod"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("due", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal(Now, map.RespawnAt[boss.Key]);
        Assert.Null(world.FieldBosses.DeadUntil(Dartan, Now));
        var (action, details) = Assert.Single(audit);
        Assert.Equal("staff_field_boss_respawn", action);
        Assert.Contains($"\"code\":{Dartan}", details);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Request(HttpMethod.Post, "/api/admin/field-bosses/999/respawn", "mod"))).StatusCode);
    }

    [Fact]
    public async Task AdministratorsSetAndResetTheRespawnRange()
    {
        var client = await StartAsync();
        var path = $"/api/admin/field-bosses/{Dartan}/timer";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Request(HttpMethod.Put, path, "mod", new { minMinutes = 60, maxMinutes = 90 }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Request(HttpMethod.Put, path, "admin", new { minMinutes = 90, maxMinutes = 60 }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Request(HttpMethod.Put, path, "admin", new { minMinutes = 60 }))).StatusCode);

        var set = await client.SendAsync(Request(HttpMethod.Put, path, "admin", new { minMinutes = 60, maxMinutes = 90 }));
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var view = await set.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((60, 90, true), (view.GetProperty("minMinutes").GetInt32(), view.GetProperty("maxMinutes").GetInt32(), view.GetProperty("customTimer").GetBoolean()));
        Assert.Equal((60, 90), world.FieldBosses.Range(Dartan));

        var reset = await client.SendAsync(Request(HttpMethod.Delete, path, "admin"));
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal((360, 360), world.FieldBosses.Range(Dartan));
        Assert.Equal(["staff_field_boss_timer", "staff_field_boss_timer"], audit.Select(a => a.Action));
        Assert.Contains("\"reset\":true", audit[1].Details);
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null) await app.DisposeAsync();
    }
}
