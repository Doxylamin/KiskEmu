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
/// The staff Accounts routes (Authentication/StaffAccountEndpoints.cs) over HTTP: the rank each route needs, the search
/// filter, the detail and the two actions, with an in-memory IAccountDirectory. The SQL (PostgresAccountDirectory,
/// migration 099) needs a database and is not run here.
/// </summary>
public sealed class StaffAccountTests : IAsyncDisposable
{
    static readonly Guid Gm = Guid.NewGuid(), Mod = Guid.NewGuid(), Admin = Guid.NewGuid(), Player = Guid.NewGuid();
    static readonly Dictionary<string, (Guid Account, string[] Roles)> Tokens = new()
    {
        ["gm"] = (Gm, ["gamemaster"]), ["mod"] = (Mod, ["moderator"]), ["admin"] = (Admin, ["admin"]), ["player"] = (Player, []),
    };
    readonly FakeDirectory directory = new();
    WebApplication? app;
    HttpClient? http;

    async Task<HttpClient> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(2));
        builder.Services.AddSingleton<IAccountAuthentication>(new FakeAuth());
        builder.Services.AddSingleton<IAccountDirectory>(directory);
        builder.Services.AddSingleton(Profiles.Create());
        builder.Services.AddRateLimiter(options => options.AddPolicy("admin", _ => RateLimitPartition.GetNoLimiter("test")));
        app = builder.Build();
        app.UseRateLimiter();
        app.MapStaffAccountEndpoints();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return http = new HttpClient { BaseAddress = new Uri(address) };
    }

    static HttpRequestMessage Request(HttpMethod method, string path, string? token)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task SearchNeedsAModeratorAndPassesTheFilter()
    {
        var client = await StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Request(HttpMethod.Get, "/api/admin/accounts", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Request(HttpMethod.Get, "/api/admin/accounts", "gm"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Request(HttpMethod.Get, "/api/admin/accounts", "player"))).StatusCode);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/api/admin/accounts?q=ali&filter=staff&page=2", "mod"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("ali", AccountFilter.Staff, 2, 50), directory.LastSearch);
        Assert.Equal(2, body.GetProperty("page").GetInt32());
        var first = body.GetProperty("accounts")[0];
        Assert.Equal("alice", first.GetProperty("username").GetString());
        Assert.Equal("Moderator", first.GetProperty("rankLabel").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("accounts")[1].GetProperty("rankLabel").ValueKind);
    }

    [Fact]
    public async Task DetailShowsTheAccountOrNotFound()
    {
        var client = await StartAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Request(HttpMethod.Get, $"/api/admin/accounts/{Guid.NewGuid()}", "mod"))).StatusCode);
        var body = await (await client.SendAsync(Request(HttpMethod.Get, $"/api/admin/accounts/{FakeDirectory.Alice}", "mod"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("totpEnabled").GetBoolean());
        Assert.Equal(3, body.GetProperty("sessions").GetInt32());
        Assert.Equal(1204, body.GetProperty("characters")[0].GetProperty("worldId").GetInt32());
        // No Quna wallet or staff tools on this host: those parts are empty, not an error.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("quna").ValueKind);
        Assert.Equal(0, body.GetProperty("sanctions").GetArrayLength());
    }

    [Fact]
    public async Task EndingSessionsNeedsAModerator()
    {
        var client = await StartAsync();
        var path = $"/api/admin/accounts/{FakeDirectory.Alice}/sessions/revoke";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Request(HttpMethod.Post, path, "gm"))).StatusCode);
        Assert.Empty(directory.Revoked);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Request(HttpMethod.Post, path, "mod"))).StatusCode);
        Assert.Equal([("mod", FakeDirectory.Alice)], directory.Revoked);
        // The database refuses (a staff target for a moderator, or the actor's own account): 403.
        directory.Refuse = true;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Request(HttpMethod.Post, path, "admin"))).StatusCode);
    }

    [Fact]
    public async Task TemporaryPasswordNeedsAnAdministrator()
    {
        var client = await StartAsync();
        var path = $"/api/admin/accounts/{FakeDirectory.Alice}/password";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Request(HttpMethod.Post, path, "mod"))).StatusCode);
        var response = await client.SendAsync(Request(HttpMethod.Post, path, "admin"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ABCD-EFGH-JKLM-NPQR", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("temporaryPassword").GetString());
        directory.Refuse = true;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Request(HttpMethod.Post, path, "admin"))).StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        http?.Dispose();
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
    }

    sealed class FakeAuth : IAccountAuthentication
    {
        public Task<VerifiedAccount?> AuthenticateAsync(string token, CancellationToken ct) =>
            Task.FromResult(Tokens.TryGetValue(token, out var t) ? new VerifiedAccount(t.Account) : null);
        public Task SignOutAsync(string token, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> RegisterAsync(string username, string password, CancellationToken ct) => Task.FromResult(false);
        public Task<AccountSession?> SignInAsync(string username, string password, CancellationToken ct) => Task.FromResult<AccountSession?>(null);
        public Task<GameTicket?> IssueGameTicketAsync(string sessionToken, int worldId, CancellationToken ct) => Task.FromResult<GameTicket?>(null);
    }

    /// <summary>IGameplayStore with only AdminProfileAsync (the roles of the signed-in account); anything else throws.</summary>
    public class Profiles : DispatchProxy
    {
        public static IGameplayStore Create() => Create<IGameplayStore, Profiles>();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name != nameof(IGameplayStore.AdminProfileAsync)) throw new NotSupportedException(method?.Name);
            var actor = (VerifiedAccount)args![0]!;
            var roles = Tokens.Values.First(t => t.Account == actor.AccountId).Roles;
            return Task.FromResult(new AdminProfile(roles, null));
        }
    }

    sealed class FakeDirectory : IAccountDirectory
    {
        public static readonly Guid Alice = Guid.NewGuid();
        public (string?, AccountFilter, int, int) LastSearch;
        public readonly List<(string, Guid)> Revoked = [];
        public bool Refuse;
        static readonly AccountSummary AliceSummary = new(Alice, "alice", DateTimeOffset.UtcNow.AddDays(-10), null, ["moderator"], DateTimeOffset.UtcNow);
        static readonly AccountSummary Bob = new(Guid.NewGuid(), "bob", DateTimeOffset.UtcNow.AddDays(-2), null, [], null);

        public Task<AccountSummary?> ProfileAsync(Guid accountId, CancellationToken ct) => Task.FromResult(accountId == Alice ? AliceSummary : null);

        public Task<AccountPage> SearchAsync(string? query, AccountFilter filter, int page, int pageSize, CancellationToken ct)
        {
            LastSearch = (query, filter, page, pageSize);
            return Task.FromResult(new AccountPage([AliceSummary, Bob], 2));
        }

        public Task<AccountDetail?> DetailAsync(Guid accountId, CancellationToken ct) => Task.FromResult(accountId == Alice
            ? new AccountDetail(AliceSummary, true, 3, 1, [new AccountCharacter(Guid.NewGuid(), 1204, "Ally", 30, 1, null)])
            : null);

        public Task<bool> RevokeSessionsAsync(string staffSession, Guid target, CancellationToken ct)
        {
            if (Refuse) return Task.FromResult(false);
            Revoked.Add((staffSession, target));
            return Task.FromResult(true);
        }

        public Task<string?> ResetPasswordAsync(string staffSession, Guid target, CancellationToken ct) =>
            Task.FromResult(Refuse ? null : "ABCD-EFGH-JKLM-NPQR");
    }
}
