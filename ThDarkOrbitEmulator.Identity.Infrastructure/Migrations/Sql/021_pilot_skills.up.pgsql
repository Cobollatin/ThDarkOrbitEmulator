CREATE TABLE economy.pilot_skill_states (
    account_id uuid PRIMARY KEY REFERENCES identity.accounts(id),
    ranks jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(ranks) = 'object'),
    revision integer NOT NULL DEFAULT 0 CHECK (revision >= 0)
);
CREATE TABLE economy.pilot_skill_actions (
    account_id uuid NOT NULL REFERENCES identity.accounts(id),
    action_key text NOT NULL,
    PRIMARY KEY (account_id, action_key)
);
