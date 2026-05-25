CREATE SEQUENCE IF NOT EXISTS identity.game_user_id_seq START WITH 100000000 INCREMENT BY 1;

ALTER TABLE identity.accounts
    ALTER COLUMN game_user_id SET DEFAULT nextval('identity.game_user_id_seq');
