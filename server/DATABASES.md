# Databases: the realm and the worlds

A server has one **realm database** and one **world database per world**. The auth host uses the realm;
each world server uses its own world database and nothing else. A world server reaches the realm only
through the **realm link**, an HTTP API on the auth host. So a world can run on another machine with its
own PostgreSQL, and no world can read another world's characters or the accounts' passwords.

| Database | Name | Holds | Used by |
| --- | --- | --- | --- |
| Realm | `aion` | `auth.*` (accounts, passwords, TOTP, sessions, game tickets, roles), account and staff events in `audit.events`, `game.worlds` (the world registry), sanctions, tickets, chat log, scheduled events, server settings, population samples, `game.character_index`, custom shop goods (`game.custom_shop_goods`); the catalogue (dashboard) | auth host |
| World | `aion_world_<id>` | the catalogue, `game.*` gameplay tables (characters, items, legions, market, mail, rewards, GM requests, shop purchases and limits, the purchase ledger `game.shop_orders`, memberships), its gameplay events in `audit.events`, a **mirror** of `auth.accounts` and `auth.account_roles` | that world server |

## Migrations

`database/migrations/NNN_name.sql` runs on both kinds of database; `NNN_name.realm.sql` only on the realm
and `NNN_name.world.sql` only on world databases (`tools/postgres_dev.py`: `migration_files(role)`).

The split happens on the first `postgres_dev.py migrate` past 092 (install.sh and update.sh run it):

1. The realm migrations up to 092. 092 adds `game.character_index` (filled from `game.characters`), the
   realm-link columns of `game.worlds` (token hash, control host, last sign-in, version) and
   `public.database_split`.
2. For every registered world without a database, `clone_world` copies the realm database
   (`pg_dump`/`pg_restore`), prunes it to that world's characters and what hangs off them (other worlds'
   characters, rows following single-column foreign keys into `game.*`, legions left without members,
   account-level audit events), then runs the world migrations (092 world drops the realm-only tables).
3. 093 realm checks that every world with characters was split (else it stops before anything is lost),
   drops character events from the realm's audit and every game table that is not the realm's, and
   makes `game.characters` a view of the character index, so the realm's queries that name characters
   (account page, login port counts, sanctions by name) are unchanged.

Account storage follows the world of the character that deposited it. Wire ids are unique within a world
database only.

`postgres_dev.py create-world <id> [--catalog DIR]` makes (or migrates) one world database and seeds a new
one with the catalogue, starter profiles, class skills and equipment rules; `ensure-worlds` does this for
every managed world and migrates every world database in the cluster. The auth host's world supervisor
runs `create-world` before it first starts a managed world (`Aion:Backups:Tool`, `Aion:Backups:PgHome`,
`Aion:Worlds:Catalog`). `backup OUTPUT` writes the realm to OUTPUT and each world database here to
`<stem>.world-<id>.dump`. `AION_PG_REALM` names a disposable realm (`tools/check_multi_world.py`); world
databases are always `<realm>_world_<id>`.

## The account mirror and the character index

A world database keeps `auth.accounts` and `auth.account_roles` so its gameplay queries and foreign keys
stay as they were. The rows are a mirror: the world server refreshes an account from the realm when it
logs in, when a staff member signs in, and when the realm reports a sanction for it (cached 30 s). The
world database has no passwords, TOTP secrets, sessions or tickets.

The realm's `game.character_index` lists every world's characters (id, account, world, wire id, name,
level, race, times). Each world server sends the changes every 15 s (a full list on its first sync), so
the login port's character counts, the account page and chat find characters without opening a world
database.

## The realm link

`/internal/realm/*` on the auth host (the account site's port; plain loopback HTTP for managed worlds).
Every request carries `X-World-Id: <id>` and `Authorization: Bearer <token>`:

- a **managed** world's token is derived by the auth host (HMAC of its control key, `realm-link:<id>`)
  and handed to the process it starts;
- a **remote** world's token (`aw_<id>_<64 hex>`) is issued on the dashboard's Worlds page, shown once;
  the realm stores only its SHA-256. Issuing a new one revokes the old.

| Route | For |
| --- | --- |
| `POST register` | sign-in every 60 s: game host and port, control host, port and token, version (a remote world's addresses come only from here) |
| `POST sessions/open`, `sessions/validate`, `sessions/close` | game logins (the routed grant from the login port) |
| `POST authenticate`, `GET accounts/{id}` | staff sign-ins on the world's control port; account snapshots for the mirror |
| `GET state`, `POST maintenance` | maintenance and active sanctions (cached 2 s) |
| `POST tickets`, `POST chat` | `//report` tickets; announcements and staff chat |
| `POST characters` | the character index sync |
| `GET shop/goods` | the custom shop goods the world sells (cached 30 s by the world) |

The world server's settings: `Aion:Role=world`, `Aion:Native:WorldId`, `ConnectionStrings:World`,
`Aion:Realm:Url` (the account site, e.g. `https://203.0.113.5:8780`), `Aion:Realm:Token`, optionally
`Aion:Realm:CertificateSha256` (pins a self-signed certificate), `Aion:LocalPort` and
`Aion:WorldProcess:ListenAddress`/`ControlHost` for the control port the auth host reaches.
`tools/server_env.py --world` writes them; `deploy/linux/install.sh --world` installs such a machine.

Chat stays on the auth host: the client learns the chat address from the bootstrap policy before it picks
a world. Whisper blocks are world data, so the auth host asks the sender's world server
(`/internal/world/blocked`).

## Not covered

- The old client bridge (`/internal/world-persistence`, `Aion:BridgeSecret` of 64+ characters) reads
  gameplay tables directly; on a split auth host it no longer finds characters. It predates the native
  ports and is not used by the launcher.
- A remote world's level cap is its own `Aion:Progression:LevelCap`, not the Worlds page's.
