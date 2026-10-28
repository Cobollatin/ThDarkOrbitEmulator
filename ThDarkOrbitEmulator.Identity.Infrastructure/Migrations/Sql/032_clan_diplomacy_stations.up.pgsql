CREATE TABLE economy.clan_leaders (
    clan_id text PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES identity.accounts(id)
);
CREATE TABLE economy.clan_relations (
    clan_low text NOT NULL,
    clan_high text NOT NULL,
    proposer text NOT NULL,
    status text NOT NULL CHECK (status IN ('pending', 'allied', 'war')),
    PRIMARY KEY (clan_low, clan_high),
    CHECK (clan_low < clan_high)
);
CREATE TABLE economy.clan_station_modules (
    clan_id text NOT NULL,
    module_id text NOT NULL CHECK (module_id IN ('hull', 'defense', 'relay')),
    level integer NOT NULL CHECK (level BETWEEN 0 AND 3),
    ready_at timestamptz,
    PRIMARY KEY (clan_id, module_id)
);
CREATE TABLE economy.clan_operations (
    account_id uuid NOT NULL REFERENCES identity.accounts(id),
    action_key text NOT NULL,
    PRIMARY KEY (account_id, action_key)
);
