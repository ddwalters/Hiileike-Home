namespace HiileikeHome.Planner;

public enum PlannerStatus { Open, InProgress, Done, Archived, Replaced }
public enum PlannerLane { Backlog, ThisWeek, InProgress, Done }
public enum ProgressMeasure { Tasks, Milestones, Numeric, Subprojects }
public enum ReviewAction { Continue, Defer, BreakDown }
public enum PlannerNoteTargetKind { Task, Project, Milestone }

public sealed record PlannerProject(Guid Id, string Name, string Notes, ProgressMeasure Measure,
    string Unit, int Target, int Value, int Revision, Guid? ParentId = null, bool HiddenFromDashboard = false,
    bool IncludeSubprojectNotes = false, bool IncludesLegacyNotes = false, Guid? NumericMetricId = null,
    string? LegacyNumericUnit = null, bool Archived = false);

public sealed record ProjectNoteCollection(Guid Id, string Name, bool IncludesLegacyNotes, bool IncludeSubprojectNotes = false)
{
    public static ProjectNoteCollection For(PlannerProject project) =>
        new(project.Id, project.Name, project.IncludesLegacyNotes, project.IncludeSubprojectNotes);
}

public sealed record PlannerMilestone(Guid Id, Guid ProjectId, string Title, DateOnly? Due, bool Done, int Revision,
    bool HiddenFromDashboard = false);

public sealed record PlannerTask(Guid Id, string Title, string Notes, Guid? ProjectId, Guid? MilestoneId,
    DateOnly? Due, DateOnly? Week, int Priority, PlannerStatus Status, Guid[] LoreIds, int Revision,
    bool HiddenFromDashboard = false, bool InboxPending = false, PlannerStatus? StatusBeforeArchive = null,
    int PriorityBeforeArchive = 0)
{
    public bool IsOpen => Status is PlannerStatus.Open or PlannerStatus.InProgress;
    public bool IsInbox => InboxPending && Status == PlannerStatus.Open && ProjectId is null && Week is null;

    public PlannerLane Lane(DateOnly week) => Status switch
    {
        PlannerStatus.InProgress => PlannerLane.InProgress,
        PlannerStatus.Done => PlannerLane.Done,
        _ when Week == week => PlannerLane.ThisWeek,
        _ => PlannerLane.Backlog
    };

    public static PlannerTask New(Guid? projectId = null, Guid? milestoneId = null) =>
        new(Guid.NewGuid(), "", "", projectId, milestoneId, null, null, 0, PlannerStatus.Open, [], 0);
}

public sealed record TaskCompletion(Guid TaskId, string Title, DateOnly Day);
public sealed record PlannerNoteLink(Guid NoteId, PlannerNoteTargetKind Kind, Guid TargetId);
public sealed record ReviewDecision(Guid TaskId, string Title, DateOnly Week, ReviewAction Action,
    DateOnly? Destination, string[] Steps, string Note, int? SourceRevision = null);
public sealed record ProgressUpdate(Guid ProjectId, DateOnly Day, int Value, Guid? MetricId = null);
public sealed record ReadingCheckpoint(Guid ProjectId, DateOnly Week, int Target, string Unit, Guid? MetricId = null)
{
    public bool UsesCurrentMetric(PlannerProject project) => project.Measure == ProgressMeasure.Numeric
        && MetricId == project.NumericMetricId && Unit.Equals(project.Unit, StringComparison.OrdinalIgnoreCase);
}

public readonly record struct ProjectProgress(int Value, int Target, string Unit, string? Detail = null)
{
    public int Percent => Target > 0 ? (int)Math.Clamp(Math.Round(100d * Value / Target), 0, 100) : 0;
    public string Label => Detail ?? (Target > 0 ? $"{Value:N0} / {Target:N0} {Unit}" : "No target yet");
}

public sealed class PlannerValidationException(string message) : Exception(message);

public sealed class PlannerWorkspace
{
    public List<PlannerProject> Projects { get; init; } = [];
    public List<ProjectNoteCollection> ArchivedNoteCollections { get; init; } = [];
    public List<PlannerMilestone> Milestones { get; init; } = [];
    public List<PlannerTask> Tasks { get; init; } = [];
    public List<TaskCompletion> Completions { get; init; } = [];
    public List<ReviewDecision> Decisions { get; init; } = [];
    public List<ProgressUpdate> ProgressUpdates { get; init; } = [];
    public List<ReadingCheckpoint> Checkpoints { get; init; } = [];
    public List<Guid> QueueOrder { get; init; } = [];
    public List<PlannerNoteLink> NoteLinks { get; init; } = [];

    public PlannerProject? Project(Guid? id) => Projects.Find(project => project.Id == id);
    public PlannerMilestone? Milestone(Guid? id) => Milestones.Find(milestone => milestone.Id == id);
    public PlannerTask? Task(Guid id) => Tasks.Find(task => task.Id == id);

    public void SetNoteLink(Guid noteId, PlannerNoteTargetKind kind, Guid targetId, bool linked)
    {
        if (kind == PlannerNoteTargetKind.Task)
        {
            var task = Task(targetId);
            if (task is null || (linked && task.Status == PlannerStatus.Replaced))
                throw new PlannerValidationException("This task is no longer available to link.");
            if (task.LoreIds.Contains(noteId) != linked)
                Tasks[Tasks.IndexOf(task)] = task with
                {
                    LoreIds = linked ? [..task.LoreIds, noteId] : task.LoreIds.Where(id => id != noteId).ToArray(),
                    Revision = task.Revision + 1
                };
            return;
        }

        var link = new PlannerNoteLink(noteId, kind, targetId);
        if (!linked)
        {
            NoteLinks.Remove(link);
            return;
        }
        if (kind switch
            {
                PlannerNoteTargetKind.Project => Project(targetId) is null,
                PlannerNoteTargetKind.Milestone => Milestone(targetId) is null,
                _ => true
            })
            throw new PlannerValidationException("This item is no longer available to link.");
        if (!NoteLinks.Contains(link))
            NoteLinks.Add(link);
    }

    public IEnumerable<ProjectNoteCollection> NoteCollections() => Projects.Select(ProjectNoteCollection.For).Concat(ArchivedNoteCollections);
    public bool HasLegacyNoteCollection() => Projects.Any(project => project.IncludesLegacyNotes)
        || ArchivedNoteCollections.Any(collection => collection.IncludesLegacyNotes);

    public IEnumerable<PlannerTask> OrderedQueue(DateOnly week)
    {
        var remaining = Tasks.Where(task => task.IsOpen && !task.IsInbox).ToDictionary(task => task.Id);
        foreach (var id in QueueOrder)
            if (remaining.Remove(id, out var task))
                yield return task;

        foreach (var task in remaining.Values
            .OrderBy(task => task.Status == PlannerStatus.InProgress ? 0 : task.Week == week ? 1 : task.Week > week ? 3 : 2)
            .ThenBy(task => task.Week == week && task.Priority > 0 ? task.Priority : 4)
            .ThenBy(task => task.Due ?? DateOnly.MaxValue)
            .ThenBy(task => task.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(task => task.Id))
            yield return task;
    }

    public void ReorderQueue(Guid taskId, Guid anchorId, bool after, DateOnly week)
    {
        if (taskId == anchorId)
            return;

        var order = OrderedQueue(week).Select(task => task.Id).ToList();
        if (!order.Remove(taskId) || !order.Contains(anchorId))
            throw new PlannerValidationException("The queue changed in another tab. Reload it before reordering.");

        order.Insert(order.IndexOf(anchorId) + (after ? 1 : 0), taskId);
        QueueOrder.Clear();
        QueueOrder.AddRange(order);
    }

    public void AttachLegacyNotes(DateOnly today)
    {
        if (HasLegacyNoteCollection())
            return;

        var project = Projects.FirstOrDefault(item => item.ParentId is null
            && item.Name.Equals("Demons Arcade", StringComparison.OrdinalIgnoreCase))
            ?? new(Guid.NewGuid(), "Demons Arcade", "", ProgressMeasure.Tasks, "", 0, 0, 0);
        SaveProject(project with { IncludesLegacyNotes = true }, today);
    }

    public IEnumerable<PlannerProject> Children(Guid? parentId) =>
        Projects.Where(project => project.ParentId == parentId).OrderBy(project => project.Name);

    public IEnumerable<PlannerProject> Ancestors(Guid? id)
    {
        while (Project(id) is { } project)
        {
            yield return project;
            id = project.ParentId;
        }
    }

    public IEnumerable<(PlannerProject Project, int Depth)> ProjectTree(Guid? parentId = null, int depth = 0)
    {
        foreach (var project in Children(parentId))
        {
            yield return (project, depth);
            foreach (var descendant in ProjectTree(project.Id, depth + 1))
                yield return descendant;
        }
    }

    public bool IsDashboardHidden(Guid? projectId) => Ancestors(projectId).Any(project => project.HiddenFromDashboard);
    public bool IsProjectArchived(Guid? projectId) => Ancestors(projectId).Any(project => project.Archived);
    public bool IsDashboardHidden(PlannerMilestone milestone) =>
        milestone.HiddenFromDashboard || IsDashboardHidden(milestone.ProjectId);
    public bool IsDashboardHidden(PlannerTask task) => task.HiddenFromDashboard || IsDashboardHidden(task.ProjectId)
        || (Milestone(task.MilestoneId)?.HiddenFromDashboard ?? false);

    public ProjectProgress Progress(PlannerProject project)
    {
        if (project.Measure == ProgressMeasure.Numeric)
            return new(project.Value, project.Target, project.Unit);

        if (project.Measure == ProgressMeasure.Subprojects)
        {
            var children = Children(project.Id).ToArray();
            var percent = children.Length > 0 ? (int)Math.Round(children.Average(child => Progress(child).Percent)) : 0;
            return new(percent, children.Length > 0 ? 100 : 0, "",
                children.Length > 0 ? $"Across {children.Length} {(children.Length == 1 ? "subproject" : "subprojects")}" : "No subprojects yet");
        }

        var scope = ProjectTree(project.Id).Select(item => item.Project.Id).Append(project.Id).ToHashSet();
        if (project.Measure == ProgressMeasure.Milestones)
        {
            var milestones = Milestones.Where(item => scope.Contains(item.ProjectId)).ToArray();
            return new(milestones.Count(item => item.Done), milestones.Length, "milestones");
        }

        var tasks = Tasks.Where(item => item.ProjectId is { } id && scope.Contains(id)
            && item.Status is not (PlannerStatus.Archived or PlannerStatus.Replaced)).ToArray();
        return new(tasks.Count(item => item.Status == PlannerStatus.Done), tasks.Length, "tasks");
    }

    public void SaveProject(PlannerProject draft, DateOnly today)
    {
        var existing = Project(draft.Id);
        CheckRevision(existing?.Revision, draft.Revision);
        var name = Text(draft.Name, 120, "Project name", required: true);
        var notes = Text(draft.Notes, 4000, "Project notes");
        if (!Enum.IsDefined(draft.Measure) || draft.Value < 0 || (draft.Measure == ProgressMeasure.Numeric && draft.Target < 1))
            throw new PlannerValidationException("Choose a progress measure and enter a positive target and non-negative current value.");
        if (draft.ParentId is { } parentId && (Project(parentId) is null
            || Ancestors(parentId).Any(parent => parent.Id == draft.Id)))
            throw new PlannerValidationException("Choose a parent outside this project's own subprojects.");

        var numeric = draft.Measure == ProgressMeasure.Numeric;
        var unit = numeric ? Text(draft.Unit, 32, "Unit", required: true) : "";
        var sameMetric = numeric && existing is { Measure: ProgressMeasure.Numeric }
            && existing.Unit.Equals(unit, StringComparison.OrdinalIgnoreCase);
        var project = draft with
        {
            Name = name,
            Notes = notes,
            Unit = unit,
            NumericMetricId = numeric ? sameMetric ? existing!.NumericMetricId : Guid.NewGuid() : null,
            LegacyNumericUnit = existing?.LegacyNumericUnit
                ?? (existing is { Measure: ProgressMeasure.Numeric, NumericMetricId: null } ? existing.Unit : null),
            Revision = draft.Revision + 1
        };
        if (numeric && (!sameMetric || existing!.Value != project.Value))
            ProgressUpdates.Add(new(project.Id, today, project.Value, project.NumericMetricId));

        if (existing is not null)
            Projects[Projects.IndexOf(existing)] = project;
        else
            Projects.Add(project);
    }

    public void DeleteProject(PlannerProject project)
    {
        var current = Project(project.Id);
        if (current is null)
            return;
        CheckRevision(current.Revision, project.Revision);

        for (var i = 0; i < Projects.Count; i++)
        {
            var child = Projects[i];
            if (child.ParentId == current.Id)
                Projects[i] = child with { ParentId = current.ParentId, Revision = child.Revision + 1 };
        }

        for (var i = 0; i < Tasks.Count; i++)
        {
            var task = Tasks[i];
            if (task.ProjectId == current.Id)
            {
                Tasks[i] = task with
                {
                    ProjectId = null, MilestoneId = null, InboxPending = false, Revision = task.Revision + 1
                };
            }
        }

        var milestoneIds = Milestones.Where(milestone => milestone.ProjectId == current.Id).Select(milestone => milestone.Id).ToHashSet();
        Milestones.RemoveAll(milestone => milestoneIds.Contains(milestone.Id));
        NoteLinks.RemoveAll(link => link.Kind == PlannerNoteTargetKind.Project && link.TargetId == current.Id
            || link.Kind == PlannerNoteTargetKind.Milestone && milestoneIds.Contains(link.TargetId));
        ProgressUpdates.RemoveAll(update => update.ProjectId == current.Id);
        Checkpoints.RemoveAll(checkpoint => checkpoint.ProjectId == current.Id);
        ArchivedNoteCollections.Add(ProjectNoteCollection.For(current) with { IncludeSubprojectNotes = false });
        Projects.Remove(current);
    }

    public void SetProjectArchived(PlannerProject project, bool archived)
    {
        var current = Project(project.Id) ?? throw new PlannerValidationException("This project is no longer available.");
        CheckRevision(current.Revision, project.Revision);
        if (current.Archived != archived)
            Projects[Projects.IndexOf(current)] = current with { Archived = archived, Revision = current.Revision + 1 };
    }

    public void SaveMilestone(PlannerMilestone draft)
    {
        var existing = Milestone(draft.Id);
        CheckRevision(existing?.Revision, draft.Revision);
        if (Project(draft.ProjectId) is null || (existing is not null && existing.ProjectId != draft.ProjectId))
            throw new PlannerValidationException("Choose an existing project for this milestone.");

        var milestone = draft with
        {
            Title = Text(draft.Title, 140, "Milestone title", required: true),
            Revision = draft.Revision + 1
        };
        if (existing is not null)
            Milestones[Milestones.IndexOf(existing)] = milestone;
        else
            Milestones.Add(milestone);
    }

    public void DeleteMilestone(PlannerMilestone milestone)
    {
        var current = Milestone(milestone.Id);
        if (current is null)
            return;
        CheckRevision(current.Revision, milestone.Revision);

        for (var i = 0; i < Tasks.Count; i++)
        {
            var task = Tasks[i];
            if (task.MilestoneId == current.Id)
                Tasks[i] = task with { MilestoneId = null, Revision = task.Revision + 1 };
        }

        Milestones.Remove(current);
        NoteLinks.RemoveAll(link => link.Kind == PlannerNoteTargetKind.Milestone && link.TargetId == current.Id);
    }

    public void SaveTask(PlannerTask draft, DateOnly today)
    {
        var existing = Task(draft.Id);
        CheckRevision(existing?.Revision, draft.Revision);
        if (existing?.Status == PlannerStatus.Replaced || draft.Status == PlannerStatus.Replaced)
            throw new PlannerValidationException("This task was broken down in a weekly review.");
        if (!Enum.IsDefined(draft.Status) || draft.Priority is < 0 or > 3)
            throw new PlannerValidationException("Choose a valid task status and priority.");
        if (draft.ProjectId is { } projectId && Project(projectId) is null)
            throw new PlannerValidationException("Choose an existing project.");
        if (draft.MilestoneId is { } milestoneId
            && (Milestone(milestoneId) is not { } milestone || milestone.ProjectId != draft.ProjectId))
            throw new PlannerValidationException("Choose a milestone from the selected project.");
        if (draft.Week is { } week && week != PlannerCalendar.WeekOf(week))
            throw new PlannerValidationException("Choose a week that starts on Monday.");
        var priority = draft.IsOpen ? draft.Priority : 0;
        if (priority != 0 && draft.Week is null)
            throw new PlannerValidationException("Schedule this task before assigning a priority.");
        if (priority != 0 && Tasks.Any(item => item.IsOpen && item.Id != draft.Id
            && item.Week == draft.Week && item.Priority == priority))
            throw new PlannerValidationException($"Priority {priority} is already assigned for that week.");

        var task = draft with
        {
            Title = Text(draft.Title, 140, "Task title", required: true),
            Notes = Text(draft.Notes, 8000, "Task notes"),
            LoreIds = draft.LoreIds.Distinct().ToArray(),
            Priority = priority,
            InboxPending = draft.IsInbox || draft.Status == PlannerStatus.Archived && draft.InboxPending,
            StatusBeforeArchive = draft.Status == PlannerStatus.Archived
                ? existing?.Status == PlannerStatus.Archived ? existing.StatusBeforeArchive : existing?.Status
                : null,
            PriorityBeforeArchive = draft.Status == PlannerStatus.Archived
                ? existing?.Status == PlannerStatus.Archived ? existing.PriorityBeforeArchive : existing?.Priority ?? 0
                : 0,
            Revision = draft.Revision + 1
        };
        if (existing is not null)
            Tasks[Tasks.IndexOf(existing)] = task;
        else
            Tasks.Add(task);
        if (task.Status == PlannerStatus.Done && existing?.Status != PlannerStatus.Done
            && existing?.StatusBeforeArchive != PlannerStatus.Done)
            Completions.Add(new(task.Id, task.Title, today));
    }

    public void SetTaskArchived(PlannerTask task, bool archived, DateOnly today)
    {
        if (archived == (task.Status == PlannerStatus.Archived))
            return;
        var status = archived ? PlannerStatus.Archived : task.StatusBeforeArchive ?? PlannerStatus.Open;
        var priority = !archived && task.Week is { } week && task.PriorityBeforeArchive > 0
            && !Tasks.Any(item => item.IsOpen && item.Id != task.Id && item.Week == week && item.Priority == task.PriorityBeforeArchive)
            ? task.PriorityBeforeArchive : 0;
        SaveTask(task with { Status = status, Priority = priority }, today);
    }

    public void DeleteTask(PlannerTask task)
    {
        var current = Task(task.Id);
        if (current is null)
            return;
        CheckRevision(current.Revision, task.Revision);

        Tasks.Remove(current);
        QueueOrder.RemoveAll(id => id == current.Id);
        Completions.RemoveAll(completion => completion.TaskId == current.Id);
        Decisions.RemoveAll(decision => decision.TaskId == current.Id);
    }

    public void MoveTask(PlannerTask task, PlannerLane lane, DateOnly today)
    {
        if (!task.IsOpen)
            task = task with { Priority = 0 };
        var week = PlannerCalendar.WeekOf(today);
        var updated = lane switch
        {
            PlannerLane.Backlog => task with { Status = PlannerStatus.Open, Week = null, Priority = 0, InboxPending = false },
            PlannerLane.ThisWeek => task with
            {
                Status = PlannerStatus.Open, Week = week, Priority = task.Week == week ? task.Priority : 0
            },
            PlannerLane.InProgress => task with { Status = PlannerStatus.InProgress, Week = task.Week ?? week },
            PlannerLane.Done => task with { Status = PlannerStatus.Done },
            _ => throw new PlannerValidationException("Choose a board column.")
        };
        SaveTask(updated, today);
    }

    public void SaveCheckpoint(Guid projectId, DateOnly week, int? target)
    {
        if (Project(projectId) is not { Measure: ProgressMeasure.Numeric } project || target < 1
            || week != PlannerCalendar.WeekOf(week))
            throw new PlannerValidationException("Choose a numeric project, a Monday, and a positive checkpoint.");

        Checkpoints.RemoveAll(item => item.ProjectId == projectId && item.Week == week && item.UsesCurrentMetric(project));
        if (target is { } value)
            Checkpoints.Add(new(projectId, week, value, project.Unit, project.NumericMetricId));
    }

    public ProjectProgress? CheckpointProgress(ReadingCheckpoint checkpoint, DateOnly day)
    {
        var project = Project(checkpoint.ProjectId);
        if (project is null)
            return null;

        if (checkpoint.MetricId is null && !checkpoint.UsesCurrentMetric(project)
            && !checkpoint.Unit.Equals(project.LegacyNumericUnit, StringComparison.OrdinalIgnoreCase))
            return null;

        var value = ProgressUpdates.LastOrDefault(item => item.ProjectId == checkpoint.ProjectId
            && item.MetricId == checkpoint.MetricId && item.Day <= day)?.Value ?? 0;
        return new(value, checkpoint.Target, checkpoint.Unit);
    }

    public void Review(PlannerTask task, DateOnly week, ReviewAction action, DateOnly? deferUntil,
        string stepsText, string note, DateOnly today)
    {
        if (!task.IsOpen || task.Week != week || !Enum.IsDefined(action))
            throw new PlannerValidationException("Only unfinished work scheduled for this week can be reviewed.");

        var nextWeek = week.AddDays(7);
        var currentWeek = PlannerCalendar.WeekOf(today);
        if (nextWeek < currentWeek)
            nextWeek = currentWeek;
        var destination = action == ReviewAction.Defer ? deferUntil : nextWeek;
        if (destination is { } date && (date < nextWeek || date != PlannerCalendar.WeekOf(date)))
            throw new PlannerValidationException("Choose a Monday in a later week, or leave the date blank for Backlog.");

        string[] steps = [];
        if (action == ReviewAction.BreakDown)
        {
            steps = stepsText.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(step => Text(step, 140, "Step title", required: true)).ToArray();
            if (steps.Length is < 2 or > 20)
                throw new PlannerValidationException("Enter 2 to 20 smaller tasks, one per line.");
        }
        note = Text(note, 2000, "Review note");

        if (Decisions.FirstOrDefault(item => item.TaskId == task.Id && item.Week == week
            && item.SourceRevision == task.Revision) is { } previous)
        {
            if (previous.Action == action && previous.Destination == destination
                && previous.Note == note && previous.Steps.SequenceEqual(steps))
                return;
            throw new PlannerValidationException("A decision for this version of the task was already saved. Reload the review and try again.");
        }
        var current = Task(task.Id);
        CheckRevision(current?.Revision, task.Revision);

        if (action == ReviewAction.BreakDown)
        {
            foreach (var title in steps)
            {
                SaveTask(PlannerTask.New(task.ProjectId, task.MilestoneId) with
                {
                    Title = title, Week = destination, Due = task.Due, LoreIds = task.LoreIds,
                    HiddenFromDashboard = task.HiddenFromDashboard
                }, today);
            }
            Tasks[Tasks.IndexOf(current!)] = task with { Status = PlannerStatus.Replaced, Priority = 0, Revision = task.Revision + 1 };
        }
        else
        {
            SaveTask(task with { Week = destination, Priority = 0,
                Status = destination is null ? PlannerStatus.Open : task.Status }, today);
        }
        Decisions.Add(new(task.Id, task.Title, week, action, destination, steps, note, task.Revision));
    }

    private static void CheckRevision(int? current, int expected)
    {
        if ((current ?? 0) != expected)
            throw new PlannerValidationException("This item changed in another tab. Reload it before saving.");
    }

    private static string Text(string value, int limit, string label, bool required = false)
    {
        value = value.Trim();
        if (required && value.Length == 0)
            throw new PlannerValidationException($"{label} is required.");
        if (value.Length > limit)
            throw new PlannerValidationException($"{label} must be {limit} characters or fewer.");
        return value;
    }
}

public static class PlannerCalendar
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zone).DateTime);
    public static DateOnly WeekOf(DateOnly day) => day.AddDays(-((int)day.DayOfWeek + 6) % 7);
    public static string Range(DateOnly start) => $"{start:MMM d} – {start.AddDays(6):MMM d, yyyy}";

    public static string Label(this PlannerLane lane) => lane switch
    {
        PlannerLane.ThisWeek => "This week",
        PlannerLane.InProgress => "In progress",
        _ => lane.ToString()
    };

    public static string Label(this PlannerStatus status) => status switch
    {
        PlannerStatus.Open => "Open",
        PlannerStatus.InProgress => "In progress",
        PlannerStatus.Replaced => "Broken down",
        _ => status.ToString()
    };

    public static string Label(this ReviewAction action) => action == ReviewAction.BreakDown ? "Break down" : action.ToString();
}
