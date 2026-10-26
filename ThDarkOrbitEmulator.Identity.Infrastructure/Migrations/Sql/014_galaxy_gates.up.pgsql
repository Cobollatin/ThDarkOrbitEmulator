CREATE TABLE economy.galaxy_gates (
    account_id uuid NOT NULL REFERENCES identity.accounts(id),
    gate_id text NOT NULL,
    parts_owned integer NOT NULL DEFAULT 0 CHECK (parts_owned >= 0),
    activated boolean NOT NULL DEFAULT false,
    PRIMARY KEY (account_id, gate_id)
);
