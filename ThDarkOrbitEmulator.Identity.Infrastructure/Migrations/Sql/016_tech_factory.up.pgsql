CREATE TABLE economy.tech_factory_jobs (
    account_id uuid PRIMARY KEY REFERENCES identity.accounts(id),
    job_id uuid NOT NULL,
    recipe_id text NOT NULL,
    output_asset_id text NOT NULL REFERENCES economy.assets(id),
    output_quantity bigint NOT NULL CHECK (output_quantity > 0),
    ready_at timestamptz NOT NULL
);
CREATE TABLE economy.tech_factory_actions (
    account_id uuid NOT NULL REFERENCES identity.accounts(id),
    action_key text NOT NULL,
    PRIMARY KEY (account_id, action_key)
);
CREATE TABLE economy.tech_factory_legacy_imports (
    account_id uuid PRIMARY KEY REFERENCES identity.accounts(id)
);
INSERT INTO economy.assets(id,kind,name) VALUES
 ('speed_boost','resource','Speed boost'),
 ('energy_charge','resource','Energy charge') ON CONFLICT (id) DO NOTHING;
INSERT INTO economy.recipes(id,name,inputs,outputs,craft_seconds) VALUES
 ('tech_speed','Speed boost','[{"kind":"resource","id":"tech_components","quantity":250}]','[{"kind":"resource","id":"speed_boost","quantity":1}]',300),
 ('tech_energy','Energy charge','[{"kind":"resource","id":"tech_components","quantity":300}]','[{"kind":"resource","id":"energy_charge","quantity":1}]',360)
ON CONFLICT (id) DO NOTHING;
