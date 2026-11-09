-- Database split, world side (server/DATABASES.md). A world database holds one world's characters, guilds, market,
-- gifts and gameplay audit. The realm's tables go; auth.accounts and auth.account_roles stay as a mirror of the
-- accounts that play here (the world server copies them from the realm at sign-in), so the gameplay queries'
-- account checks and foreign keys keep working.
DROP TABLE IF EXISTS
    auth.world_sessions, auth.client_handoffs, auth.client_launches, auth.game_tickets, auth.wire_accounts,
    auth.local_credentials, auth.totp_login_challenges, auth.totp_enrollment, auth.account_totp,
    auth.platform_client_context, auth.platform_secondary_auth, auth.sessions,
    game.worlds, game.sanctions, game.tickets, game.chat_log, game.scheduled_events, game.server_settings,
    game.population_samples
CASCADE;
DROP FUNCTION IF EXISTS auth.consume_game_ticket(bytea, integer), auth.disable_account(bytea, uuid),
    auth.set_staff_role(bytea, uuid, text, boolean);

-- The world server keeps the mirror (game role): accounts and their staff roles as the realm has them.
GRANT SELECT, INSERT, UPDATE ON auth.accounts TO aion_app;
GRANT SELECT, INSERT, DELETE ON auth.account_roles TO aion_app;
ALTER TABLE auth.accounts ADD COLUMN mirrored_at timestamptz;
