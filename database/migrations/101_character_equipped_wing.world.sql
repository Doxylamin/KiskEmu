-- The equipped wing (Wing.dat id; 0 = the race's default wing), restored at login (from A2Core-ChanAge, its migration 092).
-- World databases only: game.characters lives in each world's database.
ALTER TABLE game.characters
    ADD COLUMN IF NOT EXISTS equipped_wing_id integer NOT NULL DEFAULT 0 CHECK (equipped_wing_id >= 0);
