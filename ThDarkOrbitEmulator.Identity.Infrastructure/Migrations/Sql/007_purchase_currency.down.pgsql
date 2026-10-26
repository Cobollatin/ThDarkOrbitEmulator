ALTER TABLE economy.purchase_requests DROP COLUMN currency_id;
DELETE FROM economy.recipes WHERE id IN ('tech_targeting','tech_shield','tech_repair');
DELETE FROM economy.assets WHERE id IN ('targeting_boost','shield_boost','repair_charge');
