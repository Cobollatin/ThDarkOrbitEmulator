CREATE TABLE identity.admin_command_history (
    command_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    surface text NOT NULL CHECK (surface IN ('ServerManagement','AdminCms','InGameAdmin')),
    actor text NOT NULL,
    node_id text NOT NULL,
    command_text text NOT NULL,
    succeeded boolean NOT NULL,
    error text,
    executed_at_utc timestamptz NOT NULL DEFAULT now()
);
INSERT INTO economy.assets(id,kind,name) VALUES
('experience','currency','Experience'), ('honor','currency','Honor')
ON CONFLICT (id) DO NOTHING;
CREATE INDEX admin_command_history_at ON identity.admin_command_history(executed_at_utc DESC);
CREATE TRIGGER admin_command_history_immutable BEFORE UPDATE OR DELETE
ON identity.admin_command_history FOR EACH ROW
EXECUTE FUNCTION identity.prevent_runtime_variable_history_mutation();
