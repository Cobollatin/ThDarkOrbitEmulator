ALTER TABLE identity.accounts
    ADD COLUMN company VARCHAR(3) NULL;

ALTER TABLE identity.accounts
    ADD CONSTRAINT ck_identity_accounts_company
    CHECK (company IN ('mmo', 'eic', 'vru'));
