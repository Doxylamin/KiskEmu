using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aion.Host;

/// <summary>
/// What KiskLauncher needs to know about this server, so one launcher build plays on any KiskEmu server (no sign-in:
/// nothing here is secret):
/// <list type="bullet">
/// <item>GET /api/launcher/config: the server's name, game address, bootstrap port, the client versions it accepts and
/// the build each one is checked against.</item>
/// <item>GET /api/launcher/builds/{fingerprint}: that build's reference (launcher/builds/&lt;game version&gt;.json: the Steam
/// depot and manifest, every file with its size and hash). The launcher never gets game files from the server: the
/// player's own Steam install or Steam account provides them, and this list is what the launcher checks them against.</item>
/// </list>
/// The versions are the client game versions the server is built for (Aion:Native:ClientVersions, e.g. [3527] or
/// [3533, 3527]; the first is the default choice), named by the number the client sends in its greeting. A version without
/// a reference in launcher/builds is not offered. Unset: 3527.
/// Settings (Aion:Launcher): AllowSteam (offer the player's own Steam copy as a version, default true), SavedDirSuffix (the
/// client's private saved-folder suffix; without it each launcher derives one from the server's address), AccountUrl and
/// CertificateThumbprint (for the download ZIP's launcher.json).
/// </summary>
public sealed class LauncherEndpoints(IReadOnlyList<LauncherEndpoints.Build> builds)
{
    /// <summary>One client game version's build reference, its fingerprint checked.</summary>
    public sealed record Build(int GameVersion, byte[] Json, string Fingerprint);

    public const string ConfigPath = "/api/launcher/config";
    /// <summary>The version offered when the server names none: 2.0.6, the build the launcher embeds.</summary>
    public const int DefaultVersion = 3527;
    public IReadOnlyList<Build> Builds => builds;

    /// <summary>The build references embedded from launcher/builds; null when the host was built without any.</summary>
    public static LauncherEndpoints? Embedded()
    {
        var assembly = typeof(LauncherEndpoints).Assembly;
        var found = new List<Build>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("builds/", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            found.Add(ParseBuild(copy.ToArray()));
        }
        return found.Count == 0 ? null : new LauncherEndpoints([.. found.OrderBy(b => b.GameVersion)]);
    }

    /// <summary>One build reference as the only build (tests).</summary>
    public static LauncherEndpoints FromJson(byte[] json) => new([ParseBuild(json)]);

    /// <summary>
    /// A build reference, its fingerprint checked: the SHA-256 over the quick files' "path\0size\0sha256\n" lines
    /// (tools/launcher_build.py), the same check the launcher makes before it trusts a downloaded reference.
    /// </summary>
    public static Build ParseBuild(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var stated = root.GetProperty("fingerprint").GetString() ?? "";
        var computed = ComputeFingerprint(root.GetProperty("files"));
        if (!stated.Equals(computed, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("build reference: the fingerprint does not match its files");
        var version = root.TryGetProperty("gameVersion", out var v) ? v.GetInt32() : DefaultVersion;
        return new Build(version, json, computed);
    }

    public static string ComputeFingerprint(JsonElement files)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.EnumerateArray())
        {
            if (!file.TryGetProperty("quick", out var quick) || !quick.GetBoolean()) continue;
            digest.AppendData(Encoding.UTF8.GetBytes($"{file.GetProperty("path").GetString()!.ToLowerInvariant()}\0{file.GetProperty("size").GetInt64()}\0{file.GetProperty("sha256").GetString()}\n"));
        }
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    /// <summary>The game versions offered, in the configured order, each with its build (those without one are left out).</summary>
    public IReadOnlyList<Build> Offered(IConfiguration configuration)
    {
        var configured = configuration.GetSection("Aion:Native:ClientVersions").Get<int[]>() is { Length: > 0 } list ? list : [DefaultVersion];
        return [.. configured.Distinct().Select(v => builds.FirstOrDefault(b => b.GameVersion == v)).OfType<Build>()];
    }

    /// <param name="siteName">The server's name (Aion:Web:SiteName), read per request.</param>
    /// <param name="publicAddress">The IPv4 address the game connects to (Aion:Network:PublicAddress).</param>
    /// <param name="bootstrapPort">The platform bootstrap port (Aion:Platform:Port).</param>
    public void Map(WebApplication app, Func<string> siteName, string publicAddress, int bootstrapPort)
    {
        app.MapGet(ConfigPath, (IConfiguration configuration) =>
        {
            var offered = Offered(configuration);
            var versions = offered.Select(b => (object)new { id = b.GameVersion.ToString(), name = b.GameVersion.ToString(), kind = "private", build = b.Fingerprint }).ToList();
            if (configuration.GetValue<bool?>("Aion:Launcher:AllowSteam") ?? true) versions.Add(new { id = "steam-latest", name = "Official latest (Steam)", kind = "steam" });
            return Results.Ok(new
            {
                name = siteName(), serverAddress = publicAddress, bootstrapPort, savedDirSuffix = SavedDirSuffix(configuration),
                clientVersions = versions, selectedClientVersion = offered.FirstOrDefault()?.GameVersion.ToString(),
            });
        });
        app.MapGet("/api/launcher/builds/{id}", (string id) =>
            builds.FirstOrDefault(b => b.Fingerprint.Equals(id, StringComparison.OrdinalIgnoreCase)) is { } build
                ? Results.Bytes(build.Json, "application/json") : Results.NotFound());
    }

    static string? SavedDirSuffix(IConfiguration configuration) =>
        configuration["Aion:Launcher:SavedDirSuffix"]?.Trim() is { Length: > 0 } suffix ? suffix : null;

    /// <summary>
    /// The launcher.json of this server's download ZIP: next to KiskLauncher.exe it adds this server at the launcher's
    /// first start. The account address is Aion:Launcher:AccountUrl, else the address the player downloaded from (the
    /// account site itself). Aion:Launcher:CertificateThumbprint (40 hex) pins a self-signed account certificate.
    /// Without a savedDirSuffix the launcher derives one from the account address.
    /// </summary>
    public static byte[] LauncherJson(HttpRequest request, IConfiguration configuration, string siteName, string publicAddress, int bootstrapPort)
    {
        var accountUrl = configuration["Aion:Launcher:AccountUrl"]?.Trim().TrimEnd('/') is { Length: > 0 } configured
            ? configured : $"https://{request.Host.Value}";
        var pin = configuration["Aion:Launcher:CertificateThumbprint"]?.Replace(":", "").Trim() is { Length: 40 } thumbprint ? thumbprint : null;
        var json = new Dictionary<string, object?>
        {
            ["name"] = siteName, ["accountUrl"] = accountUrl, ["serverAddress"] = publicAddress, ["bootstrapPort"] = bootstrapPort,
        };
        if (SavedDirSuffix(configuration) is { } suffix) json["savedDirSuffix"] = suffix;
        if (pin is not null) json["certificateThumbprint"] = pin;
        return JsonSerializer.SerializeToUtf8Bytes(json, new JsonSerializerOptions { WriteIndented = true });
    }
}
