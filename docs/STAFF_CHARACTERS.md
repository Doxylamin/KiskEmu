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

## Moving a character to another world

Each world has its own database (`<realm>_world_<id>`, server/DATABASES.md). A remote world may run on another machine
with its own PostgreSQL, so no world server reads another world's database: the realm moves the character. An
administrator (ManageAccounts) picks the target world, the target account (the same one by default) and optionally a
new name; the dashboard sends `POST /api/admin/world-moves` to the auth host (`CharacterWorldMoves`), which runs three
steps over the worlds' control ports (`/internal/world/characters/{export,import,finish}`, `WorldLinks.MoveStepAsync`).
Each world keeps one `game.character_world_moves` row per request and side (migration 108), so every step can be repeated
with the same request id.

1. **Export (source world).** It is refused while the character is in the world (`character_online`), deactivated
   (`character_blocked`), in another move (`move_in_progress`), or has open market listings or unpaid sales
   (`market_pending`). Otherwise the character is blocked with reason `transfer:<request id>` and exported: its
   `game.characters` row and the rows of every table with a single-column `character_id` foreign key to it, found in the
   catalogue (`pg_constraint`) rather than listed by hand, each as `to_jsonb`. A table added later moves without a code
   change. These stay in the world: legion membership, join requests and storage history, mail, the purchase ledger
   (`shop_orders`), creation retries and audit. Account-warehouse items stay too, since they belong to the account.
   Tables keyed by other columns stay as well: friends, blocks, friend requests, market history and per-character shop
   limits.
2. **Import (target world).** The target account is mirrored from the realm first. The import is refused if the name is
   taken on that world, case-insensitively (`name_unavailable`; give a new name), the account is unknown or disabled,
   plays the other race there, or has no free lobby slot. It is also refused if the package has a table or column this
   world does not know (`schema_mismatch`; both worlds need the same migrations). Otherwise it makes a new character in
   one transaction:
   - a new `character_id`, so a character can move back to a world that still holds its deleted original;
   - new identity values (`wire_id` on characters and items, `uid` on quests), assigned by the target;
   - a new `instance_id` per item, remapped in every moving table that references items;
   - `quick_slots.item_uid` rewritten to the items' new wire ids;
   - rows the new character got from insert triggers (class skills for its level) replaced by the moved ones;
   - the staff flag dropped unless the target account is staff.
3. **Finish (source world).** After the import, the original is deleted as `delete` does: graveyard account, no slot,
   renamed `#deleted<wire id>`, its rows kept. If the target refused the import, the move is cancelled and the character
   is unblocked.

If a world stops answering after the export, the realm answers `move_incomplete`. The character stays blocked
(`transfer:<request id>`) and the dashboard keeps the request id, so pressing Move again finishes the same move. The
import answers the character it already made. Both worlds audit the move: `staff_character_world_move_out`, `_in`, and
`_finish` or `_cancel`. The realm's character index shows the new character in the target world at the next sync.
