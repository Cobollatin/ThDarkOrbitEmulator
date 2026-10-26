CREATE TABLE economy.skylab_collectors (
    account_id uuid NOT NULL REFERENCES identity.accounts(id),
    collector_id text NOT NULL,
    level integer NOT NULL CHECK (level BETWEEN 1 AND 10),
    last_collected timestamptz NOT NULL,
    PRIMARY KEY (account_id, collector_id)
);

CREATE TABLE economy.skylab_actions (
    account_id uuid NOT NULL REFERENCES identity.accounts(id),
    action_key text NOT NULL,
    PRIMARY KEY (account_id, action_key)
);
