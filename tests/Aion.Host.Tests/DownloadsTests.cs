using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.RateLimiting;
using Aion.Application.Authentication;
using Aion.Core.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aion.Host.Tests;

/// <summary>The launcher download after the rename (Downloads.cs): KiskLauncher.exe, else the old A2Core Launcher.exe.</summary>
public sealed class DownloadsTests : IAsyncDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("kiskemu-downloads-").FullName;
    WebApplication? app;

    sealed record Manifest(string Sha256, long Size, string? Version);

    async Task<HttpClient> StartAsync(Func<Microsoft.AspNetCore.Http.HttpRequest, byte[]>? launcherJson = null)
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(2));
        builder.Services.AddSingleton<IAccountAuthentication, SignedIn>();
        builder.Services.AddRateLimiter(options => options.AddPolicy("account-entry", _ => RateLimitPartition.GetNoLimiter("test")));
        app = builder.Build();
        app.UseRateLimiter();
        new Downloads(folder, TimeProvider.System).Map(app, launcherJson);
        await app.StartAsync();
        return new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    [Fact]
    public async Task The_old_launcher_file_is_served_until_a_KiskEmu_launcher_is_published()
    {
        var downloads = new Downloads(folder, TimeProvider.System);
        Assert.False(downloads.LauncherAvailable);
        File.WriteAllBytes(Path.Combine(folder, Downloads.LegacyLauncherFile), new byte[10]);
        File.WriteAllText(Path.Combine(folder, Downloads.LegacyLauncherFile + Downloads.VersionSuffix), "0.4.1\n");
        Assert.True(downloads.LauncherAvailable);

        var client = await StartAsync();
        var legacy = await client.GetFromJsonAsync<Manifest>("/api/downloads/launcher/manifest");
        Assert.Equal((10L, "0.4.1"), (legacy!.Size, legacy.Version));

        File.WriteAllBytes(Path.Combine(folder, Downloads.LauncherFile), new byte[20]);
        File.WriteAllText(Path.Combine(folder, Downloads.LauncherFile + Downloads.VersionSuffix), "0.5.0\n");
        var current = await client.GetFromJsonAsync<Manifest>("/api/downloads/launcher/manifest");
        Assert.Equal((20L, "0.5.0"), (current!.Size, current.Version));
    }

    [Fact]
    public async Task Without_either_file_there_is_no_manifest()
    {
        var client = await StartAsync();
        using var response = await client.GetAsync("/api/downloads/launcher/manifest");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_account_page_gets_a_zip_whose_launcher_json_adds_this_server_and_updates_stay_exe_only()
    {
        var exe = new byte[] { 1, 2, 3, 4, 5 };
        File.WriteAllBytes(Path.Combine(folder, Downloads.LauncherFile), exe);
        var client = await StartAsync(request => LauncherEndpoints.LauncherJson(request,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aion:Launcher:SavedDirSuffix"] = "Suffix1", ["Aion:Launcher:CertificateThumbprint"] = new string('a', 40),
            }).Build(), "Test World", "203.0.113.5", 8087));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SignedIn.Token);

        var zipLink = await (await client.PostAsync("/api/downloads/launcher?package=zip", null)).Content.ReadFromJsonAsync<JsonElement>();
        var zipUrl = zipLink.GetProperty("url").GetString()!;
        Assert.StartsWith("/download/launcher.zip?ticket=", zipUrl);
        using var archive = new ZipArchive(new MemoryStream(await client.GetByteArrayAsync(zipUrl)));
        await using (var entry = archive.GetEntry(Downloads.ZipEntryName)!.Open())
        {
            using var copy = new MemoryStream();
            await entry.CopyToAsync(copy);
            Assert.Equal(exe, copy.ToArray());
        }
        using var json = JsonDocument.Parse(archive.GetEntry("launcher.json")!.Open());
        var root = json.RootElement;
        Assert.Equal(("Test World", "203.0.113.5", 8087, "Suffix1", new string('a', 40)),
            (root.GetProperty("name").GetString(), root.GetProperty("serverAddress").GetString(), root.GetProperty("bootstrapPort").GetInt32(),
             root.GetProperty("savedDirSuffix").GetString(), root.GetProperty("certificateThumbprint").GetString()));
        Assert.Equal(client.BaseAddress!.Port, new Uri(root.GetProperty("accountUrl").GetString()!).Port);

        // The launcher's self-update asks without a package: the exe itself, which its manifest hash describes.
        var exeLink = await (await client.PostAsync("/api/downloads/launcher", null)).Content.ReadFromJsonAsync<JsonElement>();
        var exeUrl = exeLink.GetProperty("url").GetString()!;
        Assert.StartsWith("/download/launcher?ticket=", exeUrl);
        Assert.Equal(exe, await client.GetByteArrayAsync(exeUrl));
        using var noTicket = await client.GetAsync("/download/launcher.zip?ticket=" + new string('0', 64));
        Assert.Equal(HttpStatusCode.NotFound, noTicket.StatusCode);
    }

    sealed class SignedIn : IAccountAuthentication
    {
        public const string Token = "player-token";
        public Task<VerifiedAccount?> AuthenticateAsync(string token, CancellationToken ct) =>
            Task.FromResult(token == Token ? new VerifiedAccount(Guid.NewGuid()) : null);
        public Task<bool> RegisterAsync(string username, string password, CancellationToken ct) => Task.FromResult(false);
        public Task<AccountSession?> SignInAsync(string username, string password, CancellationToken ct) => Task.FromResult<AccountSession?>(null);
        public Task SignOutAsync(string token, CancellationToken ct) => Task.CompletedTask;
        public Task<GameTicket?> IssueGameTicketAsync(string sessionToken, int worldId, CancellationToken ct) => Task.FromResult<GameTicket?>(null);
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null) await app.DisposeAsync();
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }
}
