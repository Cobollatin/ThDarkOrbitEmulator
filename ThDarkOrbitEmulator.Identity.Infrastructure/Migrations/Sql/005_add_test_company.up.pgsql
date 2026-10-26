ALTER TABLE identity.accounts
    DROP CONSTRAINT ck_identity_accounts_company;

ALTER TABLE identity.accounts
    ADD CONSTRAINT ck_identity_accounts_company
    CHECK (company IN ('mmo', 'eic', 'vru', 'tst'));
