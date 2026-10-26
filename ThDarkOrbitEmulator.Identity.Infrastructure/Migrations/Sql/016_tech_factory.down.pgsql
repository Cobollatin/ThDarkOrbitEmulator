DROP TABLE economy.tech_factory_actions;
DROP TABLE economy.tech_factory_legacy_imports;
DROP TABLE economy.tech_factory_jobs;
DELETE FROM economy.recipes WHERE id IN ('tech_speed','tech_energy');
DELETE FROM economy.assets WHERE id IN ('speed_boost','energy_charge');
