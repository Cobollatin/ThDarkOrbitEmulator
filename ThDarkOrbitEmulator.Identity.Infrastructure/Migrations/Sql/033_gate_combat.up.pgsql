INSERT INTO economy.assets(id,kind,name) VALUES
 ('ucb_100','resource','UCB-100 ammunition') ON CONFLICT DO NOTHING;

ALTER TABLE economy.gate_runs ADD COLUMN lives integer NOT NULL DEFAULT 5 CHECK (lives >= 0);
ALTER TABLE economy.gate_runs ADD COLUMN ship_hp integer NOT NULL DEFAULT 1000 CHECK (ship_hp >= 0);
ALTER TABLE economy.gate_runs ADD COLUMN npc_hp integer NOT NULL DEFAULT 1000 CHECK (npc_hp > 0);
ALTER TABLE economy.gate_runs ADD COLUMN kills integer NOT NULL DEFAULT 0 CHECK (kills >= 0);
-- Earlier menu-only progress had no kill evidence and cannot be treated as completion.
UPDATE economy.gate_runs SET wave=0,total_waves=10,completed_at=NULL,lives=5,ship_hp=1000,npc_hp=1000,kills=0;

CREATE TABLE economy.gate_achievements (
 account_id uuid NOT NULL REFERENCES identity.accounts(id),
 gate_id text NOT NULL,
 completed_at timestamptz NOT NULL,
 PRIMARY KEY (account_id,gate_id)
);
