CREATE TABLE economy.crafts (
    account_id uuid NOT NULL REFERENCES identity.accounts(id),
    recipe_id text NOT NULL REFERENCES economy.recipes(id),
    quantity bigint NOT NULL CHECK (quantity > 0),
    PRIMARY KEY(account_id,recipe_id)
);
