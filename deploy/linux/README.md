# Hosting the server on Linux (Ubuntu 26.04 LTS)

One machine runs everything: the .NET host (account site, platform bootstrap, login, game,
chat, NCGuard sink) and its own PostgreSQL cluster. Measured needs: about 2-3 GB of data
(terrain 0.4 GB, navmesh 1.1 GB, catalogue 0.3 GB, database), 2-3 GB RAM with all maps loaded.
4 cores / 8 GB / 80 GB is comfortable for tens of players.

The server needs a public **IPv4** address: the login redirect and the policy's LoginServerIP
carry the address as an IPv4 literal.

## Ports

| Port | Service | Who connects |
|---|---|---|
| 443 | NCGuard sink (TLS, the client's NC hostnames point here via the player's hosts file) | game client |
| 8087 | platform bootstrap (GamePlatform.ini ConfigurationServerUrl) | game client |
| 3700 | login | game client |
| 13328 | game | game client |
| 13329–13340 | game ports of further worlds (staff dashboard → Worlds) | game client |
| 8091 | chat (HTTP + STOMP WebSocket) | game client |
| 8780 | account site and API (HTTPS) | launcher, browser |
| 80 | only for Let's Encrypt certificate requests (`--domain`) | Let's Encrypt |

Open them in the provider's firewall as well; install.sh opens them in ufw when ufw is active.
Some providers reserve 443 for their own panel: check that it is free.

## 1. On the development PC: pack the server data

```powershell
python tools/pack_server_data.py aion-server-data.tar.gz
```

This collects the bootstrap fixtures, the policy, the NCGuard fixture, terrain, navmesh and the
item/skill catalogue from `research/local` (about 0.6 GB compressed). They are captures and
extractions from the game: copy them only to your own server.

Copy the archive to the server and clone the repository there (see "Getting the code onto the
server" below):

```bash
scp aion-server-data.tar.gz user@server:~
```

## 2. On the server: install

```bash
cd ~/aion
sudo deploy/linux/install.sh --public-address 203.0.113.5 --data ~/aion-server-data.tar.gz \
    --domain aion.example.com --email you@example.com
```

Certificate choices for the account site (port 8780), exactly one:

- `--domain NAME --email ADDR`: Let's Encrypt (DNS A record of NAME must point at the server;
  port 80 open). Renewals copy the new files and restart the host automatically.
- `--self-signed`: a certificate for the IP address. Browsers warn; the launcher must pin it.
- `--certificate cert.pem --key key.pem`: your own files.
- `--proxy`: your own TLS proxy on this machine forwards to `127.0.0.1:8780` (the host then
  listens on loopback for the account site and trusts X-Forwarded-For from loopback). The proxy
  must use another public port: 443 belongs to the NCGuard sink.

### Staff dashboard on its own hostname (`--staff-port`)

By default the staff dashboard sits on the account site at a random private address (shown at the
end of install.sh). With `--staff-port 8781` it gets a port of its own instead, so the player site
and the dashboard can have separate hostnames (e.g. `world.example.com` and `admin.example.com`):

- port 8780 answers the player site only (no dashboard page, no `/api/admin`),
- port 8781 answers the dashboard only: the page at `/`, its scripts, `/api/admin`, `/api/status`,
  `/api/worlds` (world names) and the wardrobe files; everything else is 404.

Use it with `--proxy`: the staff port then listens on `127.0.0.1` only and never needs a firewall
opening. (With a certificate instead of a proxy, the staff port serves the same certificate and
install.sh opens it in ufw.) One proxy port with both names, e.g. nginx:

```nginx
server {
    listen 8443 ssl;  server_name world.example.com;
    ssl_certificate /etc/letsencrypt/live/world.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/world.example.com/privkey.pem;
    location / { proxy_pass http://127.0.0.1:8780; proxy_set_header Host $host;
                 proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for; proxy_set_header X-Forwarded-Proto https; }
}
server {
    listen 8443 ssl;  server_name admin.example.com;
    ssl_certificate /etc/letsencrypt/live/admin.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/admin.example.com/privkey.pem;
    # Optional: allow only your staff's addresses here (allow 198.51.100.0/24; deny all;).
    location / { proxy_pass http://127.0.0.1:8781; proxy_set_header Host $host;
                 proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for; proxy_set_header X-Forwarded-Proto https; }
}
```

or Caddy:

```caddy
world.example.com:8443 { reverse_proxy 127.0.0.1:8780 }
admin.example.com:8443 { reverse_proxy 127.0.0.1:8781 }
```

Never expose 8781 without TLS. Staff sign in with password and authenticator code either way.

What install.sh does:

1. Installs `dotnet-sdk-10.0`, `postgresql`, `python3`, `openssl`, `nodejs`, `npm` (and `certbot` with `--domain`).
2. Creates the `aion` service account and `/var/lib/aion` (state, data, database), `/etc/aion`
   (configuration, certificate), `/opt/aion` (published host, database tools).
3. Unpacks the data archive into `/var/lib/aion/data`.
4. Builds the account page and staff dashboard (React, `web/`) and publishes the host (`dotnet publish -c Release`) to `/opt/aion/host`.
5. First run: creates the database cluster (`tools/postgres_dev.py setup` with
   `AION_PG_HOME=/var/lib/aion/postgres`: 127.0.0.1:55432, generated passwords in
   `credentials.json`), applies migrations, imports the catalogue, installs starter profiles,
   class skills and the equipment rules (`tools/equipment_rules.py`). Later runs apply new migrations only.
6. Writes `/etc/aion/aion.env` (`tools/server_env.py`; mode 640, contains database passwords).
7. Installs and starts `aion-postgres.service` and `aion-host.service`, and enables `aion-backup.timer`
   (daily verified backups, see "Backups").

## Dashboard icons and 3D preview

The staff dashboard shows item icons and a 3D character preview from the wardrobe export. It is
extracted game data, so the host serves it only to signed-in staff (a short-lived cookie the
dashboard requests after sign-in). To add it:

```powershell
python tools/pack_server_data.py --wardrobe-only aion-wardrobe.tar   # about 2.5 GB
scp aion-wardrobe.tar root@<server>:/root/
```

```bash
sudo tar -xf ~/aion-wardrobe.tar -C /var/lib/aion/data --no-same-owner && sudo chown -R aion:aion /var/lib/aion/data/wardrobe
sudo deploy/linux/update.sh      # rewrites aion.env with Aion__WardrobePath and restarts
```

## Updating

Changes are made and tested on the development PC, committed and pushed to the private GitHub
repository. On the server:

```bash
cd ~/aion
sudo deploy/linux/update.sh
```

From the development PC, `tools/deploy.ps1` does this over SSH (ssh config host `aion`: the server's
address, `User root`, a dedicated key `~/.ssh/id_ed25519_aion` whose public half is in the server's
`authorized_keys`): `-Server` pushes the current branch and runs update.sh; `-Launcher` publishes
KiskLauncher and copies the exe and its version file to `/var/lib/aion/downloads`; `-Status` shows the
services and the last log lines.

It backs up the databases (`/var/lib/aion/backups`, newest 10 kept: the realm's `<name>.dump` and one
`<name>.world-<id>.dump` per world database on the machine), pulls, then repeats install.sh
with the options of the last install: rebuilds the host, applies new migrations, restarts.
New server data (re-extracted terrain etc.): copy a new archive and run install.sh with `--data`.

If an update goes wrong, restore its `before-update-*` set (see "Restoring a backup" below).

## Backups

Besides the backup before every update, `aion-backup.timer` (installed and enabled by install.sh, on
the auth host and on every world server) runs `aion-backup.service` daily at 04:30 (up to 15 minutes
later; a run missed while the machine was off happens at the next boot). It runs as `aion`
(`tools/postgres_dev.py scheduled-backup /var/lib/aion/backups`):

1. dumps the realm database and every world database on the machine into a set
   `scheduled-<YYYYmmdd-HHMMSS>.dump` plus `scheduled-<...>.world-<id>.dump`, in a hidden folder first;
2. **verifies** every file by restoring it into a disposable database (`verify-backup`) and checking
   that it has its migrations; only then does the set join the others;
3. applies the retention to the `scheduled-*` sets: the newest set of each of the last
   `AION_BACKUP_KEEP_DAILY` days (default 7) and the newest of each of the last
   `AION_BACKUP_KEEP_WEEKLY` ISO weeks (default 4) are kept, the rest deleted. `before-update-*`
   (newest 10, update.sh) and `before-restore-*` (never deleted; remove them by hand) are not touched.

A failed run deletes only its own files, keeps every older set and leaves the unit failed. Settings go in
`/etc/aion/local.env` (update.sh keeps that file); the timer reads them at the next run:

```bash
AION_BACKUP_KEEP_DAILY=7
AION_BACKUP_KEEP_WEEKLY=4
AION_PG_TIMEOUT=7200   # large databases: seconds a dump or restore may take (default 1800, 0: no limit)
```

Checking on it:

```bash
systemctl list-timers aion-backup.timer             # last and next run
systemctl status aion-backup                        # last run's result ("failed" when it did not verify)
journalctl -u aion-backup -n 30                     # what each run backed up, verified and removed
sudo cat /var/lib/aion/backups/last-scheduled.json  # the same as JSON: ok, files with sizes and counts, error
sudo systemctl start aion-backup                    # a backup now (waits until it is verified)
```

Another time of day: `sudo systemctl edit aion-backup.timer` with `[Timer]`, `OnCalendar=` and then
e.g. `OnCalendar=*-*-* 05:00:00`. The verification needs free database space about the size of the
largest database for a moment. `/var/lib/aion/backups` stays on the same disk: copy sets elsewhere
(e.g. rsync of the folder as root) for a backup that survives losing the machine.

`AION_PG_TIMEOUT` (or `postgres_dev.py --timeout SECONDS`) is the limit of every long database
operation: migrations, catalogue import, dumps, restores and their verification (quick queries keep
their 3 minutes). In `/etc/aion/local.env` it applies to install.sh, update.sh, restore.sh and the timer
alike; raise it when migrations or a backup of a large database time out.

### Restoring a backup

A set is restored as a whole: the realm and the world databases of the same moment, so accounts,
the character index and the worlds' characters agree.

```bash
sudo ls -l /var/lib/aion/backups
cd ~/aion
sudo deploy/linux/restore.sh /var/lib/aion/backups/scheduled-<YYYYmmdd-HHMMSS>.dump
```

Name the set by its realm file, also on a world server (whose sets have only `.world-<id>.dump`
files). restore.sh asks first (`--yes` skips the question), then:

1. stops `aion-host` (every world on this host with it; players are disconnected);
2. backs up the current databases to `before-restore-<time>.dump` (restore.sh that set to undo);
3. verifies every file of the set in a disposable database, and only then drops and recreates each
   database the set has a file for and restores it (`postgres_dev.py restore <set> --yes`; world
   databases the set has no file for are left as they are and listed);
4. applies the migrations of the installed code (`migrate` and `ensure-worlds`, on a world server
   `create-world <id>`), so an older backup works with the current host;
5. starts `aion-host`.

A backup from before the split (one file) restores the old single database; the migrations then split
it again, but keep world databases that already exist: drop those first (`DROP DATABASE
aion_world_<id>`) when restoring such a backup.

## Getting the code onto the server

The repository is private: give the server a read-only deploy key.

```bash
ssh-keygen -t ed25519 -N '' -f ~/.ssh/aion_deploy
cat ~/.ssh/aion_deploy.pub   # GitHub: repository Settings > Deploy keys > Add (read-only)
printf 'Host github.com
  IdentityFile ~/.ssh/aion_deploy
' >> ~/.ssh/config
git clone git@github.com:<owner>/<repo>.git ~/aion
```

## Operating

```bash
systemctl status aion-host aion-postgres
journalctl -u aion-host -f
# Staff roles (after the account has registered on the account site): gamemaster < moderator < admin
# (each rank has what the ranks below it have; the matrix is server/Aion.Core/Security/StaffAccess.cs):
# Staff sign in to the dashboard with password and authenticator code; an account without an authenticator
# sets one up at its first dashboard sign-in. Player sessions do not open the dashboard.
sudo runuser -u aion -- env AION_PG_HOME=/var/lib/aion/postgres python3 /opt/aion/src/tools/postgres_dev.py role USERNAME admin
```

The account site is `https://<domain or address>:8780`; players register there, and the
launcher signs in there.

The server's name on both sites, in the browser tab, on the launcher download and in authenticator
apps is `Aion__Web__SiteName` (default `KiskEmu`). Put it in `/etc/aion/local.env` (update.sh keeps
that file) and restart `aion-host`; no new build is needed. Authenticators set up earlier keep the
name they were set up with.

## Auth host and world servers

As with an authserver and its worldservers, the `aion-host` service is the **auth host**: login port,
world list, account site, chat (8091) and staff dashboard. It runs no world itself. Every world is a
**world server**, an `aion-host` process in world role on its own game port. Migration 091 turns the
world the host used to run itself (1204) into a world server on 13328.

Each world has **its own database** (`aion_world_<id>`: its characters, legions, market and gameplay
log); the auth host keeps the **realm database** `aion` (accounts, staff roles, sanctions, tickets, chat
log, the world registry and an index of every world's characters). A world server never opens the realm
database: it signs in to the auth host over the **realm link** (`/internal/realm/...` on the account
site) and asks it for logins, bans and maintenance there. See `server/DATABASES.md`. The first update
past migration 092 splits an existing database: each registered world gets a copy cut down to its own
characters, then the realm drops them.

The staff dashboard's **Worlds** page (admins) adds, changes, starts, stops and removes worlds. A world
"on this host" is started by the auth host (game port 13328 upwards; its control port 18781 upwards
stays on loopback); its database is made the first time it starts. Start and Stop on the page start and
stop it gracefully (its players are saved), and it is restarted by itself if it exits. Its output goes
to `journalctl -u aion-host` as `[world <id>]` lines. Restarting the service restarts every world on
this host.

A world "on another machine" is added on the Worlds page as remote, without addresses: it reports them
when it signs in. Issue its **sign-in token** there (shield icon; shown once, a new one replaces it),
then install the world server on its machine with the same repository and server data:

```bash
sudo deploy/linux/install.sh --public-address <its address> --data aion-server-data.tar.gz --world <id> \
  --realm-url https://<auth host>:8780 --realm-token aw_<id>_...
```

It gets its own PostgreSQL cluster with only its world's database, game port 13328 and control port 13400
(`--game-port`, `--control-port`); ufw opens the control port to the auth host's address only. With a
self-signed account-site certificate, pin it: `--realm-cert-sha256 <hex>` (`openssl x509 -in
/etc/aion/tls/cert.pem -noout -fingerprint -sha256` on the auth host). The world signs in within a
minute; the Worlds page then shows its addresses, version and last sign-in, and lists it while it
answers. `update.sh` on that machine updates it the same way. Open the game ports in the provider's
firewall as well.

One chat server (8091) serves every world: a player chats with the players of their own world, and
chat commands are answered by their world server. On the staff dashboard, the **world server** picker
chooses the world the world tools act on: live map and players, characters, rewards, GM commands,
maintenance, announcements, server health and restart. Maintenance turns players away from that world
only. Bans, mutes, tickets, the chat log, accounts and scheduled events are the realm's; the Activity
page switches between the world's log and the realm's.

## How clients reach this server

Each launch gets a one-use launch authorization (`POST /api/client-launches`). The launcher
writes it into the bootstrap URL, `ConfigurationServerUrl="http://<address>:8087/c/<authorization>"`;
the host then serves that client a policy whose api_gate keeps the code, and the client's
authn_token request redeems it for its own game ticket. A client started without a code redeems
the newest launch requested from its own IP address (players sharing one address should use codes).

CANDIDATE: that the client keeps the path of ConfigurationServerUrl (the api_gate path is
observed to be kept). The address pairing above covers the case where it does not.

KiskLauncher sets itself up from the account site: `GET /api/launcher/config` (public) gives the site's name,
`Network:PublicAddress`, the bootstrap port and the client versions, each private one naming its build by
fingerprint; `GET /api/launcher/builds/<fingerprint>` serves that build's file checklist (`launcher/supported-build.json`,
built into the host). Optional settings: `Aion:Launcher:ClientVersion` (the version's name, default `2.0.6.0`),
`Aion:Launcher:AllowSteam` (offer "Official latest (Steam)", default true), `Aion:Launcher:SavedDirSuffix` (the
client's private saved folder; unset, each launcher derives one from the account site's address; keep the value the
server's players already use when there is one). The account page's launcher download is a ZIP of
`KiskLauncher.exe` (from the downloads folder) and a `launcher.json` the host writes from these settings, which adds
this server at the launcher's first start; its account address is the one the player downloaded from, or
`Aion:Launcher:AccountUrl`, and `Aion:Launcher:CertificateThumbprint` (40 hex) pins a self-signed certificate.
Custom NPCs (staff dashboard: NPCs) need no setting: the auth host keeps them (migration 102) and every world server
places them within a minute. Their own names and dialog lines need the client patch pak (docs/CUSTOM_NPCS.md). Until
players get it from the launcher, a world treats every client as unpatched; `Aion__CustomNpcs__AssumePatchedClients=true`
in `/etc/aion/local.env` treats every client as patched (testing only: a client without the pak is then sent an NPC
template it does not know).

Players who already have KiskLauncher add the server by its account address, or with the account page's
`kisklauncher://add` link.

Players also need hosts-file lines pointing the six NC hostnames at this server's address
(`research/client-installer.md`); the installer/launcher sets those up.

## Not covered yet

- The player installer/launcher (research/client-installer.md).
- Only tested on Windows so far: the Linux scripts and units have not run on a real Ubuntu
  host yet. First install: watch `journalctl -u aion-host` and report any failure.
