ALTER TABLE economy.purchase_requests ADD COLUMN currency_id text NOT NULL DEFAULT 'credits';
INSERT INTO economy.assets(id,kind,name) VALUES
 ('targeting_boost','resource','Targeting boost'),
 ('shield_boost','resource','Shield boost'),
 ('repair_charge','resource','Repair charge') ON CONFLICT (id) DO NOTHING;
INSERT INTO economy.recipes(id,name,inputs,outputs,craft_seconds) VALUES
 ('tech_targeting','Targeting boost','[{"kind":"resource","id":"tech_components","quantity":100}]','[{"kind":"resource","id":"targeting_boost","quantity":1}]',120),
 ('tech_shield','Shield boost','[{"kind":"resource","id":"tech_components","quantity":150}]','[{"kind":"resource","id":"shield_boost","quantity":1}]',180),
 ('tech_repair','Repair charge','[{"kind":"resource","id":"tech_components","quantity":200}]','[{"kind":"resource","id":"repair_charge","quantity":1}]',240)
ON CONFLICT (id) DO NOTHING;
