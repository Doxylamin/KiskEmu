-- Staff ranks are gamemaster < moderator < admin ("Administrator"); each rank has what the ranks below it have
-- (Aion.Core/Security/StaffAccess.cs). Moderators act on players: sanctions and disabling player accounts.
ALTER TABLE auth.account_roles DROP CONSTRAINT account_roles_role_check;
ALTER TABLE auth.account_roles ADD CONSTRAINT account_roles_role_check CHECK (role IN ('gamemaster','moderator','admin'));

-- Administrators grant and revoke every staff role (an admin still cannot remove their own admin role).
CREATE OR REPLACE FUNCTION auth.set_staff_role(p_session bytea,p_target uuid,p_role text,p_granted boolean) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;
BEGIN
    IF p_role IS NULL OR p_role NOT IN ('gamemaster','moderator','admin') OR p_granted IS NULL THEN RETURN false; END IF;
    SELECT s.account_id INTO actor FROM auth.sessions s JOIN auth.accounts a USING(account_id)
      JOIN auth.account_roles r USING(account_id)
      WHERE s.token_hash=p_session AND s.expires_at>now() AND a.disabled_at IS NULL AND r.role='admin';
    IF actor IS NULL OR (actor=p_target AND p_role='admin' AND NOT p_granted) THEN RETURN false; END IF;
    PERFORM 1 FROM auth.accounts WHERE account_id=p_target AND disabled_at IS NULL FOR UPDATE;
    IF NOT FOUND THEN RETURN false; END IF;
    IF p_granted THEN
        INSERT INTO auth.account_roles VALUES(p_target,p_role) ON CONFLICT DO NOTHING;
    ELSE
        DELETE FROM auth.account_roles WHERE account_id=p_target AND role=p_role;
    END IF;
    INSERT INTO audit.events(action,actor_id,details)
      VALUES(CASE WHEN p_granted THEN 'role_granted' ELSE 'role_revoked' END,actor,
             jsonb_build_object('target',p_target,'role',p_role));
    RETURN true;
END $$;

-- Moderators and administrators disable accounts; a staff account only an administrator (nobody disables themselves).
CREATE OR REPLACE FUNCTION auth.disable_account(p_session bytea,p_target uuid) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid; actor_admin boolean;
BEGIN
    SELECT s.account_id, bool_or(r.role='admin') INTO actor, actor_admin
      FROM auth.sessions s JOIN auth.accounts a USING(account_id) JOIN auth.account_roles r USING(account_id)
      WHERE s.token_hash=p_session AND s.expires_at>now() AND a.disabled_at IS NULL AND r.role IN ('moderator','admin')
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
