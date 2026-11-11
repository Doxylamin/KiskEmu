-- Who is in the game right now (the launcher's Discord status, the account page's "In game"). A world reports the character
-- its game connection plays on that connection's world session, so presence belongs to the signed-in account, never to an
-- address (players behind one address stay apart). The world refreshes seen_at about every minute while connected; a
-- session that stopped reporting (a world that crashed) no longer counts after 3 minutes, and closing the session at
-- disconnect removes the row.
ALTER TABLE auth.world_sessions
    ADD COLUMN character_id uuid,
    ADD COLUMN character_name text CHECK (character_name IS NULL OR length(character_name) BETWEEN 1 AND 64),
    ADD COLUMN character_level integer CHECK (character_level IS NULL OR character_level BETWEEN 0 AND 1000),
    ADD COLUMN character_class text CHECK (character_class IS NULL OR length(character_class) BETWEEN 1 AND 32),
    ADD COLUMN character_race smallint,
    ADD COLUMN map_id integer,
    ADD COLUMN playing_since timestamptz,
    ADD COLUMN seen_at timestamptz;
GRANT UPDATE (character_id, character_name, character_level, character_class, character_race, map_id, playing_since, seen_at)
    ON auth.world_sessions TO aion_auth;

-- Account preferences the player sets on the account page. show_playing (default on, an opt-out): whether the character
-- they play is shown to others (the launcher's Discord status names it; a public page would). The account's own page always
-- shows it to them.
CREATE TABLE auth.account_preferences (
    account_id uuid PRIMARY KEY REFERENCES auth.accounts ON DELETE CASCADE,
    show_playing boolean NOT NULL DEFAULT true,
    updated_at timestamptz NOT NULL DEFAULT now()
);
GRANT SELECT, INSERT, UPDATE ON auth.account_preferences TO aion_auth;
