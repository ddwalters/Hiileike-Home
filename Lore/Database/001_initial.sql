-- Run while connected to hiileikehome_db. The lore schema must already exist.
CREATE TABLE IF NOT EXISTS lore.schema_migrations (
    version integer PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE lore.entries (
    id uuid PRIMARY KEY,
    kind smallint NOT NULL CHECK (kind BETWEEN 0 AND 3),
    status smallint NOT NULL CHECK (status BETWEEN 0 AND 3),
    title varchar(120) NOT NULL CHECK (length(btrim(title)) > 0),
    summary text NOT NULL,
    notes text NOT NULL,
    implementation text NOT NULL,
    pinned boolean NOT NULL,
    updated_at timestamptz NOT NULL,
    revision integer NOT NULL CHECK (revision > 0)
);

CREATE TABLE lore.entry_revisions (
    entry_id uuid NOT NULL REFERENCES lore.entries(id) ON DELETE CASCADE,
    revision integer NOT NULL CHECK (revision > 0),
    recorded_at timestamptz NOT NULL,
    snapshot jsonb NOT NULL,
    PRIMARY KEY (entry_id, revision)
);

CREATE TABLE lore.entry_links (
    entry_a uuid NOT NULL REFERENCES lore.entries(id) ON DELETE CASCADE,
    entry_b uuid NOT NULL REFERENCES lore.entries(id) ON DELETE CASCADE,
    PRIMARY KEY (entry_a, entry_b),
    CHECK (entry_a < entry_b)
);
CREATE INDEX entry_links_b_idx ON lore.entry_links (entry_b);

CREATE TABLE lore.tags (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name text NOT NULL CHECK (length(btrim(name)) > 0)
);
CREATE UNIQUE INDEX tags_name_idx ON lore.tags (lower(name));

CREATE TABLE lore.entry_tags (
    entry_id uuid NOT NULL REFERENCES lore.entries(id) ON DELETE CASCADE,
    tag_id bigint NOT NULL REFERENCES lore.tags(id),
    position integer NOT NULL CHECK (position >= 0),
    PRIMARY KEY (entry_id, tag_id),
    UNIQUE (entry_id, position)
);
CREATE INDEX entry_tags_tag_idx ON lore.entry_tags (tag_id);

CREATE TABLE lore.pictures (
    id uuid PRIMARY KEY,
    entry_id uuid NOT NULL REFERENCES lore.entries(id) ON DELETE CASCADE,
    name text NOT NULL,
    content_type text NOT NULL CHECK (content_type IN ('image/png', 'image/jpeg', 'image/webp', 'image/gif')),
    storage_key text NOT NULL UNIQUE,
    UNIQUE (entry_id, id)
);

CREATE TABLE lore.revision_pictures (
    entry_id uuid NOT NULL,
    revision integer NOT NULL,
    picture_id uuid NOT NULL,
    position integer NOT NULL CHECK (position >= 0),
    PRIMARY KEY (entry_id, revision, picture_id),
    UNIQUE (entry_id, revision, position),
    FOREIGN KEY (entry_id, revision)
        REFERENCES lore.entry_revisions(entry_id, revision) ON DELETE CASCADE,
    FOREIGN KEY (entry_id, picture_id)
        REFERENCES lore.pictures(entry_id, id) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX revision_pictures_picture_idx ON lore.revision_pictures (picture_id);

CREATE TABLE lore.drafts (
    slot_id uuid PRIMARY KEY,
    entry_id uuid UNIQUE REFERENCES lore.entries(id) ON DELETE CASCADE,
    stamp uuid NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now(),
    payload jsonb NOT NULL,
    CHECK ((entry_id IS NULL AND slot_id = '00000000-0000-0000-0000-000000000000')
        OR (entry_id IS NOT NULL AND slot_id = entry_id))
);

INSERT INTO lore.schema_migrations (version) VALUES (1);
