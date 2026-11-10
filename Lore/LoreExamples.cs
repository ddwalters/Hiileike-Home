namespace HiileikeHome.Lore;

internal static class LoreExamples
{
    public static LoreEntry[] Create()
    {
        var harbor = LoreEntry.New(EntryKind.Location) with
        {
            Title = "Brinewatch Harbor", Summary = "A salt-worn port built around a lighthouse that has gone dark.",
            Notes = "## First impression\nThe harbor smells of **brine and lamp oil**. Boats refuse to cross the outer rocks after sunset.\n\n## Points of interest\n- The old lighthouse\n- Mara’s chart room\n- The bell buoy",
            Tags = ["Coast", "Act I"], Pinned = true, Status = EntryStatus.Planned, Revision = 1
        };
        var mara = LoreEntry.New(EntryKind.Character) with
        {
            Title = "Mara Voss", Summary = "The harbor’s tidekeeper knows what happened to the light.",
            Notes = "## Motivation\nKeep the fishing families safe, even if the guild decides to blame her.\n\n**Voice:** practical, guarded, and impatient with superstition.",
            Tags = ["Coast", "NPC"], Status = EntryStatus.Idea, Revision = 1
        };
        var compass = LoreEntry.New(EntryKind.Item) with
        {
            Title = "Tideglass Compass", Summary = "Points to the nearest safe passage while the tide is rising.",
            Notes = "## Behavior\nThe needle floats in seawater. It stops moving at slack tide.",
            Implementation = "## Prototype\n- Store the current tide phase in the world clock.\n- Resolve the nearest marked passage when equipped.",
            Tags = ["Exploration", "Reward"], Status = EntryStatus.Implemented, Revision = 1
        };
        var quest = LoreEntry.New(EntryKind.Quest) with
        {
            Title = "The Lantern Below", Summary = "Find a way through the sea caves and relight Brinewatch’s beacon.",
            Notes = "## Objectives\n1. Speak with Mara on the dock.\n2. Follow the tideglass needle into the caves.\n3. Return the lantern to the lighthouse.",
            Implementation = "## In progress\n- The cave entrance opens at low tide.\n- Add a fallback hint when the player misses the window.",
            Tags = ["Coast", "Act I"], Status = EntryStatus.InProgress, Revision = 1
        };
        return [
            harbor with { Links = [mara.Id, quest.Id] },
            mara with { Links = [harbor.Id, quest.Id] },
            compass with { Links = [quest.Id] },
            quest with { Links = [harbor.Id, mara.Id, compass.Id] }
        ];
    }
}
