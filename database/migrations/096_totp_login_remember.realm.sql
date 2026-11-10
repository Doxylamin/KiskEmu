-- A TOTP sign-in keeps the session length asked for at the password step ("keep me signed in"): the challenge carries
-- it, and login/totp issues a session of that length (null = the standard 8 hours).
ALTER TABLE auth.totp_login_challenges
    ADD COLUMN session_seconds integer CHECK (session_seconds IS NULL OR session_seconds BETWEEN 60 AND 2592000);
