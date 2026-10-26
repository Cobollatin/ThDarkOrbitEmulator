-- Emulator-defined quest goals; no Live quest data was captured.
INSERT INTO economy.reward_offers(source,id,name,asset_id,amount,progress_kind,target) VALUES
 ('quest','bulk_buyer','Bulk buyer','credits',1000,'purchases',10),
 ('quest','master_engineer','Master engineer','credits',1500,'crafts',5)
ON CONFLICT (source,id) DO NOTHING;
