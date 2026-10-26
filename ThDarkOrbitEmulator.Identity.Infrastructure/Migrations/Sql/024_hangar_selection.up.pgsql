CREATE TABLE economy.hangar_selections (
    account_id uuid PRIMARY KEY REFERENCES identity.accounts(id),
    active_slot integer NOT NULL CHECK (active_slot BETWEEN 1 AND 2)
);
