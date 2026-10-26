CREATE TABLE economy.reward_offers (
    source text NOT NULL CHECK (source IN ('payment','voucher','quest','battle_pass')),
    id text NOT NULL,
    name text NOT NULL,
    asset_id text NOT NULL REFERENCES economy.assets(id),
    amount bigint NOT NULL CHECK (amount > 0),
    progress_kind text NOT NULL DEFAULT 'none' CHECK (progress_kind IN ('none','purchases','crafts')),
    target bigint NOT NULL DEFAULT 0 CHECK (target >= 0),
    enabled boolean NOT NULL DEFAULT true,
    PRIMARY KEY(source,id)
);
INSERT INTO economy.reward_offers(source,id,name,asset_id,amount,progress_kind,target) VALUES
 ('payment','starter','Starter practice grant','credits',1000,'none',0),
 ('payment','hangar','Hangar practice grant','credits',3000,'none',0),
 ('payment','fleet','Fleet practice grant','credits',5000,'none',0),
 ('voucher','TRAINING-500','Training practice code','credits',500,'none',0),
 ('voucher','HANGAR-750','Hangar practice code','credits',750,'none',0),
 ('quest','first_purchase','First supply order','credits',250,'purchases',1),
 ('quest','field_supplier','Field supplier','credits',500,'purchases',3),
 ('quest','first_craft','First assembly','credits',400,'crafts',1),
 ('battle_pass','supplier','Buy one Shop item','credits',200,'purchases',1),
 ('battle_pass','engineer','Craft one Assembly item','credits',300,'crafts',1)
ON CONFLICT (source,id) DO NOTHING;
