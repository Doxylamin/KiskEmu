using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aion.Host;

/// <summary>
/// What KiskLauncher needs to know about this server, so one launcher build plays on any KiskEmu server (no sign-in:
/// nothing here is secret):
/// <list type="bullet">
/// <item>GET /api/launcher/config: the server's name, game address, bootstrap port, the client versions it accepts and
/// the build each private version is checked against.</item>
/// <item>GET /api/launcher/builds/{fingerprint}: that build's reference (launcher/supported-build.json: the Steam depot
/// and manifest, every file with its size and hash). The launcher never gets game files from the server: the player's
/// own Steam install or Steam account provides them, and this list is what the launcher checks them against.</item>
/// </list>
/// Settings (Aion:Launcher): ClientVersion (the private version's name, default 2.0.6.0), AllowSteam (offer the player's
/// own Steam copy as a version, default true), SavedDirSuffix (the client's private saved-folder suffix; without it each
/// launcher derives one from the server's address), AccountUrl and CertificateThumbprint (for the download ZIP's launcher.json).
/// </summary>
public sealed class LauncherEndpoints(byte[] buildJson, string fingerprint)
{
    public const string ConfigPath = "/api/launcher/config";
    public string Fingerprint => fingerprint;

    /// <summary>The build embedded from launcher/supported-build.json; null when the host was built without it.</summary>
    public static LauncherEndpoints? Embedded()
    {
        using var stream = typeof(LauncherEndpoints).Assembly.GetManifestResourceStream("supported-build.json");
        if (stream is null) return null;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return FromJson(copy.ToArray());
    }

    /// <summary>
    /// A build reference, its fingerprint checked: the SHA-256 over the quick files' "path\0size\0sha256\n" lines
    /// (tools/launcher_build.py), the same check the launcher makes before it trusts a downloaded reference.
    /// </summary>
    public static LauncherEndpoints FromJson(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var stated = root.GetProperty("fingerprint").GetString() ?? "";
        var computed = ComputeFingerprint(root.GetProperty("files"));
        if (!stated.Equals(computed, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("supported-build.json: the fingerprint does not match its files");
        return new LauncherEndpoints(json, computed);
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

    /// <param name="siteName">The server's name (Aion:Web:SiteName), read per request.</param>
    /// <param name="publicAddress">The IPv4 address the game connects to (Aion:Network:PublicAddress).</param>
    /// <param name="bootstrapPort">The platform bootstrap port (Aion:Platform:Port).</param>
    public void Map(WebApplication app, Func<string> siteName, string publicAddress, int bootstrapPort)
    {
        app.MapGet(ConfigPath, (IConfiguration configuration) =>
        {
            var section = configuration.GetSection("Aion:Launcher");
            var version = section["ClientVersion"]?.Trim() is { Length: > 0 } named ? named[..Math.Min(named.Length, 40)] : "2.0.6.0";
            var versions = new List<object> { new { id = version, name = version, kind = "private", build = fingerprint } };
            if (section.GetValue<bool?>("AllowSteam") ?? true) versions.Add(new { id = "steam-latest", name = "Official latest (Steam)", kind = "steam" });
            return Results.Ok(new
            {
                name = siteName(), serverAddress = publicAddress, bootstrapPort, savedDirSuffix = SavedDirSuffix(configuration),
                clientVersions = versions, selectedClientVersion = version,
            });
        });
        app.MapGet("/api/launcher/builds/{id}", (string id) =>
            id.Equals(fingerprint, StringComparison.OrdinalIgnoreCase) ? Results.Bytes(buildJson, "application/json") : Results.NotFound());
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
