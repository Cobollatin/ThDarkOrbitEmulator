DROP INDEX IF EXISTS identity.ux_identity_accounts_game_user_id;

ALTER TABLE IF EXISTS identity.accounts
    DROP COLUMN IF EXISTS game_user_id;

DROP SEQUENCE IF EXISTS identity.game_user_id_seq;