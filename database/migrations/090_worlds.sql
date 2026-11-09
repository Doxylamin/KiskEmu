-- World registry (multi-world): every world the login port can list. A world is a world_id (characters, tickets and
-- world sessions are already scoped by it) with two login-list rows, one per race (0x3909 Light and Dark row ids).
--   primary: served by the host that runs the login port (Aion:Native:WorldId, its own game port);
--   managed: a child aion-host process the primary host starts and stops (Aion:Role=world);
--   remote:  an aion-host in world role on another machine using this database; listed, not started here.
CREATE TABLE game.worlds (
    world_id integer PRIMARY KEY CHECK (world_id BETWEEN 1 AND 65535),
    name text NOT NULL CHECK (octet_length(name) BETWEEN 1 AND 32),
    light_server_id integer NOT NULL UNIQUE CHECK (light_server_id BETWEEN 1 AND 65535),
    dark_server_id integer NOT NULL UNIQUE CHECK (dark_server_id BETWEEN 1 AND 65535),
    kind text NOT NULL DEFAULT 'managed' CHECK (kind IN ('primary','managed','remote')),
    -- Advertised IPv4 address of the game port (null: the primary host's Aion:Network:PublicAddress).
    host text CHECK (host IS NULL OR host ~ '^[0-9]{1,3}(\.[0-9]{1,3}){3}$'),
    game_port integer CHECK (game_port BETWEEN 1 AND 65535),
    -- Loopback HTTP port of a managed child (status and graceful stop).
    internal_port integer CHECK (internal_port BETWEEN 1 AND 65535),
    -- Desired state: listed on the login port and (managed) kept running.
    enabled boolean NOT NULL DEFAULT false,
    level_cap integer CHECK (level_cap BETWEEN 1 AND 100),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK (light_server_id <> dark_server_id),
    CHECK (kind = 'primary' OR game_port IS NOT NULL)
);
CREATE UNIQUE INDEX world_primary_idx ON game.worlds ((true)) WHERE kind = 'primary';
CREATE UNIQUE INDEX world_name_idx ON game.worlds (lower(name));
INSERT INTO game.worlds(world_id,name,light_server_id,dark_server_id,kind,enabled) VALUES (1204,'PotatoeWorld',1361,2361,'primary',true);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.worlds TO aion_app;

-- The login port rebinds a handoff grant to the world the player picked (0x390e) and records the picked row's race
-- for that world's lobby, which may run in another process.
ALTER TABLE auth.client_handoffs ADD COLUMN race smallint;
GRANT UPDATE ON auth.client_handoffs TO aion_auth;
