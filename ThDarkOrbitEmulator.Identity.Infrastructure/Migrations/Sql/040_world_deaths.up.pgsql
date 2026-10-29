CREATE TABLE economy.world_deaths (
    account_id uuid PRIMARY KEY REFERENCES identity.accounts(id) ON DELETE CASCADE,
    death_id text NOT NULL
);
