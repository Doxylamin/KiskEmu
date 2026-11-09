# The realm socket

The auth host (the realm) and its world servers used to talk over two one-way HTTP links: the auth host called each
world's control port (`/internal/world/*`, `WorldLinks`), and each world called the realm (`/internal/realm/*`,
`RealmLinkClient`) mostly on a 5 s timer. The **realm socket** adds one long-lived WebSocket per world that carries
both directions. HTTP stays as the fallback, so a world without the socket still works as before.

Code: `server/Aion.Host/Realm/RealmSocket.cs` (frames, channel, tunnel, counters), `RealmSocketHub` (auth host),
`RealmSocketClient` (world server). Tests: `tests/Aion.Host.Tests/RealmSocketTests.cs`.

## Connection and sign-in

- The **world dials out**: `ws(s)://<Aion:Realm:Url>/internal/realm/socket` (http → ws, https → wss). A world needs no
  inbound port for the realm; its control port can stay on loopback.
- The upgrade request carries the same credentials as every realm-link call: `X-World-Id: <id>` and
  `Authorization: Bearer <world token>` (managed: derived by the auth host; remote: issued on the Worlds page).
  `Aion:Realm:CertificateSha256` pins a self-signed certificate as for HTTP.
- A wrong id or token is answered **403 before the upgrade**; the world logs it and retries with the longest backoff.
- One socket per world: a new socket for the same world replaces the old one (the old one is closed).
- `Aion:Realm:Socket=false` on a world server turns the socket off (HTTP only).

## Frames

Each WebSocket text message is one JSON object. Short keys; absent keys are omitted.

| Key | Meaning |
| --- | --- |
| `t` | `req`, `res`, `evt`, `ping`, `pong` |
| `id` | request id (per sender, increasing); a `res` repeats the id of its `req` |
| `m`, `p` | HTTP method and path with query (`req`) |
| `h` | headers (name → value; several values joined with `, `) (`req`, `res`) |
| `b` | body bytes, base64 (`req`, `res`) |
| `s` | HTTP status (`res`) |
| `e`, `d` | event name and JSON data (`evt`) |

Both sides may send requests and events at any time. Requests are answered in any order (matched by id).
Messages are at most 32 MiB.

### Step 1: the HTTP tunnel

A `req` is an HTTP request for the receiver's own existing routes, unchanged: the receiver replays it against its own
HTTP port on loopback (the same endpoints, filters and checks as before) and sends back status, headers and body.

- World → realm: only `/internal/realm/*`. The realm overwrites `X-World-Id` and `Authorization` with the socket's
  own (signed-in) identity, so a world cannot speak for another one, and drops `X-Forwarded-*`.
- Realm → world: only `/internal/world/*` and `/api/*` (the staff dashboard's `?world=` requests); the world still
  checks `X-World-Control` and the staff sign-in as before.
- Both add `X-Realm-Link: socket` to the replayed request (for logs and tests).

The callers did not change: `WorldLinks` and `RealmLinkClient` send through an `HttpClient` whose handler
(`LinkTunnelHandler`) puts the request on the socket when the world's socket is open and else sends it over HTTP.

### Step 2: events

| Event | From | Data | Effect |
| --- | --- | --- | --- |
| `account.changed` | realm | `{account}` | the world reloads that account into its mirror (roles, disabled) and forgets cached sign-ins of it |
| `state.changed` | realm | `{}` | the world reloads maintenance, sanctions and scheduled events now (instead of within 30 s) |
| `presence.snapshot` | world | `[entries]` | the realm replaces its live list of that world's characters |
| `presence.enter` | world | entry | a character entered the world (or changed name, legion) |
| `presence.leave` | world | `{characterId}` | a character left the world |
| `status` | world | `WorldProcessStatus` | players and connections every 5 s (a remote world is online while it sends these) |

The realm sends `account.changed` after a staff role change or an account disable, and `state.changed` after a
scheduled event is created or cancelled. Sanctions, kicks, notices and announcements were already pushes
(`/internal/world/sanctions`, `/notice`); they now ride the tunnel.

While a world's socket is open, the realm's presence for it is the pushed list (live, no polling); the 2 s poll of
`/internal/world/presence` only runs for worlds without a socket.

## Liveness, reconnect, fallback

- **Heartbeat**: each side sends `ping` every 15 s and closes the socket when it has received nothing for 45 s.
- **Reconnect**: the world retries with exponential backoff (1 s, 2 s, 4 s … at most 30 s, ±20 % jitter); the backoff
  resets after a successful connect.
- **Resync** after every connect: the world signs in again (`register`, over the new socket), reloads maintenance,
  sanctions and events, reloads every account it has mirrored since it started, and sends a full presence snapshot.
- **Fail fast**: when the socket closes, every call waiting on it fails at once. A call that was not yet written, or
  is safe to repeat (GET, PUT, DELETE, or a POST marked retry-safe: register, session validation, sign-in, the
  character sync, sanction reloads), is sent again over HTTP; any other POST already on the wire fails like an
  unreachable world (it may have run on the other side).
- A call made while no socket is open goes over HTTP directly.

## Logs and counters

Logged at Information: connected, closed (with reason), replaced; at Warning: refused token, heartbeat timeout,
reconnect failures. Counters per side (`connects`, `reconnects`, `disconnects`, `refused`, `tunnelled`, `fallbacks`,
`eventsSent`, `eventsReceived`, `heartbeatTimeouts`): the auth host's in `/api/admin/worlds` (`link` per world and
`linkCounters`).

## Still HTTP

- The world supervisor's own calls to managed worlds on loopback: `/internal/world/status` (process supervision) and
  `/internal/world/stop`.
- Every call while a world has no socket (and as the fallback described above).
- The client bridge (`/internal/world-persistence`) and everything player-facing.
