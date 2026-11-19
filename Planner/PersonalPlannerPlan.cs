namespace HiileikeHome.Planner;

internal static class PersonalPlannerPlan
{
    public static void Add(PlannerWorkspace workspace, DateOnly today)
    {
        if (workspace.PersonalPlanAdded)
            return;

        var week = PlannerCalendar.WeekOf(today);
        var game = Project("Demons Arcade", "Develop the game through art direction, story, and a playable introduction.",
            ProgressMeasure.Milestones);
        var art = Project("Character & Art Direction", "Define a first visual direction by taking one character from concept to a Blender model.",
            ProgressMeasure.Milestones, game);
        var concept = Milestone(art, "First character concept");
        Start(art, concept, 1, "Pick one character concept to take into Blender",
            "Done when you have chosen one direction and saved a reference or sketch in Project Notes.");
        AddTasks(art, concept,
            ("Record the character's visual direction",
                "Done when a note captures the references and the decisions you want to carry into the model."),
            ("Prepare a modeling reference",
                "Done when you have a reference that makes the character's major shapes and proportions clear enough to model."));
        AddTasks(art, Milestone(art, "Blender base character"),
            ("Block out the character in Blender",
                "Done when the main shapes and silhouette are visible from front and side views."),
            ("Build a first usable base mesh",
                "Done when a saved Blender model has the parts needed to show the design and stand up to a close look."),
            ("Review the character model",
                "Done when you have saved a neutral preview and picked the most important revision, if one is needed."));

        var story = Project("Story & Intro Demo", "Develop the story toward a short playable introduction.",
            ProgressMeasure.Milestones, game, hidden: true);
        AddTasks(story, Milestone(story, "Intro story outline"),
            ("Decide what the player should understand by the end of the intro",
                "Done when Project Notes contains a short statement of the intro's purpose."),
            ("Outline the opening beats",
                "Done when the order of events and the player's role in each one are recorded."),
            ("Write the first intro scene draft",
                "Done when the story outline is detailed enough to block out in the game."));
        AddTasks(story, Milestone(story, "Playable intro blockout"),
            ("Block out the intro scene",
                "Done when you can play the planned sequence from a fresh start to its intended end."),
            ("Review one full playthrough",
                "Done when you have recorded the biggest gap between the playable intro and what you wanted it to communicate."));

        var customization = Project("Character Customization", "Explore a first small customization option after the base character is ready.",
            ProgressMeasure.Milestones, game, hidden: true);
        AddTasks(customization, Milestone(customization, "First customization prototype"),
            ("Choose one customization option to prototype",
                "Done when you have picked the variation you want to support and recorded what counts as a working prototype."),
            ("Prepare two test variations",
                "Done when both variations are ready to try on the same base character."),
            ("Demonstrate switching between the variations",
                "Done when you can switch both ways in a test scene and have recorded what to improve next."));

        var graphics = Project("Graphics Programming · First Skin Shader",
            "Build one skin shader you can demonstrate and explain. Work through one numbered task at a time. After finishing it, move the next numbered task from Backlog to This Week. Use a stand-in head so this can proceed independently of the game model.",
            ProgressMeasure.Tasks);
        var setup = Milestone(graphics, "First custom shader");
        Start(graphics, setup, 2, "01 · Create a shader test scene",
            "Done when the scene opens with a simple object ready to try materials on.");
        AddTasks(graphics, setup,
            ("02 · Get your first shader on screen", "Done when your object displays a solid color from a shader you made."),
            ("03 · Make the color editable", "Done when a material setting visibly changes the color and you can explain how the setting relates to what you see."),
            ("04 · Display a texture", "Done when a test image appears on the object through your shader."));
        AddTasks(graphics, Milestone(graphics, "Basic lighting and comparison"),
            ("05 · Make the surface respond to one light", "Done when moving the light changes which side is bright and you can explain why."),
            ("06 · Add an adjustable highlight", "Done when you can make the highlight tighter or broader and see it change as you move the camera."),
            ("07 · Save a comparison scene", "Done when you can view a stand-in head with your basic lit shader under consistent lighting."),
            ("08 · Compare two skin-rendering approaches", "Done when you have noted what each approach changes visually, picked one to try, and described what success would look like."));
        AddTasks(graphics, Milestone(graphics, "Finished skin shader demo"),
            ("09 · Get one skin effect working", "Done when you can compare the same head with the effect on and off and point to the difference you were aiming for."),
            ("10 · Make the skin effect adjustable", "Done when you can dial the effect from subtle to strong using a material control."),
            ("11 · Test under front, side, and back lighting", "Done when you have saved a comparison for each and know where the effect holds up and where it breaks down."),
            ("12 · Fix the biggest visible issue", "Done when a before-and-after comparison shows an improvement under the lighting that exposed it."),
            ("13 · Save a finished demo", "Done when you can reopen the scene and show the baseline and final shader side by side, with a short note on what you learned and one thing to improve."));

        var reading = Project("Reading", "Finish English Fairy Tales, then start Emily Wilde’s.", ProgressMeasure.Subprojects);
        var fairyTales = Project("English Fairy Tales", "Finish the book. The last recorded position was page 38.",
            ProgressMeasure.Numeric, reading, unit: "pages", target: 138, value: 38);
        if (fairyTales.Measure == ProgressMeasure.Numeric && Same(fairyTales.Unit, "pages")
            && fairyTales.Value < fairyTales.Target && !workspace.Checkpoints.Any(checkpoint =>
                checkpoint.ProjectId == fairyTales.Id && checkpoint.Week == week && checkpoint.UsesCurrentMetric(fairyTales)))
        {
            var checkpoint = (int)Math.Min((long)fairyTales.Value + 30, fairyTales.Target);
            workspace.SaveCheckpoint(fairyTales.Id, week, checkpoint);
        }

        var emily = Project("Emily Wilde’s", "Start this after finishing English Fairy Tales. Use the page count for your own edition.",
            ProgressMeasure.Tasks, reading, hidden: true);
        AddTasks(emily, Milestone(emily, "Start the next book"),
            ("Record the edition and total pages", "Done when the book's total pages are set as its numeric target."),
            ("Choose the first reading checkpoint", "Done when you have set the first week's page goal."));
        AddTasks(emily, Milestone(emily, "Finish the next book"),
            ("Finish Emily Wilde’s", "Done when your reading progress reaches the last page."));

        workspace.PersonalPlanAdded = true;

        PlannerProject Project(string name, string notes, ProgressMeasure measure, PlannerProject? parent = null,
            bool hidden = false, string unit = "", int target = 0, int value = 0)
        {
            var existing = workspace.Projects.FirstOrDefault(item => item.ParentId == parent?.Id && Same(item.Name, name));
            if (existing is not null)
                return existing;

            var draft = new PlannerProject(Guid.NewGuid(), name, notes, measure, unit, target, value, 0, parent?.Id, hidden);
            workspace.SaveProject(draft, today);
            return workspace.Project(draft.Id)!;
        }

        PlannerMilestone Milestone(PlannerProject project, string title)
        {
            var existing = workspace.Milestones.FirstOrDefault(item => item.ProjectId == project.Id && Same(item.Title, title));
            if (existing is not null)
                return existing;

            var draft = new PlannerMilestone(Guid.NewGuid(), project.Id, title, null, false, 0);
            workspace.SaveMilestone(draft);
            return draft;
        }

        void Start(PlannerProject project, PlannerMilestone milestone, int preferredRank, string title, string notes)
        {
            var rank = workspace.Tasks.Any(item => item.IsOpen && item.Week == week && item.Priority == preferredRank)
                ? 0 : preferredRank;
            AddTask(project, milestone, title, notes, week, rank);
        }

        void AddTasks(PlannerProject project, PlannerMilestone milestone, params (string Title, string Notes)[] tasks)
        {
            foreach (var (title, notes) in tasks)
                AddTask(project, milestone, title, notes);
        }

        void AddTask(PlannerProject project, PlannerMilestone milestone, string title, string notes,
            DateOnly? plannedWeek = null, int rank = 0)
        {
            if (workspace.Tasks.Any(item => item.ProjectId == project.Id && Same(item.Title, title)))
                return;
            var draft = PlannerTask.New(project.Id, milestone.Id) with
            {
                Title = title, Notes = notes, Week = plannedWeek, Priority = rank
            };
            workspace.SaveTask(draft, today);
        }
    }

    private static bool Same(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
}
