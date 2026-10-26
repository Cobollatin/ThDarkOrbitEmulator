CREATE TABLE economy.auction_lots (
    id text PRIMARY KEY,
    item_id text NOT NULL REFERENCES economy.items(id),
    opening_bid bigint NOT NULL CHECK (opening_bid > 0),
    duration_seconds integer NOT NULL CHECK (duration_seconds > 0),
    enabled boolean NOT NULL DEFAULT true
);
INSERT INTO economy.auction_lots(id,item_id,opening_bid,duration_seconds) VALUES
 ('laser_lf3','laser_lf3',700,300),
 ('shield_sg3n_b02','shield_sg3n_b02',450,300),
 ('drone_flax','drone_flax',200,300) ON CONFLICT (id) DO NOTHING;
CREATE TABLE economy.auction_bids (
    account_id uuid NOT NULL REFERENCES identity.accounts(id),
    lot_id text NOT NULL REFERENCES economy.auction_lots(id),
    bid bigint NOT NULL CHECK (bid > 0),
    ends_at timestamptz NOT NULL,
    claimed boolean NOT NULL DEFAULT false,
    PRIMARY KEY(account_id,lot_id)
);
