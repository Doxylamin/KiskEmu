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
7. Installs and starts `aion-postgres.service` and `aion-host.service`.

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

Restore a backup if an update goes wrong:

```bash
sudo systemctl stop aion-host
sudo runuser -u aion -- env AION_PG_HOME=/var/lib/aion/postgres /usr/lib/postgresql/*/bin/pg_restore --clean --if-exists -h 127.0.0.1 -p 55432 -U aion_admin -d aion /var/lib/aion/backups/<file>.dump
# and each world database from its own file:
sudo runuser -u aion -- env AION_PG_HOME=/var/lib/aion/postgres /usr/lib/postgresql/*/bin/pg_restore --clean --if-exists -h 127.0.0.1 -p 55432 -U aion_admin -d aion_world_<id> /var/lib/aion/backups/<file>.world-<id>.dump
```

A backup from before the split (one file) restores the old single database; the next update splits it
again. Drop the world databases first (`DROP DATABASE aion_world_<id>`), or the split keeps them.

(pg_restore asks for the aion_admin password from `/var/lib/aion/postgres/credentials.json`.)

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

Players also need hosts-file lines pointing the six NC hostnames at this server's address
(`research/client-installer.md`); the installer/launcher sets those up.

## Not covered yet

- The player installer/launcher (research/client-installer.md).
- Only tested on Windows so far: the Linux scripts and units have not run on a real Ubuntu
  host yet. First install: watch `journalctl -u aion-host` and report any failure.
