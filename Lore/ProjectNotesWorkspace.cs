using System.Security.Claims;
using HiileikeHome.Planner;

namespace HiileikeHome.Lore;

public sealed class ProjectNotesWorkspace(PlannerStore planner, LoreStore notes, ILogger<ProjectNotesWorkspace> logger)
{
    public async Task<PlannerWorkspace> ReadAsync(ClaimsPrincipal user)
    {
        var workspace = await planner.ReadAsync(user);
        if (workspace.HasLegacyNoteCollection())
            return workspace;

        bool hasLegacyNotes;
        try
        {
            hasLegacyNotes = notes.HasLegacyNotes();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            logger.LogWarning(exception, "Could not check for existing Project Notes");
            return workspace;
        }

        return hasLegacyNotes ? await planner.ChangeAsync(user, state => state.AttachLegacyNotes(PlannerCalendar.Today)) : workspace;
    }

    public async Task<IReadOnlySet<Guid>> PictureScopeAsync(ClaimsPrincipal user)
    {
        var workspace = await planner.ReadAsync(user);
        return workspace.NoteCollections().Select(collection => collection.Id).Append(NoteCatalog.LegacyId).ToHashSet();
    }

    public static IReadOnlySet<Guid> CatalogIds(IEnumerable<ProjectNoteCollection> collections)
    {
        var scope = new HashSet<Guid>();
        foreach (var collection in collections)
        {
            scope.Add(collection.Id);
            if (collection.IncludesLegacyNotes)
                scope.Add(NoteCatalog.LegacyId);
        }
        return scope;
    }

    public static NoteScope Access(PlannerWorkspace workspace) => new(
        CatalogIds(workspace.NoteCollections()), FindCollection(workspace, NoteCatalog.LegacyId)?.Id);

    public static ProjectNoteCollection? FindCollection(PlannerWorkspace workspace, Guid catalogId) => catalogId == NoteCatalog.LegacyId
        ? workspace.NoteCollections().FirstOrDefault(collection => collection.IncludesLegacyNotes)
        : workspace.NoteCollections().FirstOrDefault(collection => collection.Id == catalogId);
}
