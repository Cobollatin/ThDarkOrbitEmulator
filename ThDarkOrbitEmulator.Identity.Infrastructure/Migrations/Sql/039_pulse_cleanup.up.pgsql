ALTER TABLE economy.pulse_actions
    DROP CONSTRAINT pulse_actions_status_check;

ALTER TABLE economy.pulse_actions
    ADD CONSTRAINT pulse_actions_status_check
    CHECK (status IN ('charged', 'started', 'completed', 'settled', 'refunded'));
