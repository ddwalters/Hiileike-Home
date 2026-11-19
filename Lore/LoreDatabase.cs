using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace HiileikeHome.Lore;

internal sealed class LoreDatabase(string connectionString) : IDisposable
{
    // The same lock is used by the Lore backup script while copying database and pictures.
    internal const long AdvisoryLock = 727930184011;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource source = NpgsqlDataSource.Create(connectionString);
    private bool initialized;

    public static LoreDatabase? Create(IConfiguration configuration)
    {
        return GetConnectionString(configuration) is { } connection ? new LoreDatabase(connection) : null;
    }

    internal static string? GetConnectionString(IConfiguration configuration)
    {
        if (configuration.GetConnectionString("Lore") is { Length: > 0 } connectionString)
            return connectionString;
        if (configuration["Lore:Database:Password"] is not { Length: > 0 } password)
            return null;

        var options = new NpgsqlConnectionStringBuilder
        {
            Host = configuration["Lore:Database:Host"] ?? "postgresql",
            Port = configuration.GetValue("Lore:Database:Port", 5432),
            Database = configuration["Lore:Database:Name"] ?? "hiileikehome_db",
            Username = configuration["Lore:Database:Username"] ?? "hiileike",
            Password = password,
            GssEncryptionMode = GssEncryptionMode.Disable,
            ApplicationName = "HiileikeHome Lore"
        };
        return options.ConnectionString;
    }

    public Session Begin()
    {
        var connection = source.OpenConnection();
        try
        {
            if (!initialized)
            {
                using var migration = connection.BeginTransaction();
                using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", connection, migration);
                command.Parameters.AddWithValue(AdvisoryLock);
                command.ExecuteNonQuery();
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS lore.schema_migrations (
                        version integer PRIMARY KEY,
                        applied_at timestamptz NOT NULL DEFAULT now()
                    );
                    """;
                command.Parameters.Clear();
                command.ExecuteNonQuery();
                command.CommandText = "SELECT coalesce(max(version), 0) FROM lore.schema_migrations";
                var version = (int)command.ExecuteScalar()!;
                string[] scripts = ["001_initial.sql", "002_workspace_entry_types.sql", "003_project_notes.sql"];
                if (version > scripts.Length)
                    throw new IOException("The Project Notes schema is newer than this application.");
                foreach (var scriptName in scripts.Skip(version))
                {
                    using var script = typeof(LoreDatabase).Assembly.GetManifestResourceStream(
                        $"HiileikeHome.Lore.Database.{scriptName}")
                        ?? throw new InvalidOperationException("The Lore database migration is missing.");
                    using var reader = new StreamReader(script);
                    command.CommandText = reader.ReadToEnd();
                    command.ExecuteNonQuery();
                }
                migration.Commit();
                initialized = true;
            }
            return new Session(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Dispose() => source.Dispose();

    internal sealed class Session : IDisposable
    {
        private readonly NpgsqlConnection connection;
        private readonly NpgsqlTransaction transaction;
        private Dictionary<Guid, LoreRevision[]>? original;

        public Session(NpgsqlConnection connection)
        {
            this.connection = connection;
            transaction = connection.BeginTransaction();
            try
            {
                Execute("SELECT pg_advisory_xact_lock($1)", AdvisoryLock);
            }
            catch
            {
                transaction.Dispose();
                throw;
            }
        }

        public Dictionary<Guid, LoreRevision[]> Load()
        {
            if (original is not null)
                return original;

            using var command = Command("SELECT entry_id, snapshot FROM lore.entry_revisions ORDER BY entry_id, revision");
            using var reader = command.ExecuteReader();
            var versions = new Dictionary<Guid, List<LoreRevision>>();
            while (reader.Read())
            {
                var id = reader.GetGuid(0);
                var version = JsonSerializer.Deserialize<LoreRevision>(reader.GetString(1), Json)
                    ?? throw new JsonException("A Lore revision is invalid.");
                if (!versions.TryGetValue(id, out var list))
                    versions.Add(id, list = []);
                list.Add(version);
            }
            return original = versions.ToDictionary(item => item.Key, item => item.Value.ToArray());
        }

        public bool HasLegacyNotes()
        {
            using var command = Command("""
                SELECT EXISTS (SELECT 1 FROM lore.entries WHERE project_id = $1)
                    OR EXISTS (SELECT 1 FROM lore.drafts WHERE project_id = $1)
                """, NoteCatalog.LegacyId);
            return (bool)command.ExecuteScalar()!;
        }

        public NoteCategory[] LoadCategories(Guid projectId)
        {
            using var command = Command("SELECT id, name FROM lore.note_categories WHERE project_id = $1", projectId);
            using var reader = command.ExecuteReader();
            var categories = new List<NoteCategory>();
            while (reader.Read())
                categories.Add(new(reader.GetGuid(0), reader.GetString(1)));
            return categories.ToArray();
        }

        public void SaveCategory(Guid projectId, NoteCategory category)
        {
            Execute("""
                INSERT INTO lore.note_categories (project_id, id, name) VALUES ($1, $2, $3)
                ON CONFLICT (project_id, id) DO UPDATE SET name = excluded.name
                """, projectId, category.Id, category.Name);
            transaction.Commit();
        }

        public LoreDraft? LoadDraft(Guid projectId, Guid id)
        {
            using var command = Command("SELECT payload FROM lore.drafts WHERE project_id = $1 AND slot_id = $2", projectId, id);
            return command.ExecuteScalar() is string json
                ? JsonSerializer.Deserialize<LoreDraft>(json, Json) ?? throw new JsonException("A Lore draft is invalid.")
                : null;
        }

        public void SaveDraft(Guid projectId, Guid id, LoreDraft draft)
        {
            Execute("""
                INSERT INTO lore.drafts (project_id, slot_id, entry_id, stamp, payload)
                VALUES ($1, $2, $3, $4, $5)
                ON CONFLICT (project_id, slot_id) DO UPDATE
                SET stamp = excluded.stamp, payload = excluded.payload, updated_at = now()
                """, projectId, id, id == Guid.Empty ? null : id, draft.Stamp, JsonValue(draft));
            transaction.Commit();
        }

        public bool ClearDraft(Guid projectId, Guid id, Guid stamp)
        {
            var count = Execute("DELETE FROM lore.drafts WHERE project_id = $1 AND slot_id = $2 AND stamp = $3", projectId, id, stamp);
            transaction.Commit();
            return count > 0;
        }

        public void Persist(Dictionary<Guid, LoreRevision[]> next)
        {
            var previous = Load();
            foreach (var id in previous.Keys.Except(next.Keys))
                Execute("DELETE FROM lore.entries WHERE id = $1", id);

            var changed = next.Where(item => !previous.TryGetValue(item.Key, out var before)
                || before[^1].Entry.Revision != item.Value[^1].Entry.Revision).ToArray();
            foreach (var item in changed)
            {
                var entry = item.Value[^1].Entry;
                Execute("""
                    INSERT INTO lore.entries
                        (id, kind, status, title, summary, notes, implementation, pinned, updated_at, revision, project_id)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
                    ON CONFLICT (id) DO UPDATE SET
                        kind = excluded.kind, status = excluded.status, title = excluded.title,
                        summary = excluded.summary, notes = excluded.notes, implementation = excluded.implementation,
                        pinned = excluded.pinned, updated_at = excluded.updated_at, revision = excluded.revision
                    """, entry.Id, (short?)entry.Kind, (short)entry.Status, entry.Title, entry.Summary, entry.Notes,
                    entry.Implementation, entry.Pinned, entry.Updated, entry.Revision, entry.CatalogId);
            }

            foreach (var item in changed)
            {
                var entry = item.Value[^1].Entry;
                var lastRevision = previous.TryGetValue(item.Key, out var before) ? before[^1].Entry.Revision : 0;
                foreach (var version in item.Value.Where(version => version.Entry.Revision > lastRevision))
                {
                    Execute("""
                        INSERT INTO lore.entry_revisions (entry_id, revision, recorded_at, snapshot)
                        VALUES ($1, $2, $3, $4)
                        """, entry.Id, version.Entry.Revision, version.Entry.Updated, JsonValue(version));
                    for (var position = 0; position < version.Entry.Pictures.Length; position++)
                    {
                        var picture = version.Entry.Pictures[position];
                        Execute("""
                            INSERT INTO lore.pictures (id, entry_id, name, content_type, storage_key)
                            VALUES ($1, $2, $3, $4, $5) ON CONFLICT (id) DO NOTHING
                            """, picture.Id, entry.Id, picture.Name, picture.ContentType, PictureKey(picture));
                        Execute("""
                            INSERT INTO lore.revision_pictures (entry_id, revision, picture_id, position)
                            VALUES ($1, $2, $3, $4)
                            """, entry.Id, version.Entry.Revision, picture.Id, position);
                    }
                }

                Execute("DELETE FROM lore.entry_tags WHERE entry_id = $1", entry.Id);
                var tags = entry.Tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                for (var position = 0; position < tags.Length; position++)
                {
                    using var tag = Command("""
                        INSERT INTO lore.tags (name) VALUES ($1)
                        ON CONFLICT (lower(name)) DO UPDATE SET name = lore.tags.name RETURNING id
                        """, tags[position]);
                    var tagId = (long)tag.ExecuteScalar()!;
                    Execute("INSERT INTO lore.entry_tags (entry_id, tag_id, position) VALUES ($1, $2, $3)", entry.Id, tagId, position);
                }
                Execute("DELETE FROM lore.entry_links WHERE entry_a = $1 OR entry_b = $1", entry.Id);
            }

            foreach (var item in changed)
                foreach (var linkedId in item.Value[^1].Entry.Links)
                    Execute("""
                        INSERT INTO lore.entry_links (entry_a, entry_b)
                        VALUES (least($1::uuid, $2::uuid), greatest($1::uuid, $2::uuid))
                        ON CONFLICT DO NOTHING
                        """, item.Key, linkedId);

            transaction.Commit();
        }

        private NpgsqlCommand Command(string sql, params object?[] values)
        {
            var command = new NpgsqlCommand(sql, connection, transaction);
            foreach (var value in values)
            {
                if (value is NpgsqlParameter parameter)
                    command.Parameters.Add(parameter);
                else
                    command.Parameters.AddWithValue(value ?? DBNull.Value);
            }
            return command;
        }

        private int Execute(string sql, params object?[] values)
        {
            using var command = Command(sql, values);
            return command.ExecuteNonQuery();
        }

        private static NpgsqlParameter JsonValue<T>(T value) => new()
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = JsonSerializer.Serialize(value, Json)
        };

        public void Dispose()
        {
            transaction.Dispose();
            connection.Dispose();
        }
    }

    internal static string PictureKey(Picture picture) => picture.Id.ToString("N") + (picture.ContentType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => throw new InvalidOperationException("Unsupported Lore picture format.")
    });
}
