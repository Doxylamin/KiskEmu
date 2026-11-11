-- A refund gives back a purchase only. 095's game.quna_refund took any negative entry, so a world could also undo a
-- staff deduction (reason 'staff') by its request id. Same signature and grant; only the match changes.
CREATE OR REPLACE FUNCTION game.quna_refund(p_account uuid, p_spend text, p_world integer)
RETURNS TABLE (ok boolean, balance bigint, entry_id bigint, error text, repeated boolean)
LANGUAGE plpgsql AS $$
DECLARE
    v_spend game.quna_ledger%ROWTYPE;
BEGIN
    SELECT * INTO v_spend FROM game.quna_ledger l
    WHERE l.request_id = p_spend AND l.account_id = p_account AND l.reason = 'purchase' AND l.delta < 0;
    IF NOT FOUND THEN
        RETURN QUERY SELECT false, COALESCE((SELECT w.balance FROM game.quna_wallets w WHERE w.account_id = p_account), 0),
            COALESCE((SELECT w.last_entry_id FROM game.quna_wallets w WHERE w.account_id = p_account), 0), 'not_found'::text, false;
        RETURN;
    END IF;
    RETURN QUERY SELECT * FROM game.quna_apply(p_account, 'refund:' || p_spend, -v_spend.delta, 'refund', COALESCE(p_world, v_spend.world_id),
        v_spend.character_id, jsonb_build_object('refunds', p_spend));
END $$;
