using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aion.Host.Tests;

/// <summary>KiskLauncher's server settings and build reference (LauncherEndpoints.cs).</summary>
public sealed class LauncherEndpointsTests : IAsyncDisposable
{
    WebApplication? app;

    async Task<HttpClient> StartAsync(LauncherEndpoints endpoints, Dictionary<string, string?>? settings = null)
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(settings ?? []);
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(2));
        app = builder.Build();
        endpoints.Map(app, () => "Test Realm", "203.0.113.5", 8087);
        await app.StartAsync();
        return new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    [Fact]
    public async Task The_config_names_the_server_its_versions_and_the_build_and_the_build_is_served_by_fingerprint()
    {
        var endpoints = LauncherEndpoints.Embedded();
        Assert.NotNull(endpoints);
        var client = await StartAsync(endpoints);

        using var config = JsonDocument.Parse(await client.GetStringAsync(LauncherEndpoints.ConfigPath));
        var root = config.RootElement;
        Assert.Equal(("Test Realm", "203.0.113.5", 8087), (root.GetProperty("name").GetString(), root.GetProperty("serverAddress").GetString(), root.GetProperty("bootstrapPort").GetInt32()));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("savedDirSuffix").ValueKind);
        var versions = root.GetProperty("clientVersions").EnumerateArray().ToList();
        Assert.Equal(["private", "steam"], versions.Select(v => v.GetProperty("kind").GetString()));
        Assert.Equal(endpoints.Fingerprint, versions[0].GetProperty("build").GetString());
        Assert.Equal("2.0.6.0", root.GetProperty("selectedClientVersion").GetString());

        using var build = JsonDocument.Parse(await client.GetStringAsync("/api/launcher/builds/" + endpoints.Fingerprint.ToUpperInvariant()));
        Assert.Equal(endpoints.Fingerprint, LauncherEndpoints.ComputeFingerprint(build.RootElement.GetProperty("files")));
        using var unknown = await client.GetAsync("/api/launcher/builds/" + new string('0', 64));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Settings_rename_the_version_set_the_saved_folder_and_can_hide_the_Steam_copy()
    {
        var client = await StartAsync(LauncherEndpoints.Embedded()!, new()
        {
            ["Aion:Launcher:ClientVersion"] = "2.0.7.0", ["Aion:Launcher:AllowSteam"] = "false", ["Aion:Launcher:SavedDirSuffix"] = "abc123",
        });
        var config = await client.GetFromJsonAsync<JsonElement>(LauncherEndpoints.ConfigPath);
        Assert.Equal("abc123", config.GetProperty("savedDirSuffix").GetString());
        var version = Assert.Single(config.GetProperty("clientVersions").EnumerateArray());
        Assert.Equal(("2.0.7.0", "2.0.7.0"), (version.GetProperty("id").GetString(), config.GetProperty("selectedClientVersion").GetString()));
    }

    [Fact]
    public void A_reference_whose_files_do_not_match_its_fingerprint_is_refused()
    {
        var json = """{"fingerprint":"00","files":[{"path":"a","size":1,"sha256":"ff","quick":true}]}"""u8.ToArray();
        Assert.Throws<InvalidDataException>(() => LauncherEndpoints.FromJson(json));
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null) await app.DisposeAsync();
    }
}
