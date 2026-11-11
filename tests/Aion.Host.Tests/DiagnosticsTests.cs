using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aion.Application.Authentication;
using Aion.Core.Accounts;
using Aion.Core.Security;
using Aion.Host.Diagnostics;
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
/// The staff dashboard's diagnostics card: the unanswered-request collector's bounds, the warning/error counter, and the
/// /api/admin/diagnostics permission check (administrators only) over HTTP.
/// </summary>
public sealed class DiagnosticsTests : IAsyncDisposable
{
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    [Fact]
    public void UnansweredKeepsCountsTimesAndTheLastBodiesNewestFirst()
    {
        var clock = new Clock();
        var collector = new UnansweredRequests(clock);
        var first = clock.Now;
        for (var i = 0; i < UnansweredRequests.MaxBodies + 3; i++)
        {
            collector.Record(0x3a01, [0x01, 0x3a, (byte)i]);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        var entry = Assert.Single(collector.Read().Opcodes);
        Assert.Equal(0x3a01, entry.Opcode);
        Assert.Equal(UnansweredRequests.MaxBodies + 3, entry.Count);
        Assert.Equal(first, entry.FirstSeen);
        Assert.Equal(first.AddSeconds(UnansweredRequests.MaxBodies + 2), entry.LastSeen);
        Assert.Equal(UnansweredRequests.MaxBodies, entry.Bodies.Count);
        Assert.Equal($"013A{UnansweredRequests.MaxBodies + 2:X2}", entry.Bodies[0].Hex);
        Assert.Equal("013A03", entry.Bodies[^1].Hex);
    }

    [Fact]
    public void UnansweredCutsLongBodiesButKeepsTheirLength()
    {
        var collector = new UnansweredRequests();
        collector.Record(0x1234, new byte[1000]);
        var body = Assert.Single(Assert.Single(collector.Read().Opcodes).Bodies);
        Assert.Equal(1000, body.Length);
        Assert.Equal(UnansweredRequests.MaxBodyBytes * 2, body.Hex.Length);
    }

    [Fact]
    public void UnansweredDropsTheLeastRecentlySeenOpcodeWhenFull()
    {
        var clock = new Clock();
        var collector = new UnansweredRequests(clock);
        for (var op = 0; op < UnansweredRequests.MaxOpcodes; op++)
        {
            collector.Record(op, [1]);
            clock.Advance(TimeSpan.FromMilliseconds(1));
        }
        collector.Record(0, [2]);   // opcode 0 is now the most recent; opcode 1 the oldest
        clock.Advance(TimeSpan.FromMilliseconds(1));
        collector.Record(0xffff, [3]);
        var read = collector.Read();
        Assert.Equal(UnansweredRequests.MaxOpcodes, read.Opcodes.Count);
        Assert.Equal(1, read.Evicted);
        Assert.DoesNotContain(read.Opcodes, e => e.Opcode == 1);
        Assert.Contains(read.Opcodes, e => e.Opcode == 0 && e.Count == 2);
        Assert.Equal(0xffff, read.Opcodes[0].Opcode);

        Assert.Equal(UnansweredRequests.MaxOpcodes, collector.Clear());
        Assert.Empty(collector.Read().Opcodes);
        Assert.Equal(0, collector.Read().Evicted);
    }

    [Fact]
    public void LogCounterCountsWarningsAndErrorsByWindow()
    {
        var clock = new Clock();
        var counter = new LogCounter(clock);
        var logger = counter.CreateLogger("Test");
        logger.LogInformation("ignored");
        logger.LogWarning("old warning");
        clock.Advance(TimeSpan.FromMinutes(10));
        logger.LogWarning("new warning");
        logger.LogError(new InvalidOperationException("boom"), "new error {Value}", 7);
        var counts = counter.Read();
        Assert.Equal((1, 1, 2, 1), (counts.Warnings5m, counts.Errors5m, counts.Warnings60m, counts.Errors60m));
        Assert.Equal("new error 7 (InvalidOperationException: boom)", counts.Recent[0].Message);
        Assert.Equal("error", counts.Recent[0].Level);
        clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal((0, 0, 0, 0), (counter.Read().Warnings5m, counter.Read().Errors5m, counter.Read().Warnings60m, counter.Read().Errors60m));
        for (var i = 0; i < LogCounter.MaxRecent + 5; i++) logger.LogWarning(new string('x', 1000));
        Assert.Equal(LogCounter.MaxRecent, counter.Read().Recent.Count);
        Assert.Equal(LogCounter.MaxMessage + 1, counter.Read().Recent[0].Message.Length);
    }

    [Fact]
    public void DiagnosticsAreForAdministratorsOnly()
    {
        Assert.Equal(StaffRank.Administrator, StaffAccess.Required(StaffPermission.ViewDiagnostics));
        Assert.Contains("viewDiagnostics", StaffAccess.PermissionNames(StaffRank.Administrator));
        Assert.DoesNotContain("viewDiagnostics", StaffAccess.PermissionNames(StaffRank.Moderator));
    }

    // Tokens: one per role, plus a signed-in player.
    static readonly Dictionary<string, (Guid Account, string[] Roles)> Tokens = new()
    {
        ["admin"] = (Guid.NewGuid(), [StaffAccess.AdministratorRole]),
        ["moderator"] = (Guid.NewGuid(), [StaffAccess.ModeratorRole]),
        ["gamemaster"] = (Guid.NewGuid(), [StaffAccess.GamemasterRole]),
        ["player"] = (Guid.NewGuid(), []),
    };

    sealed class FakeAuth : IAccountAuthentication
    {
        public Task<VerifiedAccount?> AuthenticateAsync(string token, CancellationToken ct) =>
            Task.FromResult(Tokens.TryGetValue(token, out var t) ? new VerifiedAccount(t.Account) : null);
        public Task<bool> RegisterAsync(string username, string password, CancellationToken ct) => throw new NotSupportedException();
        public Task<AccountSession?> SignInAsync(string username, string password, CancellationToken ct) => throw new NotSupportedException();
        public Task SignOutAsync(string token, CancellationToken ct) => throw new NotSupportedException();
        public Task<GameTicket?> IssueGameTicketAsync(string sessionToken, int worldId, CancellationToken ct) => throw new NotSupportedException();
    }

    WebApplication? app;
    readonly UnansweredRequests collector = new();

    async Task<HttpClient> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(2));
        builder.Services.AddSingleton<IAccountAuthentication>(new FakeAuth());
        builder.Services.AddSingleton(collector);
        builder.Services.AddSingleton(new LogCounter());
        app = builder.Build();
        DiagnosticsEndpoints.Map(app, "world", (_, account, _) =>
            Task.FromResult<IReadOnlyList<string>>(Tokens.Values.First(t => t.Account == account.AccountId).Roles));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new HttpClient { BaseAddress = new Uri(address) };
    }

    static HttpRequestMessage Request(HttpMethod method, string path, string? token)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task EndpointAnswersAdministratorsAndRefusesEveryoneElse()
    {
        var client = await StartAsync();
        collector.Record(0x3a01, [0x01, 0x3a, 0xff]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Request(HttpMethod.Get, "/api/admin/diagnostics", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Request(HttpMethod.Get, "/api/admin/diagnostics", "nonsense"))).StatusCode);
        foreach (var token in new[] { "player", "gamemaster", "moderator" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Request(HttpMethod.Get, "/api/admin/diagnostics", token))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Request(HttpMethod.Delete, "/api/admin/diagnostics/unanswered", token))).StatusCode);
        }
        Assert.Single(collector.Read().Opcodes);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/api/admin/diagnostics", "admin"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("world", body.GetProperty("role").GetString());
        var opcode = body.GetProperty("unanswered").GetProperty("opcodes")[0];
        Assert.Equal(0x3a01, opcode.GetProperty("opcode").GetInt32());
        Assert.Equal("013AFF", opcode.GetProperty("bodies")[0].GetProperty("hex").GetString());
        Assert.True(body.GetProperty("process").GetProperty("memoryMb").GetInt64() > 0);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("database").ValueKind);

        var cleared = await client.SendAsync(Request(HttpMethod.Delete, "/api/admin/diagnostics/unanswered", "admin"));
        Assert.Equal(1, (await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cleared").GetInt32());
        Assert.Empty(collector.Read().Opcodes);
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null) await app.DisposeAsync();
    }
}
