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
Verteron captures of 2026-11-02 to 11-05 (697 tables). Each boss there is alive with its spawn time, or dead with its
announced respawn time.

- The respawn time is fixed at the kill. Every respawn we saw (25) came exactly at the announced millisecond.
- The wait differs from boss to boss, and from kill to kill:
  - 101008 and 101009 came back after about 3.0 h.
  - 101006 came back after at most 1.8 h.
  - 101012, 101014 and 101020 took at least 5.6 h.
  - The big bosses 101021 (Eternal Gartua) and 101024 (High Commander Lagta) took at least 10.8 h.
  - Reshanta's 2003 took at least 6.2 h.

The client data has no respawn period: `PeriodSpawnId` points at a table only the server has. So the defaults are
**CANDIDATE** (`FieldBossTimers`):

| Kind | Which | Default range |
| --- | --- | --- |
| named | grade 1-5 below level 65 (the starter field maps' named monsters) | 1.5 – 6 h |
| boss | grade 6 and up, level 65 and up (Abyss, level-80 regions) | 6 – 12 h |

Staff can set a range per boss, from 1 minute to 7 days. Set both ends to the same value for a fixed wait.

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
