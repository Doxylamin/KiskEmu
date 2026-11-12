# Bug reports

A player types `/bug <what happened>` in chat (`//bug` works too). The world server stores the text together with
everything it knows at that moment, so the bug can be reproduced later without asking the player. Staff read the
reports on the dashboard's **Bug reports** page (Players group, needs the HandleTickets permission). Every player can use
it, not only staff. `//report <text>` stays the way to reach the staff about players or being stuck.

## What a report holds

The world server captures the context on the player's own game loop (`GameSession.CaptureBugContext`,
`server/Aion.World/GameSession.BugReport.cs`) and stores it as JSON (`context` in `game.bug_reports`):

- **server**: the commit the host runs (`Aion:Commit` or `AION_COMMIT` when the deployment sets it, else the
  `+<sha>` the .NET SDK puts in the informational version when it builds from a git checkout), the world id.
- **character**: account and character id, name, race, class, level, staff role, HP, dead, in combat.
- **location**: map id and name, instance, position (cm), heading (the latest movement report), subzone id and name.
- **quests**: the active quests with their status, step, goals and the step's objectives (from `tools/data/quests.json`);
  the connection's last 40 quest events (accept, progress, complete, reward) with how long before the report they
  happened; the last 10 rewarded quests.
- **mapEvents**: the map events running for the player (name, stage, step, goals).
- **nearby**: NPCs within 50 m (nearest first, at most 120) with key, NPC id, name, position, heading, state and whether
  the server showed it to this player. `source` says where the NPC comes from: `map_data` with the file
  (`tools/data/world-npcs.json`), the map, the row index in that map's list, the MapData unique key and the row's own
  position and yaw; or `custom_npc`, `staff_spawn`, `summon`, `kibelisk`, `training_dummy`, `runtime`.
- **nearbyObjects**: quest and gather objects within 50 m (`tools/data/world-objects.json` row, shown or not).
- **packets**: the connection's last 256 game messages, oldest first: time before the report, direction (C client,
  S server), opcode and name (`tools/data/opcode-names.json`), length, and the first 40 body bytes as hex. The login
  request's body is never kept (it carries the game ticket).

The packet ring (`ConnectionPacketTrace`) is a fixed buffer per connection (about 14 KB, allocated once). Recording a
message copies at most 40 bytes, so it costs nothing noticeable with many players. The disconnect log still shows the
last 40 messages from the same ring.

## Limits

- One report per character every 60 seconds (`BugReporter.Cooldown`, in memory on the world server).
- At most 30 open reports per account; text 3 to 2000 characters; context at most 1 MB.

## Storage and the realm link

Reports live in the realm (`game.bug_reports`, realm migration 106), so the dashboard lists every world's reports in
one place. A world server sends its reports over the realm link (`POST /internal/realm/bug-reports`); the realm sets the
world id from the caller's sign-in. The host's request limit is 16 KB; this route reads its body under a limit of its own.

## Staff API

All under the account site with a staff sign-in (`Authorization: Bearer <token>`):

| Route | Answers |
| --- | --- |
| `GET /api/admin/bug-reports?status=&map=&quest=&worldId=&before=&limit=` | the list, newest first (`before`: an id, for the next page) |
| `GET /api/admin/bug-reports/{id}` | `{report, context}`: the row and the whole context JSON |
| `GET /api/admin/bug-reports/{id}/markdown` | a self-contained Markdown brief for an AI session (the page's **Copy as Markdown**) |
| `GET /api/admin/bug-reports/{id}/screenshot` | the attached screenshot |
| `PUT /api/admin/bug-reports/{id}` | `{status, note}`: status `open`, `in_progress`, `fixed` or `wont_fix` (audited as `staff_bug_report`) |

The world filter is `worldId`, not `world`: `?world=` sends an `/api/admin` request to that world server.

## Screenshots

`POST /api/bug-reports/screenshot?character=<character id>` with the player's account sign-in and the image as the body
(`image/png`, `image/jpeg` or `image/webp`, at most 8 MB) attaches it to that character's latest report from the last 30
minutes (a second upload replaces the first). It answers the report id, or 404 `no_recent_bug_report`. This is the hook
for the launcher's screenshot button.
