-- Two-step sign-in replay protection (after addiboii/A2Core, its migration 040): last_step is the last accepted 30 s
-- time step of the account's authenticator. A code is accepted only for a later step, so a code that was seen (typed
-- over someone's shoulder, logged by a proxy) cannot be used again inside its window. NULL until the first code is
-- accepted after this migration (accounts set up earlier).
ALTER TABLE auth.account_totp ADD COLUMN last_step bigint;
