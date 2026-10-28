DROP TABLE economy.gate_achievements;
ALTER TABLE economy.gate_runs DROP COLUMN kills, DROP COLUMN npc_hp, DROP COLUMN ship_hp, DROP COLUMN lives;
DELETE FROM economy.assets WHERE id='ucb_100'
 AND NOT EXISTS (SELECT 1 FROM economy.balances WHERE asset_id='ucb_100')
 AND NOT EXISTS (SELECT 1 FROM economy.ledger WHERE asset_id='ucb_100');
