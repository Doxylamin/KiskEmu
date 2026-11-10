-- Population history per world (the staff Dashboard): the realm's sampler writes one row for the realm total
-- (world_id NULL, as before) and one row per running world every five minutes.
ALTER TABLE game.population_samples ADD COLUMN world_id integer;
ALTER TABLE game.population_samples DROP CONSTRAINT population_samples_pkey;
CREATE UNIQUE INDEX population_samples_at_world_idx ON game.population_samples(sampled_at, COALESCE(world_id, 0));
CREATE INDEX population_samples_world_idx ON game.population_samples(world_id, sampled_at);
