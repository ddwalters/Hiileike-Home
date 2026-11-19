ALTER TABLE lore.entries
    ADD COLUMN project_id uuid NOT NULL DEFAULT '45d7d424-dc10-49e9-8cae-ce9e8bcdc500',
    ALTER COLUMN kind DROP NOT NULL;
CREATE INDEX entries_project_idx ON lore.entries (project_id);

ALTER TABLE lore.drafts
    ADD COLUMN project_id uuid NOT NULL DEFAULT '45d7d424-dc10-49e9-8cae-ce9e8bcdc500',
    DROP CONSTRAINT drafts_pkey,
    ADD PRIMARY KEY (project_id, slot_id);

CREATE TABLE lore.note_categories (
    project_id uuid NOT NULL,
    id uuid NOT NULL,
    name text NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 64),
    PRIMARY KEY (project_id, id)
);
CREATE UNIQUE INDEX note_categories_name_idx ON lore.note_categories (project_id, lower(name));

INSERT INTO lore.schema_migrations (version) VALUES (3);
