-- World servers (multi-world): the auth host (login port, account site, chat, staff dashboard) runs no world of its own.
-- Every world is a world server process: managed (started by the auth host) or remote (run elsewhere on this database).
-- The login host's former own world ("primary") becomes a managed world server on the game port it used (13328).

-- Maintenance is per world: the realm-wide setting of migration 028 becomes that world's ("maintenance:{world id}").
UPDATE game.server_settings
SET key = 'maintenance:' || COALESCE((SELECT world_id FROM game.worlds WHERE kind = 'primary'), 1204)
WHERE key = 'maintenance';

UPDATE game.worlds
SET kind = 'managed',
    game_port = COALESCE(game_port, 13328),
    internal_port = COALESCE(internal_port, (SELECT GREATEST(COALESCE(max(internal_port), 18780), 18780) + 1 FROM game.worlds)),
    enabled = true,
    updated_at = now()
WHERE kind = 'primary';

DROP INDEX game.world_primary_idx;
DO $$
DECLARE c text;
BEGIN
    FOR c IN SELECT conname FROM pg_constraint
             WHERE conrelid = 'game.worlds'::regclass AND contype = 'c' AND pg_get_constraintdef(oid) LIKE '%primary%'
    LOOP
        EXECUTE format('ALTER TABLE game.worlds DROP CONSTRAINT %I', c);
    END LOOP;
END $$;
ALTER TABLE game.worlds
    ADD CONSTRAINT world_kind_check CHECK (kind IN ('managed', 'remote')),
    ALTER COLUMN game_port SET NOT NULL;
