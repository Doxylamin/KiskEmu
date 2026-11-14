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
        // Unset Aion:Native:ClientVersions: 3527 (2.0.6), the build the launcher embeds.
        var build3527 = endpoints.Builds.Single(b => b.GameVersion == 3527);
        Assert.Equal(("3527", build3527.Fingerprint), (versions[0].GetProperty("id").GetString(), versions[0].GetProperty("build").GetString()));
        Assert.Equal("3527", root.GetProperty("selectedClientVersion").GetString());

        using var build = JsonDocument.Parse(await client.GetStringAsync("/api/launcher/builds/" + build3527.Fingerprint.ToUpperInvariant()));
        Assert.Equal(build3527.Fingerprint, LauncherEndpoints.ComputeFingerprint(build.RootElement.GetProperty("files")));
        using var unknown = await client.GetAsync("/api/launcher/builds/" + new string('0', 64));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task The_server_offers_its_configured_versions_in_order_and_can_hide_the_Steam_copy()
    {
        var endpoints = LauncherEndpoints.Embedded()!;
        var client = await StartAsync(endpoints, new()
        {
            ["Aion:Native:ClientVersions:0"] = "3533", ["Aion:Native:ClientVersions:1"] = "3527", ["Aion:Native:ClientVersions:2"] = "9999",
            ["Aion:Launcher:AllowSteam"] = "false", ["Aion:Launcher:SavedDirSuffix"] = "abc123",
        });
        var config = await client.GetFromJsonAsync<JsonElement>(LauncherEndpoints.ConfigPath);
        Assert.Equal("abc123", config.GetProperty("savedDirSuffix").GetString());
        // 9999 has no build reference: not offered. The first is the default choice.
        var versions = config.GetProperty("clientVersions").EnumerateArray().ToList();
        Assert.Equal(["3533", "3527"], versions.Select(v => v.GetProperty("id").GetString()));
        Assert.Equal("3533", config.GetProperty("selectedClientVersion").GetString());
        var build3533 = endpoints.Builds.Single(b => b.GameVersion == 3533);
        Assert.Equal(build3533.Fingerprint, versions[0].GetProperty("build").GetString());
        using var build = JsonDocument.Parse(await client.GetStringAsync("/api/launcher/builds/" + build3533.Fingerprint));
        Assert.Equal(("586322693862358479", 3533), (build.RootElement.GetProperty("manifestId").GetString(), build.RootElement.GetProperty("gameVersion").GetInt32()));
    }

    [Theory]
    [InlineData(new int[0], 3533, true)]
    [InlineData(new[] { 3527 }, 0, true)]
    [InlineData(new[] { 3527 }, 3527, true)]
    [InlineData(new[] { 3527 }, 3535, false)]
    public void The_game_port_lets_in_only_listed_versions(int[] accepted, int version, bool allowed) =>
        Assert.Equal(allowed, Aion.Host.Native.NativeListener.VersionAccepted(accepted, version));

    [Fact]
    public void The_game_greeting_carries_the_client_version_after_its_key()
    {
        // OBSERVED (official 2026-11-04): ... 020103 | cd0d0000 (3533) | 04 06 05 02 44 45 03 00.
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var body = new Aion.Protocol.Native.PacketWriter().U16(0x3610).Bytes(rsa.ExportRSAPublicKey()).I32(3533)
            .Raw(Convert.FromHexString("0406050244450300")).ToArray();
        Aion.Protocol.Native.NativeHandshake.ReadGreeting(body, Aion.Protocol.Native.NativeHandshake.GameGreeting, out var version);
        Assert.Equal(3533, version);
        var bare = new Aion.Protocol.Native.PacketWriter().U16(0x3610).Bytes(rsa.ExportRSAPublicKey()).ToArray();
        Aion.Protocol.Native.NativeHandshake.ReadGreeting(bare, Aion.Protocol.Native.NativeHandshake.GameGreeting, out var none);
        Assert.Equal(0, none);
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
