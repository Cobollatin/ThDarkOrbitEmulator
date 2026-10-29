INSERT INTO economy.items(id,name,type) VALUES
 ('pulse_charge','Pulse charge','consumable') ON CONFLICT (id) DO UPDATE SET enabled=true;

INSERT INTO economy.listings(id,item_id,prices) VALUES
 ('pulse_charge','pulse_charge','{"credits":1000}'::jsonb) ON CONFLICT (id) DO UPDATE SET enabled=true;
