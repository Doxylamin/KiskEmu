-- Push rewards (gift box): global rewards and per-character take records.
CREATE TABLE game.push_rewards (
    push_reward_id serial PRIMARY KEY,
    reward_type smallint NOT NULL CHECK (reward_type >= 0),
    sender text NOT NULL,
    title text NOT NULL,
    body text NOT NULL,
    item_template_id integer NOT NULL,
    item_count bigint NOT NULL CHECK (item_count > 0),
    sent_at timestamptz NOT NULL DEFAULT now(),
    expire_at timestamptz NOT NULL
);
CREATE TABLE game.character_push_rewards (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    push_reward_id integer NOT NULL REFERENCES game.push_rewards(push_reward_id) ON DELETE CASCADE,
    taken_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, push_reward_id)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.push_rewards TO aion_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_push_rewards TO aion_app;
GRANT USAGE, SELECT ON SEQUENCE game.push_rewards_push_reward_id_seq TO aion_app;

-- No gifts are seeded: a migration also runs on live servers, where it would hand every character items. The official
-- launch set (OBSERVED: 533460001 x500 and 532760001 x1 for 70 days; 531060026 x3 weekly; 530116006 x3, 503700031 x1
-- and 530116001 x10 daily, 7 days each) can be added by an operator with an INSERT into game.push_rewards.
