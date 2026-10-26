CREATE TABLE identity.runtime_variables (
    variable_key text PRIMARY KEY,
    category text NOT NULL,
    value_type text NOT NULL,
    default_value text NOT NULL,
    current_value text NOT NULL,
    updated_at_utc timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE identity.runtime_variable_history (
    change_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    variable_key text NOT NULL REFERENCES identity.runtime_variables(variable_key),
    category text NOT NULL,
    previous_value text NOT NULL,
    new_value text NOT NULL,
    default_value text NOT NULL,
    value_type text NOT NULL,
    surface text NOT NULL CHECK (surface IN ('ServerManagement','AdminCms','InGameAdmin')),
    changed_by text NOT NULL,
    node_id text NOT NULL,
    reason text NOT NULL,
    raw_command text,
    changed_at_utc timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX runtime_variable_history_key_id ON identity.runtime_variable_history(variable_key, change_id DESC);

-- Audit rows are append only, including when an override is rolled back.
CREATE FUNCTION identity.prevent_runtime_variable_history_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'runtime variable history is immutable'; END $$;
CREATE TRIGGER runtime_variable_history_immutable BEFORE UPDATE OR DELETE
ON identity.runtime_variable_history FOR EACH ROW
EXECUTE FUNCTION identity.prevent_runtime_variable_history_mutation();
