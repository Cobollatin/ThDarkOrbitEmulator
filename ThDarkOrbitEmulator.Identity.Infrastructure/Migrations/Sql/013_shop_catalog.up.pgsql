-- Emulator-defined offers for existing Hangar item definitions; no Live Shop prices were captured.
INSERT INTO economy.listings(id,item_id,prices,stock_available,per_account_limit,restock_interval_seconds,restock_amount)
VALUES
 ('laser_lf2','laser_lf2','{"credits":300}',NULL,NULL,NULL,NULL),
 ('shield_sg3n_a03','shield_sg3n_a03','{"credits":450}',NULL,NULL,NULL,NULL),
 ('drone_iris','drone_iris','{"credits":2000,"uridium":10}',5,2,3600,2)
ON CONFLICT (id) DO NOTHING;
