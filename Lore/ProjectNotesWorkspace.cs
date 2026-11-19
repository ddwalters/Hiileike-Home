using System.Security.Claims;
using HiileikeHome.Planner;

namespace HiileikeHome.Lore;

public sealed class ProjectNotesWorkspace(PlannerStore planner, LoreStore notes, ILogger<ProjectNotesWorkspace> logger)
{
    public async Task<PlannerWorkspace> ReadAsync(ClaimsPrincipal user)
    {
        var workspace = await planner.ReadAsync(user);
        if (workspace.Projects.Any(project => project.IncludesLegacyNotes))
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
        return workspace.Projects.Select(project => project.Id).Append(NoteCatalog.LegacyId).ToHashSet();
    }

    public static IReadOnlySet<Guid> CatalogIds(IEnumerable<PlannerProject> projects)
    {
        var scope = new HashSet<Guid>();
        foreach (var project in projects)
        {
            scope.Add(project.Id);
            if (project.IncludesLegacyNotes)
                scope.Add(NoteCatalog.LegacyId);
        }
        return scope;
    }

    public static PlannerProject? FindProject(PlannerWorkspace workspace, Guid catalogId) => catalogId == NoteCatalog.LegacyId
        ? workspace.Projects.FirstOrDefault(project => project.IncludesLegacyNotes)
        : workspace.Project(catalogId);

    public static Guid PrimaryCatalog(PlannerProject project) => project.IncludesLegacyNotes ? NoteCatalog.LegacyId : project.Id;
}
