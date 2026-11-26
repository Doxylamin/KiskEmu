-- Characters moved between worlds by staff (docs/STAFF_CHARACTERS.md). The realm runs a move as three steps over the
-- worlds' control ports: export (source: the character is blocked with reason transfer:<request id>), import (target: a
-- new character with new ids) and finish (source: the original is deleted as the lobby does, or unblocked when the move
-- is cancelled). One row per request and side makes every step repeatable. No foreign keys: the other side's character
-- is in another database.
CREATE TABLE game.character_world_moves (
    request_id uuid NOT NULL,
    direction text NOT NULL CHECK (direction IN ('out', 'in')),
    character_id uuid NOT NULL,
    other_world integer NOT NULL,
    other_character uuid,
    state text NOT NULL CHECK (state IN ('exported', 'imported', 'finished', 'cancelled')),
    created_at timestamptz NOT NULL DEFAULT now(),
    finished_at timestamptz,
    PRIMARY KEY (request_id, direction)
);
GRANT SELECT, INSERT, UPDATE ON game.character_world_moves TO aion_app;
