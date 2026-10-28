INSERT INTO economy.assets(id,kind,name) VALUES ('prometid','resource','Prometid') ON CONFLICT DO NOTHING;

CREATE TABLE economy.skylab_upgrade_jobs (
    account_id uuid NOT NULL REFERENCES identity.accounts(id),
    collector_id text NOT NULL,
    expected_level integer NOT NULL CHECK (expected_level BETWEEN 1 AND 9),
    ready_at timestamptz NOT NULL,
    action_key text NOT NULL,
    PRIMARY KEY (account_id, collector_id),
    UNIQUE (account_id, action_key)
);

CREATE TABLE economy.skylab_refinery_jobs (
    account_id uuid PRIMARY KEY REFERENCES identity.accounts(id),
    ready_at timestamptz NOT NULL,
    action_key text NOT NULL,
    UNIQUE (account_id, action_key)
);

CREATE TABLE economy.gate_runs (
    account_id uuid NOT NULL REFERENCES identity.accounts(id),
    gate_id text NOT NULL,
    wave integer NOT NULL CHECK (wave >= 0),
    total_waves integer NOT NULL CHECK (total_waves > 0),
    completed_at timestamptz,
    PRIMARY KEY (account_id, gate_id)
);

CREATE TABLE economy.gate_wave_actions (
    account_id uuid NOT NULL REFERENCES identity.accounts(id),
    action_key text NOT NULL,
    PRIMARY KEY (account_id, action_key)
);
