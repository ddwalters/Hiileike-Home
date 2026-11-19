CREATE TABLE planner.workspaces (
    owner_id text PRIMARY KEY,
    payload jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(payload) = 'object'),
    updated_at timestamptz NOT NULL DEFAULT now()
);

INSERT INTO planner.schema_migrations (version) VALUES (1);
