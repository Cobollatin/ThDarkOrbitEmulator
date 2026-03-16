CREATE TABLE IF NOT EXISTS identity.accounts
(
    id UUID PRIMARY KEY,
    username VARCHAR(128) NOT NULL,
    email VARCHAR(256) NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_identity_accounts_username ON identity.accounts (username);
