ALTER TABLE economy.pulse_actions
    ADD COLUMN target_generations bigint[] NOT NULL DEFAULT '{}';
