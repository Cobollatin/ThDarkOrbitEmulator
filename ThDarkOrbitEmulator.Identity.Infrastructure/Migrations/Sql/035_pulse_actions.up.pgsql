CREATE TABLE economy.pulse_actions (
    account_id uuid NOT NULL REFERENCES identity.accounts(id),
    action_id uuid NOT NULL,
    item_id text NOT NULL REFERENCES economy.items(id),
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (account_id, action_id)
);
