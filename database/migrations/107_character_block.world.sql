-- Staff block of one character (dashboard Characters → Deactivate): the character stays on its account and in the
-- lobby list (LobbyCharDesc _block_reason_no), but the world refuses to enter it until staff reactivate it.
-- Account-wide bans stay in the realm's game.sanctions; this is per character, so it lives with the character.
ALTER TABLE game.characters
    ADD COLUMN blocked_at timestamptz,
    ADD COLUMN block_reason text NOT NULL DEFAULT '' CHECK (length(block_reason) <= 200);
