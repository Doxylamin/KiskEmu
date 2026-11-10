using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.RateLimiting;
using Aion.Application.Authentication;
using Aion.Core.Accounts;
using Aion.Host.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aion.Host.Tests;

/// <summary>
/// The staff sign-in routes and the /api/admin gate (Authentication/StaffSessionEndpoints.cs) over HTTP, with an in-memory
/// IStaffSessions. The Postgres side (sessions.kind, challenge purposes, migration 098) needs a database and is not run here.
/// </summary>
public sealed class StaffSessionTests : IAsyncDisposable
{
    const string StaffToken = "staff-token", PlayerToken = "player-token";
    readonly FakeStaff staff = new();
    WebApplication? app;
    HttpClient? http;

    async Task<HttpClient> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(2));
        builder.Services.AddSingleton<IStaffSessions>(staff);
        builder.Services.AddSingleton<IAccountAuthentication>(staff);
        builder.Services.AddRateLimiter(options =>
        {
            foreach (var policy in new[] { "account-entry", "totp-verify" })
                options.AddPolicy(policy, _ => RateLimitPartition.GetNoLimiter("test"));
        });
        app = builder.Build();
        app.UseRateLimiter();
        app.UseStaffSessions();
        app.MapStaffSessionEndpoints();
        app.MapGet("/api/admin/me", () => Results.Ok(new { ok = true }));
        app.MapDelete("/api/admin/wardrobe-access", () => Results.NoContent());
        app.MapGet("/api/characters", () => Results.Ok(Array.Empty<int>()));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return http = new HttpClient { BaseAddress = new Uri(address) };
    }

    static HttpRequestMessage Get(string path, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task AdminRoutesNeedAStaffSession()
    {
        var client = await StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/api/admin/me", StaffToken))).StatusCode);
        foreach (var token in new[] { PlayerToken, null, "nonsense" })
        {
            var response = await client.SendAsync(Get("/api/admin/me", token));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("staff_session_required", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        }
        // Not staff routes: untouched. The sign-in and dropping the wardrobe pass need no session.
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/api/characters", PlayerToken))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/admin/wardrobe-access")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/admin/session", new { username = "x", password = "wrong" })).StatusCode);
    }

    [Fact]
    public async Task PlayerAccountsAreRefused()
    {
        var client = await StartAsync();
        var response = await client.PostAsJsonAsync("/api/admin/session", new { username = "player", password = FakeStaff.Password });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task EnrolledStaffSignInWithACode()
    {
        var client = await StartAsync();
        var begin = await (await client.PostAsJsonAsync("/api/admin/session", new { username = "gm", password = FakeStaff.Password, remember = true }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("code", begin.GetProperty("step").GetString());
        var challenge = begin.GetProperty("challengeToken").GetString();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/admin/session/totp", new { challengeToken = challenge, code = "000000" })).StatusCode);
        var done = await client.PostAsJsonAsync("/api/admin/session/totp", new { challengeToken = challenge, code = FakeStaff.Code });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var token = (await done.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        Assert.True(staff.Remembered);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/api/admin/me", token))).StatusCode);
        // An enrolment route does not take a code challenge.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/admin/session/enroll", new { challengeToken = challenge })).StatusCode);
        // Signing out ends the staff session.
        var signOut = new HttpRequestMessage(HttpMethod.Delete, "/api/admin/session");
        signOut.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(signOut)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get("/api/admin/me", token))).StatusCode);
    }

    [Fact]
    public async Task StaffWithoutAnAuthenticatorSetOneUpFirst()
    {
        var client = await StartAsync();
        var begin = await (await client.PostAsJsonAsync("/api/admin/session", new { username = "new-gm", password = FakeStaff.Password }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("enroll", begin.GetProperty("step").GetString());
        var challenge = begin.GetProperty("challengeToken").GetString();
        // The code step does not take an enrolment challenge: there is no session before the authenticator works.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/admin/session/totp", new { challengeToken = challenge, code = FakeStaff.Code })).StatusCode);
        var key = await (await client.PostAsJsonAsync("/api/admin/session/enroll", new { challengeToken = challenge })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("SECRET", key.GetProperty("secret").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/admin/session/enroll/confirm", new { challengeToken = challenge, code = "111111" })).StatusCode);
        var done = await (await client.PostAsJsonAsync("/api/admin/session/enroll/confirm", new { challengeToken = challenge, code = FakeStaff.Code }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, done.GetProperty("recoveryCodes").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/api/admin/me", done.GetProperty("token").GetString()))).StatusCode);
        // The challenge is used up.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/admin/session/enroll/confirm", new { challengeToken = challenge, code = FakeStaff.Code })).StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        http?.Dispose();
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
    }

    /// <summary>gm: staff with an authenticator; new-gm: staff without one; player: no staff role.</summary>
    sealed class FakeStaff : IStaffSessions, IAccountAuthentication
    {
        public const string Password = "correct horse", Code = "123456";
        readonly Dictionary<string, (Guid Account, string Purpose)> challenges = [];
        readonly HashSet<string> staffTokens = [StaffToken];
        readonly Dictionary<string, Guid> accounts = new() { ["gm"] = Guid.NewGuid(), ["new-gm"] = Guid.NewGuid(), ["player"] = Guid.NewGuid() };
        public bool Remembered;
        static readonly Guid Someone = Guid.NewGuid();

        public Task<StaffSignIn> BeginAsync(string username, string password, bool remember, CancellationToken ct)
        {
            if (password != Password || !accounts.TryGetValue(username, out var account)) return Task.FromResult(new StaffSignIn(StaffSignInStep.Failed));
            if (username == "player") return Task.FromResult(new StaffSignIn(StaffSignInStep.NotStaff));
            Remembered = remember;
            var token = Guid.NewGuid().ToString("N");
            var purpose = username == "gm" ? "code" : "enroll";
            challenges[token] = (account, purpose);
            return Task.FromResult(new StaffSignIn(purpose == "code" ? StaffSignInStep.Code : StaffSignInStep.Enroll, token, DateTimeOffset.UtcNow.AddMinutes(5)));
        }

        AccountSession Session(Guid account)
        {
            var token = Guid.NewGuid().ToString("N");
            staffTokens.Add(token);
            return new(token, account, DateTimeOffset.UtcNow.AddHours(12));
        }

        public Task<AccountSession?> CompleteAsync(string challengeToken, string? code, string? recoveryCode, CancellationToken ct)
        {
            if (!challenges.TryGetValue(challengeToken, out var c) || c.Purpose != "code" || code != Code) return Task.FromResult<AccountSession?>(null);
            challenges.Remove(challengeToken);
            return Task.FromResult<AccountSession?>(Session(c.Account));
        }

        public Task<TotpEnrollment?> BeginEnrollmentAsync(string challengeToken, CancellationToken ct) =>
            Task.FromResult(challenges.TryGetValue(challengeToken, out var c) && c.Purpose == "enroll"
                ? new TotpEnrollment("SECRET", "otpauth://totp/x?secret=SECRET", []) : null);

        public Task<(AccountSession Session, string[] RecoveryCodes)?> ConfirmEnrollmentAsync(string challengeToken, string code, CancellationToken ct)
        {
            if (!challenges.TryGetValue(challengeToken, out var c) || c.Purpose != "enroll" || code != Code)
                return Task.FromResult<(AccountSession, string[])?>(null);
            challenges.Remove(challengeToken);
            return Task.FromResult<(AccountSession, string[])?>((Session(c.Account), ["AAAA-BBBB", "CCCC-DDDD"]));
        }

        Task<VerifiedAccount?> IStaffSessions.AuthenticateAsync(string token, CancellationToken ct) =>
            Task.FromResult(staffTokens.Contains(token) ? new VerifiedAccount(Someone) : null);

        // The player API's sign-in: any session (player or staff).
        Task<VerifiedAccount?> IAccountAuthentication.AuthenticateAsync(string token, CancellationToken ct) =>
            Task.FromResult(token == PlayerToken || staffTokens.Contains(token) ? new VerifiedAccount(Someone) : null);
        public Task SignOutAsync(string token, CancellationToken ct) { staffTokens.Remove(token); return Task.CompletedTask; }
        public Task<bool> RegisterAsync(string username, string password, CancellationToken ct) => Task.FromResult(false);
        public Task<AccountSession?> SignInAsync(string username, string password, CancellationToken ct) => Task.FromResult<AccountSession?>(null);
        public Task<GameTicket?> IssueGameTicketAsync(string sessionToken, int worldId, CancellationToken ct) => Task.FromResult<GameTicket?>(null);
    }
}
