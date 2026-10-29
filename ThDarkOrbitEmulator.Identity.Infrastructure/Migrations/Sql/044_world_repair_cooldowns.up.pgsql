ALTER TABLE economy.world_deaths
    ADD COLUMN death_repair_at timestamptz,
    ADD COLUMN portal_repair_at timestamptz;
