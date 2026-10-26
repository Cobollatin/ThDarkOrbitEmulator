CREATE TABLE economy.clan_memberships (
    account_id uuid PRIMARY KEY REFERENCES identity.accounts(id),
    clan_id text NOT NULL
);
CREATE TABLE economy.clan_actions (
    account_id uuid NOT NULL REFERENCES identity.accounts(id),
    action_key text NOT NULL,
    PRIMARY KEY (account_id, action_key)
);
