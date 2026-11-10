using System.Text.Json;

namespace HiileikeHome.Lore;

public sealed class LoreStore(IWebHostEnvironment environment, IConfiguration configuration)
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
    private Dictionary<Guid, LoreEntry>? entries;

    private string CatalogPath => Path.Combine(root, "entries.json");
    public string PicturePath(Guid id) => Path.Combine(root, "pictures", id.ToString("N"));

    public LoreEntry[] Snapshot()
    {
        lock (gate)
            return Load().Values.ToArray();
    }

    public Picture? FindPicture(Guid id)
    {
        lock (gate)
            return Load().Values.SelectMany(entry => entry.Pictures)
                .FirstOrDefault(picture => picture.Id == id && File.Exists(PicturePath(id)));
    }

    public LoreEntry Save(LoreEntry draft, IReadOnlyList<PictureUpload> uploads)
    {
        lock (gate)
        {
            var current = Load();
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

            var saved = draft with
            {
                Title = title,
                Links = links,
                Pictures = pictures,
                Updated = DateTimeOffset.UtcNow,
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
                    Revision = linked.Revision + 1
                };
            }

            next[saved.Id] = saved;
            var written = new List<Guid>();
            try
            {
                if (uploads.Count > 0)
                    Directory.CreateDirectory(Path.Combine(root, "pictures"));

                foreach (var upload in uploads)
                {
                    using var file = new FileStream(PicturePath(upload.Picture.Id), FileMode.CreateNew);
                    written.Add(upload.Picture.Id);
                    file.Write(upload.Bytes);
                    file.Flush(flushToDisk: true);
                }

                Persist(next);
            }
            catch
            {
                foreach (var id in written)
                    RemoveFile(PicturePath(id));
                throw;
            }

            foreach (var picture in original?.Pictures ?? [])
                if (!pictures.Contains(picture))
                    RemoveFile(PicturePath(picture.Id));

            return saved;
        }
    }

    public void Delete(Guid id, int revision)
    {
        lock (gate)
        {
            var current = Load();
            if (!current.TryGetValue(id, out var original))
                return;

            VerifyRevision(original, revision);
            var next = new Dictionary<Guid, LoreEntry>(current);
            next.Remove(id);
            foreach (var link in original.Links)
                if (next.TryGetValue(link, out var entry))
                    next[link] = entry with
                    {
                        Links = entry.Links.Where(other => other != id).ToArray(),
                        Revision = entry.Revision + 1
                    };

            Persist(next);
            foreach (var picture in original.Pictures)
                RemoveFile(PicturePath(picture.Id));
        }
    }

    public bool LoadExamples()
    {
        lock (gate)
        {
            if (Load().Count > 0)
                return false;

            var entriesById = LoreExamples.Create().ToDictionary(entry => entry.Id);
            Persist(entriesById);
            return true;
        }
    }

    private Dictionary<Guid, LoreEntry> Load()
    {
        if (entries is not null)
            return entries;

        return entries = File.Exists(CatalogPath)
            ? (JsonSerializer.Deserialize<LoreEntry[]>(File.ReadAllBytes(CatalogPath), Json)
                ?? throw new InvalidDataException("The lore catalog is empty or invalid.")).ToDictionary(entry => entry.Id)
            : [];
    }

    private void Persist(Dictionary<Guid, LoreEntry> next)
    {
        Directory.CreateDirectory(root);
        var temp = Path.Combine(root, $"entries-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew))
            {
                JsonSerializer.Serialize(file, next.Values, Json);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, CatalogPath, overwrite: true);
            entries = next;
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

    private static void RemoveFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
