using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Aion.Launcher;
using Microsoft.Win32;

namespace AionLauncher;

sealed class LauncherException(string message) : Exception(message);

/// <summary>Where the supported client build can come from on this PC.</summary>
sealed record ClientSource(string Folder, ClientFiles.Mode Mode, string Description);

/// <summary>Setup and play on one saved server, without UI: the form shows status and progress.</summary>
sealed class Workflow(ServerEntry server, LauncherSettings settings)
{
    /// <summary>The client build the chosen version is checked against (<see cref="PrepareAsync"/> resolves the server's).</summary>
    public SupportedBuild Build { get; private set; } = SupportedBuild.Embedded;
    public LauncherConfig Config => server.Config;
    LauncherConfig config => server.Config;
    public ServerEntry Profile => server;
    public LauncherSettings Settings => settings;
    public AccountClient Account { get; } = new(server.Config);
    /// <summary>The client version chosen on the Play page (kept per server).</summary>
    public ClientVersion Version { get; private set; } = ClientVersions.Selected(server.Config, server.SelectedClientVersion);
    /// <summary>The folder of the client started last (for waiting until it closes).</summary>
    public string? GameFolder { get; private set; }

    public static SteamInstall? FindSteam()
    {
        var path = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        return path is not null && Directory.Exists(path) ? new SteamInstall(Path.GetFullPath(path)) : null;
    }

    /// <summary>The private copy of <see cref="Build"/> (shared by every server on that build).</summary>
    ClientCopy Copy => settings.Copy(Build.Fingerprint);
    public string? InstallFolder => settings.Copies.TryGetValue(Build.Fingerprint, out var copy) ? copy.Folder : null;
    /// <summary>Every private copy this PC has, for the uninstall.</summary>
    public IEnumerable<string> AllCopies => settings.Copies.Values.Select(c => c.Folder).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The chosen client is there to start: the private copy of <see cref="Build"/>, or for the Steam version the
    /// player's Steam copy (any build; it is never checked against the reference or copied).
    /// </summary>
    public bool Installed => Version.IsSteam
        ? ClientVersions.SteamReady(Version, config, FindSteam(), Build.Executable)
        : InstallFolder is { } folder && File.Exists(Path.Combine(folder, Build.Executable));

    /// <summary>The server's Steam version, when it offers one and the player's Steam copy is there.</summary>
    public ClientVersion? SteamAlternative => ClientVersions.SteamAlternative(config, FindSteam(), Build.Executable);

    /// <summary>
    /// Asks the server for its current settings (address, port, versions; kept for the next start) and resolves the
    /// chosen version's build reference: the embedded one, a cached one, or the server's (checked against its
    /// fingerprint). A server that predates the settings, or cannot be reached now, keeps what the launcher has.
    /// </summary>
    public async Task PrepareAsync(CancellationToken ct)
    {
        try
        {
            if (await Account.ServerConfigAsync(ct) is { } reply)
            {
                server.Config = ServerSetup.Apply(server.Config, new Uri(server.Config.AccountUrl), reply, null);
                Version = ClientVersions.Selected(server.Config, server.SelectedClientVersion);
                settings.Save();
            }
        }
        catch (Exception error) when (error is AccountException or InvalidDataException or System.Text.Json.JsonException) { }
        Build = await BuildForAsync(Version, ct);
    }

    async Task<SupportedBuild> BuildForAsync(ClientVersion version, CancellationToken ct)
    {
        // The Steam copy is not checked against a build; its executable path is the embedded build's.
        if (version.IsSteam || version.Build is not { Length: > 0 } fingerprint || fingerprint.Equals(SupportedBuild.Embedded.Fingerprint, StringComparison.OrdinalIgnoreCase))
            return SupportedBuild.Embedded;
        var cached = Path.Combine(LauncherSettings.BuildsFolder, fingerprint.ToLowerInvariant() + ".json");
        if (File.Exists(cached))
            try { return SupportedBuild.Parse(await File.ReadAllBytesAsync(cached, ct), fingerprint); }
            catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException) { File.Delete(cached); }
        var (build, json) = await Account.BuildReferenceAsync(fingerprint, ct);
        Directory.CreateDirectory(LauncherSettings.BuildsFolder);
        await File.WriteAllBytesAsync(cached, json, ct);
        return build;
    }

    /// <summary>Chooses a version on the Play page (kept per server) and resolves its build.</summary>
    public async Task ChooseVersionAsync(ClientVersion version, CancellationToken ct)
    {
        var build = await BuildForAsync(version, ct);
        Version = version;
        Build = build;
        server.SelectedClientVersion = version.Id;
        settings.Save();
    }

    /// <summary>
    /// The private copy's folder, on the same drive as its source so placing it is a hard link or a
    /// move (no extra space): under %LOCALAPPDATA% on the system drive, else &lt;drive&gt;:\KiskPrivate.
    /// The embedded build keeps the folder name launchers always used (AION2); another build gets its own.
    /// </summary>
    public string TargetFor(string source)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = ClientFiles.SameVolume(source, local) ? PrivateFolder.Local : PrivateFolder.Resolve(Path.GetPathRoot(Path.GetFullPath(source))!);
        return Path.Combine(root, CopyName);
    }

    string CopyName => Build.Fingerprint.Equals(SupportedBuild.Embedded.Fingerprint, StringComparison.OrdinalIgnoreCase) ? "AION2" : "AION2-" + Build.Fingerprint[..12];

    public static long FreeBytes(string folder) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;

    /// <summary>
    /// The Steam install, when it is still at the supported build (hard-linked on the same drive, else
    /// copied). Sizes only (instant); the copy is hashed after. Otherwise the game is downloaded.
    /// </summary>
    public async Task<ClientSource?> FindSourceAsync(SteamInstall steam)
    {
        if (steam.FindApp() is { } app && (app.Manifest == Build.ManifestId || (await ClientFiles.CheckAsync(app.Folder, Build, CheckLevel.Sizes)).Ok))
        {
            var mode = ClientFiles.SameVolume(app.Folder, TargetFor(app.Folder)) ? ClientFiles.Mode.HardLink : ClientFiles.Mode.Copy;
            if (mode == ClientFiles.Mode.HardLink || FreeBytes(TargetFor(app.Folder)) > Build.TotalBytes)
                return new(app.Folder, mode, "your Steam install");
        }
        return null;
    }

    // ---------- Download from Steam ----------

    /// <summary>Bytes still to download into <paramref name="folder"/> (what an interrupted download already has counts).</summary>
    public long StillNeeded(string folder) => Build.TotalBytes - (Directory.Exists(folder) ? ClientFiles.PresentBytes(folder, Build) : 0);

    /// <summary>
    /// Where the download goes: the folder of an interrupted download, else %LOCALAPPDATA% on the
    /// system drive when it has room, else &lt;drive&gt;:\KiskPrivate\AION2 on the fixed drive with the
    /// most free space. Null when no drive has room.
    /// </summary>
    public string? DownloadFolder()
    {
        if (settings.DownloadFolder is { } previous && Build.Fingerprint.Equals(settings.DownloadBuild, StringComparison.OrdinalIgnoreCase)) return previous;
        const long reserve = 2L << 30;
        var local = TargetFor(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        if (FreeBytes(local) > Build.TotalBytes + reserve) return local;
        var best = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .OrderByDescending(d => d.AvailableFreeSpace).FirstOrDefault();
        return best is not null && best.AvailableFreeSpace > Build.TotalBytes + reserve ? Path.Combine(PrivateFolder.Resolve(best.RootDirectory.FullName), CopyName) : null;
    }

    /// <summary>
    /// Downloads the supported build from Steam with the player's account (QR sign-in, or the
    /// saved sign-in of an interrupted download) into <see cref="DownloadFolder"/>.
    /// </summary>
    public async Task<string> DownloadAsync(Action<string> qrCode, IProgress<SteamDownload.Status> progress, CancellationToken ct)
    {
        var folder = DownloadFolder() ?? throw new LauncherException($"No drive has {Build.TotalBytes / 1e9:0} GB free for the game.");
        settings.DownloadFolder = folder;
        settings.DownloadBuild = Build.Fingerprint;
        settings.Save();
        using var steam = new SteamDownload();
        steam.QrCode += qrCode;
        var signIn = await steam.SignInAsync(settings.SteamSignIn(), ct);
        settings.SetSteamSignIn(signIn);
        settings.Save();
        await steam.DownloadAsync(Build, folder, progress, ct);
        return folder;
    }

    // ---------- Install ----------

    public async Task InstallAsync(ClientSource source, Action<string> status, IProgress<(long, long)> progress, CancellationToken ct)
    {
        var target = TargetFor(source.Folder);
        Directory.CreateDirectory(target);
        status(source.Mode == ClientFiles.Mode.HardLink
            ? "Linking the game files from your Steam install (no extra disk space)…"
            : "Copying the game files from your Steam install (about 88 GB)…");
        await ClientFiles.PlaceAsync(source.Folder, target, Build, source.Mode, progress, ct);
        status("Checking the game files…");
        var check = await ClientFiles.CheckAsync(target, Build, CheckLevel.Quick, progress, ct);
        if (!check.Ok) throw new LauncherException($"The installed files do not match the supported build ({check.Problem}).");
        await FinishAsync(target, status);
    }

    /// <summary>Uses a folder that already holds the supported build (a download, an older copy) in place: checked, not copied.</summary>
    public async Task AdoptAsync(string folder, Action<string> status, IProgress<(long, long)> progress, CancellationToken ct)
    {
        // Accept the folder that contains the game folder, and the Aion2 folder inside it, too.
        folder = new[] { folder, Path.Combine(folder, "AION2"), Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder)) }
            .FirstOrDefault(f => f is not null && File.Exists(Path.Combine(f, Build.Executable)))
            ?? throw new LauncherException("That folder has no game files. Choose the folder that contains the Aion2 and Engine folders.");
        status("Checking the game files…");
        var check = await ClientFiles.CheckAsync(folder, Build, CheckLevel.Quick, progress, ct);
        if (!check.Ok) throw new LauncherException($"That copy is not the version this server needs ({check.Problem}).");
        await FinishAsync(Path.GetFullPath(folder), status);
    }

    async Task FinishAsync(string folder, Action<string> status)
    {
        status("Allowing the game to reach only this server (approve the Windows prompt)…");
        if (!await Elevated.RunAsync("--setup-firewall", folder, config.ServerAddress))
            throw new LauncherException("The Windows permission prompt was declined, so the firewall rules were not added. Press Try again and choose Yes.");
        Copy.Folder = folder;
        Copy.VerifiedStamp = QuickStamp(folder);
        Copy.FirewallAddress = config.ServerAddress;
        // The Steam sign-in was only kept to resume the download.
        settings.DownloadFolder = null;
        settings.DownloadBuild = null;
        settings.SetSteamSignIn(null);
        settings.Save();
    }

    public static bool FirewallRulesPresent()
    {
        using var process = Process.Start(new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{FirewallRules.RuleName}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    /// <summary>The firewall rules for this copy and this server (another server's address replaces the rules).</summary>
    public async Task<bool> RepairFirewallAsync()
    {
        // The Steam copy has no rules (they would block the official game too), and with it alone there is no private copy.
        if (InstallFolder is not { } folder) throw new LauncherException("There is no private game copy to set firewall rules for; the Steam copy runs without them.");
        if (!await Elevated.RunAsync("--setup-firewall", folder, config.ServerAddress)) return false;
        Copy.FirewallAddress = config.ServerAddress;
        settings.Save();
        return true;
    }

    /// <summary>
    /// Starts the game: account launch code into the private settings, hosts lines for this session
    /// (elevated helper), then AION2.exe unelevated with Steam's launch arguments.
    /// </summary>
    public async Task<Process> PlayAsync(SteamInstall steam, Action<string> status, IProgress<(long, long)> progress, CancellationToken ct)
    {
        var version = Version;
        var folder = version.IsSteam
            ? ClientVersions.SteamFolder(version, config, steam) ?? throw new LauncherException("The Steam copy of AION2 was not found in Steam's libraries.")
            : version.ClientPath ?? InstallFolder ?? throw new LauncherException("The game is not installed.");
        if (Elevated.GameRunning(folder)) throw new LauncherException("The game is already running.");
        GameFolder = folder;
        if (Process.GetProcessesByName("steam").Length == 0)
        {
            status("Starting Steam…");
            Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true });
            for (var i = 0; i < 60 && Process.GetProcessesByName("steam").Length == 0; i++) await Task.Delay(1000, ct);
            await Task.Delay(5000, ct);
        }

        // Steam version: used read-only in place. No build check (it is newer than supported-build.json),
        // no firewall rules (they would also block the official game) and no pak patch (it would write into Steam's folder).
        if (!version.IsSteam)
        {
        status("Checking the game files…");
        var sizes = await ClientFiles.CheckAsync(folder, Build, CheckLevel.Sizes, null, ct);
        if (!sizes.Ok) throw new LauncherException($"Game files changed ({sizes.Problem}). Use Check all files, or Uninstall and set up again.");
        var stamp = QuickStamp(folder);
        if (stamp != Copy.VerifiedStamp)
        {
            var quick = await ClientFiles.CheckAsync(folder, Build, CheckLevel.Quick, progress, ct);
            if (!quick.Ok) throw new LauncherException($"Game files changed ({quick.Problem}). Uninstall and set up again.");
            Copy.VerifiedStamp = stamp;
            settings.Save();
        }
        if (!FirewallRulesPresent() || Copy.FirewallAddress != config.ServerAddress)
        {
            status(Copy.FirewallAddress is { } other && other != config.ServerAddress
                ? "Pointing the firewall rules at this server (approve the Windows prompt)…" : "Restoring the firewall rules (approve the Windows prompt)…");
            if (!await RepairFirewallAsync()) throw new LauncherException("The firewall rules are missing and setup was cancelled.");
        }
        }

        var app = AppInfo.Read(await File.ReadAllBytesAsync(steam.AppInfoPath, ct), SteamInstall.AppId);
        var (executable, steamArguments) = AppInfo.LaunchArguments(app, config.SavedDirSuffix);
        var arguments = ClientVersions.Arguments(steamArguments, version);
        if (!executable.Equals(Build.Executable, StringComparison.OrdinalIgnoreCase))
            throw new LauncherException("Steam's launch entry for the game changed; KiskLauncher needs an update.");

        status("Getting a launch code from the server…");
        var launch = await Account.AuthorizeLaunchAsync(ct);
        GamePlatformIni.Write(GamePlatformIni.PathFor(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), config.SavedDirSuffix),
            config.BootstrapUrl(launch.Authorization));

        status("Pointing the game's online services at this server (approve the Windows prompt)…");
        var helper = Elevated.Start("--hosts-session", folder, config.ServerAddress)
            ?? throw new LauncherException("The game needs the hosts entries for this session; the prompt was cancelled.");
        helper.Dispose();
        for (var i = 0; i < 30 && !HostsFile.HasBlock(await File.ReadAllTextAsync(HostsFile.SystemPath, ct)); i++) await Task.Delay(500, ct);
        if (!HostsFile.HasBlock(await File.ReadAllTextAsync(HostsFile.SystemPath, ct)))
            throw new LauncherException("The hosts entries were not added (see elevated.log in %LOCALAPPDATA%\\KiskPrivate).");

        if (!version.IsSteam) ClientPatchSupport.DeployIfEnabled(folder, settings.AllowClientPatchPaks);
        else if (settings.AllowClientPatchPaks) status("Client patch paks are not applied to the Steam copy (its files are never changed).");

        status("Starting the game…");
        var exe = Path.Combine(folder, executable.Replace('/', '\\'));
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new LauncherException("The game did not start.");
    }

    public async Task<BuildCheck> VerifyAllAsync(IProgress<(long, long)> progress, CancellationToken ct)
    {
        // The Steam copy is newer than the reference by design; only a private copy is checked.
        if (Version.IsSteam || InstallFolder is not { } folder) return BuildCheck.Fail("the Steam copy is used as it is and not checked against the server's version", 0);
        var result = await ClientFiles.CheckAsync(folder, Build, CheckLevel.Full, progress, ct);
        if (result.Ok) { Copy.VerifiedStamp = QuickStamp(folder); settings.Save(); }
        return result;
    }

    /// <summary>Sizes and write times of the quick-check files: unchanged means the last hash check still holds.</summary>
    string QuickStamp(string folder)
    {
        var text = new StringBuilder();
        foreach (var file in Build.Files.Where(f => f.Quick))
        {
            var info = new FileInfo(Path.Combine(folder, file.Path));
            text.Append(file.Path).Append('|').Append(info.Exists ? info.Length : -1).Append('|').Append(info.Exists ? info.LastWriteTimeUtc.Ticks : 0).Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
