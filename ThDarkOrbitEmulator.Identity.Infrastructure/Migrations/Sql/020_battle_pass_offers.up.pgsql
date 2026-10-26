-- Emulator-defined higher battle pass tiers matching LocalEvents.Tiers; no Live battle pass data was captured.
INSERT INTO economy.reward_offers(source,id,name,asset_id,amount,progress_kind,target) VALUES
 ('battle_pass','quartermaster','Buy three Shop items','credits',400,'purchases',3),
 ('battle_pass','artisan','Craft three Assembly items','credits',600,'crafts',3),
 ('battle_pass','tycoon','Buy ten Shop items','credits',1000,'purchases',10)
ON CONFLICT (source,id) DO NOTHING;
