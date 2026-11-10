-- Staff dashboard sessions are apart from player sessions (IStaffSessions, Authentication/StaffSessionEndpoints.cs):
-- they need the password, a staff role and an authenticator code. Every /api/admin request needs one; a player
-- session no longer opens the dashboard.
ALTER TABLE auth.sessions ADD COLUMN kind text NOT NULL DEFAULT 'account' CHECK (kind IN ('account','staff'));
CREATE INDEX sessions_staff_idx ON auth.sessions(account_id) WHERE kind='staff';

-- A sign-in challenge is for a player sign-in ('account'), a staff sign-in's code step ('staff') or a staff sign-in that
-- sets up an authenticator first ('staff_enroll': staff must use two-step verification).
ALTER TABLE auth.totp_login_challenges
    ADD COLUMN purpose text NOT NULL DEFAULT 'account' CHECK (purpose IN ('account','staff','staff_enroll'));

-- Role changes and disabling need a staff session. A removed role ends the account's staff sessions at once (the
-- dashboard signs it out; the world servers forget its sign-ins on account.changed).
CREATE OR REPLACE FUNCTION auth.set_staff_role(p_session bytea,p_target uuid,p_role text,p_granted boolean) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;
BEGIN
    IF p_role IS NULL OR p_role NOT IN ('gamemaster','moderator','admin') OR p_granted IS NULL THEN RETURN false; END IF;
    SELECT s.account_id INTO actor FROM auth.sessions s JOIN auth.accounts a USING(account_id)
      JOIN auth.account_roles r USING(account_id)
      WHERE s.token_hash=p_session AND s.kind='staff' AND s.expires_at>now() AND a.disabled_at IS NULL AND r.role='admin';
    IF actor IS NULL OR (actor=p_target AND p_role='admin' AND NOT p_granted) THEN RETURN false; END IF;
    PERFORM 1 FROM auth.accounts WHERE account_id=p_target AND disabled_at IS NULL FOR UPDATE;
    IF NOT FOUND THEN RETURN false; END IF;
    IF p_granted THEN
        INSERT INTO auth.account_roles VALUES(p_target,p_role) ON CONFLICT DO NOTHING;
    ELSE
        DELETE FROM auth.account_roles WHERE account_id=p_target AND role=p_role;
        DELETE FROM auth.sessions WHERE account_id=p_target AND kind='staff';
    END IF;
    INSERT INTO audit.events(action,actor_id,details)
      VALUES(CASE WHEN p_granted THEN 'role_granted' ELSE 'role_revoked' END,actor,
             jsonb_build_object('target',p_target,'role',p_role));
    RETURN true;
END $$;

CREATE OR REPLACE FUNCTION auth.disable_account(p_session bytea,p_target uuid) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid; actor_admin boolean;
BEGIN
    SELECT s.account_id, bool_or(r.role='admin') INTO actor, actor_admin
      FROM auth.sessions s JOIN auth.accounts a USING(account_id) JOIN auth.account_roles r USING(account_id)
      WHERE s.token_hash=p_session AND s.kind='staff' AND s.expires_at>now() AND a.disabled_at IS NULL AND r.role IN ('moderator','admin')
      GROUP BY s.account_id;
    IF actor IS NULL OR actor=p_target THEN RETURN false; END IF;
    IF NOT actor_admin AND EXISTS (SELECT 1 FROM auth.account_roles WHERE account_id=p_target) THEN RETURN false; END IF;
    PERFORM 1 FROM auth.local_credentials WHERE account_id=p_target FOR UPDATE;
    UPDATE auth.accounts SET disabled_at=COALESCE(disabled_at,now()) WHERE account_id=p_target;
    IF NOT FOUND THEN RETURN false; END IF;
    DELETE FROM auth.sessions WHERE account_id=p_target;
    INSERT INTO audit.events(action,actor_id,details)
      VALUES('account_disabled',actor,jsonb_build_object('target',p_target));
    RETURN true;
END $$;
