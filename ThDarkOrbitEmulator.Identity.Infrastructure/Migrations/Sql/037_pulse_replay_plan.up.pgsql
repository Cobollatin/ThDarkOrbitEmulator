ALTER TABLE economy.pulse_actions
    ADD COLUMN map_id integer NOT NULL DEFAULT 0,
    ADD COLUMN target_ids integer[] NOT NULL DEFAULT '{}';
