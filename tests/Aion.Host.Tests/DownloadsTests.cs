using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aion.Host.Tests;

/// <summary>The launcher download after the rename (Downloads.cs): KiskEmu Launcher.exe, else the old A2Core Launcher.exe.</summary>
public sealed class DownloadsTests : IAsyncDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("kiskemu-downloads-").FullName;
    WebApplication? app;

    sealed record Manifest(string Sha256, long Size, string? Version);

    async Task<HttpClient> StartAsync()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(2));
        app = builder.Build();
        new Downloads(folder, TimeProvider.System).Map(app);
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

    public async ValueTask DisposeAsync()
    {
        if (app is not null) await app.DisposeAsync();
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }
}
