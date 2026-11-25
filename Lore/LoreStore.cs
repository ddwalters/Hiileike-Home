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
    private readonly string root = DataRoot(environment, configuration);
    private readonly object gate = new();
    private Dictionary<Guid, LoreRevision[]>? history;
    private readonly LoreDatabase? database = LoreDatabase.Create(configuration);

    internal static string DataRoot(IWebHostEnvironment environment, IConfiguration configuration) => Path.GetFullPath(
        configuration["Lore:DataDirectory"] is { Length: > 0 } path ? path : Path.Combine("App_Data", "lore-builder"),
        environment.ContentRootPath);

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
            || File.Exists(DraftPath(NoteCatalog.LegacyId, Guid.Empty))
            || File.Exists(CategoryPath(NoteCatalog.LegacyId))));

    public Dictionary<Guid, NoteCategory[]> Categories(NoteScope scope) => Run(session =>
    {
        var categories = scope.Catalogs.Select(scope.ProjectFor).Distinct()
            .ToDictionary(id => id, id => Categories(id, session));
        if (scope.SharedNotesProject is not { } projectId || CategoryAliases(projectId, session) is not null)
            return categories;

        var projectCategories = categories[projectId].ToList();
        var added = new List<NoteCategory>();
        var aliases = new Dictionary<Guid, Guid>();
        foreach (var source in Categories(NoteCatalog.LegacyId, session))
        {
            var category = projectCategories.Find(item => item.Id == source.Id)
                ?? projectCategories.Find(item => item.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase));
            if (category is null)
            {
                category = new NoteCategory(source.Id, source.Name);
                projectCategories.Add(category);
                added.Add(category);
            }
            aliases.Add(source.Id, category.Id);
        }

        if (session is null)
            WriteJson(CategoryPath(projectId), new CategoryFile([.. projectCategories], aliases, CategoryReplacements(projectId, null)));
        else
            session.ImportCategories(projectId, added, aliases);
        categories[projectId] = projectCategories.OrderBy(category => category.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return categories;
    });

    public NoteCategory SaveCategory(Guid projectId, Guid? id, string name, string? expectedName, NoteScope scope)
    {
        RequireAccess(projectId, scope);
        if (projectId == NoteCatalog.LegacyId)
            throw new InvalidOperationException("Manage categories from the project.");
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
                WriteJson(CategoryPath(projectId), new CategoryFile(
                    categories.Where(category => category.Id != saved.Id).Append(saved).ToArray(),
                    CategoryAliases(projectId, null), CategoryReplacements(projectId, null)));
            return saved;
        });
    }

    public void DeleteCategory(Guid projectId, Guid id, string expectedName, Guid? replacementId, NoteScope scope)
    {
        RequireAccess(projectId, scope);
        if (projectId == NoteCatalog.LegacyId)
            throw new InvalidOperationException("Manage categories from the project.");
        Run<object?>(session =>
        {
            var categories = Categories(projectId, session);
            if (!categories.Any(category => category.Id == id && category.Name == expectedName))
                throw new InvalidOperationException("This category changed in another tab. Reload Project Notes and try again.");
            if (replacementId is { } replacement && (replacement == id || !categories.Any(category => category.Id == replacement)))
                throw new InvalidOperationException("Choose an existing category to move the notes to.");

            if (session is not null)
                session.DeleteCategory(projectId, id, replacementId);
            else
            {
                var file = ReadCategoryFile(CategoryPath(projectId));
                var replacements = file.Replacements ?? [];
                foreach (var prior in replacements.Where(item => item.Value == id).Select(item => item.Key).ToArray())
                    replacements[prior] = replacementId;
                replacements[id] = replacementId;
                WriteJson(CategoryPath(projectId), file with
                {
                    Categories = categories.Where(category => category.Id != id).ToArray(), Replacements = replacements
                });
            }
            return null;
        });
    }

    private string CategoryPath(Guid projectId) => Path.Combine(root, "categories", $"{projectId:N}.json");

    private sealed record CategoryFile(NoteCategory[] Categories, Dictionary<Guid, Guid>? Aliases = null,
        Dictionary<Guid, Guid?>? Replacements = null);

    private Dictionary<Guid, Guid>? CategoryAliases(Guid projectId, DatabaseSession? session) =>
        session is null ? ReadCategoryFile(CategoryPath(projectId)).Aliases : session.LoadCategoryAliases(projectId);

    private Dictionary<Guid, Guid?> CategoryReplacements(Guid projectId, DatabaseSession? session) =>
        session is null ? ReadCategoryFile(CategoryPath(projectId)).Replacements ?? [] : session.LoadCategoryReplacements(projectId);

    public Dictionary<Guid, Dictionary<Guid, Guid?>> CategoryReplacements(NoteScope scope) => Run(session =>
        scope.Catalogs.Select(scope.ProjectFor).Distinct().ToDictionary(id => id, id => CategoryReplacements(id, session)));

    private static LoreEntry ApplyCategoryReplacement(LoreEntry entry, Dictionary<Guid, Guid?> replacements) =>
        entry.EffectiveCategoryId is { } id && replacements.TryGetValue(id, out var replacement)
            ? entry with { CategoryId = replacement, CategoryName = null, Kind = null }
            : entry;

    private NoteCategory[] Categories(Guid projectId, DatabaseSession? session)
    {
        var categories = projectId == NoteCatalog.LegacyId
            ? NoteCatalog.LegacyCategories.ToDictionary(category => category.Id) : new Dictionary<Guid, NoteCategory>();
        var stored = session is null ? ReadCategoryFile(CategoryPath(projectId)).Categories : session.LoadCategories(projectId);
        foreach (var category in stored)
            categories[category.Id] = category with { LegacyKind = categories.GetValueOrDefault(category.Id)?.LegacyKind };
        return categories.Values.OrderBy(category => category.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static CategoryFile ReadCategoryFile(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var json = JsonDocument.Parse(file);
            if (json.RootElement.ValueKind == JsonValueKind.Array
                && json.RootElement.Deserialize<NoteCategory[]>(Json) is { } categories)
                return new CategoryFile(categories);
            return json.RootElement.Deserialize<CategoryFile>(Json) is { Categories: not null } data
                ? data : throw new JsonException("The note categories are invalid.");
        }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new CategoryFile([]);
        }
    }

    public LoreDraft? LoadDraft(Guid projectId, Guid id, NoteScope scope)
    {
        RequireAccess(projectId, scope);
        return Run(session =>
        {
            var draftProject = id == Guid.Empty ? projectId : scope.ProjectFor(projectId);
            var draft = LoadDraft(draftProject, id, session);
            if (draft is not null || draftProject == projectId)
                return draft is null ? null : PrepareDraft(draft, id, scope, session);

            if (LoadDraft(projectId, id, session) is not { } shared)
                return null;
            var recovered = shared with
            {
                Entry = PrepareDraft(shared with
                {
                    Entry = ProjectSharedCategory(shared.Entry, draftProject, CategoryAliases(draftProject, session), scope)
                }, id, scope, session).Entry
            };
            if (session is null)
            {
                WriteJson(DraftPath(draftProject, id), recovered);
                RemoveFile(DraftPath(projectId, id));
            }
            else
                session.MoveDraft(projectId, draftProject, id, recovered);
            return recovered;
        });
    }

    private LoreDraft PrepareDraft(LoreDraft draft, Guid id, NoteScope scope, DatabaseSession? session)
    {
        var entry = draft.Entry;
        var projectId = scope.ProjectFor(entry);
        if (entry.CatalogId == NoteCatalog.LegacyId && id != Guid.Empty
            && Load(session).TryGetValue(id, out var revisions))
        {
            var sharedProject = scope.ProjectFor(entry.CatalogId);
            var current = ProjectSharedCategory(revisions[^1].Entry, sharedProject, CategoryAliases(sharedProject, session), scope);
            projectId = scope.ProjectFor(current);
            if ((draft.WorkspaceProject ?? sharedProject) != projectId)
                entry = entry with { CategoryId = current.CategoryId, CategoryName = current.CategoryName, Kind = null };
            entry = entry with { WorkspaceProjectId = projectId };
        }

        entry = ApplyCategoryReplacement(entry, CategoryReplacements(projectId, session));
        return draft with { Entry = entry, WorkspaceProject = projectId };
    }

    public LoreEntry CopyLegacyDraft(Guid projectId, NoteScope scope)
    {
        RequireAccess(projectId, scope);
        RequireAccess(NoteCatalog.LegacyId, scope);
        if (projectId == NoteCatalog.LegacyId)
            throw new InvalidOperationException("Choose your own project to recover this draft.");

        return Run(session =>
        {
            if (LoadDraft(projectId, Guid.Empty, session) is not null)
                throw new InvalidOperationException("Finish your current new-note draft before recovering the other draft.");
            var source = LoadDraft(NoteCatalog.LegacyId, Guid.Empty, session)
                ?? throw new InvalidOperationException("No draft is available.");
            var entry = ProjectSharedCategory(source.Entry, projectId, CategoryAliases(projectId, session), scope) with
            {
                Id = Guid.NewGuid(), ProjectId = projectId, Kind = null, Revision = 0, Links = [], Pictures = []
            };
            entry = ApplyCategoryReplacement(entry, CategoryReplacements(projectId, session));
            var copy = new LoreDraft(Guid.NewGuid(), entry, source.Tags);
            if (session is null)
                WriteJson(DraftPath(projectId, Guid.Empty), copy);
            else
                session.SaveDraft(projectId, Guid.Empty, copy);
            return entry;
        });
    }

    private LoreDraft? LoadDraft(Guid projectId, Guid id, DatabaseSession? session)
    {
        var draft = session is null ? ReadDraft(DraftPath(projectId, id)) : session.LoadDraft(projectId, id);
        return id == Guid.Empty && draft is not null && Load(session).ContainsKey(draft.Entry.Id) ? null : draft;
    }

    public bool SaveDraft(Guid id, LoreDraft draft, Guid? expectedStamp, NoteScope scope)
    {
        var projectId = draft.Entry.CatalogId;
        RequireAccess(projectId, scope);
        if (projectId == NoteCatalog.LegacyId && id == Guid.Empty)
            return false;
        var draftProject = scope.ProjectFor(projectId);
        return Run(session =>
        {
            var catalog = Load(session);
            var previous = LoadDraft(draftProject, id, session);
            if (previous?.Stamp != expectedStamp || (id == Guid.Empty
                ? catalog.ContainsKey(draft.Entry.Id)
                : draft.Entry.Id != id || !catalog.TryGetValue(id, out var versions) || versions[^1].Entry.CatalogId != projectId))
                return false;

            draft = draft with { WorkspaceProject = scope.ProjectFor(draft.Entry) };
            if (session is null)
                WriteJson(DraftPath(draftProject, id), draft);
            else
                session.SaveDraft(draftProject, id, draft);
            return true;
        });
    }

    public bool ClearDraft(Guid projectId, Guid id, Guid stamp, NoteScope scope)
    {
        RequireAccess(projectId, scope);
        projectId = scope.ProjectFor(projectId);
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

    private static Guid SharedProject(LoreEntry entry, Guid projectId, NoteScope scope) =>
        entry.ProjectCategories?.GetValueOrDefault(projectId)?.ProjectId is { } destination && scope.Catalogs.Contains(destination)
            ? destination : projectId;

    private static LoreEntry ProjectSharedCategory(LoreEntry entry, Guid projectId, Dictionary<Guid, Guid>? aliases, NoteScope scope)
    {
        var selection = entry.ProjectCategories?.GetValueOrDefault(projectId);
        var categoryId = selection is not null ? selection.Id
            : entry.EffectiveCategoryId is { } id ? aliases?.GetValueOrDefault(id, id) ?? id : null;
        return entry with
        {
            CategoryId = categoryId,
            CategoryName = selection is null && categoryId is not null ? entry.CategoryLabel : selection?.Name,
            Kind = null,
            ProjectCategories = null,
            WorkspaceProjectId = SharedProject(entry, projectId, scope)
        };
    }

    public IReadOnlyDictionary<Guid, Guid> CategoryAliases(NoteScope scope) => scope.SharedNotesProject is { } projectId
        ? Run(session => CategoryAliases(projectId, session)) ?? new Dictionary<Guid, Guid>()
        : new Dictionary<Guid, Guid>();

    public (LoreEntry[] Entries, LoreRevision[] Revisions) Snapshot(Guid? entryId, NoteScope scope)
    {
        return Run(session =>
        {
            var catalog = Load(session);
            var entries = catalog.Values.Select(versions => versions[^1].Entry)
                .Where(entry => scope.Catalogs.Contains(entry.CatalogId)).ToArray();
            var revisions = entryId is { } id && catalog.TryGetValue(id, out var found)
                && scope.Catalogs.Contains(found[^1].Entry.CatalogId) ? found : [];
            var replacements = new Dictionary<Guid, Dictionary<Guid, Guid?>>();
            LoreEntry CurrentCategory(LoreEntry entry)
            {
                var workspaceProject = scope.ProjectFor(entry);
                if (!replacements.TryGetValue(workspaceProject, out var projectReplacements))
                    replacements[workspaceProject] = projectReplacements = CategoryReplacements(workspaceProject, session);
                return ApplyCategoryReplacement(entry, projectReplacements);
            }
            if (scope.SharedNotesProject is not { } projectId)
                return (entries.Select(CurrentCategory).ToArray(), revisions);

            var aliases = CategoryAliases(projectId, session);
            var backlinks = entries.Where(entry => entry.CatalogId != NoteCatalog.LegacyId)
                .SelectMany(entry => entry.Links.Select(id => (Target: id, Source: entry.Id)))
                .ToLookup(link => link.Target, link => link.Source);
            LoreEntry ProjectSharedEntry(LoreEntry entry) => ProjectSharedCategory(entry, projectId, aliases, scope) with
            {
                Links = entry.Links.Union(backlinks[entry.Id]).ToArray()
            };
            LoreEntry CurrentEntry(LoreEntry entry) =>
                CurrentCategory(entry.CatalogId == NoteCatalog.LegacyId ? ProjectSharedEntry(entry) : entry);
            var visibleRevisions = revisions.Select(version => version.Entry.CatalogId == NoteCatalog.LegacyId
                ? version with { Entry = ProjectSharedCategory(version.Entry, projectId, aliases, scope) }
                : version).ToArray();
            return (entries.Select(CurrentEntry).ToArray(), visibleRevisions);
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

    public LoreEntry Save(LoreEntry draft, IReadOnlyList<PictureUpload> uploads, NoteScope scope, Guid[]? initialLinks = null)
    {
        var projectId = draft.CatalogId;
        RequireAccess(projectId, scope);
        return Run(session =>
        {
            var catalog = Load(session);
            var current = CurrentEntries(catalog);
            current.TryGetValue(draft.Id, out var original);
            if (original is null && projectId == NoteCatalog.LegacyId)
                throw new InvalidOperationException("Create new notes in your own project.");
            if (original is not null && original.CatalogId != projectId)
                throw new InvalidOperationException("This note belongs to a different project.");
            VerifyRevision(original, draft.Revision);
            var shared = projectId == NoteCatalog.LegacyId;
            var workspaceProject = scope.ProjectFor(projectId);
            var destination = shared && original is not null ? SharedProject(original, workspaceProject, scope) : workspaceProject;
            draft = ApplyCategoryReplacement(draft, CategoryReplacements(destination, session));
            var title = draft.Title.Trim();
            if (title.Length is 0 or > 120)
                throw new InvalidOperationException("Enter a title between 1 and 120 characters.");

            var next = new Dictionary<Guid, LoreEntry>(current);
            NoteCategory? category = null;
            if (draft.EffectiveCategoryId is { } categoryId)
            {
                category = Categories(destination, session).FirstOrDefault(item => item.Id == categoryId)
                    ?? throw new InvalidOperationException("Choose a category from this project.");
            }
            var links = draft.Links.Where(id => id != draft.Id && next.TryGetValue(id, out var linked)
                && scope.Catalogs.Contains(linked.CatalogId)).Distinct().ToArray();
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

            var previousLinks = original?.Links ?? [];
            if (shared)
            {
                previousLinks = previousLinks.Union(current.Values.Where(entry => entry.CatalogId != projectId
                    && scope.Catalogs.Contains(entry.CatalogId) && entry.Links.Contains(draft.Id)).Select(entry => entry.Id)).ToArray();
            }
            foreach (var id in previousLinks.Union(links))
            {
                if (!next.TryGetValue(id, out var linked) || !scope.Catalogs.Contains(linked.CatalogId)
                    || linked.CatalogId == NoteCatalog.LegacyId && !shared)
                    continue;

                var shouldLink = links.Contains(id);
                if (shared && linked.CatalogId != projectId && initialLinks is not null && initialLinks.Contains(id) == shouldLink)
                    continue;
                if (shouldLink == linked.Links.Contains(draft.Id))
                    continue;

                next[id] = linked with
                {
                    Links = shouldLink ? [.. linked.Links, draft.Id] : linked.Links.Where(link => link != draft.Id).ToArray(),
                    Updated = updated,
                    Revision = linked.Revision + 1
                };
            }

            if (shared && original is not null)
            {
                // Keep private categories and backlinks out of the shared revision fields.
                var assignments = new Dictionary<Guid, NoteCategorySelection>(original.ProjectCategories ?? []);
                assignments[workspaceProject] = new NoteCategorySelection(category?.Id, category?.Name,
                    destination == workspaceProject ? null : destination);
                next[saved.Id] = saved with
                {
                    CategoryId = original.CategoryId, CategoryName = original.CategoryName, Kind = original.Kind,
                    Links = links.Where(id => current[id].CatalogId == NoteCatalog.LegacyId).ToArray(),
                    ProjectCategories = assignments
                };
            }
            else
                next[saved.Id] = saved with { ProjectCategories = null };
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

    public void Move(Guid id, int revision, Guid destination, Guid? categoryId, NoteScope scope)
    {
        RequireAccess(destination, scope);
        if (destination == NoteCatalog.LegacyId)
            throw new InvalidOperationException("Choose a project to move the note to.");

        Run<object?>(session =>
        {
            var catalog = Load(session);
            var current = CurrentEntries(catalog);
            var original = current.GetValueOrDefault(id);
            if (original is null || !scope.Catalogs.Contains(original.CatalogId))
                throw new InvalidOperationException("This note is no longer available.");
            VerifyRevision(original, revision);

            var shared = original.CatalogId == NoteCatalog.LegacyId;
            var source = scope.ProjectFor(original.CatalogId);
            var currentProject = shared ? SharedProject(original, source, scope) : source;
            if (destination == currentProject)
                throw new InvalidOperationException("Choose a different project.");
            var category = categoryId is { } requested
                ? Categories(destination, session).FirstOrDefault(item => item.Id == requested)
                    ?? throw new InvalidOperationException("Choose a category in the destination project.")
                : null;
            var moved = original with { Updated = DateTimeOffset.UtcNow, Revision = revision + 1 };
            if (shared)
            {
                var assignments = new Dictionary<Guid, NoteCategorySelection>(original.ProjectCategories ?? []);
                assignments[source] = new(category?.Id, category?.Name, destination == source ? null : destination);
                moved = moved with { ProjectCategories = assignments };
            }
            else
            {
                moved = moved with { ProjectId = destination, CategoryId = category?.Id, CategoryName = category?.Name, Kind = null };
            }
            current[id] = moved;

            var draft = LoadDraft(source, id, session);
            var draftDestination = shared ? source : destination;
            if (draft is not null && draftDestination != source && LoadDraft(draftDestination, id, session) is { } destinationDraft
                && (session is not null || destinationDraft.Stamp != draft.Stamp))
                throw new InvalidOperationException("This note already has a saved draft in the destination project.");
            var movedDraft = draft is null ? null : draft with
            {
                Entry = draft.Entry with
                {
                    ProjectId = moved.ProjectId, CategoryId = category?.Id, CategoryName = category?.Name, Kind = null,
                    Revision = draft.Entry.Revision == revision ? moved.Revision : draft.Entry.Revision
                },
                WorkspaceProject = destination
            };
            if (session is not null)
            {
                if (movedDraft is not null)
                    session.MoveDraft(source, draftDestination, id, movedDraft, commit: false);
                Persist(RecordChanges(catalog, current), session);
                return null;
            }

            if (movedDraft is not null)
                WriteJson(DraftPath(draftDestination, id), movedDraft);
            try
            {
                Persist(RecordChanges(catalog, current), null);
            }
            catch
            {
                if (movedDraft is not null && draftDestination != source)
                    RemoveFile(DraftPath(draftDestination, id));
                else if (draft is not null)
                    WriteJson(DraftPath(source, id), draft);
                throw;
            }
            if (draft is not null && draftDestination != source)
                RemoveFile(DraftPath(source, id));
            return null;
        });
    }

    public void Delete(Guid id, int revision, NoteScope scope)
    {
        Run<object?>(session =>
        {
            var catalog = Load(session);
            var current = CurrentEntries(catalog);
            if (!current.TryGetValue(id, out var original) || !scope.Catalogs.Contains(original.CatalogId))
                return null;

            VerifyRevision(original, revision);
            var next = new Dictionary<Guid, LoreEntry>(current);
            next.Remove(id);
            var updated = DateTimeOffset.UtcNow;
            foreach (var entry in current.Values.Where(entry => entry.Id != id && entry.Links.Contains(id)))
                next[entry.Id] = entry with
                {
                    Links = entry.Links.Where(other => other != id).ToArray(),
                    Updated = updated,
                    Revision = entry.Revision + 1
                };

            Persist(RecordChanges(catalog, next), session);
            if (session is null)
            {
                RemoveFile(DraftPath(original.CatalogId, id));
                if (scope.ProjectFor(original.CatalogId) != original.CatalogId)
                    RemoveFile(DraftPath(scope.ProjectFor(original.CatalogId), id));
            }
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

    private static void RequireAccess(Guid projectId, NoteScope scope)
    {
        if (!scope.Catalogs.Contains(projectId))
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
