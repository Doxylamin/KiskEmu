-- Quna (NC GameCash, the paid currency) moves from item 535300006 stacks in each world database to one wallet per account
-- in the realm (server/DATABASES.md). Every change is a ledger entry; request_id makes a repeated call return the first
-- result. tools/postgres_dev.py (migrate_quna, run by ensure-worlds) moves the old stacks in as 'migration' entries.

CREATE TABLE game.quna_wallets (
    account_id uuid PRIMARY KEY REFERENCES auth.accounts(account_id),
    balance bigint NOT NULL DEFAULT 0 CHECK (balance >= 0),
    -- The account's newest ledger entry: a world keeps the balance with the highest entry when answers and pushes cross.
    last_entry_id bigint NOT NULL DEFAULT 0,
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE game.quna_ledger (
    entry_id bigserial PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES game.quna_wallets(account_id),
    delta bigint NOT NULL CHECK (delta <> 0 AND abs(delta) <= 100000000000),
    balance_after bigint NOT NULL CHECK (balance_after >= 0),
    reason text NOT NULL CHECK (reason IN ('purchase', 'grant', 'refund', 'staff', 'migration')),
    request_id text NOT NULL UNIQUE CHECK (length(request_id) BETWEEN 1 AND 200),
    -- The world and character the change came from (null for the realm's own changes).
    world_id integer,
    character_id uuid,
    details jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    -- A purchase takes, a grant, refund or migration adds; a staff change may do either.
    CHECK (reason = 'staff' OR (reason = 'purchase') = (delta < 0))
);
CREATE INDEX quna_ledger_account ON game.quna_ledger (account_id, entry_id DESC);

-- Append-only: entries are never changed or removed (a mistake is corrected with a new entry).
CREATE FUNCTION game.quna_ledger_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'game.quna_ledger is append-only';
END $$;
CREATE TRIGGER quna_ledger_append_only BEFORE UPDATE OR DELETE ON game.quna_ledger
    FOR EACH ROW EXECUTE FUNCTION game.quna_ledger_append_only();
CREATE TRIGGER quna_ledger_no_truncate BEFORE TRUNCATE ON game.quna_ledger
    FOR EACH STATEMENT EXECUTE FUNCTION game.quna_ledger_append_only();

-- One change of an account's balance: p_delta < 0 takes (refused with 'insufficient' when the balance is lower), > 0 adds.
-- The same p_request again returns the first result (repeated = true); with another account or amount it is refused
-- ('request_conflict'). Changes of one account are serialised on its wallet row.
CREATE FUNCTION game.quna_apply(p_account uuid, p_request text, p_delta bigint, p_reason text, p_world integer, p_character uuid, p_details jsonb)
RETURNS TABLE (ok boolean, balance bigint, entry_id bigint, error text, repeated boolean)
LANGUAGE plpgsql AS $$
DECLARE
    v_balance bigint;
    v_last bigint;
    v_done game.quna_ledger%ROWTYPE;
BEGIN
    INSERT INTO game.quna_wallets AS w (account_id) VALUES (p_account) ON CONFLICT (account_id) DO NOTHING;
    SELECT w.balance, w.last_entry_id INTO v_balance, v_last FROM game.quna_wallets w WHERE w.account_id = p_account FOR UPDATE;
    SELECT * INTO v_done FROM game.quna_ledger l WHERE l.request_id = p_request;
    IF FOUND THEN
        IF v_done.account_id = p_account AND v_done.delta = p_delta THEN
            RETURN QUERY SELECT true, v_done.balance_after, v_done.entry_id, NULL::text, true;
        ELSE
            RETURN QUERY SELECT false, v_balance, v_last, 'request_conflict'::text, false;
        END IF;
        RETURN;
    END IF;
    IF v_balance + p_delta < 0 THEN
        RETURN QUERY SELECT false, v_balance, v_last, 'insufficient'::text, false;
        RETURN;
    END IF;
    INSERT INTO game.quna_ledger (account_id, delta, balance_after, reason, request_id, world_id, character_id, details)
    VALUES (p_account, p_delta, v_balance + p_delta, p_reason, p_request, p_world, p_character, p_details)
    RETURNING * INTO v_done;
    UPDATE game.quna_wallets w SET balance = v_done.balance_after, last_entry_id = v_done.entry_id, updated_at = now()
    WHERE w.account_id = p_account;
    RETURN QUERY SELECT true, v_done.balance_after, v_done.entry_id, NULL::text, false;
END $$;

-- Gives back a purchase (its request id) once: request 'refund:' || p_spend, 'not_found' when there is no such spend
-- of this account.
CREATE FUNCTION game.quna_refund(p_account uuid, p_spend text, p_world integer)
RETURNS TABLE (ok boolean, balance bigint, entry_id bigint, error text, repeated boolean)
LANGUAGE plpgsql AS $$
DECLARE
    v_spend game.quna_ledger%ROWTYPE;
BEGIN
    SELECT * INTO v_spend FROM game.quna_ledger l WHERE l.request_id = p_spend AND l.account_id = p_account AND l.delta < 0;
    IF NOT FOUND THEN
        RETURN QUERY SELECT false, COALESCE((SELECT w.balance FROM game.quna_wallets w WHERE w.account_id = p_account), 0),
            COALESCE((SELECT w.last_entry_id FROM game.quna_wallets w WHERE w.account_id = p_account), 0), 'not_found'::text, false;
        RETURN;
    END IF;
    RETURN QUERY SELECT * FROM game.quna_apply(p_account, 'refund:' || p_spend, -v_spend.delta, 'refund', COALESCE(p_world, v_spend.world_id),
        v_spend.character_id, jsonb_build_object('refunds', p_spend));
END $$;

GRANT SELECT, INSERT, UPDATE ON game.quna_wallets TO aion_app;
GRANT SELECT, INSERT ON game.quna_ledger TO aion_app;
GRANT USAGE, SELECT ON SEQUENCE game.quna_ledger_entry_id_seq TO aion_app;
GRANT EXECUTE ON FUNCTION game.quna_apply(uuid, text, bigint, text, integer, uuid, jsonb) TO aion_app;
GRANT EXECUTE ON FUNCTION game.quna_refund(uuid, text, integer) TO aion_app;
