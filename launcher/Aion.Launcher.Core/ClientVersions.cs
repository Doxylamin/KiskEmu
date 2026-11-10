
using System.Text.Json.Serialization;

namespace Aion.Launcher;

/// <summary>
/// One client the launcher can start. "private": the launcher's own copy of the supported build
/// (downloaded or linked, checked against supported-build.json). "steam": the player's own Steam
/// install, used read-only in place (no file is written there; no pak patch, no firewall rules).
/// <paramref name="Build"/>: a private version's build fingerprint (the server's /api/launcher/builds/{fingerprint});
/// none means the build embedded in the launcher.
/// </summary>
public sealed record ClientVersion(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("clientPath")] string? ClientPath = null,
    [property: JsonPropertyName("launchArgs")] string[]? LaunchArgs = null,
    [property: JsonPropertyName("build"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Build = null)
{
    public const string PrivateKind = "private", SteamKind = "steam";
    public const string DefaultId = "2.0.6.0", SteamLatestId = "steam-latest";
    public bool IsSteam => Kind.Equals(SteamKind, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<ClientVersion> Defaults { get; } =
    [
        new(DefaultId, "2.0.6.0", PrivateKind),
        new(SteamLatestId, "Official latest (Steam)", SteamKind),
    ];
}

public static class ClientVersions
{
    /// <summary>The configured versions (the server's "clientVersions"), else the two defaults.</summary>
    public static IReadOnlyList<ClientVersion> All(LauncherConfig config) =>
        config.ClientVersions is { Length: > 0 } list ? list : ClientVersion.Defaults;

    /// <summary>
    /// The player's choice (<paramref name="chosen"/>, kept in the launcher settings), else the server's
    /// "selectedClientVersion", else the first (2.0.6.0).
    /// </summary>
    public static ClientVersion Selected(LauncherConfig config, string? chosen = null)
    {
        var all = All(config);
        return all.FirstOrDefault(v => v.Id.Equals(chosen, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(v => v.Id.Equals(config.SelectedClientVersion, StringComparison.OrdinalIgnoreCase))
            ?? all[0];
    }

    /// <summary>
    /// The Steam game folder for a "steam" version: its own clientPath, else the server entry's
    /// "steamClientPath", else the AION2 folder found through Steam's libraryfolders.vdf. Null when none exists.
    /// </summary>
    public static string? SteamFolder(ClientVersion version, LauncherConfig config, SteamInstall? steam)
    {
        foreach (var path in new[] { version.ClientPath, config.SteamClientPath })
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) return Path.GetFullPath(path);
        return steam?.FindApp() is { } app ? Path.GetFullPath(app.Folder) : null;
    }

    /// <summary>Steam's launch arguments for the game plus the version's extra ones (duplicates dropped).</summary>
    public static string[] Arguments(IEnumerable<string> steamArguments, ClientVersion version)
    {
        var list = steamArguments.ToList();
        foreach (var extra in version.LaunchArgs ?? [])
            if (!string.IsNullOrWhiteSpace(extra) && !list.Contains(extra, StringComparer.OrdinalIgnoreCase)) list.Add(extra);
        return [.. list];
    }

}
