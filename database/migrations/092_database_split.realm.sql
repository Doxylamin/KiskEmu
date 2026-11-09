-- Database split, realm side (tools/postgres_dev.py; server/DATABASES.md). The realm database keeps accounts, staff
-- tools and the world registry; every world keeps its characters in a database of its own (<realm>_world_<id>).
--
-- The character index: the few columns of every world's characters the realm needs (account page, login port's
-- character counts, sanctions by character name, staff search). World servers keep it current over the realm link.
CREATE TABLE game.character_index (
    character_id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES auth.accounts ON DELETE CASCADE,
    world_id integer NOT NULL,
    wire_id integer NOT NULL DEFAULT 0,
    name text NOT NULL,
    level integer NOT NULL DEFAULT 1,
    race integer NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL DEFAULT now(),
    last_login_at timestamptz,
    last_logout_at timestamptz,
    synced_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX character_index_account_idx ON game.character_index(account_id);
CREATE INDEX character_index_name_idx ON game.character_index(lower(name));
CREATE INDEX character_index_world_idx ON game.character_index(world_id);
INSERT INTO game.character_index(character_id,account_id,world_id,wire_id,name,level,race,created_at,last_login_at,last_logout_at)
SELECT character_id,account_id,world_id,COALESCE(wire_id,0),name,level,race,created_at,last_login_at,last_logout_at FROM game.characters;
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_index TO aion_app;
GRANT SELECT ON game.character_index TO aion_auth;

-- World registration: a world server signs in to the realm with its world token (a hash is kept; managed worlds use the
-- token the auth host derives for them). It reports where its game and control ports are and the token the realm
-- uses on its control port.
ALTER TABLE game.worlds
    ADD COLUMN token_hash bytea,
    ADD COLUMN control_host text CHECK (control_host IS NULL OR control_host ~ '^[0-9]{1,3}(\.[0-9]{1,3}){3}$'),
    ADD COLUMN registered_at timestamptz,
    ADD COLUMN last_seen_at timestamptz,
    ADD COLUMN version text,
    -- A remote world's game port is what it reports when it signs in.
    ALTER COLUMN game_port DROP NOT NULL,
    ADD CONSTRAINT world_managed_game_port CHECK (kind <> 'managed' OR game_port IS NOT NULL);

-- Worlds cut from this database by the split (postgres_dev.py split_realm); migration 093 checks it.
CREATE TABLE public.database_split (
    world_id integer PRIMARY KEY,
    database text NOT NULL,
    split_at timestamptz NOT NULL DEFAULT now()
);
