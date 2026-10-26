INSERT INTO economy.assets(id,kind,name) VALUES
 ('experience','currency','Experience'), ('honor','currency','Honor')
ON CONFLICT (id) DO NOTHING;

ALTER TABLE economy.reward_offers DROP CONSTRAINT reward_offers_progress_kind_check;
ALTER TABLE economy.reward_offers ADD CONSTRAINT reward_offers_progress_kind_check
 CHECK (progress_kind IN ('none','purchases','crafts','npc_kills'));

INSERT INTO economy.reward_offers(source,id,name,asset_id,amount,progress_kind,target) VALUES
 ('quest','first_streuner','First Streuner','credits',500,'npc_kills',1),
 ('quest','sector_patrol','Sector patrol','credits',1500,'npc_kills',5)
ON CONFLICT (source,id) DO NOTHING;
