-- Staff ranks are gamemaster < moderator < admin (Aion.Core/Security/StaffAccess.cs). The world's account mirror
-- holds the realm's roles, so it accepts moderator too.
ALTER TABLE auth.account_roles DROP CONSTRAINT account_roles_role_check;
ALTER TABLE auth.account_roles ADD CONSTRAINT account_roles_role_check CHECK (role IN ('gamemaster','moderator','admin'));
