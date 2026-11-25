CREATE TABLE lore.project_category_imports (
    project_id uuid PRIMARY KEY,
    aliases jsonb NOT NULL
);

ALTER TABLE lore.drafts DROP CONSTRAINT drafts_entry_id_key;
CREATE INDEX drafts_entry_idx ON lore.drafts (entry_id);

INSERT INTO lore.schema_migrations (version) VALUES (4);
