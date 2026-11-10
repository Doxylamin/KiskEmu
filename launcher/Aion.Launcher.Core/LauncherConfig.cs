using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.Launcher;

/// <summary>
/// One server the launcher plays on. The launcher keeps one per saved server (from the server's /api/launcher/config,
/// <see cref="ServerSetup"/>). A launcher built with a launcher.json (embedded, or a launcher.json next to the exe) starts
/// with that server; a generic build (GenericLauncher=true) asks for one.
/// </summary>
public sealed record LauncherConfig(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("accountUrl")] string AccountUrl,
    [property: JsonPropertyName("serverAddress")] string ServerAddress,
    [property: JsonPropertyName("bootstrapPort")] int BootstrapPort,
    [property: JsonPropertyName("savedDirSuffix")] string SavedDirSuffix,
    [property: JsonPropertyName("certificateThumbprint")] string? CertificateThumbprint = null,
    [property: JsonPropertyName("clientVersions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ClientVersion[]? ClientVersions = null,
    [property: JsonPropertyName("selectedClientVersion"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SelectedClientVersion = null,
    [property: JsonPropertyName("steamClientPath"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SteamClientPath = null)
{
    public static LauncherConfig Load(string? overridePath = null)
    {
        LauncherConfig config;
        if (overridePath is not null && File.Exists(overridePath))
            config = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(overridePath)) ?? throw new InvalidDataException("launcher.json");
        else
        {
            using var stream = typeof(LauncherConfig).Assembly.GetManifestResourceStream("launcher.json")
                ?? throw new InvalidOperationException("launcher.json is not embedded");
            config = JsonSerializer.Deserialize<LauncherConfig>(stream) ?? throw new InvalidDataException("launcher.json");
        }
        config.Validate();
        return config;
    }

    /// <summary>
    /// The server this build was made for (launcher.json next to the exe, else the embedded one); null for a generic
    /// build. An invalid file throws, as before.
    /// </summary>
    public static LauncherConfig? LoadDefault(string? overridePath = null)
    {
        if (overridePath is not null && File.Exists(overridePath)) return Load(overridePath);
        return typeof(LauncherConfig).Assembly.GetManifestResourceInfo("launcher.json") is null ? null : Load();
    }

    [JsonIgnore] public IPAddress Server => IPAddress.Parse(ServerAddress);

    public void Validate()
    {
        if (!IPAddress.TryParse(ServerAddress, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidDataException("serverAddress must be an IPv4 address");
        if (!Uri.TryCreate(AccountUrl, UriKind.Absolute, out var account) || account.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("accountUrl must be an https URL");
        if (BootstrapPort is < 1 or > 65535) throw new InvalidDataException("bootstrapPort");
        if (SavedDirSuffix.Length is < 1 or > 32 || !SavedDirSuffix.All(char.IsAsciiLetterOrDigit))
            throw new InvalidDataException("savedDirSuffix must be 1-32 letters or digits");
        if (SavedDirSuffix.Equals("Steam", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("savedDirSuffix must not be the official game's (Steam)");
        if (CertificateThumbprint is { } pin && (pin.Length != 40 || !pin.All(char.IsAsciiHexDigit)))
            throw new InvalidDataException("certificateThumbprint must be 40 hex characters");
        foreach (var version in ClientVersions ?? [])
        {
            if (string.IsNullOrWhiteSpace(version.Id) || string.IsNullOrWhiteSpace(version.Name))
                throw new InvalidDataException("clientVersions entries need an id and a name");
            if (!version.Kind.Equals(ClientVersion.PrivateKind, StringComparison.OrdinalIgnoreCase) && !version.IsSteam)
                throw new InvalidDataException("clientVersions kind must be private or steam");
        }
    }

    /// <summary>The bootstrap URL for one launch: the launch authorization is the code in the path.</summary>
    public string BootstrapUrl(string authorization) => $"http://{ServerAddress}:{BootstrapPort}/c/{authorization}";
}
