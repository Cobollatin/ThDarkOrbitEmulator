CREATE SCHEMA IF NOT EXISTS economy AUTHORIZATION app;

CREATE TABLE economy.assets (
    id text PRIMARY KEY,
    kind text NOT NULL CHECK (kind IN ('currency', 'resource')),
    name text NOT NULL,
    enabled boolean NOT NULL DEFAULT true
);
INSERT INTO economy.assets (id, kind, name) VALUES
 ('credits','currency','Credits'), ('uridium','currency','Uridium'),
 ('prometium','resource','Prometium'), ('endurium','resource','Endurium'),
 ('terbium','resource','Terbium'), ('assembly_components','resource','Assembly components'),
 ('tech_components','resource','Tech Factory components'), ('galaxy_energy','resource','Galaxy Gate energy')
ON CONFLICT (id) DO NOTHING;

CREATE TABLE economy.items (
    id text PRIMARY KEY,
    name text NOT NULL,
    type text NOT NULL,
    rarity text NOT NULL DEFAULT 'common',
    stats jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(stats) = 'object'),
    asset_key text,
    enabled boolean NOT NULL DEFAULT true
);
INSERT INTO economy.items (id,name,type,asset_key) VALUES
 ('laser_lf3','LF-3 laser','laser','laser_lf3'),
 ('laser_lf2','LF-2 laser','laser','laser_lf2'),
 ('shield_sg3n_b02','SG3N-B02 shield','shield','shield_sg3n_b02'),
 ('shield_sg3n_a03','SG3N-A03 shield','shield','shield_sg3n_a03'),
 ('drone_flax','Flax drone','drone','drone_flax'),
 ('drone_iris','Iris drone','drone','drone_iris'),
 ('pet_10','P.E.T. 10','pet','pet_10')
ON CONFLICT (id) DO NOTHING;

CREATE TABLE economy.balances (
    account_id uuid NOT NULL REFERENCES identity.accounts(id),
    asset_id text NOT NULL REFERENCES economy.assets(id),
    amount bigint NOT NULL DEFAULT 0 CHECK (amount >= 0),
    PRIMARY KEY (account_id,asset_id)
);
CREATE TABLE economy.inventory (
    account_id uuid NOT NULL REFERENCES identity.accounts(id),
    item_id text NOT NULL REFERENCES economy.items(id),
    quantity bigint NOT NULL DEFAULT 0 CHECK (quantity >= 0),
    PRIMARY KEY (account_id,item_id)
);
CREATE TABLE economy.account_imports (account_id uuid PRIMARY KEY REFERENCES identity.accounts(id), imported_at timestamptz NOT NULL DEFAULT now());
CREATE TABLE economy.resource_imports (
    account_id uuid NOT NULL REFERENCES identity.accounts(id),
    asset_id text NOT NULL REFERENCES economy.assets(id),
    imported_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (account_id,asset_id)
);
CREATE TABLE economy.ledger (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES identity.accounts(id),
    asset_id text NOT NULL REFERENCES economy.assets(id),
    delta bigint NOT NULL CHECK (delta <> 0),
    balance_after bigint NOT NULL CHECK (balance_after >= 0),
    reason text NOT NULL,
    menu_source text NOT NULL,
    idempotency_key text NOT NULL,
    occurred_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (account_id,idempotency_key,asset_id)
);
CREATE INDEX ix_economy_ledger_account ON economy.ledger(account_id,id DESC);
CREATE FUNCTION economy.reject_ledger_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION 'economy ledger is append-only'; END $$;
CREATE TRIGGER ledger_immutable BEFORE UPDATE OR DELETE ON economy.ledger
FOR EACH ROW EXECUTE FUNCTION economy.reject_ledger_change();

CREATE TABLE economy.recipes (
    id text PRIMARY KEY,
    name text NOT NULL,
    inputs jsonb NOT NULL CHECK (jsonb_typeof(inputs) = 'array'),
    outputs jsonb NOT NULL CHECK (jsonb_typeof(outputs) = 'array'),
    craft_seconds integer NOT NULL DEFAULT 0 CHECK (craft_seconds >= 0),
    enabled boolean NOT NULL DEFAULT true
);
INSERT INTO economy.recipes(id,name,inputs,outputs) VALUES
 ('lf3','LF-3 laser','[{"kind":"resource","id":"assembly_components","quantity":150},{"kind":"currency","id":"credits","quantity":500}]','[{"kind":"item","id":"laser_lf3","quantity":1}]'),
 ('shield','SG3N-B02 shield','[{"kind":"resource","id":"assembly_components","quantity":100},{"kind":"currency","id":"credits","quantity":400}]','[{"kind":"item","id":"shield_sg3n_b02","quantity":1}]'),
 ('flax','Flax drone','[{"kind":"resource","id":"assembly_components","quantity":200},{"kind":"currency","id":"credits","quantity":600}]','[{"kind":"item","id":"drone_flax","quantity":1}]')
ON CONFLICT (id) DO NOTHING;

CREATE TABLE economy.listings (
    id text PRIMARY KEY,
    item_id text NOT NULL REFERENCES economy.items(id),
    prices jsonb NOT NULL CHECK (jsonb_typeof(prices) = 'object'),
    stock_available bigint CHECK (stock_available >= 0),
    per_account_limit bigint CHECK (per_account_limit > 0),
    restock_interval_seconds integer CHECK (restock_interval_seconds > 0),
    restock_amount bigint CHECK (restock_amount > 0),
    last_restock_at timestamptz NOT NULL DEFAULT now(),
    enabled boolean NOT NULL DEFAULT true,
    CHECK ((restock_interval_seconds IS NULL) = (restock_amount IS NULL))
);
INSERT INTO economy.listings(id,item_id,prices) VALUES
 ('laser_lf3','laser_lf3','{"credits":1500}'),
 ('shield_sg3n_b02','shield_sg3n_b02','{"credits":1000}'),
 ('drone_flax','drone_flax','{"credits":500}'),
 ('pet_10','pet_10','{"credits":3000}')
ON CONFLICT (id) DO NOTHING;
CREATE TABLE economy.purchases (
    account_id uuid NOT NULL REFERENCES identity.accounts(id),
    listing_id text NOT NULL REFERENCES economy.listings(id),
    quantity bigint NOT NULL CHECK (quantity > 0),
    PRIMARY KEY (account_id,listing_id)
);
CREATE TABLE economy.purchase_requests (
    account_id uuid NOT NULL REFERENCES identity.accounts(id),
    idempotency_key text NOT NULL,
    listing_id text NOT NULL,
    quantity bigint NOT NULL,
    purchased_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (account_id,idempotency_key)
);
CREATE TABLE economy.stock_audit (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    listing_id text NOT NULL REFERENCES economy.listings(id),
    delta bigint NOT NULL,
    stock_after bigint NOT NULL CHECK (stock_after >= 0),
    reason text NOT NULL,
    actor_account_id uuid,
    occurred_at timestamptz NOT NULL DEFAULT now()
);
