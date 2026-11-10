namespace HiileikeHome.Lore;

public enum EntryKind { Item, Character, Quest, Location }
public enum EntryStatus { Idea, Planned, InProgress, Implemented }

public sealed record Picture(Guid Id, string Name, string ContentType);
public sealed record PictureUpload(Picture Picture, byte[] Bytes);
public sealed record LoreLink(Guid Id, EntryKind Kind, string Title);
public sealed record LoreRevision(LoreEntry Entry, LoreLink[] Links);
public sealed record LoreDraft(Guid Stamp, LoreEntry Entry, string Tags);

public sealed record LoreEntry(
    Guid Id, EntryKind Kind, EntryStatus Status, string Title, string Summary,
    string Notes, string Implementation, string[] Tags, bool Pinned,
    Guid[] Links, Picture[] Pictures, DateTimeOffset Updated, int Revision)
{
    public static LoreEntry New(EntryKind kind) => new(
        Guid.NewGuid(), kind, EntryStatus.Idea, "", "", "", "", [], false, [], [], DateTimeOffset.UtcNow, 0);
}

public static class LoreLabels
{
    public static string Plural(this EntryKind kind) => kind == EntryKind.Location ? "World locations" : $"{kind}s";
    public static string Label(this EntryStatus status) => status == EntryStatus.InProgress ? "In progress" : status.ToString();
}
