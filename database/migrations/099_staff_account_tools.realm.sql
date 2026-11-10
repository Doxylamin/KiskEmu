-- The staff Accounts page (IAccountDirectory, Authentication/StaffAccountEndpoints.cs) and the staff sign-in read an
-- account's roles over the account connection: aion_auth had no SELECT on auth.account_roles until now.
GRANT SELECT ON auth.account_roles TO aion_auth;
-- The Accounts page also finds accounts by character name (game.character_index): 092 granted SELECT on that table
-- but not the schema. aion_auth has SELECT on game.character_index and game.characters only; no game function runs
-- as its owner (none is SECURITY DEFINER), so the schema gives it nothing else.
GRANT USAGE ON SCHEMA game TO aion_auth;

-- Ending every session of an account from the dashboard. Needs a staff session of a moderator (players only) or an
-- administrator (anyone but themselves).
CREATE OR REPLACE FUNCTION auth.staff_revoke_sessions(p_session bytea,p_target uuid) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid; actor_admin boolean;
BEGIN
    SELECT s.account_id, bool_or(r.role='admin') INTO actor, actor_admin
      FROM auth.sessions s JOIN auth.accounts a USING(account_id) JOIN auth.account_roles r USING(account_id)
      WHERE s.token_hash=p_session AND s.kind='staff' AND s.expires_at>now() AND a.disabled_at IS NULL AND r.role IN ('moderator','admin')
      GROUP BY s.account_id;
    IF actor IS NULL OR actor=p_target THEN RETURN false; END IF;
    IF NOT actor_admin AND EXISTS (SELECT 1 FROM auth.account_roles WHERE account_id=p_target) THEN RETURN false; END IF;
    PERFORM 1 FROM auth.accounts WHERE account_id=p_target;
    IF NOT FOUND THEN RETURN false; END IF;
    DELETE FROM auth.sessions WHERE account_id=p_target;
    INSERT INTO audit.events(action,actor_id,details) VALUES('account_sessions_revoked',actor,jsonb_build_object('target',p_target));
    RETURN true;
END $$;

-- A temporary password set by an administrator (the host makes it and hashes it; it is shown once on the dashboard).
-- Ends the target's sessions and lifts a sign-in lockout. Not for the administrator's own account.
CREATE OR REPLACE FUNCTION auth.staff_reset_password(p_session bytea,p_target uuid,p_hash text) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;
BEGIN
    IF p_hash IS NULL OR length(p_hash) < 20 THEN RETURN false; END IF;
    SELECT s.account_id INTO actor FROM auth.sessions s JOIN auth.accounts a USING(account_id) JOIN auth.account_roles r USING(account_id)
      WHERE s.token_hash=p_session AND s.kind='staff' AND s.expires_at>now() AND a.disabled_at IS NULL AND r.role='admin';
    IF actor IS NULL OR actor=p_target THEN RETURN false; END IF;
    UPDATE auth.local_credentials SET password_hash=p_hash, failed_attempts=0, locked_until=NULL WHERE account_id=p_target;
    IF NOT FOUND THEN RETURN false; END IF;
    DELETE FROM auth.sessions WHERE account_id=p_target;
    INSERT INTO audit.events(action,actor_id,details) VALUES('account_password_reset',actor,jsonb_build_object('target',p_target));
    RETURN true;
END $$;

REVOKE ALL ON FUNCTION auth.staff_revoke_sessions(bytea,uuid), auth.staff_reset_password(bytea,uuid,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION auth.staff_revoke_sessions(bytea,uuid), auth.staff_reset_password(bytea,uuid,text) TO aion_auth;
