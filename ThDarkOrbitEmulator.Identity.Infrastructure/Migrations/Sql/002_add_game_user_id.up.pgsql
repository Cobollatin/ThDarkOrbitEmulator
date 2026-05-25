CREATE SEQUENCE IF NOT EXISTS identity.game_user_id_seq START WITH 100000000 INCREMENT BY 1;

ALTER TABLE identity.accounts
    ADD COLUMN IF NOT EXISTS game_user_id BIGINT;

UPDATE identity.accounts
SET game_user_id = nextval('identity.game_user_id_seq')
WHERE game_user_id IS NULL;

ALTER TABLE identity.accounts
    ALTER COLUMN game_user_id SET NOT NULL,
    ALTER COLUMN game_user_id SET DEFAULT nextval('identity.game_user_id_seq');

CREATE UNIQUE INDEX IF NOT EXISTS ux_identity_accounts_game_user_id
    ON identity.accounts (game_user_id);
