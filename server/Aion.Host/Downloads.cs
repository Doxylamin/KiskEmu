using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using Aion.Application.Authentication;

namespace Aion.Host;

/// <summary>
/// Player downloads (the launcher) for signed-in accounts only. A browser download cannot send the
/// page's bearer token, so the page asks POST /api/downloads/launcher for a link with a random
/// ticket, valid five minutes (several requests, for resumed downloads), then follows it.
/// The launcher updates itself the same way: GET /api/downloads/launcher/manifest (no sign-in: a
/// hash and size are not secret) tells it what the server's file is, and a launcher that differs
/// downloads it through a ticket link.
/// The account page asks for the ZIP instead (POST /api/downloads/launcher?package=zip): KiskLauncher.exe with a
/// launcher.json next to it that adds this server (the exe itself has no server built in). Self-updates stay exe-only.
/// </summary>
public sealed class Downloads(string folder, TimeProvider clock)
{
    /// <summary>The file in the downloads folder, and the name the browser saves it as (the route is /download/launcher).</summary>
    public const string LauncherFile = "KiskLauncher.exe";
    /// <summary>The name before the rename: served until a KiskLauncher is published to the same folder.</summary>
    public const string LegacyLauncherFile = "A2Core Launcher.exe";
    const string Route = "/download/launcher";
    const string ZipRoute = "/download/launcher.zip";
    public const string ZipEntryName = "KiskLauncher.exe";
    sealed record ZipCache(long Length, DateTime Written, string Json, byte[] Zip);
    ZipCache? zipped;
    /// <summary>Optional text file next to the exe with its version (exe name + ".version"), shown by the launcher before it updates.</summary>
    public const string VersionSuffix = ".version";
    static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    readonly ConcurrentDictionary<string, DateTimeOffset> tickets = new();
    (long Length, DateTime Written, string Sha256)? hashed;

    /// <summary>The launcher file served: the KiskEmu one, else the legacy A2Core one; null when neither is there.</summary>
    string? Current => new[] { LauncherFile, LegacyLauncherFile }.Select(name => Path.Combine(folder, name)).FirstOrDefault(File.Exists);

    public bool LauncherAvailable => Current is not null;

    string Issue()
    {
        var now = clock.GetUtcNow();
        foreach (var stale in tickets.Where(t => t.Value <= now).ToList()) tickets.TryRemove(stale);
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        tickets[ticket] = now + Lifetime;
        return ticket;
    }

    bool Valid(string? ticket) => ticket is { Length: 64 } && tickets.TryGetValue(ticket, out var until) && until > clock.GetUtcNow();

    /// <summary>The launcher file's hash (computed once per file version), size and version text; null without a launcher.</summary>
    async Task<object?> ManifestAsync(CancellationToken ct)
    {
        if (Current is not { } path) return null;
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        string sha256;
        if (hashed is { } cached && cached.Length == info.Length && cached.Written == info.LastWriteTimeUtc) sha256 = cached.Sha256;
        else
        {
            await using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            hashed = (info.Length, info.LastWriteTimeUtc, sha256);
        }
        var versionPath = path + VersionSuffix;
        var version = File.Exists(versionPath) ? (await File.ReadAllTextAsync(versionPath, ct)).Trim() : "";
        return new { sha256, size = info.Length, version = version.Length > 0 ? version : null };
    }

    /// <summary>
    /// The ZIP of the current launcher and this server's launcher.json, made once per launcher file and settings (the
    /// exe barely compresses, so it is stored).
    /// </summary>
    byte[] Zip(string path, byte[] launcherJson)
    {
        var info = new FileInfo(path);
        var json = Convert.ToBase64String(launcherJson);
        if (zipped is { } cached && cached.Length == info.Length && cached.Written == info.LastWriteTimeUtc && cached.Json == json) return cached.Zip;
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntryFromFile(path, ZipEntryName, CompressionLevel.NoCompression);
            var entry = archive.CreateEntry("launcher.json", CompressionLevel.Optimal);
            using var stream = entry.Open();
            stream.Write(launcherJson);
        }
        var zip = buffer.ToArray();
        zipped = new ZipCache(info.Length, info.LastWriteTimeUtc, json, zip);
        return zip;
    }

    /// <param name="launcherJson">This server's launcher.json for the ZIP (<see cref="LauncherEndpoints.LauncherJson"/>);
    /// null serves the exe alone.</param>
    public void Map(WebApplication app, Func<HttpRequest, byte[]>? launcherJson = null)
    {
        app.MapPost("/api/downloads/launcher", async (HttpContext context, IAccountAuthentication auth, string? package, CancellationToken ct) =>
        {
            var header = context.Request.Headers.Authorization.ToString();
            if (await auth.AuthenticateAsync(header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : "", ct) is null)
                return Results.Unauthorized();
            if (!LauncherAvailable) return Results.NotFound();
            var zip = package == "zip" && launcherJson is not null;
            return Results.Ok(new { url = $"{(zip ? ZipRoute : Route)}?ticket={Issue()}" });
        }).RequireRateLimiting("account-entry");
        app.MapGet("/api/downloads/launcher/manifest", async (CancellationToken ct) =>
            await ManifestAsync(ct) is { } manifest ? Results.Ok(manifest) : Results.NotFound());
        app.MapGet(Route, (string? ticket) =>
            Valid(ticket) && Current is { } path
                ? Results.File(path, "application/octet-stream", Path.GetFileName(path), enableRangeProcessing: true)
                : Results.NotFound());
        if (launcherJson is not null)
            app.MapGet(ZipRoute, (HttpRequest request, string? ticket) =>
                Valid(ticket) && Current is { } path
                    ? Results.File(Zip(path, launcherJson(request)), "application/zip", "KiskLauncher.zip", enableRangeProcessing: true)
                    : Results.NotFound());
    }
}
