using System.Text.Json;
using System.Data.Common;
using DatabaseSession = HiileikeHome.Lore.LoreDatabase.Session;

namespace HiileikeHome.Lore;

public sealed class LoreStore(IWebHostEnvironment environment, IConfiguration configuration, ILogger<LoreStore> logger) : IDisposable
{
    public const int PictureLimit = 24;
    public const int PictureSize = 8 * 1024 * 1024;
    public const int PendingPictureBytes = 48 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string root = Path.GetFullPath(
        configuration["Lore:DataDirectory"] is { Length: > 0 } dataPath ? dataPath
            : Path.Combine("App_Data", "lore-builder"),
        environment.ContentRootPath);
    private readonly object gate = new();
    private Dictionary<Guid, LoreRevision[]>? history;
    private readonly LoreDatabase? database = LoreDatabase.Create(configuration);

    private string CatalogPath => Path.Combine(root, "entries.json");
    private string PicturePath(Picture picture) => Path.Combine(root, "pictures", LoreDatabase.PictureKey(picture));
    private string ExistingPicturePath(Picture picture)
    {
        var path = PicturePath(picture);
        return File.Exists(path) ? path : Path.Combine(root, "pictures", picture.Id.ToString("N"));
    }
    private string DraftPath(Guid projectId, Guid id)
    {
        var directory = projectId == NoteCatalog.LegacyId ? Path.Combine(root, "drafts") : Path.Combine(root, "drafts", projectId.ToString("N"));
        return Path.Combine(directory, id == Guid.Empty ? "new.json" : $"{id:N}.json");
    }

    public bool HasLegacyNotes() => Run(session => session?.HasLegacyNotes()
        ?? (Load(null).Values.Any(versions => versions[^1].Entry.CatalogId == NoteCatalog.LegacyId)
            || File.Exists(DraftPath(NoteCatalog.LegacyId, Guid.Empty))));

    public Dictionary<Guid, NoteCategory[]> Categories(IReadOnlySet<Guid> projectIds) =>
        Run(session => projectIds.ToDictionary(id => id, id => Categories(id, session)));

    public NoteCategory SaveCategory(Guid projectId, Guid? id, string name, string? expectedName, IReadOnlySet<Guid> scope)
    {
        RequireAccess(projectId, scope);
        return Run(session =>
        {
            var categories = Categories(projectId, session);
            var current = categories.FirstOrDefault(category => category.Id == id);
            if (id is not null && (current is null || current.Name != expectedName))
                throw new InvalidOperationException("This category changed in another tab. Reload Project Notes and try again.");

            name = name.Trim();
            if (name.Length is < 1 or > 64)
                throw new InvalidOperationException("Enter a category name between 1 and 64 characters.");
            if (categories.Any(category => category.Id != id && category.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("This project already has a category with that name.");

            var saved = new NoteCategory(id ?? Guid.NewGuid(), name, current?.LegacyKind);
            if (session is not null)
                session.SaveCategory(projectId, saved);
            else
                WriteJson(CategoryPath(projectId), categories.Where(category => category.Id != saved.Id).Append(saved).ToArray());
            return saved;
        });
    }

    private string CategoryPath(Guid projectId) => Path.Combine(root, "categories", $"{projectId:N}.json");

    private NoteCategory[] Categories(Guid projectId, DatabaseSession? session)
    {
        var categories = projectId == NoteCatalog.LegacyId
            ? NoteCatalog.LegacyCategories.ToDictionary(category => category.Id) : new Dictionary<Guid, NoteCategory>();
        var stored = session is null ? ReadCategories(CategoryPath(projectId)) : session.LoadCategories(projectId);
        foreach (var category in stored)
            categories[category.Id] = category with { LegacyKind = categories.GetValueOrDefault(category.Id)?.LegacyKind };
        return categories.Values.OrderBy(category => category.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static NoteCategory[] ReadCategories(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            return JsonSerializer.Deserialize<NoteCategory[]>(file, Json) ?? throw new JsonException("The note categories are invalid.");
        }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    public LoreDraft? LoadDraft(Guid projectId, Guid id, IReadOnlySet<Guid> scope)
    {
        RequireAccess(projectId, scope);
        return Run(session => LoadDraft(projectId, id, session));
    }

    private LoreDraft? LoadDraft(Guid projectId, Guid id, DatabaseSession? session)
    {
        var draft = session is null ? ReadDraft(DraftPath(projectId, id)) : session.LoadDraft(projectId, id);
        return id == Guid.Empty && draft is not null && Load(session).ContainsKey(draft.Entry.Id) ? null : draft;
    }

    public bool SaveDraft(Guid id, LoreDraft draft, Guid? expectedStamp, IReadOnlySet<Guid> scope)
    {
        var projectId = draft.Entry.CatalogId;
        RequireAccess(projectId, scope);
        return Run(session =>
        {
            var catalog = Load(session);
            var previous = LoadDraft(projectId, id, session);
            if (previous?.Stamp != expectedStamp || (id == Guid.Empty
                ? catalog.ContainsKey(draft.Entry.Id)
                : draft.Entry.Id != id || !catalog.TryGetValue(id, out var versions) || versions[^1].Entry.CatalogId != projectId))
                return false;

            if (session is null)
                WriteJson(DraftPath(projectId, id), draft);
            else
                session.SaveDraft(projectId, id, draft);
            return true;
        });
    }

    public bool ClearDraft(Guid projectId, Guid id, Guid stamp, IReadOnlySet<Guid> scope)
    {
        RequireAccess(projectId, scope);
        return Run(session =>
        {
            if (session is not null)
                return session.ClearDraft(projectId, id, stamp);
            var path = DraftPath(projectId, id);
            if (ReadDraft(path)?.Stamp != stamp)
                return false;

            File.Delete(path);
            return true;
        });
    }

    private static LoreDraft? ReadDraft(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            var draft = JsonSerializer.Deserialize<LoreDraft>(file, Json);
            if (draft is not { Entry: not null, Tags: not null })
                throw new JsonException("The lore draft is invalid.");
            return draft;
        }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public (LoreEntry[] Entries, LoreRevision[] Revisions) Snapshot(Guid? entryId, IReadOnlySet<Guid> scope)
    {
        return Run(session =>
        {
            var catalog = Load(session);
            var revisions = entryId is { } id && catalog.TryGetValue(id, out var found)
                && scope.Contains(found[^1].Entry.CatalogId) ? found : [];
            return (catalog.Values.Select(versions => versions[^1].Entry).Where(entry => scope.Contains(entry.CatalogId)).ToArray(), revisions);
        });
    }

    public (Stream Stream, string ContentType)? OpenPicture(Guid id, IReadOnlySet<Guid> scope)
    {
        return Run<(Stream Stream, string ContentType)?>(session =>
        {
            var picture = Load(session).Values.Where(versions => scope.Contains(versions[^1].Entry.CatalogId)).SelectMany(versions => versions)
                .SelectMany(version => version.Entry.Pictures).FirstOrDefault(picture => picture.Id == id);
            if (picture is null)
                return null;

            try
            {
                var stream = File.Open(ExistingPicturePath(picture), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                return (stream, picture.ContentType);
            }
            catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                logger.LogWarning(ex, "The catalog references a missing lore picture {PictureId}.", id);
                return null;
            }
        });
    }

    public LoreEntry Save(LoreEntry draft, IReadOnlyList<PictureUpload> uploads, IReadOnlySet<Guid> scope)
    {
        var projectId = draft.CatalogId;
        RequireAccess(projectId, scope);
        return Run(session =>
        {
            var catalog = Load(session);
            var current = CurrentEntries(catalog);
            current.TryGetValue(draft.Id, out var original);
            if (original is not null && original.CatalogId != projectId)
                throw new InvalidOperationException("This note belongs to a different project.");
            VerifyRevision(original, draft.Revision);
            var title = draft.Title.Trim();
            if (title.Length is 0 or > 120)
                throw new InvalidOperationException("Enter a title between 1 and 120 characters.");

            var next = new Dictionary<Guid, LoreEntry>(current);
            NoteCategory? category = null;
            if (draft.EffectiveCategoryId is { } categoryId)
            {
                category = Categories(projectId, session).FirstOrDefault(item => item.Id == categoryId)
                    ?? throw new InvalidOperationException("Choose a category from this project.");
            }
            var links = draft.Links.Where(id => id != draft.Id && next.TryGetValue(id, out var linked)
                && scope.Contains(linked.CatalogId) && NoteCatalog.CanLink(projectId, linked.CatalogId)).Distinct().ToArray();
            var pictures = draft.Pictures.Concat(uploads.Select(upload => upload.Picture)).ToArray();
            if (pictures.Length > PictureLimit || uploads.Sum(upload => (long)upload.Bytes.Length) > PendingPictureBytes)
                throw new InvalidOperationException("Too many pictures in this save.");

            var updated = DateTimeOffset.UtcNow;
            var saved = draft with
            {
                Title = title,
                ProjectId = projectId,
                CategoryId = category?.Id,
                CategoryName = category?.Name,
                Kind = category?.LegacyKind,
                Links = links,
                Pictures = pictures,
                Updated = updated,
                Revision = draft.Revision + 1
            };

            foreach (var id in (original?.Links ?? []).Union(links))
            {
                if (!next.TryGetValue(id, out var linked))
                    continue;

                var shouldLink = links.Contains(id);
                if (shouldLink == linked.Links.Contains(draft.Id))
                    continue;

                next[id] = linked with
                {
                    Links = shouldLink ? [.. linked.Links, draft.Id] : linked.Links.Where(link => link != draft.Id).ToArray(),
                    Updated = updated,
                    Revision = linked.Revision + 1
                };
            }

            next[saved.Id] = saved;
            var written = new List<Picture>();
            try
            {
                if (uploads.Count > 0)
                    Directory.CreateDirectory(Path.Combine(root, "pictures"));

                foreach (var upload in uploads)
                {
                    using var file = new FileStream(PicturePath(upload.Picture), FileMode.CreateNew);
                    written.Add(upload.Picture);
                    file.Write(upload.Bytes);
                    file.Flush(flushToDisk: true);
                }

                Persist(RecordChanges(catalog, next), session);
            }
            catch
            {
                if (session is null)
                    foreach (var picture in written)
                        RemoveFile(PicturePath(picture));
                throw;
            }

            logger.LogInformation("Saved lore entry {EntryId} at revision {Revision}.", saved.Id, saved.Revision);
            return saved;
        });
    }

    public void Delete(Guid id, int revision, IReadOnlySet<Guid> scope)
    {
        Run<object?>(session =>
        {
            var catalog = Load(session);
            var current = CurrentEntries(catalog);
            if (!current.TryGetValue(id, out var original) || !scope.Contains(original.CatalogId))
                return null;

            VerifyRevision(original, revision);
            var next = new Dictionary<Guid, LoreEntry>(current);
            next.Remove(id);
            var updated = DateTimeOffset.UtcNow;
            foreach (var link in original.Links)
                if (next.TryGetValue(link, out var entry))
                    next[link] = entry with
                    {
                        Links = entry.Links.Where(other => other != id).ToArray(),
                        Updated = updated,
                        Revision = entry.Revision + 1
                    };

            Persist(RecordChanges(catalog, next), session);
            if (session is null)
                RemoveFile(DraftPath(original.CatalogId, id));
            foreach (var picture in catalog[id].SelectMany(version => version.Entry.Pictures).DistinctBy(picture => picture.Id))
                RemoveFile(ExistingPicturePath(picture));
            logger.LogInformation("Deleted lore entry {EntryId} with {RevisionCount} revisions.", id, catalog[id].Length);
            return null;
        });
    }

    private Dictionary<Guid, LoreRevision[]> Load(DatabaseSession? session)
    {
        if (session is not null)
            return session.Load();
        if (history is not null)
            return history;
        FileStream file;
        try
        {
            file = File.OpenRead(CatalogPath);
        }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return history = [];
        }

        using var input = file;
        using var document = JsonDocument.Parse(input);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            var legacy = new Dictionary<Guid, LoreEntry>();
            foreach (var entry in document.RootElement.Deserialize<LoreEntry[]>(Json) ?? [])
                if (!IsValid(entry) || !legacy.TryAdd(entry.Id, entry))
                    throw new JsonException("The lore catalog contains an invalid or duplicate entry.");

            return history = RecordChanges([], legacy);
        }

        var catalog = document.RootElement.Deserialize<Dictionary<Guid, LoreRevision[]>>(Json);
        if (catalog is null || catalog.Any(item => item.Value is not { Length: > 0 }
            || item.Value.Any(version => version is null || !IsValid(version.Entry)
                || version.Entry.Id != item.Key || version.Links is null)))
            throw new JsonException("The lore catalog contains invalid revision history.");

        return history = catalog;
    }

    private static bool IsValid([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] LoreEntry? entry) =>
        entry is { Revision: > 0, Title: not null, Summary: not null, Notes: not null, Implementation: not null,
            Tags: not null, Links: not null, Pictures: not null };

    private static Dictionary<Guid, LoreEntry> CurrentEntries(Dictionary<Guid, LoreRevision[]> catalog) =>
        catalog.ToDictionary(item => item.Key, item => item.Value[^1].Entry);

    private static Dictionary<Guid, LoreRevision[]> RecordChanges(
        Dictionary<Guid, LoreRevision[]> catalog, Dictionary<Guid, LoreEntry> entries)
    {
        var next = new Dictionary<Guid, LoreRevision[]>(entries.Count);
        foreach (var entry in entries.Values)
        {
            catalog.TryGetValue(entry.Id, out var versions);
            if (versions is not null && versions[^1].Entry.Revision == entry.Revision)
            {
                next.Add(entry.Id, versions);
                continue;
            }

            var links = entry.Links.Where(entries.ContainsKey)
                .Select(id => entries[id].LinkSnapshot()).ToArray();
            next.Add(entry.Id, [.. versions ?? [], new LoreRevision(entry, links)]);
        }
        return next;
    }

    private void Persist(Dictionary<Guid, LoreRevision[]> next, DatabaseSession? session)
    {
        if (session is not null)
        {
            session.Persist(next);
            return;
        }
        WriteJson(CatalogPath, next);
        history = next;
    }

    private void WriteJson<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew))
            {
                JsonSerializer.Serialize(file, value, Json);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            RemoveFile(temp);
        }
    }

    private static void VerifyRevision(LoreEntry? entry, int revision)
    {
        if ((entry?.Revision ?? 0) != revision)
            throw new InvalidOperationException("This entry changed in another tab. Copy any unsaved text you want to keep, then reopen the entry.");
    }

    private static void RequireAccess(Guid projectId, IReadOnlySet<Guid> scope)
    {
        if (!scope.Contains(projectId))
            throw new InvalidOperationException("This project's notes are unavailable.");
    }

    private void RemoveFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not clean up lore file {FileName}.", Path.GetFileName(path));
        }
    }

    private T Run<T>(Func<DatabaseSession?, T> action)
    {
        lock (gate)
        {
            try
            {
                using var session = database?.Begin();
                return action(session);
            }
            catch (DbException ex)
            {
                logger.LogError(ex, "The Lore database operation failed.");
                throw new IOException("Lore storage is temporarily unavailable.", ex);
            }
        }
    }

    public void Dispose() => database?.Dispose();
}
