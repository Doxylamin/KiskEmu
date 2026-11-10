using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aion.Host.Tests;

/// <summary>The player site and the staff dashboard on separate ports (WebSites.cs, Aion:Web:StaffPort).</summary>
public sealed class WebSitesTests : IAsyncDisposable
{
    readonly string webRoot = Directory.CreateTempSubdirectory("aion-web-").FullName;
    WebApplication? app;

    [Theory]
    [InlineData("/", true)]
    [InlineData("/api/admin/me", true)]
    [InlineData("/api/admin/session", true)]
    [InlineData("/api/status", true)]
    [InlineData("/wardrobe/items/1.webp", true)]
    [InlineData("/wardrobe-viewer.js", true)]
    [InlineData("/assets/admin-AbC123.js", true)]
    [InlineData("/assets/session-AbC123.js", true)]
    [InlineData("/assets/index-AbC123.js", false)]
    [InlineData("/index.html", false)]
    [InlineData("/admin.html", false)]
    [InlineData("/api/characters", false)]
    [InlineData("/api/accounts/login", false)]
    [InlineData("/internal/realm/authenticate", false)]
    [InlineData("/downloads/launcher.zip", false)]
    [InlineData("/api/administrator", false)]
    public void StaffPortAnswersTheDashboardOnly(string path, bool allowed) =>
        Assert.Equal(allowed, WebSites.StaffAllows(path));

    [Theory]
    [InlineData("/api/admin/me", true)]
    [InlineData("/api/admin/session", true)]
    [InlineData("/admin.html", true)]
    [InlineData("/ADMIN.HTML", true)]
    [InlineData("/assets/admin-AbC123.css", true)]
    [InlineData("/wardrobe/items/1.webp", true)]
    [InlineData("/wardrobe-viewer.js", true)]
    [InlineData("/", false)]
    [InlineData("/index.html", false)]
    [InlineData("/assets/index-AbC123.js", false)]
    [InlineData("/assets/session-AbC123.js", false)]
    [InlineData("/api/characters", false)]
    [InlineData("/api/status", false)]
    [InlineData("/internal/realm/authenticate", false)]
    public void AccountPortHidesTheDashboard(string path, bool refused) =>
        Assert.Equal(refused, WebSites.PlayerRefuses(path));

    static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    async Task<(HttpClient Account, HttpClient Staff)> StartAsync()
    {
        File.WriteAllText(Path.Combine(webRoot, "index.html"), "player page");
        File.WriteAllText(Path.Combine(webRoot, "admin.html"), "staff page");
        Directory.CreateDirectory(Path.Combine(webRoot, "assets"));
        File.WriteAllText(Path.Combine(webRoot, "assets", "index-A1.js"), "player script");
        File.WriteAllText(Path.Combine(webRoot, "assets", "admin-B2.js"), "staff script");
        File.WriteAllText(Path.Combine(webRoot, "assets", "session-C3.js"), "shared script");
        int accountPort = FreePort(), staffPort = FreePort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, accountPort);
            options.Listen(IPAddress.Loopback, staffPort);
        });
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(2));
        app = builder.Build();
        app.UseSiteSplit(accountPort, staffPort, webRoot);
        var files = new PhysicalFileProvider(webRoot);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        app.MapGet("/api/admin/me", () => Results.Ok(new { staff = true }));
        app.MapGet("/api/characters", () => Results.Ok(Array.Empty<int>()));
        app.MapGet("/api/status", () => Results.Ok(new { up = true }));
        await app.StartAsync();
        return (new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{accountPort}") },
                new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{staffPort}") });
    }

    static async Task<(HttpStatusCode Status, string Body)> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EachPortServesItsOwnSite()
    {
        var (account, staff) = await StartAsync();
        Assert.Equal((HttpStatusCode.OK, "player page"), await GetAsync(account, "/"));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(account, "/api/characters")).Status);
        Assert.Equal((HttpStatusCode.OK, "player script"), await GetAsync(account, "/assets/index-A1.js"));
        Assert.Equal((HttpStatusCode.OK, "shared script"), await GetAsync(account, "/assets/session-C3.js"));
        foreach (var path in new[] { "/admin.html", "/assets/admin-B2.js", "/api/admin/me" })
            Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(account, path)).Status);

        Assert.Equal((HttpStatusCode.OK, "staff page"), await GetAsync(staff, "/"));
        Assert.Equal((HttpStatusCode.OK, "staff script"), await GetAsync(staff, "/assets/admin-B2.js"));
        Assert.Equal((HttpStatusCode.OK, "shared script"), await GetAsync(staff, "/assets/session-C3.js"));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(staff, "/api/admin/me")).Status);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(staff, "/api/status")).Status);
        foreach (var path in new[] { "/index.html", "/admin.html", "/assets/index-A1.js", "/api/characters" })
            Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(staff, path)).Status);
        using var page = await staff.GetAsync("/");
        Assert.Equal("no-store", page.Headers.CacheControl?.ToString());
        Assert.Contains("noindex", page.Headers.GetValues("X-Robots-Tag").Single());
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null) await app.DisposeAsync();
        try { Directory.Delete(webRoot, recursive: true); } catch (IOException) { }
    }
}
