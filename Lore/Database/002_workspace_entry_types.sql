ALTER TABLE lore.entries DROP CONSTRAINT entries_kind_check;
ALTER TABLE lore.entries ADD CONSTRAINT entries_kind_check CHECK (kind BETWEEN 0 AND 6);

INSERT INTO lore.schema_migrations (version) VALUES (2);
