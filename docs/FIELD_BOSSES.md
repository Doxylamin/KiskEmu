# Field bosses

Field bosses respawn on their own timer, and the world server keeps it in its database. A boss killed before a restart
stays dead until its time. Staff see and manage the timers on the dashboard's **Field bosses** page.

## Which monsters

The client's `WorldMapFieldNamed` table lists the bosses the official world map shows. There are 85 rows: 24 on Verteron
(1010), 24 on Altgard (1110), 8 on Lower and 5 on Middle Reshanta (20, 22), and 24 in the level-80 regions of Eltnen
(1011) and Morheim (1111). Each row gives:

- a code: map × 100 + n on the starter field maps and in the Abyss, or the NPC code itself in the level-80 regions;
- the boss's NpcData row (`FieldNamedName`).

`tools/field_bosses.py build <table.json>` turns that table (as `{code: NpcData id}`) into `tools/data/field-bosses.json`.
It takes the map from the MapData spawns in `tools/data/world-npcs.json`. The input is extracted from the local client and
stays out of the repository.

A boss is the first spawn of its NPC on its map (lowest entity key). On map 20, Watcher Kaira (2002) has six spawn
records; the other five stay ordinary monsters. Instance layers and dungeons keep their own rules.

## Respawn

When a boss dies, the server draws its respawn time from the boss's range. The kill can come from a player, a summon,
another NPC or the staff's "clear area". The server stores the kill time and the respawn time in `game.field_bosses`
(world migration 104). The corpse goes `GameSession.CorpseMs` after the kill. The boss appears again at its respawn time,
on the next tick of a player on its map.

At startup the world server reads the stored timers (`FieldBossWorld`, retried every 10 s until the database answers):

- A boss whose respawn time is still ahead is dead until then.
- A map loaded before the timers arrived drops such a boss as soon as they do.
- Rows whose time has passed are bosses that are up.

### What is official

OBSERVED: the official field boss table (`0x9101`, `DevPacketData_Common_WorldMapFieldNamedInfo`) was decoded from our
captures of 2026-11-02 to 11-05 (29 captures, 697 tables, all Verteron, about one a second while the player is on the
map). Each boss there is alive with its spawn time, or dead with its announced respawn time.

- The respawn time is fixed at the kill. Every respawn we saw (25) came exactly at the announced millisecond.
- The wait differs from boss to boss, but every kill of a boss fits one fixed wait; no boss has conflicting kills.
  A kill lies between the last table showing the boss up and the first showing it dead, so the wait lies between
  respawn − first dead and respawn − last up. The respawn still ahead when a boss is first seen dead gives a lower bound.

#### Verteron and Altgard: fixed waits

The bounds of all kills of a boss, intersected, and the wait the server uses (`VERTERON_WAITS` in
`tools/field_bosses.py`, written to `waits` in `tools/data/field-bosses.json`). All are **CANDIDATE**. "Fit" means the
bounds pin that round value; "guess" means a value inside wide or one-sided bounds.

| Code | Bounds (h) | Wait | |
| --- | --- | --- | --- |
| 101001 | 0.42 – 3.50 | 3 h | guess |
| 101002 | 0.45 – 6.65 | 3 h | guess |
| 101003 | 0.39 – 3.46 | 3 h | guess |
| 101004 | 1.79 – 6.99 | 4 h | guess |
| 101005 | 1.90 – 6.94 | 4 h | guess |
| 101006 | 1.45 – 1.77 (4 kills) | 1.5 h | fit |
| 101007 | 0.86 – 7.22 | 4 h | guess |
| 101008 | 2.99 – 3.01 (one kill bracketed within 91 s) | 3 h | fit |
| 101009 | 2.99 – 3.02 | 3 h | fit |
| 101010 | 2.92 – 3.17 | 3 h | fit |
| 101011 | 3.60 – 4.24 | 4 h | fit |
| 101012 | 5.57 – 15.86 | 6 h | guess |
| 101013 | ≥ 3.83 | 4 h | guess |
| 101014 | 5.83 – 15.72 | 6 h | guess |
| 101015 | 2.70 – 3.34 | 3 h | fit |
| 101016 | 3.67 – 4.31 | 4 h | fit |
| 101017 | 1.86 – 2.07 | 2 h | fit |
| 101018 | ≥ 3.58 | 4 h | guess |
| 101019 | 1.89 – 2.11 | 2 h | fit |
| 101020 | ≥ 5.88 | 6 h | guess |
| 101021 (Eternal Gartua) | ≥ 10.80 | 12 h | guess |
| 101022 | ≥ 5.68 | 6 h | guess |
| 101023 | ≥ 5.74 | 6 h | guess |
| 101024 (High Commander Lagta) | ≥ 10.85 | 12 h | guess |

We have no captures of Altgard (1110). Its 24 bosses have the same NPC grade and level as Verteron's, index by index
(111001 and 101001 through 111024 and 101024), so 1110nn uses 1010nn's wait. CANDIDATE.

#### The Abyss bosses: official schedules

The client's `PeriodSpawn` table gives the 13 Abyss bosses a fixed schedule. Their WorldMapFieldNamed rows have
SpawnType `PeriodSpawn` and a `PeriodSpawnId`. `tools/field_bosses.py schedules <WorldMapFieldNamed dump> <PeriodSpawn dump>`
writes them into `tools/data/field-bosses.json`. The data is the same in the clients of 2026-11-04 and 2.0.11 (2026-11-11).

| Codes | Map | Schedule (server local time) |
| --- | --- | --- |
| 2001 | 20 | every 12 h from 10:30 |
| 2002 | 20 | every 3 h from 01:00 |
| 2003 – 2005 | 20 | Sunday and Friday, 21:00 |
| 2006 – 2008 | 20 | Thursday and Saturday, 21:30 |
| 2201 – 2202 | 22 | Sunday and Friday, 21:00 |
| 2203 – 2205 | 22 | Monday, Thursday and Saturday, 21:30 |

A killed Abyss boss comes back at the first time of its schedule after the kill. "Server local time" is the world
host's time zone (`FieldBossTimers.TimeZone`); which zone the official servers use is not known.

CANDIDATE: a boss that is never killed stays up. Whether official Abyss bosses also leave or appear only at their time
is not known; our captures cover Verteron only.

#### The level-80 regions: candidate ranges

The other 72 bosses have SpawnType `Basic` and no PeriodSpawn row, so the client data gives them no period. The 48 of
Verteron and Altgard have the fixed waits above. The 24 of the level-80 regions (1011, 1111) have no captures, so
they keep a **CANDIDATE** range (`FieldBossTimers`), drawn after each kill:

| Kind | Which | Default range |
| --- | --- | --- |
| named | grade 1-5 below level 65 | 1.5 – 6 h |
| boss | grade 6 and up, or level 65 and up (the level-80 regions) | 6 – 12 h |

Staff can set a range per boss, from 1 minute to 7 days. It replaces the boss's fixed wait or schedule until it is reset.
Set both ends to the same value for a fixed wait.

Not done: the server does not send the official field boss table (`0x9101`) yet, so the client's world map does not list
the bosses. Its layout is known only in part: there is an optional byte we cannot explain yet. The captures show no kill
notice for field bosses, so the server sends none.

## Staff

The **Field bosses** page shows the world picked in the top bar. It lists each boss with:

- its state: alive, dead, or due (its time has come; it appears when a player is on the map);
- its last kill and its next respawn;
- its timer.

| Action | Route | Permission | Audit |
| --- | --- | --- | --- |
| list | `GET /api/admin/field-bosses` | ViewMonsters (Gamemaster) | – |
| respawn now | `POST /api/admin/field-bosses/{code}/respawn` | ManageMonsters (Moderator) | `staff_field_boss_respawn` |
| set the range | `PUT /api/admin/field-bosses/{code}/timer` `{minMinutes, maxMinutes}` | EditFieldBossTimers (Administrator) | `staff_field_boss_timer` |
| reset the range | `DELETE /api/admin/field-bosses/{code}/timer` | EditFieldBossTimers (Administrator) | `staff_field_boss_timer` (`reset: true`) |

**Respawn now** keeps the last kill on record. A new range applies from the boss's next kill; the current wait does not
change. The live map's **Respawn all** also brings dead bosses back.
