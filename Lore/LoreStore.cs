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
    private string DraftPath(Guid id) => Path.Combine(root, "drafts", id == Guid.Empty ? "new.json" : $"{id:N}.json");

    public LoreDraft? LoadDraft(Guid id)
    {
        return Run(session =>
        {
            return LoadDraft(id, session);
        });
    }

    private LoreDraft? LoadDraft(Guid id, DatabaseSession? session)
    {
        var draft = session is null ? ReadDraft(DraftPath(id)) : session.LoadDraft(id);
        return id == Guid.Empty && draft is not null && Load(session).ContainsKey(draft.Entry.Id) ? null : draft;
    }

    public bool SaveDraft(Guid id, LoreDraft draft, Guid? expectedStamp)
    {
        return Run(session =>
        {
            var path = DraftPath(id);
            var previous = LoadDraft(id, session);
            if (previous?.Stamp != expectedStamp || (id == Guid.Empty
                ? Load(session).ContainsKey(draft.Entry.Id) : !Load(session).ContainsKey(id)))
                return false;

            if (session is null)
                WriteJson(path, draft);
            else
                session.SaveDraft(id, draft);
            return true;
        });
    }

    public bool ClearDraft(Guid id, Guid stamp)
    {
        return Run(session =>
        {
            if (session is not null)
                return session.ClearDraft(id, stamp);
            var path = DraftPath(id);
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

    public (LoreEntry[] Entries, LoreRevision[] Revisions) Snapshot(Guid? entryId)
    {
        return Run(session =>
        {
            var catalog = Load(session);
            var revisions = entryId is { } id && catalog.TryGetValue(id, out var found) ? found : [];
            return (catalog.Values.Select(versions => versions[^1].Entry).ToArray(), revisions);
        });
    }

    public (Stream Stream, string ContentType)? OpenPicture(Guid id)
    {
        return Run<(Stream Stream, string ContentType)?>(session =>
        {
            var picture = Load(session).Values.SelectMany(versions => versions)
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

    public LoreEntry Save(LoreEntry draft, IReadOnlyList<PictureUpload> uploads)
    {
        return Run(session =>
        {
            var catalog = Load(session);
            var current = CurrentEntries(catalog);
            current.TryGetValue(draft.Id, out var original);
            VerifyRevision(original, draft.Revision);
            var title = draft.Title.Trim();
            if (title.Length is 0 or > 120)
                throw new InvalidOperationException("Enter a title between 1 and 120 characters.");

            var next = new Dictionary<Guid, LoreEntry>(current);
            var links = draft.Links.Where(id => id != draft.Id && next.ContainsKey(id)).Distinct().ToArray();
            var pictures = draft.Pictures.Concat(uploads.Select(upload => upload.Picture)).ToArray();
            if (pictures.Length > PictureLimit || uploads.Sum(upload => (long)upload.Bytes.Length) > PendingPictureBytes)
                throw new InvalidOperationException("Too many pictures in this save.");

            var updated = DateTimeOffset.UtcNow;
            var saved = draft with
            {
                Title = title,
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

    public void Delete(Guid id, int revision)
    {
        Run<object?>(session =>
        {
            var catalog = Load(session);
            var current = CurrentEntries(catalog);
            if (!current.TryGetValue(id, out var original))
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
                RemoveFile(DraftPath(id));
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
                .Select(id => new LoreLink(id, entries[id].Kind, entries[id].Title)).ToArray();
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
