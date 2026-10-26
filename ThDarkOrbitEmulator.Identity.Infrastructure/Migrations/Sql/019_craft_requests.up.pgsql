CREATE TABLE economy.craft_requests (
    account_id uuid NOT NULL REFERENCES identity.accounts(id),
    idempotency_key text NOT NULL CHECK (length(idempotency_key) BETWEEN 1 AND 160),
    recipe_id text NOT NULL REFERENCES economy.recipes(id),
    requested_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (account_id, idempotency_key)
);
