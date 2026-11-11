<p align="center"><img src="web/src/shared/kiskemu-icon.svg" alt="" width="128" height="128"></p>

<p align="center">
  <a href="https://kisk.dev/"><img alt="Website" src="https://img.shields.io/badge/Website-kisk.dev-4DEBFF?style=for-the-badge"></a>
  <a href="https://discord.gg/7XDVga5N3t"><img alt="Discord" src="https://img.shields.io/badge/Discord-join-5865F2?style=for-the-badge&logo=discord&logoColor=white"></a>
  <a href="deploy/linux/README.md"><img alt="Run a server" src="https://img.shields.io/badge/Server-host_one-4166D8?style=for-the-badge"></a>
  <a href="launcher/README.md"><img alt="KiskLauncher" src="https://img.shields.io/badge/KiskLauncher-play-4DA6E8?style=for-the-badge"></a>
  <a href="docs/tools/CLIENT_EDITOR.md"><img alt="Client editor" src="https://img.shields.io/badge/Editor-client_tool-8B43E8?style=for-the-badge"></a>
  <a href="CONTRIBUTING.md"><img alt="Contributing" src="https://img.shields.io/badge/Contributing-guide-526AAB?style=for-the-badge"></a>
</p>

# KiskEmu

An open-source server emulator for the AION2 Steam client, with its Windows launcher, KiskLauncher.
KiskEmu is a community project for learning and interoperability. It is not affiliated with, endorsed by or connected
to NCSoft. You need your own copy of the game; the client is not included. See [NOTICE.md](NOTICE.md).
KiskEmu began as a fork of [A2Core](https://github.com/Addiboii/A2Core). Questions, bug reports and help: the
[KiskEmu Discord](https://discord.gg/7XDVga5N3t).

## What it does

- **Game:** login, character creation, the world (NPCs, monsters, objects, quests, map events, dungeons), combat,
  flight, gliding and swimming, inventory, shops (with custom goods and passes), crafting, gathering, parties,
  legions, achievements, chat and mail.
- **Several worlds per server:** one **auth host** (login, world list, account site, chat, staff dashboard) and one
  **world server** per world, each with its own database. Worlds run on the same machine or on others; the realm and
  its worlds talk over one WebSocket per world (the realm socket). Quna is one wallet per account, kept by the realm.
- **Account site:** players register, sign in, set up two-step verification (QR code) and download the launcher.
  Each character has a page: class, level, world, in the game or last seen, where it is (live while playing),
  combat power and legion.
- **Staff dashboard:** gamemaster, moderator and administrator ranks, sign-in with password and authenticator code
  only. Accounts, characters, moderation (bans, mutes, tickets, chat log), rewards and Quna, the shop, events and
  announcements, a live world map, activity logs, server health, and the Worlds page that adds, starts and stops worlds.
- **KiskLauncher:** downloads the supported client build with the player's own Steam account, installs itself
  for the Windows user, updates itself from the server, and starts the game against it.
- **Client editor:** a Windows tool to edit the supported client build without touching its shipped paks
  ([docs/tools/CLIENT_EDITOR.md](docs/tools/CLIENT_EDITOR.md)).

## What is included

A running version: the code under the [AGPL-3.0](LICENSE) and the game data it runs on, generated from the client:
the game tables in `tools/data/`, the server data bundle (`aion-server-data.tar.gz`: terrain, navigation meshes,
item/skill catalogue, platform and NCGuard fixtures; a download on the Releases page, as it is too large for the
repository) and the client type mappings. Setting up a server needs nothing else.

Not included: the client archive key. You only need it to extract new or updated data from the client (see
"Game data" below); finding it is up to you.

## Layout

| Folder | Contents |
|---|---|
| `server/` | The .NET 10 host. One program in two roles: the auth host (account site and API, platform bootstrap, login on 3700, chat, NCGuard sink, staff dashboard) and the world server (game on 13328 and up). `Aion.World` is the game logic, `Aion.Protocol` the packet layer. [`DATABASES.md`](server/DATABASES.md) explains the realm and world databases, [`REALM_LINK.md`](server/REALM_LINK.md) the realm socket. |
| `web/` | The account site (`src/player`) and staff dashboard (`src/staff`), React and Vite, built into the host. |
| `launcher/` | KiskLauncher (WinForms, single-file exe). See [`launcher/README.md`](launcher/README.md). |
| `editor/` | The client editor (WinForms). |
| `deploy/linux/` | `install.sh` / `update.sh` and systemd units for an Ubuntu server. See [`deploy/linux/README.md`](deploy/linux/README.md). |
| `database/` | PostgreSQL migrations: `NNN_name.sql` for every database, `.realm.sql` and `.world.sql` for one kind. |
| `tools/` | Python and .NET tools: client data extraction (`WorldProbe`, `TerrainProbe`, `NavProbe`, `AssetProbe`, ...), table builders (`quests.py`, `world_npcs.py`, `world_objects.py`, `map_events.py`, ...), database setup (`postgres_dev.py`), packet tools, `deploy.ps1`. |
| `tools/data/` | The game tables the server embeds (quests, NPC spawns, objects, map events, items, loot, shops, skills...). |
| `tests/` | xUnit projects (`Aion.World.Tests`, `Aion.Host.Tests`, `Aion.Core.Tests`, `Aion.ClientEditor.Tests`), the `Aion.*.Checks` programs and the Python tests. |
| `data/` | Put the server data bundle from Releases here (`data/aion-server-data.tar.gz`); `install.sh --data` takes it. |
| `third_party/CUE4Parse/` | CUE4Parse (Apache-2.0, upstream commit cdef687 plus three CriWare export classes), which the extraction tools build against. |
| `research/local/tooling/` | The opcode ledger (`opcodes.json`) and the client type mappings (`mappings/*.usmap`). |
| `docs/` | [`CAPTURE.md`](docs/CAPTURE.md) (recording the official server) and the tool guides. |

## Run a server (Ubuntu 26.04, public IPv4)

Clone the repository on the server, download `aion-server-data.tar.gz` from the Releases page into `data/`, then:

```bash
sudo deploy/linux/install.sh --public-address <your IPv4> --data data/aion-server-data.tar.gz --self-signed
```

or `--domain <name> --email <address>` for a Let's Encrypt certificate. This installs the auth host with one world
server (world 1204 on game port 13328) on the same machine. `deploy/linux/README.md` has the ports (443, 3700, 8087,
8091, 8780, 13328 and up), certificate options, backups, and how to run worlds on other machines (`--world`).

Register an account on `https://<server>:8780`, then make it staff (`gamemaster`, `moderator` or `admin`):

```bash
sudo runuser -u aion -- env AION_PG_HOME=/var/lib/aion/postgres python3 /opt/aion/src/tools/postgres_dev.py role <username> admin
```

The staff dashboard is at the private address install.sh prints at the end (on port 8780), or on a port of its own
with `--staff-port`. Staff sign in with password and authenticator code; an account without an authenticator sets
one up at its first sign-in. More worlds are added on the dashboard's Worlds page.

The server's name on both sites, in authenticator apps and on the launcher download is `Aion__Web__SiteName`
(default `KiskEmu`) in `/etc/aion/local.env`; restart `aion-host` after changing it, no rebuild needed.

The first install creates empty databases and fills only the game tables (catalogue, starter profiles, class skills,
equipment rules). To apply code changes, run `update.sh`: it backs up the databases, pulls, rebuilds and applies new
migrations.

## KiskLauncher for your players

KiskLauncher has no server built in: the same exe plays on any KiskEmu server. Get it from the
[GitHub releases](https://github.com/Doxylamin/KiskEmu/releases) (or build it, below) and put it in the server's
downloads folder, `/var/lib/aion/downloads/KiskLauncher.exe` (with an optional `KiskLauncher.exe.version`), or run
`tools/deploy.ps1 -Launcher`.

Signed-in players download it from the account site as a ZIP: `KiskLauncher.exe` and a `launcher.json` the server
writes from its own settings (name, account address, game address). Started next to that file, the launcher adds your
server; installed copies keep it and update themselves from the server. Players who already have KiskLauncher add the
server with the account page's "Add this server" link, or by typing its account address. The launcher gets the game
address and the supported client build from `/api/launcher/config`. (Installs of the earlier "A2Core Launcher" update to
KiskLauncher and move themselves to the new name; the server serves `A2Core Launcher.exe` until a `KiskLauncher.exe`
is there.)

Build it yourself (Windows, .NET 10 SDK):

```powershell
dotnet run --project tests/Aion.Launcher.Checks
dotnet publish launcher/AionLauncher -c Release -r win-x64 -o out/launcher -p:DebugType=none
```

Players need a Steam account that owns the game; the launcher downloads the supported build with it.

> **Do not run the launcher as administrator** (no right-click → "Run as administrator", no compatibility setting).
> The game must not run elevated, and a launcher started as administrator starts the game as administrator too. Start
> it normally: the steps that need administrator rights (the firewall rules and the hosts entries) ask through their
> own Windows prompt.

## Develop

- Server: `dotnet build server/Aion.Host`. Game logic lives in `server/Aion.World` (`GameSession*.cs`, `NpcAi.cs`,
  `MapEvents.cs`, `Quests.cs`, ...); realm and world hosting in `server/Aion.Host` (`Worlds/`, `Realm/`).
- Database: `python tools/postgres_dev.py setup` makes a local PostgreSQL cluster; `migrate` applies the migrations
  to the realm and every world database.
- Web: `npm ci` and `npm run build` in `web/` (the output goes into the host's `wwwroot`); `npm run dev` for Vite.
- Tests: `dotnet test tests/Aion.World.Tests` (the game; the full suite takes a while), `tests/Aion.Host.Tests`,
  `tests/Aion.Core.Tests`; `dotnet run --project tests/Aion.Native.Checks` (packet layouts and game flows) and the
  other `tests/Aion.*.Checks`; Python: `python -m unittest discover -s tests -q`. Some tests skip without raw
  extractions.
- See [CONTRIBUTING.md](CONTRIBUTING.md) for how the code marks what is known and what is guessed.
- Help settle the guesses: [docs/CAPTURE.md](docs/CAPTURE.md) explains how to record and decode the official
  server's messages from your own session, and lists what is still wanted.

## Game data

The included data matches the supported client build. To regenerate or extend it, the table builders in `tools/` read
raw extractions of your client under `research/local/` (MapData, the gameplay tables, the catalogue) and write
`tools/data/*.json`. The extractions come from the .NET tools (`tools/WorldProbe`, `TerrainProbe`, `NavProbe`,
`AssetProbe`) run against your own client install with the client archive key, which you provide yourself
(`research/local/archive-key-validated.json`, ignored by git). Each tool's docstring has its command.
`tools/pack_server_data.py` builds the server data bundle from `research/local/`.
