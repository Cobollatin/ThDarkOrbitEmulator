DELETE FROM economy.reward_offers WHERE source='quest' AND id IN ('first_streuner','sector_patrol');
ALTER TABLE economy.reward_offers DROP CONSTRAINT reward_offers_progress_kind_check;
ALTER TABLE economy.reward_offers ADD CONSTRAINT reward_offers_progress_kind_check
 CHECK (progress_kind IN ('none','purchases','crafts'));
DELETE FROM economy.assets WHERE id IN ('experience','honor')
  AND NOT EXISTS (SELECT 1 FROM economy.ledger WHERE asset_id IN ('experience','honor'));
