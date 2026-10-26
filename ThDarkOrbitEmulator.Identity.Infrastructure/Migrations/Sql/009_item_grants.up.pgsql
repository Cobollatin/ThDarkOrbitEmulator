CREATE TABLE economy.item_grants (
    account_id uuid NOT NULL REFERENCES identity.accounts(id),
    idempotency_key text NOT NULL,
    item_id text NOT NULL REFERENCES economy.items(id),
    quantity bigint NOT NULL CHECK (quantity > 0),
    granted_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY(account_id,idempotency_key)
);
