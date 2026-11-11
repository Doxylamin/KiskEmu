#!/usr/bin/env bash
# Restores a backup set: the realm database and every world database the set has a file for, together. Name the set by
# its realm file, also on a world server (which has only <name>.world-<id>.dump files):
#   sudo deploy/linux/restore.sh /var/lib/aion/backups/scheduled-20260101-043000.dump
# Stops aion-host (and with it every world on this host), backs the current databases up to before-restore-*.dump,
# verifies and restores the set (tools/postgres_dev.py restore), applies the migrations of the installed code, then
# starts aion-host again. --yes skips the question.
set -euo pipefail
[[ $EUID -eq 0 ]] || { echo "Run as root (sudo)." >&2; exit 2; }
SET="" YES=0
for arg in "$@"; do
  case "$arg" in
    --yes) YES=1 ;;
    -*) echo "Unknown option $arg" >&2; exit 2 ;;
    *) SET="$(realpath -m "$arg")" ;;
  esac
done
[[ -n "$SET" ]] || { echo "Usage: restore.sh /var/lib/aion/backups/<name>.dump [--yes]" >&2; exit 2; }
STEM="${SET%.dump}"
FILES=("$SET" "$STEM".world-*.dump)
FOUND=()
for f in "${FILES[@]}"; do [[ -f $f ]] && FOUND+=("$f"); done
[[ ${#FOUND[@]} -gt 0 ]] || { echo "No backup files for $SET" >&2; exit 1; }
echo "Restoring, replacing the databases of the same names:"
printf '  %s\n' "${FOUND[@]}"
if [[ $YES -eq 0 ]]; then
  read -r -p "Players are disconnected and current data since this backup is lost (a backup is taken first). Continue? [y/N] " answer
  [[ $answer == [yY]* ]] || exit 1
fi

PG_BIN="$(ls -d /usr/lib/postgresql/*/bin | sort -V | tail -1)"
as_aion() { cd / && runuser -u aion -- env AION_PG_HOME=/var/lib/aion/postgres AION_PG_BIN="$PG_BIN" \
  ${AION_PG_TIMEOUT:+AION_PG_TIMEOUT="$AION_PG_TIMEOUT"} python3 /opt/aion/src/tools/postgres_dev.py "$@"; }
# A larger AION_PG_TIMEOUT from local.env applies here too.
[[ -n "${AION_PG_TIMEOUT:-}" || ! -f /etc/aion/local.env ]] || \
  AION_PG_TIMEOUT="$(sed -n 's/^AION_PG_TIMEOUT=//p' /etc/aion/local.env | tail -1 | tr -dc 0-9)"

systemctl stop aion-backup.service 2>/dev/null || true
systemctl stop aion-host
systemctl start aion-postgres
BEFORE=/var/lib/aion/backups/before-restore-$(date +%Y%m%d-%H%M%S).dump
as_aion backup "$BEFORE"
echo "Current databases backed up to $BEFORE"
as_aion restore "$SET" --yes

# A backup from older code gets the migrations since (as install.sh would apply them).
WORLD=""
if [[ -f /etc/aion/install-args ]]; then
  eval "set -- $(cat /etc/aion/install-args)"
  while [[ $# -gt 0 ]]; do [[ $1 == --world ]] && WORLD="$2"; shift; done
fi
if [[ -n "$WORLD" ]]; then
  as_aion create-world "$WORLD"
else
  as_aion migrate
  as_aion ensure-worlds --catalog /var/lib/aion/data/game-catalog-v2
fi
systemctl start aion-host
echo "Restored from $SET. Status: systemctl status aion-host; undo: restore.sh $BEFORE"
