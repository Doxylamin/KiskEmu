-- Content tickets (ContentsTicket.dat Count tickets; GameSession.ContentTickets.cs, forge #72): the count a character held
-- at updated_at; the daily, weekly and cycle recharges since are added when it is read. Replaces the per-period used counts
-- of migration 045, whose only ticket (4 for every expedition) was a stub: ticket 4 is the boss challenge's.
CREATE TABLE game.character_content_tickets (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    ticket_id integer NOT NULL,
    remaining integer NOT NULL CHECK (remaining >= 0),
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (character_id, ticket_id)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_content_tickets TO aion_app;

DROP TABLE IF EXISTS game.character_expedition_tickets;
