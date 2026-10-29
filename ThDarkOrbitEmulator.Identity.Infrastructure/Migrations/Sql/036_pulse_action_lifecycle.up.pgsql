ALTER TABLE economy.pulse_actions
    ADD COLUMN status text NOT NULL DEFAULT 'completed'
    CHECK (status IN ('charged', 'started', 'completed', 'refunded'));

ALTER TABLE economy.pulse_actions
    ALTER COLUMN status SET DEFAULT 'charged';
