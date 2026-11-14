-- Small per-account settings stored as JSON by name, shared by every character of the account on this world, like the
-- account warehouse's items (item_instances.account_storage_owner): the account warehouse's slot-line expansions
-- (account_storage_extends: [by item, by Kina]).
CREATE TABLE game.account_settings (
    account_id uuid NOT NULL,
    name text NOT NULL CHECK (name ~ '^[a-z_]{1,32}$'),
    value jsonb NOT NULL,
    PRIMARY KEY (account_id, name)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.account_settings TO aion_app;
