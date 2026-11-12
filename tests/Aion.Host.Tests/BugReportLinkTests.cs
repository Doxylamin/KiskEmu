using System.Net;
using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Aion.Host.Authentication;
using Aion.Host.Realm;
using Aion.World;
using Aion.World.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aion.Host.Tests;

/// <summary>"/bug" reports (docs/BUG_REPORTS.md): sent by a world over the realm link, read back as a Markdown brief.</summary>
public sealed class BugReportLinkTests
{
    const long Now = 1_793_000_000_000;

    sealed class MemoryBugReports : IBugReports
    {
        public List<NewBugReport> Reports { get; } = [];
        public Task<long> CreateAsync(NewBugReport report, CancellationToken ct = default) { Reports.Add(report); return Task.FromResult(41L + Reports.Count); }
        public Task<IReadOnlyList<BugReportSummary>> ListAsync(VerifiedAccount actor, BugReportFilter filter, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BugReport?> GetAsync(VerifiedAccount actor, long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> UpdateAsync(VerifiedAccount actor, long id, string status, string note, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long?> AttachScreenshotAsync(Guid account, Guid character, BugScreenshot screenshot, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BugScreenshot?> ScreenshotAsync(VerifiedAccount actor, long id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_world_sends_a_report_larger_than_the_request_limit_and_the_realm_records_the_calling_world()
    {
        var reports = new MemoryBugReports();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 16384;   // as the host sets it
            options.Listen(IPAddress.Loopback, 0);
        });
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(2));
        builder.Services.AddSingleton<IBugReports>(reports);
        await using var realm = builder.Build();
        RealmLinkEndpoints.MapBugReports(realm.MapGroup("/internal/realm"), _ => 7);
        await realm.StartAsync();
        var url = realm.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        var link = new RealmLinkClient(new RealmLinkOptions { Url = url, Token = "t" }, 7, NullLogger<RealmLinkClient>.Instance);
        var context = JsonSerializer.Serialize(new { packets = new string('a', 200_000) });
        var id = await new LinkedBugReports(link).CreateAsync(
            new NewBugReport(Guid.NewGuid(), Guid.NewGuid(), "Tester", 99, 1010, "Verteron", ["Q1"], "the door is shut", "abc", context));

        Assert.Equal(42, id);
        var stored = Assert.Single(reports.Reports);
        Assert.Equal((7, "Tester", 1010, "the door is shut"), (stored.WorldId, stored.CharacterName, stored.MapId, stored.Text));
        Assert.Equal(context, stored.ContextJson);
        Assert.Equal(["Q1"], stored.Quests);
        await realm.StopAsync();
    }

    [Fact]
    public async Task The_markdown_brief_carries_place_quests_spawn_rows_and_packets()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1);
        await session.HandleClientPacketSafeAsync(new Aion.Protocol.Native.PacketWriter().U16(0x3602).U64(5).ToArray(), Now + 10, default);
        var captured = session.CaptureBugContext(Now + 100, DateTimeOffset.FromUnixTimeMilliseconds(Now), store.AccountId, "feedc0de", Aion.Host.Debug.BuildInfo.OpcodeName);
        using var json = JsonDocument.Parse(BugReporter.Serialize(captured));
        var summary = new BugReportSummary(5, DateTimeOffset.FromUnixTimeMilliseconds(Now), store.AccountId, "tester", session.CharacterId, session.CharacterName,
            store.WorldId, session.MapId, captured.Location!.MapName, captured.Quests.Codes(), "NPC hidden behind | the wall\nsecond line", "open", "", null, null,
            "feedc0de", null);

        var md = BugReportMarkdown.Build(new BugReport(summary, json.RootElement.Clone()), "/api/admin/bug-reports/5");

        Assert.StartsWith("# Bug report #5: NPC hidden behind | the wall", md);
        Assert.Contains("> second line", md);
        Assert.Contains("`feedc0de`", md);
        Assert.Contains($"- Map: {captured.Location.MapName} ({captured.Location.MapId})", md);
        Assert.Contains($"//Teleport {captured.Location.MapId} ", md);
        Assert.Contains($"| `{captured.Quests.Active[0].Code}` |", md);
        if (captured.Nearby.FirstOrDefault(n => n.Source.Kind == "map_data") is { } npc)
            Assert.Contains($"map_data maps[\"{npc.Source.Map}\"][{npc.Source.Index}]", md);
        Assert.Contains(" C 0x3602 ", md);
        Assert.Contains("GET /api/admin/bug-reports/5", md);
    }
}
