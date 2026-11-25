using System.Text.Json.Serialization;

namespace HiileikeHome.Lore;

public enum EntryKind { Item, Character, Quest, Location, Environment, Ambiance, Misc }
public enum EntryStatus { Idea, Planned, InProgress, Implemented }

public sealed record Picture(Guid Id, string Name, string ContentType);
public sealed record PictureUpload(Picture Picture, byte[] Bytes);
public sealed record LoreLink(Guid Id, EntryKind? Kind, string Title, Guid? CategoryId = null, string? CategoryName = null)
{
    [JsonIgnore] public string CategoryLabel => CategoryName ?? NoteCatalog.LegacyCategory(Kind)?.Name ?? "Uncategorized";
    [JsonIgnore] public string IconName => NoteCatalog.LegacyCategory(Kind)?.IconName ?? "book";
}
public sealed record LoreRevision(LoreEntry Entry, LoreLink[] Links);
public sealed record LoreDraft(Guid Stamp, LoreEntry Entry, string Tags, Guid? WorkspaceProject = null);

public sealed record LoreEntry(
    Guid Id, EntryKind? Kind, EntryStatus Status, string Title, string Summary,
    string Notes, string Implementation, string[] Tags, bool Pinned,
    Guid[] Links, Picture[] Pictures, DateTimeOffset Updated, int Revision,
    Guid? ProjectId = null, Guid? CategoryId = null, string? CategoryName = null,
    Dictionary<Guid, NoteCategorySelection>? ProjectCategories = null)
{
    [JsonIgnore] public Guid CatalogId => ProjectId ?? NoteCatalog.LegacyId;
    [JsonIgnore] public Guid? WorkspaceProjectId { get; init; }
    [JsonIgnore] public Guid? EffectiveCategoryId => CategoryId ?? NoteCatalog.LegacyCategory(Kind)?.Id;
    [JsonIgnore] public string CategoryLabel => CategoryName ?? NoteCatalog.LegacyCategory(Kind)?.Name ?? "Uncategorized";
    [JsonIgnore] public string IconName => NoteCatalog.LegacyCategory(Kind)?.IconName ?? "book";

    public static LoreEntry New(Guid projectId, Guid? categoryId = null) => new(
        Guid.NewGuid(), null, EntryStatus.Idea, "", "", "", "", [], false, [], [], DateTimeOffset.UtcNow, 0,
        projectId, categoryId);

    public LoreLink LinkSnapshot() => new(Id, Kind, Title, EffectiveCategoryId, CategoryLabel);
}

public sealed record NoteCategory(Guid Id, string Name, EntryKind? LegacyKind = null)
{
    [JsonIgnore] public string IconName => LegacyKind?.ToString() ?? "book";
}

public sealed record NoteCategorySelection(Guid? Id, string? Name, Guid? ProjectId = null);

public sealed record NoteScope(IReadOnlySet<Guid> Catalogs, Guid? SharedNotesProject = null)
{
    public Guid ProjectFor(Guid catalogId) =>
        catalogId == NoteCatalog.LegacyId && SharedNotesProject is { } projectId ? projectId : catalogId;

    public Guid ProjectFor(LoreEntry entry) => entry.WorkspaceProjectId ?? ProjectFor(entry.CatalogId);
}

public static class NoteCatalog
{
    public static readonly Guid LegacyId = new("45d7d424-dc10-49e9-8cae-ce9e8bcdc500");
    public static IReadOnlyList<NoteCategory> LegacyCategories { get; } =
    [
        new(new("45d7d424-dc10-49e9-8cae-ce9e8bcdc501"), "Items", EntryKind.Item),
        new(new("45d7d424-dc10-49e9-8cae-ce9e8bcdc502"), "Characters", EntryKind.Character),
        new(new("45d7d424-dc10-49e9-8cae-ce9e8bcdc503"), "Quests", EntryKind.Quest),
        new(new("45d7d424-dc10-49e9-8cae-ce9e8bcdc504"), "World locations", EntryKind.Location),
        new(new("45d7d424-dc10-49e9-8cae-ce9e8bcdc505"), "Environment", EntryKind.Environment),
        new(new("45d7d424-dc10-49e9-8cae-ce9e8bcdc506"), "Ambiance", EntryKind.Ambiance),
        new(new("45d7d424-dc10-49e9-8cae-ce9e8bcdc507"), "Misc", EntryKind.Misc)
    ];

    public static NoteCategory? LegacyCategory(EntryKind? kind) =>
        kind is null ? null : LegacyCategories.FirstOrDefault(category => category.LegacyKind == kind);
}

public static class LoreLabels
{
    public static string Label(this EntryStatus status) => status == EntryStatus.InProgress ? "In progress" : status.ToString();
}
