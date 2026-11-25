ALTER TABLE lore.note_categories
    ADD COLUMN deleted boolean NOT NULL DEFAULT false,
    ADD COLUMN replacement_id uuid;

DROP INDEX lore.note_categories_name_idx;
CREATE UNIQUE INDEX note_categories_name_idx ON lore.note_categories (project_id, lower(name)) WHERE NOT deleted;

INSERT INTO lore.schema_migrations (version) VALUES (5);
