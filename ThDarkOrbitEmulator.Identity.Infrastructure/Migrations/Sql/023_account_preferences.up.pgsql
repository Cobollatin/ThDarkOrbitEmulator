CREATE TABLE economy.account_preferences (
    account_id uuid PRIMARY KEY REFERENCES identity.accounts(id),
    language text NOT NULL CHECK (length(language) BETWEEN 1 AND 8),
    sound_enabled boolean NOT NULL,
    notifications_enabled boolean NOT NULL,
    revision integer NOT NULL DEFAULT 0 CHECK (revision >= 0)
);
