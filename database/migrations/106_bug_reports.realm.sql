-- In-game bug reports ("/bug <text>" in chat, docs/BUG_REPORTS.md): the player's text plus what the world server saw at
-- that moment (character, map, position, quests, nearby spawns and their data rows, the connection's last packets, the
-- server commit) in context, so a report can be reproduced without asking the player. Every world sends its reports over
-- the realm link; the staff dashboard (Bug reports) lists and answers them. A screenshot can be attached to a
-- character's latest report afterwards (the launcher), one per report.
CREATE TABLE game.bug_reports (
    report_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    created_at timestamptz NOT NULL DEFAULT now(),
    account_id uuid REFERENCES auth.accounts,
    character_id uuid,
    character_name text CHECK (char_length(character_name) <= 64),
    world_id integer NOT NULL,
    map_id integer,
    map_name text,
    -- Quest codes active (or just finished) when the report was made: the dashboard's quest filter.
    quests text[] NOT NULL DEFAULT '{}',
    content text NOT NULL CHECK (char_length(content) BETWEEN 3 AND 2000),
    server_commit text CHECK (char_length(server_commit) <= 64),
    context jsonb NOT NULL CHECK (jsonb_typeof(context) = 'object'),
    status text NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'in_progress', 'fixed', 'wont_fix')),
    staff_note text NOT NULL DEFAULT '' CHECK (char_length(staff_note) <= 2000),
    handled_by uuid REFERENCES auth.accounts,
    handled_at timestamptz
);
CREATE INDEX bug_reports_status_idx ON game.bug_reports(status, report_id DESC);
CREATE INDEX bug_reports_map_idx ON game.bug_reports(map_id, report_id DESC);
CREATE INDEX bug_reports_character_idx ON game.bug_reports(character_id, report_id DESC);
CREATE INDEX bug_reports_quests_idx ON game.bug_reports USING gin(quests);

CREATE TABLE game.bug_report_screenshots (
    report_id bigint PRIMARY KEY REFERENCES game.bug_reports ON DELETE CASCADE,
    content_type text NOT NULL CHECK (content_type IN ('image/png', 'image/jpeg', 'image/webp')),
    data bytea NOT NULL CHECK (octet_length(data) BETWEEN 1 AND 8388608),
    created_at timestamptz NOT NULL DEFAULT now()
);

GRANT SELECT, INSERT, UPDATE, DELETE ON game.bug_reports, game.bug_report_screenshots TO aion_app;
GRANT USAGE, SELECT ON SEQUENCE game.bug_reports_report_id_seq TO aion_app;
