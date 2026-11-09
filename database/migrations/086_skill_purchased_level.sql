-- Skill levels bought with skill points (SkillLevelUp_RQ) are recorded apart from the level the client shows.
-- character_skills.level is also written by the level-managed kit (sync_character_skills), so counting every level above
-- 1 as spent charged characters for points they never spent and refused every purchase.
ALTER TABLE game.character_skills ADD COLUMN purchased_level integer NOT NULL DEFAULT 0 CHECK (purchased_level >= 0);
-- Existing characters keep what they bought: the stored level is carried over. The server counts a stored level as bought
-- only when it is above the class kit level at the character's level (GameSession.SkillLevels), so kit-written levels
-- still cost nothing and nobody is refunded or charged by this migration.
UPDATE game.character_skills SET purchased_level = level WHERE level > 1;
