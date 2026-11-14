# KiskLauncher (the player program, Windows)

`KiskLauncher.exe` (project AionLauncher) sets up a private copy of AION2 at the one build the server
supports and starts it against a KiskEmu server. No server is built in: the player adds servers, or a
server's download ZIP carries a `launcher.json` that adds it. It ships no NCSoft files: the game comes from the
player's own Steam install or Steam account. One self-contained exe: downloaded, it offers to install itself for the Windows user
(`%LOCALAPPDATA%\Programs\KiskLauncher`, desktop and Start menu shortcuts, an Apps & features entry whose
uninstall runs `--uninstall`; no administrator rights), or runs where it is ("Run without installing",
remembered). The installed copy is the one the self-update replaces.
Design and evidence: `research/client-installer.md`.

## Projects

- `Aion.Launcher.Core/`: Steam metadata (libraryfolders.vdf, appmanifest, binary appinfo.vdf launch
  arguments), the supported-build reference and file checks/placement, GamePlatform.ini, hosts and
  firewall text, the account API client. Cross-platform; tested by `tests/Aion.Launcher.Checks`.
- `AionLauncher/`: the WinForms app and its elevated helper modes.
- `builds/<game version>.json`: one build reference per client game version, the number the client sends in its
  greeting: every file of that Steam depot 3393111 manifest (path, size, SHA-256), made by `tools/launcher_build.py`.
  - `3527.json` (2.0.6, manifest 1699273097574069541, 1218 files): `--depot-manifest <Steam>/depotcache/3393111_1699273097574069541.manifest`
    from `research/local/dev-client/verification.json`. The launcher embeds it as its fallback build.
  - `3533.json` (Steam build 26262663, manifest 586322693862358479, 1230 files): `--game-version 3533 --scan <client folder>`.
  The host embeds them all and offers the ones its `Aion:Native:ClientVersions` names.
- `launcher.example.json`: what a `launcher.json` next to the exe looks like (a server's download ZIP has one, written
  by the server: name, account URL, IPv4 address, bootstrap port; optional `savedDirSuffix`, `certificateThumbprint`,
  `steamClientPath`). It adds that server at the launcher's first start; nothing is embedded in the exe.
- `AionLauncher/icon.ico` (exe, window, taskbar, shortcuts) and `AionLauncher/emblem.png` (the rail): the KiskEmu
  icon (`web/src/shared/kiskemu-icon.svg`, also the client editor's icon) rendered with resvg; emblem.png is
  1024 px, the ico holds 16-256 px as PNG entries.

## What it does

A guided window with four steps (Sign in, Get the game, Install, Play). Each step works out the
next one itself; the player only confirms and answers Windows prompts.

1. Sign in at the account API first (no 88 GB download for someone without an account). "Keep me signed
   in on this PC" (on by default) asks for a 30-day session and keeps its token in the settings file,
   DPAPI-encrypted for the Windows user (never the password); the next start continues without the
   sign-in page until the server ends the session or the player signs out. Unticked, the session ends
   when the launcher closes.
   Then the launcher asks the server for its launcher's hash (`GET /api/downloads/launcher/manifest`).
   When the server's file differs from the running exe, it says so and offers **Install update**; the
   player chooses ("Not now" continues). Installing downloads the file through the ticket link next to
   the exe (`KiskLauncher.exe.new`), checks the SHA-256, renames the running exe to `KiskLauncher.exe.old`,
   moves the download into place and starts it; the next start removes the old file. `KiskLauncher.exe.version`
   next to the server's file (optional) is the version shown. `AION_LAUNCHER_NO_UPDATE=1` skips the check
   (development builds).
2. Get the game. If the Steam install is still the supported build it is hard-linked (same drive,
   no extra space) or copied. Otherwise the launcher downloads it from Steam itself
   (`Aion.Launcher.Core/SteamDownload.cs`, SteamKit2): the player signs in by scanning a QR code with
   the Steam mobile app (no password reaches the launcher), then the launcher asks for the depot key
   and the manifest request code of depot 3393111 manifest 1699273097574069541 and fetches every
   chunk from Steam's content servers into the private folder (16 chunks in parallel, each checked
   against its SHA-1 id; a bad chunk is fetched again from another server). The folder is
   `%LOCALAPPDATA%\KiskPrivate\AION2` when the system drive has room, else `<drive>:\KiskPrivate\AION2`
   on the fixed drive with the most free space. Pausing or closing keeps the download: finished
   files are listed in `.download-state`, other existing chunks are kept when their SHA-1 matches,
   and the Steam refresh token is kept (DPAPI, current Windows user) so resuming needs no new scan;
   it is deleted when the game is installed. "I already have this version in another folder" uses an
   existing copy (for example a finished Steam console `download_depot` folder) in place.
3. Install: a download is used where it is; a Steam-install source is placed on its own drive. The
   quick hash check runs (game binary and pak index files). Then, elevated once (UAC): firewall rules named "Private game server" for
   every exe in the copy, blocking all remote addresses except loopback and the server, in and out.

The reference lists only files the depot contains. A copy that has been run also holds files the
game creates: `Aion2/InstallLog.txt` and NCGuard's `Aion2/Binaries/Win64/NCGuard/bb64.dll`
(15.8 MB, not the 7.7 MB `Plugins/NCGuardSDK` copy). Whether a fresh copy creates that module while
NC's addresses point at our server is not yet tested.

Play:
1. Checks sizes (and re-hashes the quick set when those files changed).
2. Gets a launch authorization and writes
   `%LOCALAPPDATA%\AION2\Saved_<suffix>\Config\Windows\GamePlatform.ini` (the server's saved-folder suffix) with
   `ConfigurationServerUrl="http://<server>:8087/c/<authorization>"` (the official Saved_Steam is not touched).
3. Elevated per session (UAC): hosts lines sending the six NC hostnames to the server; the helper
   removes them when the private copy's processes exit (so the official game is not redirected otherwise).
4. Starts AION2.exe unelevated with Steam's cached launch arguments, `-saveddirsuffix=<suffix>`.
The session stays signed in for further plays; it signs out when the launcher closes unless it is kept.

Uninstall (the Play page, or Apps & features) removes the firewall rules, any hosts lines, the settings,
the installed program with its shortcuts and (if chosen) the private copy.

## Servers

The launcher keeps a list of servers (`launcher-settings.json`), each with its own sign-in, kept session and
chosen client version. A `launcher.json` next to the exe adds its server once and makes it the current one (the
installed copy and its updates keep it in the settings, without the file); without one the launcher starts on the
servers it has, or asks for one. An older launcher's game copy moves into the new layout, and its user name goes to
the first server added. Its kept session is dropped: the launcher cannot tell which server it was for.

- **The account site's download** is a ZIP (`POST /api/downloads/launcher?package=zip`, then `/download/launcher.zip`
  with the ticket): `KiskLauncher.exe` and the server's `launcher.json`. Self-updates download the exe alone.
- **Add a server**: the player types the account site's address (`world.example.com`, `host:port` or
  `https://...`; without a port the launcher also tries 8780). The launcher reads `GET /api/launcher/config`
  (no sign-in) and shows the server's name, account site and game address before it is added. A certificate
  Windows does not trust (self-signed) is shown as its SHA-1 fingerprint, `AB:CD:...`; the player compares it
  with the one the server's owner published, and the launcher pins it. A saved server whose certificate later
  differs from its pin is refused.
- **`kisklauncher://add?url=<account site>`** links open the installed launcher on that page with the address
  filled in (the account site's "Already have KiskLauncher? Add this server" link). The installed exe registers
  the scheme for the Windows user (`HKCU\Software\Classes\kisklauncher`); the uninstall removes it. A link
  clicked while the launcher is open starts a second window.
- At every start the launcher fetches the server's settings again: name, game address, bootstrap port and
  client versions follow the server; the saved-folder suffix (the server's `savedDirSuffix`, else 32 letters
  derived from the account address) and the pin never change once set.
- Each private client version names its build by fingerprint (SHA-256 over the quick-check files' `path\0size\0sha256\n`
  lines, `tools/launcher_build.py`). The embedded build (3527) is used when it matches; another build's
  reference comes from `GET /api/launcher/builds/<fingerprint>`, is checked against the fingerprint (and that
  every path stays inside the game folder) and cached under `%LOCALAPPDATA%\KiskPrivate\builds`. The server
  sends only this checklist: the game files always come from the player's own Steam install or Steam account.
- Game copies are per build and shared by every server on it (`AION2` for the embedded 3527 build,
  `AION2-<fingerprint>` otherwise). The firewall rules allow one server address; playing on a server with another address re-points
  them (one UAC prompt).

## Client versions

The Play page has a **Client version** dropdown (dark theme, like the rest). The versions are the server's
(`clientVersions` from `/api/launcher/config`); the choice is kept per server in the launcher's settings.

- A game version (`3527`, `3533`, ...): the launcher's private copy of that build, checked against its reference,
  with the firewall rules and the optional Client Editor patch paks. The server lists the versions it is built for
  (`Aion:Native:ClientVersions`, the first is the default); a launcher without a server list offers `3527`.
- `Official latest (Steam)` (`steam-latest`): the player's own Steam install, started in place and
  **read-only**: no file in the Steam folder is written, so no patch paks (they would land in its Paks
  folder), no size/hash check (it is newer than the reference) and no firewall rules (they are per exe
  path and would also block the official game). The redirect works without touching game files: the
  private `Saved_<suffix>\...\GamePlatform.ini` (via `-saveddirsuffix=<suffix>`) plus the per-session hosts
  lines. The folder is `clientPath` of the version, else `steamClientPath` (from the `launcher.json` the server was
  added with), else the AION2 folder found through Steam's `libraryfolders.vdf`.
  The Steam copy counts as installed for this version whatever its build, and when the private build is not on the
  PC yet and Steam has moved past it (every official patch, e.g. 2026-11-11), the download page offers
  "Use my Steam copy" next to the 88 GB download, so the version picker on the Play page is not the only way to it.

When `Aion:Native:ClientVersions` is set, the game port refuses a client whose greeting names another version (one log
line on the server) instead of letting it stall on the loading screen; that includes a Steam copy the official servers
have moved past. `Aion:Native:ClientVersions` and `Aion:Launcher:AllowSteam` choose what the server offers
(`deploy/linux/README.md`).

## The KiskPrivate folder

Launchers before 0.6 kept everything in `A2CorePrivate`. At its first start KiskLauncher renames
`%LOCALAPPDATA%\A2CorePrivate`, and `<drive>:\A2CorePrivate` of a game copy on another drive, to `KiskPrivate` in
place: the same drive, so no copying, and the game copy's hard links to the Steam install stay. Saved game copies
and an interrupted download follow the move, and a moved copy's firewall rules are set up again at the next Play
(they name the game's programs by path). While the old folder is in use (the game or an older launcher running from
it), it is used as it is and the move is tried at the next start. Client patch paks under the old name
(`z_A2Core_Patch-Windows_P.*`) are removed from the game copy at Play.

## Discord

With Discord running on the same PC the launcher shows a Rich Presence (`Aion.Launcher.Core/DiscordPresence.cs`, a small
client for Discord's local IPC pipe `\\.\pipe\discord-ipc-0..9`; no extra package). The logo (`kisklauncher`) is the large
image; text and button come from the server in use, never from the launcher:

- In the launcher: "In the launcher", the server's name.
- Playing: "Playing on <server>", "In game", and the time since Play started the game; back to the launcher state when the
  game's processes have closed.
- A "Server website" button opening the server's account site (only an https address).

Without Discord nothing fails: the launcher tries again every 30 seconds in the background. Closing the launcher clears
the presence. The Play page's **Show the server and the game in Discord** box turns it off (`"DiscordRichPresence": false`
in `launcher-settings.json`; on by default); `"DiscordApplicationId"` there uses another Discord application (with the
same asset keys).

While the game runs, the launcher asks the server every 30 seconds which character the account plays
(`GET /api/characters/current`, with the account session, so players behind one address each see their own) and shows
"Naicha · Cleric · Lv 38" with the class image (`assassin`, `chanter`, `cleric`, `elementalist`, `fighter`, `gladiator`,
`ranger`, `sorcerer`, `templar`). The world reports the character about once a minute, so a new character or level shows
within about a minute and a half. Players who turn off **Show what I play** on the account page's Security tab get the
server and "In game" only. A server without the endpoint leaves the character out.

## Third-party components

SteamKit2 (LGPL-2.1, https://github.com/SteamRE/SteamKit) and QRCoder (MIT) are NuGet packages
bundled into the single-file exe. Steam's console `download_depot` (and DepotDownloader, GPL-2.0, not
used) download the same manifest.

## Build and publish

```powershell
dotnet run --project tests/Aion.Launcher.Checks
dotnet publish launcher/AionLauncher -c Release -r win-x64 -o research/local/launcher-publish -p:DebugType=none
echo 0.6.0 > "research/local/launcher-publish/KiskLauncher.exe.version"
scp "research/local/launcher-publish/KiskLauncher.exe" "research/local/launcher-publish/KiskLauncher.exe.version" root@<server>:/var/lib/aion/downloads/
```

Signed-in players then see "Download launcher" on the account page (a five-minute link from
POST /api/downloads/launcher; the ZIP with the server's `launcher.json`, see Servers).
Installed copies offer the new file at their next start (the version in `KiskLauncher.exe.version` is what they show; keep
it equal to `<Version>` in AionLauncher.csproj).

### GitHub releases

`.github/workflows/kisklauncher.yml` runs on GitHub's Windows runners (the GitHub repository mirrors the forge, tags
included). A push to main that touches the launcher runs the checks and keeps the exe as a workflow artifact. A tag
`launcher-v<Version>` (for example `git tag launcher-v0.5.1 && git push origin launcher-v0.5.1`) must match
`<Version>` in AionLauncher.csproj. It also makes a GitHub release with `KiskLauncher.exe` and `SHA256SUMS`. A server
serves the same exe from its downloads folder.

## Not yet verified (CANDIDATE)

- A full run on a second PC: remote certificate acceptance by the client, Steam ownership checks
  without our dev tools, the per-session hosts helper, and the firewall rules with a remote server.
- Whether Steam still serves manifest 1699273097574069541 to `download_depot`.
- The exe is not code-signed: Windows SmartScreen warns on first start ("More info" -> "Run anyway").
