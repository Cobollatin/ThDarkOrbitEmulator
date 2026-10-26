CREATE TABLE economy.messaging_states (
    account_id uuid PRIMARY KEY REFERENCES identity.accounts(id),
    state jsonb NOT NULL CHECK (jsonb_typeof(state) = 'object'),
    revision integer NOT NULL DEFAULT 0 CHECK (revision >= 0)
);
CREATE TABLE economy.messaging_actions (
    account_id uuid NOT NULL REFERENCES identity.accounts(id),
    action_key text NOT NULL,
    PRIMARY KEY (account_id, action_key)
);
