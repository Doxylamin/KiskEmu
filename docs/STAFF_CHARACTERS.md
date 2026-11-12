# Staff character management

The dashboard's Characters view (`web/src/staff/Characters.tsx`) acts on one stored character through
`POST /api/admin/characters/{id}/actions?world=<id>` (`server/Aion.Host/Authentication/GameplayEndpoints.cs`). The realm
forwards the request to that world's server, which runs it against its own database
(`PostgresGameplayStore.AdminCharacterActionAsync`). Every action:

- needs a staff session. Deactivate, reactivate and cancel deletion need EditCharacters (moderator). Delete and move need
  ManageAccounts (administrator). A staff account's characters need ManageStaff, as sanctions do;
- is confirmed in the dashboard and can be repeated by its request id (`game.admin_requests`). The same id with other
  details is `request_conflict`;
- is recorded in the world's `audit.events` as `staff_character_<action>`, with the actor, the reason and the accounts
  involved;
- is refused (`409 character_online`) while the character is in the world on that server.

| Action | What it does |
| --- | --- |
| `delete` | The same as a lobby deletion that has come due (`CompleteDeletions`): the character moves to the graveyard account `00000000-0000-0000-0000-00000000dead`, loses its lobby slot and any pending deletion, and is renamed `<name>#deleted<wire id>`. This frees the name. Items, quests and audit rows stay. There is no undelete in the dashboard. |
| `cancel_delete` | Clears a deletion the owner asked for in the lobby that is not due yet (`not_pending` otherwise). |
| `block` / `unblock` | Deactivate or reactivate the character (migration 107: `game.characters.blocked_at`, `block_reason`). A blocked character stays on its account and in the lobby list (LobbyCharDesc `_block_reason_no` 1, `_block_end_time` 2100-01-01; how the client shows this is a CANDIDATE, not observed), but `LoadWireAsync` does not return it, so the world refuses to select it. Account-wide bans stay in the realm's `game.sanctions`. |
| `transfer` | Moves the character to another account in the same world. The world server first refreshes the target's account mirror from the realm (`AccountMirrorService.RefreshAsync`). The action is refused when the target is unknown or disabled (`unknown_account`), is the character's own account (`same_account`), already plays the other race in this world (`other_race`), or has no free lobby slot (`no_free_slot`; it has 4 slots plus its purchased extensions). Otherwise the character takes the target's lowest free slot and any pending deletion is cleared. Legion membership, friends, mail and wire ids stay as they are. |

The search (`GET /api/admin/characters?deleted=true|false`) leaves out characters on the graveyard account unless
"Show deleted characters" is ticked. Rows show `deleted`, `disabled`, `deactivated` and `deleting` badges. The realm's
`game.character_index` picks up the new account and name at the next character sync (every 15 s).

## Moving a character to another world (not built)

Each world has its own database (`<realm>_world_<id>`, server/DATABASES.md). A remote world may run on another machine
with its own PostgreSQL, so no world server can read another world's database. A move between worlds therefore goes
through the realm.

1. **Export (source world).** A control-port call `POST /internal/world/characters/{id}/export` locks the character.
   It refuses if the character is online or already blocked by an export. It sets `blocked_at` with reason
   `transfer:<request id>` and returns a JSON package: the `game.characters` row and the rows of every table with a
   `character_id` foreign key to it (about 30 tables in `database/migrations`, all `ON DELETE CASCADE`). The export
   builds this table list from `information_schema` rather than a hand-kept list, and refuses unless every table is
   known to it, so a new character table cannot be skipped by mistake.
2. **Import (target world).** `POST /internal/world/characters/import` with the package and the target account, in one
   transaction:
   - Name: unique per world, case-insensitive. On a clash the import fails with `name_unavailable`, unless the
     dashboard sent a new name (the same rules as the lobby rename).
   - Identities are regenerated. `characters.wire_id`, `item_instances.wire_id` and `character_quests.uid` are
     `GENERATED ALWAYS` and are inserted without these columns, so the target assigns new ones. The import keeps an
     old→new map of item wire ids and rewrites `quick_slots.item_uid` with it. Any other column that stores an item's
     wire id must be listed next to `quick_slots.item_uid` in the import.
   - `character_id` and `instance_id` (UUIDs) can stay as they are: they are unique across worlds.
   - Not moved: legion membership (`guild_members`, plus a legion it leads), friends and friend requests, blocks, mail
     in either direction, market listings, and per-world shop limits keyed by the character. The dashboard says so
     before the staff member confirms.
   - The target account gets a lobby slot and its mirror row, as in `transfer`.
3. **Finish (source world).** After the import commits, the realm tells the source to soft-delete the character, as
   `delete` does. It keeps the original row, renamed, on the graveyard account. If the import fails, the realm tells
   the source to unblock it. If the realm loses track of the move part-way, the `transfer:<request id>` block reason
   lets staff see the stuck character and finish or roll back the move by hand.
4. **Index and audit.** Both worlds record `staff_character_world_transfer` with the request id. The next index sync
   moves the character's row in `game.character_index` to the new world under its new character id, if the import had
   to change it.

The open question is whether to keep `character_id` across worlds. Keeping it makes audit history easy to follow, but
the realm index's primary key is `character_id`. For a short time the source's soft-deleted row and the target's row
would both claim it, so the index needs a `(world_id, character_id)` key first. The alternative is to give the copy a
new `character_id`.
