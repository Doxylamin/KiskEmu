-- Custom shop goods (staff dashboard: Shop). Goods the server adds to the in-game shop's catalogue (goods/v2.0/goods, which
-- the client takes from the server, unlike NPC and coin shops). Every world server reads them over the realm link and sells
-- them through NPShopPurchase on the buyer's world. Ids 9000000-9999999 stay clear of the official goods ids.
CREATE TABLE game.custom_shop_goods (
    goods_id integer PRIMARY KEY CHECK (goods_id BETWEEN 9000000 AND 9999999),
    name text NOT NULL CHECK (length(name) BETWEEN 1 AND 80),
    description text NOT NULL DEFAULT '' CHECK (length(description) <= 400),
    -- The shop tab (goods_data catl for Light, catd for Dark) and the shop icon (gi), from the official catalogue's values.
    tab_light text NOT NULL CHECK (length(tab_light) BETWEEN 1 AND 64),
    tab_dark text NOT NULL CHECK (length(tab_dark) BETWEEN 1 AND 64),
    icon text NOT NULL DEFAULT '' CHECK (length(icon) <= 128),
    -- [[item template id, quantity], ...]
    items jsonb NOT NULL CHECK (jsonb_typeof(items) = 'array' AND jsonb_array_length(items) BETWEEN 1 AND 10),
    currency text NOT NULL CHECK (currency IN ('kina', 'quna', 'free')),
    price bigint NOT NULL CHECK (price >= 0 AND price <= 10000000000),
    -- Purchase limit: 0 for none; per character (1) or account (2); period 0 ever, 1 day, 2 week, 3 month (game reset times).
    limit_count integer NOT NULL DEFAULT 0 CHECK (limit_count BETWEEN 0 AND 100000),
    limit_target smallint NOT NULL DEFAULT 1 CHECK (limit_target IN (1, 2)),
    limit_period smallint NOT NULL DEFAULT 0 CHECK (limit_period BETWEEN 0 AND 3),
    -- 0 both races, 1 Light, 2 Dark.
    race smallint NOT NULL DEFAULT 0 CHECK (race BETWEEN 0 AND 2),
    min_level smallint NOT NULL DEFAULT 0 CHECK (min_level BETWEEN 0 AND 100),
    sale_from timestamptz,
    sale_to timestamptz,
    enabled boolean NOT NULL DEFAULT true,
    sort_order integer NOT NULL DEFAULT 0,
    updated_at timestamptz NOT NULL DEFAULT now(),
    updated_by uuid,
    CHECK (sale_from IS NULL OR sale_to IS NULL OR sale_from < sale_to)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.custom_shop_goods TO aion_app;
