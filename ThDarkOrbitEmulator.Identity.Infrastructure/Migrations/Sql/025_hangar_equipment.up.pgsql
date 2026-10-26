CREATE TABLE economy.hangar_equipment_assignments (
    account_id uuid NOT NULL REFERENCES identity.accounts(id),
    hangar_slot integer NOT NULL CHECK (hangar_slot BETWEEN 1 AND 2),
    configuration integer NOT NULL CHECK (configuration BETWEEN 1 AND 2),
    item_id text NOT NULL REFERENCES economy.items(id),
    quantity integer NOT NULL CHECK (quantity BETWEEN 1 AND 999),
    PRIMARY KEY (account_id, hangar_slot, configuration, item_id)
);
CREATE TABLE economy.hangar_equipment_imports (
    account_id uuid PRIMARY KEY REFERENCES identity.accounts(id)
);
