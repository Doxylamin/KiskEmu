-- Database split, step two (server/DATABASES.md): the realm database stops holding character data. Every world with
-- characters here must have been cut into its own database first (tools/postgres_dev.py migrate does that between
-- 092 and 093); otherwise this stops before anything is lost.
DO $guard$
BEGIN
    IF EXISTS (SELECT FROM game.characters c
               WHERE NOT EXISTS (SELECT FROM public.database_split s WHERE s.world_id = c.world_id)) THEN
        RAISE EXCEPTION 'Characters of a world without its own database: run tools/postgres_dev.py migrate (it splits first)';
    END IF;
END $guard$;

-- Character events moved with their world (each world database keeps its own audit); account and staff events stay.
DELETE FROM audit.events WHERE character_id IS NOT NULL;
ALTER TABLE audit.events DROP CONSTRAINT IF EXISTS events_character_id_fkey;

-- Everything but the realm's own tables leaves the game schema.
DO $drop$
DECLARE r record;
BEGIN
    FOR r IN SELECT tablename FROM pg_tables WHERE schemaname = 'game'
             AND tablename NOT IN ('worlds', 'sanctions', 'tickets', 'chat_log', 'scheduled_events', 'server_settings',
                                   'population_samples', 'character_index')
    LOOP
        EXECUTE format('DROP TABLE IF EXISTS game.%I CASCADE', r.tablename);
    END LOOP;
END $drop$;

-- The realm's queries that name characters (account page, login port counts, sanctions by name, staff audit) read
-- the index under the old name.
CREATE VIEW game.characters AS
SELECT character_id, account_id, world_id, wire_id, name, level, race, created_at, last_login_at, last_logout_at
FROM game.character_index;
GRANT SELECT ON game.characters TO aion_app, aion_auth;
