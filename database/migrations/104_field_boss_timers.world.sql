-- Field boss respawn timers (docs/FIELD_BOSSES.md), per world: the last kill of each field boss (the client's
-- WorldMapFieldNamed code, tools/data/field-bosses.json) and its announced respawn, so a restart keeps a dead boss dead
-- until its time; and the staff's respawn range in minutes (NULL: the server's default). A row whose respawn_at has
-- passed is a boss that is up.
CREATE TABLE IF NOT EXISTS game.field_bosses (
    code integer PRIMARY KEY,
    killed_at timestamptz,
    respawn_at timestamptz,
    min_minutes integer CHECK (min_minutes BETWEEN 1 AND 10080),
    max_minutes integer CHECK (max_minutes BETWEEN 1 AND 10080),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK ((min_minutes IS NULL) = (max_minutes IS NULL) AND (min_minutes IS NULL OR min_minutes <= max_minutes))
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.field_bosses TO aion_app;
