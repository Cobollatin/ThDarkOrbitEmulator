ALTER TABLE identity.accounts
    DROP CONSTRAINT IF EXISTS ck_identity_accounts_company;

ALTER TABLE identity.accounts
    DROP COLUMN IF EXISTS company;
