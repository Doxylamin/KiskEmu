-- Shop purchase ledger and memberships, per world (a purchase runs on the buyer's world).

-- Every shop purchase line, written in the purchase's transaction: NPC, coin and guild shops, the in-game shop (official and
-- custom goods), the Daeva Pass unlock and the membership. price is the total paid in currency_item (0 when free).
CREATE TABLE game.shop_orders (
    order_id bigserial PRIMARY KEY,
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    account_id uuid NOT NULL,
    kind text NOT NULL CHECK (kind IN ('npc', 'coin', 'guild', 'bm', 'custom', 'pass', 'membership')),
    goods_id integer NOT NULL,
    quantity integer NOT NULL CHECK (quantity > 0),
    currency_item integer NOT NULL,
    price bigint NOT NULL CHECK (price >= 0),
    ordered_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX shop_orders_ordered_at ON game.shop_orders (ordered_at DESC);
CREATE INDEX shop_orders_character ON game.shop_orders (character_id, ordered_at DESC);
GRANT SELECT, INSERT, DELETE ON game.shop_orders TO aion_app;
GRANT USAGE, SELECT ON SEQUENCE game.shop_orders_order_id_seq TO aion_app;

-- The account's Special Quai Membership on this world (bought in the Pass Shop or set by a GM); without a row the world's
-- default policy applies (Aion:World:MembershipByDefault).
CREATE TABLE game.account_memberships (
    account_id uuid PRIMARY KEY,
    starts_at timestamptz NOT NULL,
    ends_at timestamptz NOT NULL,
    revoked boolean NOT NULL DEFAULT false,
    updated_at timestamptz NOT NULL DEFAULT now()
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.account_memberships TO aion_app;
