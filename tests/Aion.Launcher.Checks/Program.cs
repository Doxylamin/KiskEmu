// Checks of the launcher's core logic: Steam metadata parsing, the supported-build reference,
// settings/hosts/firewall text, and file placement. Uses temporary folders; when this PC has
// Steam with AION2 cached, the real appinfo.vdf is parsed too (read-only).
using System.Net;
using System.Text;
using Aion.Launcher;

var checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAILED: " + label);
    checks++;
}

// ---------- Config ----------
// No server is built in; the checks use the example launcher.json (what a server's download ZIP carries).
var example = AppContext.BaseDirectory;
while (example is not null && !File.Exists(Path.Combine(example, "launcher", "launcher.example.json"))) example = Path.GetDirectoryName(example);
var config = LauncherConfig.Load(Path.Combine(example ?? throw new Exception("launcher/launcher.example.json not found"), "launcher", "launcher.example.json"));
Check(config.Server.ToString() == config.ServerAddress && config.AccountUrl.StartsWith("https://"), "example launcher.json");
Check(config.SavedDirSuffix == ServerSetup.DerivedSuffix(new Uri(config.AccountUrl)), "no savedDirSuffix: derived from the account address, as for a server added by hand");
Check(config.BootstrapUrl(new string('A', 64)) == $"http://{config.ServerAddress}:{config.BootstrapPort}/c/{new string('A', 64)}", "bootstrap URL carries the code");
(config with { CertificateThumbprint = new string('a', 40) }).Validate();
checks++;
foreach (var bad in new[] { config with { ServerAddress = "example.com" }, config with { AccountUrl = "http://x" }, config with { SavedDirSuffix = "Steam" }, config with { CertificateThumbprint = "zz" } })
{
    try { bad.Validate(); Check(false, "invalid config accepted"); } catch (InvalidDataException) { checks++; }
}

// ---------- Client versions ----------
Check(ClientVersions.Selected(config).Id == ClientVersion.DefaultId && !ClientVersions.Selected(config).IsSteam, "default client version is 2.0.6.0");
Check(ClientVersions.Selected(config with { SelectedClientVersion = "steam-latest" }).IsSteam, "steam version selectable");
Check(ClientVersions.Selected(config with { SelectedClientVersion = "nope" }).Id == ClientVersion.DefaultId, "unknown selection falls back");
Check(ClientVersions.Arguments(["-steam", "-lp:x"], new("a", "a", "steam", null, ["-foo", "-steam"])).SequenceEqual(["-steam", "-lp:x", "-foo"]), "extra launch args appended once");
try { (config with { ClientVersions = [new("x", "x", "other")] }).Validate(); Check(false, "bad kind accepted"); } catch (InvalidDataException) { checks++; }
{
    var temp = Directory.CreateTempSubdirectory("a2versions").FullName;
    var cvSteamRoot = Path.Combine(temp, "Steam"); var library = Path.Combine(temp, "Lib");
    Directory.CreateDirectory(Path.Combine(cvSteamRoot, "steamapps")); Directory.CreateDirectory(Path.Combine(library, "steamapps", "common", "AION2"));
    File.WriteAllText(Path.Combine(cvSteamRoot, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\"\n{\n\"1\"\n{\n\"path\" \"" + library.Replace("\\", "\\\\") + "\"\n}\n}\n");
    File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_3393110.acf"), "\"AppState\"\n{\n\"installdir\" \"AION2\"\n\"buildid\" \"26262663\"\n}\n");
    var steamVersion = ClientVersion.Defaults[1];
    Check(ClientVersions.SteamFolder(steamVersion, config, new SteamInstall(cvSteamRoot)) == Path.Combine(library, "steamapps", "common", "AION2"), "steam client found via libraryfolders.vdf");
    Check(ClientVersions.SteamFolder(steamVersion, config with { SteamClientPath = temp }, new SteamInstall(cvSteamRoot)) == temp, "steamClientPath overrides detection");
    Check(ClientVersions.Selected(config, "steam-latest").IsSteam, "the player's saved choice selects a version");
    Check(ClientVersions.Selected(config with { SelectedClientVersion = "steam-latest" }, "2.0.6.0").Id == ClientVersion.DefaultId, "the player's choice beats launcher.json");
    Check(ClientVersions.Selected(config with { SelectedClientVersion = "steam-latest" }, "gone").IsSteam, "an unknown saved choice falls back to launcher.json");
    Directory.Delete(temp, true);
}
// ---------- Account API: kept session, launcher manifest and self-update ----------
var requests = new List<(string Method, string Path, string Body, string? Bearer)>();
var launcherBytes = "new launcher"u8.ToArray();
var launcherHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(launcherBytes));
static HttpResponseMessage Json(int status, string body) => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
using (var account = new AccountClient(config, new StubHandler(async request =>
{
    var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
    requests.Add((request.Method.Method, request.RequestUri!.PathAndQuery, body, request.Headers.Authorization?.Parameter));
    return request.RequestUri.PathAndQuery switch
    {
        "/api/accounts/login" => Json(200, """{"token":"TOKEN1","accountId":"a","expiresAt":"2030-01-01T00:00:00Z"}"""),
        "/api/accounts/me" => request.Headers.Authorization?.Parameter == "TOKEN1" ? Json(200, """{"accountId":"a"}""") : Json(401, "{}"),
        "/api/downloads/launcher/manifest" => Json(200, $$"""{"sha256":"{{launcherHash}}","size":{{launcherBytes.Length}},"version":"0.4.0"}"""),
        "/api/downloads/launcher" => Json(200, """{"url":"/download/Launcher.exe?ticket=T"}"""),
        "/download/Launcher.exe?ticket=T" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(launcherBytes) },
        "/api/accounts/logout" => new HttpResponseMessage(HttpStatusCode.NoContent),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    };
})))
{
    await account.SignInAsync("player", "secret", remember: true);
    Check(account.SignedIn && account.Token == "TOKEN1" && account.ExpiresAt == new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)
        && requests[^1].Body.Contains("\"remember\":true"), "sign-in asks for a kept session");
    await account.SignOutAsync();
    Check(!account.SignedIn && await account.ResumeAsync("TOKEN1") && account.SignedIn && requests[^1].Bearer == "TOKEN1", "a kept session resumes");
    Check(!await account.ResumeAsync("STALE") && !account.SignedIn, "a stale session is refused");
    await account.ResumeAsync("TOKEN1");
    var manifest = await account.LauncherManifestAsync();
    Check(manifest is { Version: "0.4.0" } && manifest.Sha256 == launcherHash && manifest.Size == launcherBytes.Length, "launcher manifest");
    Check(LauncherUpdate.Needed(manifest!, new string('0', 64), launcherBytes.Length) && LauncherUpdate.Needed(manifest!, launcherHash, 1)
        && !LauncherUpdate.Needed(manifest!, launcherHash.ToUpperInvariant(), launcherBytes.Length), "update needed only when the file differs");
    var updateRoot = Directory.CreateTempSubdirectory("aion-launcher-update");
    try
    {
        var exe = Path.Combine(updateRoot.FullName, "Launcher.exe");
        File.WriteAllBytes(exe, "old launcher"u8.ToArray());
        var downloaded = exe + LauncherUpdate.NewSuffix;
        await account.DownloadLauncherAsync(downloaded, null);
        Check(await LauncherUpdate.HashAsync(downloaded) == launcherHash && requests[^1].Path == "/download/Launcher.exe?ticket=T" && requests[^1].Bearer == "TOKEN1",
            "launcher downloaded through a ticket link");
        LauncherUpdate.Swap(exe, downloaded);
        Check(File.ReadAllText(exe) == "new launcher" && File.ReadAllText(exe + LauncherUpdate.OldSuffix) == "old launcher" && !File.Exists(downloaded), "swap keeps the old exe aside");
        File.WriteAllBytes(downloaded, "partial"u8.ToArray());
        await LauncherUpdate.RemoveLeftoversAsync(exe);
        Check(File.Exists(exe) && !File.Exists(exe + LauncherUpdate.OldSuffix) && !File.Exists(downloaded), "leftovers removed");
    }
    finally { updateRoot.Delete(recursive: true); }
}

// ---------- Supported build ----------
var build = SupportedBuild.Embedded;
Check(build.ManifestId == "1699273097574069541" && build.Files.Count == build.FileCount && build.FileCount > 1000, "supported build reference");
Check(build.Files.Single(f => f.Path == build.Executable).Sha256 == "d93b050d623550fcf509888a9afe9cb8bd3864869f06dd3fe9ecaa697337f1ba", "supported AION2.exe hash");
Check(build.Files.Count(f => f.Quick) > 500 && build.DownloadCommand == "download_depot 3393110 3393111 1699273097574069541", "quick set and download command");

// ---------- Generic launcher: server setup, build references, the default server ----------
Check(ServerSetup.Normalize("world.example.com").ToString() == "https://world.example.com/", "bare host gets https");
Check(ServerSetup.Normalize(" https://world.example.com:8780/account/ ").ToString() == "https://world.example.com:8780/", "path dropped, port kept");
foreach (var bad in new[] { "", "http://world.example.com", "https://user@world.example.com", "not a host at all" })
{
    try { ServerSetup.Normalize(bad); Check(false, "bad address accepted: " + bad); } catch (InvalidDataException) { checks++; }
}
Check(ServerSetup.Candidates("world.example.com").Select(u => u.Port).SequenceEqual([443, 8780]), "no port tries 443 then 8780");
Check(ServerSetup.Candidates("world.example.com:9000").Select(u => u.Port).SequenceEqual([9000]), "typed port only");
Check(ServerSetup.AddressFromLink("kisklauncher://add?url=https%3A%2F%2Fworld.example.com%3A8780") == "https://world.example.com:8780", "link carries the address");
Check(ServerSetup.AddressFromLink("kisklauncher://remove?url=x") is null && ServerSetup.AddressFromLink("https://add?url=x") is null, "other links ignored");
{
    var url = new Uri("https://world.example.com:8780/");
    var reply = new ServerReply("World", "203.0.113.5", 8788);
    var first = ServerSetup.Apply(null, url, reply, new string('a', 40));
    Check(first.AccountUrl == "https://world.example.com:8780" && first.SavedDirSuffix == ServerSetup.DerivedSuffix(url) && first.SavedDirSuffix.Length == 32, "derived saved-folder suffix");
    var moved = ServerSetup.Apply(first, url, reply with { ServerAddress = "203.0.113.6", SavedDirSuffix = "Other", Name = "Renamed" }, null);
    Check(moved.ServerAddress == "203.0.113.6" && moved.Name == "Renamed" && moved.SavedDirSuffix == first.SavedDirSuffix && moved.CertificateThumbprint == first.CertificateThumbprint,
        "server moves address and name; suffix and pin stay");
    Check(ServerSetup.DerivedSuffix(url) != ServerSetup.DerivedSuffix(new Uri("https://other.example.com/")), "suffix differs per server");
    try { ServerSetup.Apply(null, url, reply with { ServerAddress = "world.example.com" }, null); Check(false, "hostname game address accepted"); } catch (InvalidDataException) { checks++; }
    Check(ServerSetup.Readable("ab12cd") == "AB:12:CD", "readable thumbprint");
}
{
    var probes = 0;
    var probe = await ServerSetup.ProbeAsync("world.example.com", new StubHandler(request =>
    {
        probes++;
        return Task.FromResult(request.RequestUri!.Port == 8780 && request.RequestUri.AbsolutePath == ServerSetup.ConfigPath
            ? Json(200, """{"name":"World","serverAddress":"203.0.113.5","bootstrapPort":8788}""")
            : throw new HttpRequestException("refused"));
    }));
    Check(probes == 2 && probe.AccountUrl.Port == 8780 && probe.Reply.Name == "World" && probe.UntrustedThumbprint is null, "probe falls back to 8780");
    try
    {
        await ServerSetup.ProbeAsync("world.example.com:8780", new StubHandler(_ => Task.FromResult(Json(404, "{}"))));
        Check(false, "server without the setup accepted");
    }
    catch (AccountException) { checks++; }
}
{
    var files = build.Files;
    var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(build);
    Check(SupportedBuild.Parse(json, build.Fingerprint).Fingerprint == build.Fingerprint && SupportedBuild.ComputeFingerprint(files) == build.Fingerprint, "build reference round trip");
    try { SupportedBuild.Parse(json, new string('0', 64)); Check(false, "wrong fingerprint accepted"); } catch (InvalidDataException) { checks++; }
    var escaping = build with { Files = [.. files.Take(1).Select(f => f with { Path = @"..\evil.dll" })] };
    var escapingJson = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(escaping with { Fingerprint = SupportedBuild.ComputeFingerprint(escaping.Files) });
    try { SupportedBuild.Parse(escapingJson, SupportedBuild.ComputeFingerprint(escaping.Files)); Check(false, "path outside the game folder accepted"); } catch (InvalidDataException) { checks++; }
}
{
    var temp = Directory.CreateTempSubdirectory("a2default").FullName;
    var file = Path.Combine(temp, "launcher.json");
    Check(LauncherConfig.LoadDefault(file) is null, "no launcher.json next to the exe: no server");
    File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(config with { Name = "Override" }));
    Check(LauncherConfig.LoadDefault(file)?.Name == "Override", "launcher.json next to the exe wins");
    Directory.Delete(temp, true);
}

// ---------- Text KeyValues ----------
var kv = KeyValuesText.Parse("""
"libraryfolders"
{
	"0" { "path" "C:\\Program Files (x86)\\Steam" "apps" { "1" "2" } }
	// comment
	"1" { "path" "G:\\SteamLibrary" }
}
""");
Check(kv.Object("libraryfolders")!.Object("1")!.String("path") == @"G:\SteamLibrary", "libraryfolders parse with escapes and comments");

// ---------- Settings file ----------
var ini = GamePlatformIni.WithBootstrap("[/Script/Aion2.AionGamePlatformUserSettings]\r\nFunnelLogHistory=1\r\n\r\n[/Script/Aion2.AionGamePlatformSettings]\r\nOld=1\r\n", "http://1.2.3.4:8087/c/AB");
Check(ini.Contains("FunnelLogHistory=1") && !ini.Contains("Old=1") && ini.Contains("ConfigurationServerUrl=\"http://1.2.3.4:8087/c/AB\""), "settings section replaced, others kept");
Check(GamePlatformIni.WithBootstrap(ini, "http://1.2.3.4:8087/c/CD").Split("[/Script/Aion2.AionGamePlatformSettings]").Length == 2, "settings section appears once");
Check(GamePlatformIni.PathFor(@"C:\L", "KiskPrivate").Replace('\\', '/').EndsWith("AION2/Saved_KiskPrivate/Config/Windows/GamePlatform.ini"), "private saved folder");

// ---------- Hosts ----------
var server = IPAddress.Parse("203.0.113.5");
var original = "127.0.0.1 localhost\r\n# keep me\r\n";
var withBlock = HostsFile.With(original, server);
Check(HostsFile.HasBlock(withBlock) && withBlock.Contains("203.0.113.5 bladnoch.ncsoft.com") && withBlock.Contains("# keep me"), "hosts block added");
Check(HostsFile.With(withBlock, server).Split("bladnoch").Length == 2, "hosts block replaced, not duplicated");
Check(HostsFile.Without(withBlock) == original && !HostsFile.HasBlock(HostsFile.Without(withBlock)), "hosts block removed exactly");
var legacy = original + "# >>> AION2 private server (added by AionLauncher; removed when the game closes)\r\n203.0.113.5 bladnoch.ncsoft.com\r\n# <<< AION2 private server\r\n";
Check(HostsFile.HasBlock(legacy) && HostsFile.Without(legacy) == original, "hosts block from an older launcher removed");
Check(!withBlock.Contains("AION", StringComparison.OrdinalIgnoreCase), "hosts block does not name the game");

// ---------- Firewall ----------
var ranges = FirewallRules.BlockedRanges(server);
Check(ranges.SequenceEqual(["0.0.0.0-126.255.255.255", "128.0.0.0-203.0.113.4", "203.0.113.6-255.255.255.255", "::2-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff"]), "firewall leaves loopback and the server open");
Check(FirewallRules.BlockedRanges(IPAddress.Parse("10.0.0.1"))[0] == "0.0.0.0-10.0.0.0" && FirewallRules.BlockedRanges(IPAddress.Parse("10.0.0.1"))[1] == "10.0.0.2-126.255.255.255", "server below loopback");
var commands = FirewallRules.Commands([@"C:\G\AION2.exe"], server).ToList();
Check(commands.Count == 4 && commands[0].Contains("delete rule") && commands[1].Contains($"delete rule name=\"{FirewallRules.LegacyRuleName}\"")
    && commands[2].Contains("dir=out") && commands[3].Contains("dir=in") && commands[2].Contains("program=\"C:\\G\\AION2.exe\""), "firewall commands (legacy rule deleted too)");

// ---------- The private folder: KiskPrivate, an old A2CorePrivate renamed in place ----------
var privateRoot = Directory.CreateTempSubdirectory("kisk-private-checks");
try
{
    var parent = privateRoot.FullName;
    Check(PrivateFolder.Resolve(parent) == Path.Combine(parent, "KiskPrivate") && !Directory.Exists(Path.Combine(parent, "KiskPrivate")),
        "private folder: KiskPrivate when neither exists (nothing created)");
    var legacyCopy = Path.Combine(parent, "A2CorePrivate", "AION2");
    Directory.CreateDirectory(Path.Combine(legacyCopy, "Aion2"));
    File.WriteAllText(Path.Combine(legacyCopy, "Aion2", "game.txt"), "kept");
    Check(PrivateFolder.Rebase(legacyCopy) == Path.Combine(parent, "KiskPrivate", "AION2")
        && File.ReadAllText(Path.Combine(parent, "KiskPrivate", "AION2", "Aion2", "game.txt")) == "kept"
        && !Directory.Exists(Path.Combine(parent, "A2CorePrivate")), "private folder: an old A2CorePrivate is renamed with its contents");
    Check(PrivateFolder.Rebase(legacyCopy) == Path.Combine(parent, "KiskPrivate", "AION2"), "private folder: a stored old path follows the moved folder");
    var elsewhere = Path.Combine(parent, "Games", "AION2");
    Check(PrivateFolder.Rebase(elsewhere) == elsewhere && PrivateFolder.Rebase(null) is null && PrivateFolder.Rebase("") == "",
        "private folder: other paths stay as they are");
    // Both exist (an old launcher made the old one again): the new one is used and the old one is left alone.
    Directory.CreateDirectory(Path.Combine(parent, "A2CorePrivate"));
    Check(PrivateFolder.Resolve(parent) == Path.Combine(parent, "KiskPrivate") && Directory.Exists(Path.Combine(parent, "A2CorePrivate")),
        "private folder: with both, KiskPrivate wins and the old one is not touched");
    Check(PrivateFolder.Rebase(Path.Combine(parent, "A2CorePrivate", "AION2")) == Path.Combine(parent, "KiskPrivate", "AION2"),
        "private folder: a path in the old folder points at the new one once that exists");
    // The old folder in use (a file open without sharing): it stays, and the path is kept until the next start.
    Directory.Delete(Path.Combine(parent, "KiskPrivate"), recursive: true);
    var busy = Path.Combine(parent, "A2CorePrivate", "busy.bin");
    using (var open = new FileStream(busy, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
    {
        Check(PrivateFolder.Resolve(parent) == Path.Combine(parent, "A2CorePrivate") && PrivateFolder.Rebase(busy) == busy,
            "private folder: in use, the old folder is used as it is");
    }
    Check(PrivateFolder.Resolve(parent) == Path.Combine(parent, "KiskPrivate") && File.Exists(Path.Combine(parent, "KiskPrivate", "busy.bin")),
        "private folder: moved at the next try");

    // The patch pak: KiskEmu's name; files under the old A2Core name leave the game copy.
    var game = Path.Combine(parent, "game");
    var paks = ClientPatchSupport.GamePaksFolder(game);
    Directory.CreateDirectory(paks);
    foreach (var ext in new[] { ".pak", ".sig" }) File.WriteAllText(Path.Combine(paks, ClientPatchSupport.LegacyPatchBaseName + ext), "old");
    File.WriteAllText(Path.Combine(paks, "pakchunk0-Windows.pak"), "base");
    ClientPatchSupport.DeployIfEnabled(game, enabled: false);
    Check(ClientPatchSupport.PatchBaseName == "z_KiskEmu_Patch-Windows_P" && !File.Exists(Path.Combine(paks, ClientPatchSupport.LegacyPatchBaseName + ".pak"))
        && !File.Exists(Path.Combine(paks, ClientPatchSupport.LegacyPatchBaseName + ".sig")) && File.Exists(Path.Combine(paks, "pakchunk0-Windows.pak")),
        "patch pak: the old A2Core pak is removed, base paks stay");
}
finally { try { privateRoot.Delete(recursive: true); } catch (IOException) { } }

// ---------- File checks and placement ----------
var root = Directory.CreateTempSubdirectory("aion-launcher-checks");
try
{
    var source = Path.Combine(root.FullName, "source");
    var files = new Dictionary<string, byte[]> { ["Aion2/Binaries/Win64/AION2.exe"] = "exe"u8.ToArray(), ["Aion2/Content/Paks/a.utoc"] = "toc"u8.ToArray(), ["Aion2/Content/Paks/a.ucas"] = "big data"u8.ToArray() };
    foreach (var (path, bytes) in files) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(source, path))!); File.WriteAllBytes(Path.Combine(source, path), bytes); }
    var test = build with
    {
        Files = files.Select(f => new BuildFile(f.Key, f.Value.Length, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(f.Value)), !f.Key.EndsWith(".ucas"))).ToList(),
        TotalBytes = files.Values.Sum(v => v.Length), FileCount = files.Count,
    };
    Check((await ClientFiles.CheckAsync(source, test, CheckLevel.Full)).Ok, "full check passes");
    Check(ClientFiles.PresentBytes(source, test) == test.TotalBytes, "download progress: all present");
    File.WriteAllBytes(Path.Combine(source, "Aion2/Content/Paks/a.utoc"), "toc and more"u8.ToArray());
    Check(ClientFiles.PresentBytes(source, test) == test.TotalBytes, "download progress: oversized file counts its expected size");
    File.WriteAllBytes(Path.Combine(source, "Aion2/Content/Paks/a.utoc"), "toc"u8.ToArray());
    File.WriteAllBytes(Path.Combine(source, "Aion2/Content/Paks/a.ucas"), "BIG DATA"u8.ToArray());
    var quick = await ClientFiles.CheckAsync(source, test, CheckLevel.Quick);
    var full = await ClientFiles.CheckAsync(source, test, CheckLevel.Full);
    Check(quick.Ok && !full.Ok && full.Problem!.Contains("a.ucas"), "quick check skips data files; full check finds the change");
    File.WriteAllBytes(Path.Combine(source, "Aion2/Content/Paks/a.ucas"), "big data"u8.ToArray());
    foreach (var mode in OperatingSystem.IsWindows() ? new[] { ClientFiles.Mode.Copy, ClientFiles.Mode.HardLink } : [ClientFiles.Mode.Copy])
    {
        var target = Path.Combine(root.FullName, mode.ToString());
        await ClientFiles.PlaceAsync(source, target, test, mode);
        Check((await ClientFiles.CheckAsync(target, test, CheckLevel.Full)).Ok, $"placed by {mode}");
    }
    var moved = Path.Combine(root.FullName, "moved");
    await ClientFiles.PlaceAsync(source, moved, test, ClientFiles.Mode.Move);
    Check((await ClientFiles.CheckAsync(moved, test, CheckLevel.Full)).Ok && !File.Exists(Path.Combine(source, "Aion2/Content/Paks/a.ucas")), "placed by move");
    Check(!(await ClientFiles.CheckAsync(source, test, CheckLevel.Sizes)).Ok, "missing files fail the size check");
}
finally { root.Delete(recursive: true); }

// ---------- Real Steam cache (this PC, read-only) ----------
var steamRoot = Environment.GetEnvironmentVariable("AION_STEAM_ROOT") ?? @"C:\Program Files (x86)\Steam";
var steam = new SteamInstall(steamRoot);
if (File.Exists(steam.AppInfoPath))
{
    var app = AppInfo.Read(File.ReadAllBytes(steam.AppInfoPath), SteamInstall.AppId);
    var (executable, arguments) = AppInfo.LaunchArguments(app, "KiskPrivate");
    Check(executable.Equals("AION2/Binaries/Win64/AION2.exe", StringComparison.OrdinalIgnoreCase), "real appinfo: launch executable");
    Check(arguments.Contains("-saveddirsuffix=KiskPrivate") && arguments.Any(a => a.StartsWith("-lp:")) && arguments.Contains("-steam"), "real appinfo: launch arguments with our suffix");
    Check(steam.Libraries().Count >= 1 && steam.FindApp() is { } found && Directory.Exists(found.Folder), "real Steam: libraries and AION2 install");
    Console.WriteLine("Real Steam cache parsed.");
}

Console.WriteLine($"{checks} launcher checks passed.");

/// <summary>An account server that answers from a function (no network).</summary>
sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
}
