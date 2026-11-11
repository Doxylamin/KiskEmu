#!/usr/bin/env bash
# Installs or updates the AION2 server on Ubuntu (26.04 LTS; 24.04 with extra repositories).
# Run as root from a checkout of this repository, with one way to get the account site's certificate:
#   sudo deploy/linux/install.sh --public-address 203.0.113.5 --data aion-server-data.tar.gz --domain aion.example.com --email you@example.com
#   sudo deploy/linux/install.sh --public-address 203.0.113.5 --self-signed
#   sudo deploy/linux/install.sh --public-address 203.0.113.5 --certificate cert.pem --key key.pem
#   sudo deploy/linux/install.sh --public-address 203.0.113.5 --proxy   (your own TLS proxy; see README)
# A world server on its own machine (World Management issues the token; server/DATABASES.md):
#   sudo deploy/linux/install.sh --public-address 198.51.100.9 --data aion-server-data.tar.gz --world 1300 \
#     --realm-url https://203.0.113.5:8780 --realm-token aw_1300_... [--realm-cert-sha256 HEX] [--game-port 13328] [--control-port 13400]
# --data is needed on the first install (pack_server_data.py makes it on the development PC);
# later runs rebuild the host, apply new migrations and restart. See deploy/linux/README.md.
set -euo pipefail

ARGS=("$@")
PUBLIC_ADDRESS="" DATA_ARCHIVE="" TLS="" CERT="" KEY="" DOMAIN="" EMAIL="" SKIP_PACKAGES=0
WORLD="" REALM_URL="" REALM_TOKEN="" REALM_CERT="" GAME_PORT=13328 CONTROL_PORT=13400 STAFF_PORT=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --public-address) PUBLIC_ADDRESS="$2"; shift 2 ;;
    --data) DATA_ARCHIVE="$(realpath "$2")"; shift 2 ;;
    --proxy) TLS=proxy; shift ;;
    --self-signed) TLS=self-signed; shift ;;
    --domain) TLS=letsencrypt; DOMAIN="$2"; shift 2 ;;
    --email) EMAIL="$2"; shift 2 ;;
    --certificate) TLS=files; CERT="$(realpath "$2")"; shift 2 ;;
    --key) KEY="$(realpath "$2")"; shift 2 ;;
    --skip-packages) SKIP_PACKAGES=1; shift ;;
    --world) WORLD="$2"; shift 2 ;;
    --realm-url) REALM_URL="$2"; shift 2 ;;
    --realm-token) REALM_TOKEN="$2"; shift 2 ;;
    --realm-cert-sha256) REALM_CERT="$2"; shift 2 ;;
    --game-port) GAME_PORT="$2"; shift 2 ;;
    --control-port) CONTROL_PORT="$2"; shift 2 ;;
    --staff-port) STAFF_PORT="$2"; shift 2 ;;
    *) echo "Unknown option $1" >&2; exit 2 ;;
  esac
done
[[ $EUID -eq 0 ]] || { echo "Run as root (sudo)." >&2; exit 2; }
[[ -n "$PUBLIC_ADDRESS" ]] || { echo "--public-address is required" >&2; exit 2; }
if [[ -n "$WORLD" ]]; then
  [[ -z "$TLS" ]] || { echo "A world server has no account site: no TLS options with --world" >&2; exit 2; }
  [[ -n "$REALM_URL" && -n "$REALM_TOKEN" ]] || { echo "--world needs --realm-url and --realm-token" >&2; exit 2; }
else
  [[ -n "$TLS" ]] || { echo "Choose --domain/--email, --self-signed, --certificate/--key or --proxy" >&2; exit 2; }
fi
[[ $TLS != letsencrypt || -n "$EMAIL" ]] || { echo "--domain needs --email" >&2; exit 2; }
[[ $TLS != files || -n "$KEY" ]] || { echo "--certificate needs --key" >&2; exit 2; }

REPO="$(cd "$(dirname "$0")/../.." && pwd)"
SRC=/opt/aion/src HOST=/opt/aion/host STATE=/var/lib/aion DATA=/var/lib/aion/data CONFIG=/etc/aion
export AION_PG_HOME=$STATE/postgres

if [[ $SKIP_PACKAGES -eq 0 ]]; then
  apt-get update
  apt-get install -y dotnet-sdk-10.0 postgresql python3 openssl nodejs npm
  [[ $TLS != letsencrypt ]] || apt-get install -y certbot
fi
dotnet --list-sdks | grep -q '^10\.' || { echo ".NET 10 SDK missing (dotnet-sdk-10.0)" >&2; exit 1; }
PG_BIN="$(ls -d /usr/lib/postgresql/*/bin | sort -V | tail -1)"
[[ -x "$PG_BIN/postgres" ]] || { echo "PostgreSQL server binaries missing" >&2; exit 1; }
command -v npm &>/dev/null || { echo "npm missing (nodejs, npm)" >&2; exit 1; }
echo "Using $(dotnet --version), node $(node --version) and $("$PG_BIN/postgres" --version)"

id aion &>/dev/null || useradd --system --home-dir $STATE --shell /usr/sbin/nologin aion
install -d -o aion -g aion -m 750 $STATE $DATA $STATE/ncguard
install -d -o aion -g aion -m 755 $STATE/downloads
install -d -o root -g aion -m 750 $CONFIG
install -d -m 755 /opt/aion

# Server data (bootstrap and NCGuard fixtures, terrain, navmesh, catalogue).
if [[ -n "$DATA_ARCHIVE" ]]; then
  tar -xzf "$DATA_ARCHIVE" -C $DATA --no-same-owner
  chown -R aion:aion $DATA
fi
if [[ -n "$WORLD" ]]; then
  [[ -d $DATA/game-catalog-v2 ]] || { echo "No server data in $DATA; pass --data" >&2; exit 1; }
else
  [[ -f $DATA/bootstrap/keys/app-key.json ]] || { echo "No server data in $DATA; pass --data" >&2; exit 1; }
fi

# Database tools and migrations (the service account must read them), then the host.
rm -rf $SRC && install -d -m 755 $SRC
cp -r "$REPO/tools" "$REPO/database" $SRC/
# Account page and staff dashboard (React) into wwwroot first, so the publish includes them.
(cd "$REPO/web" && npm ci --no-audit --no-fund && npm run build)
dotnet publish "$REPO/server/Aion.Host/Aion.Host.csproj" -c Release -o $HOST.new --nologo
rm -rf $HOST && mv $HOST.new $HOST

# AION_PG_TIMEOUT (seconds for migrations, dumps and restores on large databases) from the environment or local.env.
[[ -n "${AION_PG_TIMEOUT:-}" || ! -f $CONFIG/local.env ]] || AION_PG_TIMEOUT="$(sed -n 's/^AION_PG_TIMEOUT=//p' $CONFIG/local.env | tail -1 | tr -dc 0-9)"
as_aion() { cd / && runuser -u aion -- env AION_PG_HOME="$AION_PG_HOME" AION_PG_BIN="$PG_BIN" ${AION_PG_TIMEOUT:+AION_PG_TIMEOUT="$AION_PG_TIMEOUT"} "$@"; }
FIRST=0
[[ -f $AION_PG_HOME/credentials.json ]] || FIRST=1
systemctl stop aion-host 2>/dev/null || true
# Databases (server/DATABASES.md): the auth host has the realm database and one per managed world (the first migration
# past 092 splits an existing database into them); a world server on its own machine has only its world's. A new
# world database gets the catalogue, starter profiles, class skills and equipment rules.
databases() {
  if [[ -n "$WORLD" ]]; then
    as_aion python3 $SRC/tools/postgres_dev.py create-world "$WORLD" --catalog $DATA/game-catalog-v2
  else
    as_aion python3 $SRC/tools/postgres_dev.py migrate
    # The realm keeps the catalogue for the dashboard (items, maps, NPCs).
    [[ $FIRST -eq 0 ]] || as_aion python3 $SRC/tools/postgres_dev.py import-catalog --directory $DATA/game-catalog-v2
    as_aion python3 $SRC/tools/postgres_dev.py ensure-worlds --catalog $DATA/game-catalog-v2
  fi
}
if systemctl is-active --quiet aion-postgres; then
  # Running under systemd: apply new migrations in place.
  databases
else
  # setup creates the cluster (first run) and starts it with pg_ctl.
  if [[ -n "$WORLD" ]]; then as_aion python3 $SRC/tools/postgres_dev.py setup --world-only
  else as_aion python3 $SRC/tools/postgres_dev.py setup; fi
  databases
  as_aion python3 $SRC/tools/postgres_dev.py stop
fi

if [[ -n "$WORLD" ]]; then
  # A world server: its own database, game and control ports, and the realm link (no account site, chat or login).
  WORLD_ARGS=(--world "$WORLD" --realm-url "$REALM_URL" --realm-token "$REALM_TOKEN" --game-port "$GAME_PORT" --control-port "$CONTROL_PORT")
  [[ -z "$REALM_CERT" ]] || WORLD_ARGS+=(--realm-cert-sha256 "$REALM_CERT")
  as_aion python3 $SRC/tools/server_env.py --public-address "$PUBLIC_ADDRESS" --data $DATA --state $STATE "${WORLD_ARGS[@]}" > $CONFIG/aion.env.new
else
# The account site's certificate, readable by the service: /etc/aion/tls/{cert,key}.pem.
TLS_DIR=$CONFIG/tls
install -d -o root -g aion -m 750 $TLS_DIR
place_tls() { install -o root -g aion -m 640 "$1" $TLS_DIR/cert.pem; install -o root -g aion -m 640 "$2" $TLS_DIR/key.pem; }
case $TLS in
  files) place_tls "$CERT" "$KEY" ;;
  self-signed)
    if [[ ! -f $TLS_DIR/cert.pem ]]; then
      openssl req -x509 -newkey rsa:2048 -nodes -days 825 -subj "/CN=$PUBLIC_ADDRESS"         -addext "subjectAltName=IP:$PUBLIC_ADDRESS" -keyout $TLS_DIR/key.pem -out $TLS_DIR/cert.pem
      chown root:aion $TLS_DIR/*.pem && chmod 640 $TLS_DIR/*.pem
    fi ;;
  letsencrypt)
    # HTTP-01 on port 80 (443 belongs to the NCGuard sink). Renewals copy the files and restart the host.
    HOOK=/etc/letsencrypt/renewal-hooks/deploy/aion.sh
    install -d /etc/letsencrypt/renewal-hooks/deploy
    printf '#!/bin/sh
install -o root -g aion -m 640 /etc/letsencrypt/live/%s/fullchain.pem %s/cert.pem
install -o root -g aion -m 640 /etc/letsencrypt/live/%s/privkey.pem %s/key.pem
systemctl try-restart aion-host
'       "$DOMAIN" "$TLS_DIR" "$DOMAIN" "$TLS_DIR" > $HOOK
    chmod 755 $HOOK
    if command -v ufw &>/dev/null && ufw status | grep -q "Status: active"; then ufw allow 80/tcp; fi
    [[ -d /etc/letsencrypt/live/$DOMAIN ]] || certbot certonly --standalone --non-interactive --agree-tos -m "$EMAIL" -d "$DOMAIN"
    place_tls /etc/letsencrypt/live/$DOMAIN/fullchain.pem /etc/letsencrypt/live/$DOMAIN/privkey.pem ;;
esac
if [[ $TLS == proxy ]]; then TLS_ARGS=(--proxy); else TLS_ARGS=(--certificate $TLS_DIR/cert.pem --key $TLS_DIR/key.pem); fi

# The staff dashboard on its own port (--staff-port, a hostname of its own behind the proxy), or else at a private
# address on the account site, kept across updates.
if [[ -n "$STAFF_PORT" ]]; then TLS_ARGS+=(--staff-port "$STAFF_PORT")
else
  STAFF_PATH="$(grep -s '^Aion__Web__StaffPath=' $CONFIG/aion.env | cut -d= -f2- || true)"
  [[ -n "$STAFF_PATH" ]] && TLS_ARGS+=(--staff-path "$STAFF_PATH")
fi
as_aion python3 $SRC/tools/server_env.py --public-address "$PUBLIC_ADDRESS" --data $DATA --state $STATE "${TLS_ARGS[@]}" > $CONFIG/aion.env.new
fi
chown root:aion $CONFIG/aion.env.new && chmod 640 $CONFIG/aion.env.new && mv $CONFIG/aion.env.new $CONFIG/aion.env

sed "s#@PG_BIN@#$PG_BIN#" "$REPO/deploy/linux/aion-postgres.service" > /etc/systemd/system/aion-postgres.service
cp "$REPO/deploy/linux/aion-host.service" /etc/systemd/system/aion-host.service
# Daily verified backups into /var/lib/aion/backups (owned by aion, like the rest of /var/lib/aion).
install -d -o aion -g aion -m 750 $STATE/backups
sed "s#@PG_BIN@#$PG_BIN#" "$REPO/deploy/linux/aion-backup.service" > /etc/systemd/system/aion-backup.service
cp "$REPO/deploy/linux/aion-backup.timer" /etc/systemd/system/aion-backup.timer
systemctl daemon-reload
systemctl enable --now aion-postgres
systemctl enable aion-host
systemctl restart aion-host
systemctl enable --now aion-backup.timer

if [[ -n "$WORLD" ]] && command -v ufw &>/dev/null && ufw status | grep -q "Status: active"; then
  ufw allow "$GAME_PORT"/tcp
  # The control port answers the auth host only.
  REALM_HOST="$(sed -E 's#^[a-z]+://([^/:]+).*#\1#' <<< "$REALM_URL")"
  if [[ $REALM_HOST =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then ufw allow from "$REALM_HOST" to any port "$CONTROL_PORT" proto tcp
  else echo "Open port $CONTROL_PORT to the auth host only (ufw allow from <its address> to any port $CONTROL_PORT proto tcp)." >&2; fi
elif command -v ufw &>/dev/null && ufw status | grep -q "Status: active"; then
  for port in 443 3700 8087 8091 13328; do ufw allow $port/tcp; done
  # Game ports of further worlds started from the staff dashboard (World Management picks 13329 upwards).
  ufw allow 13329:13340/tcp
  [[ $TLS == proxy ]] || ufw allow 8780/tcp
  # Behind a proxy the staff port listens on loopback only; without one it is a TLS port of its own.
  [[ -n "$STAFF_PORT" && $TLS != proxy ]] && ufw allow "$STAFF_PORT"/tcp
fi
# update.sh repeats this install with the same options (without --data: the data stays unpacked).
SAVED=()
for ((i = 0; i < ${#ARGS[@]}; i++)); do
  if [[ ${ARGS[i]} == --data ]]; then ((i++)); continue; fi
  [[ ${ARGS[i]} == --skip-packages ]] && continue
  SAVED+=("${ARGS[i]}")
done
printf '%q ' "${SAVED[@]}" > $CONFIG/install-args && chmod 640 $CONFIG/install-args
echo "Installed. Status: systemctl status aion-host; logs: journalctl -u aion-host -f"
if [[ -n "$WORLD" ]]; then
  echo "World $WORLD signs in to $REALM_URL within a minute; World Management shows it."
else
  if [[ -n "$STAFF_PORT" ]]; then echo "Staff dashboard: port $STAFF_PORT (point the staff hostname of your proxy at 127.0.0.1:$STAFF_PORT)"
  else echo "Staff dashboard: $(grep '^Aion__Web__StaffPath=' $CONFIG/aion.env | cut -d= -f2-) on the account site (port 8780)"; fi
fi
