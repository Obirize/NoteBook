using System.Globalization;
using System.Text.Json.Serialization;

namespace Notlar;

public sealed class Note
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
    public bool Pinned { get; set; }
    public bool Deleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public long Revision { get; set; } = 1;
    // Photos and videos; the encrypted files live beside the vault, their keys only here.
    public List<Attachment> Attachments { get; set; } = [];
    [JsonIgnore] public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? L10n.T("NewNote") : Title;
    [JsonIgnore] public string Preview => !string.IsNullOrWhiteSpace(Text) ? Text.Replace('\r', ' ').Replace('\n', ' ').Trim() : Attachments.Count > 0 ? AttachmentLabel : L10n.T("StartWriting");
    [JsonIgnore] public string AttachmentLabel => Attachments.Count == 0 ? "" : L10n.Count("AttachmentCountOne", "AttachmentCountMany", Attachments.Count);
    [JsonIgnore] public bool HasAttachments => Attachments.Count > 0;
    [JsonIgnore] public string DateLabel => Updated.LocalDateTime.Date == DateTime.Today ? Updated.LocalDateTime.ToString("t", L10n.Culture) : Updated.LocalDateTime.ToString("d MMM", L10n.Culture);
    [JsonIgnore] public string TrashLabel
    {
        get
        {
            if (!Deleted || DeletedAt == null) return "";
            int days = (int)Math.Ceiling((DeletedAt.Value.AddDays(TrashPolicy.RetentionDays) - DateTimeOffset.UtcNow).TotalDays);
            return days <= 0 ? L10n.T("TrashLabelToday") : L10n.T("TrashLabelDays", days);
        }
    }
}

public sealed class Attachment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string MediaType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public byte[] Key { get; set; } = [];
    public byte[] Sha256 { get; set; } = [];
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTimeOffset Added { get; set; } = DateTimeOffset.UtcNow;
    [JsonIgnore] public bool IsImage => MediaType.StartsWith("image/", StringComparison.Ordinal);
    [JsonIgnore] public bool IsVideo => MediaType.StartsWith("video/", StringComparison.Ordinal);
    [JsonIgnore] public string SizeLabel => Size < 1024 * 1024 ? Math.Max(1, Size / 1024) + " KB" : Size < 1024L * 1024 * 1024 ? (Size / (1024.0 * 1024)).ToString("0.#", L10n.Culture) + " MB" : (Size / (1024.0 * 1024 * 1024)).ToString("0.##", L10n.Culture) + " GB";
}

// Remembers a permanent deletion so a phone that still has the note removes it too instead of bringing it back.
public sealed class PurgeRecord
{
    public string Id { get; set; } = "";
    public long Revision { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public static class TrashPolicy
{
    public const int RetentionDays = 30, PurgeMemoryDays = 180;
    public static void Delete(IEnumerable<Note> notes, DateTimeOffset now)
    { foreach (var n in notes) { n.Deleted = true; n.DeletedAt = now; n.Updated = now; n.Revision++; } }
    public static void Restore(IEnumerable<Note> notes, DateTimeOffset now)
    { foreach (var n in notes) { n.Deleted = false; n.DeletedAt = null; n.Updated = now; n.Revision++; } }
    public static bool InitializeDates(Notebook book, DateTimeOffset now)
    {
        bool changed = false;
        // Old versions did not record deletion time; give those notes a full grace period.
        foreach (var n in book.Notes.Where(n => n.Deleted && n.DeletedAt == null)) { n.DeletedAt = now; changed = true; }
        return changed;
    }
    public static List<Note> Expired(Notebook book, DateTimeOffset now) => book.Notes.Where(n => n.Deleted && n.DeletedAt.HasValue && n.DeletedAt.Value <= now.AddDays(-RetentionDays)).ToList();
    public static int Purge(Notebook book, IEnumerable<Note> notes)
    {
        var ids = notes.Where(n => n.Deleted).Select(n => n.Id).ToHashSet();
        var now = DateTimeOffset.UtcNow;
        foreach (var n in book.Notes.Where(n => n.Deleted && ids.Contains(n.Id)))
        {
            book.Purged.RemoveAll(p => p.Id == n.Id);
            book.Purged.Add(new PurgeRecord { Id = n.Id, Revision = n.Revision, At = now });
        }
        book.Purged.RemoveAll(p => p.At < now.AddDays(-PurgeMemoryDays));
        int count = book.Notes.RemoveAll(n => n.Deleted && ids.Contains(n.Id));
        // Do not retain deleted contents in the imported historical source files.
        foreach (string name in book.LegacyArchive.Keys.ToList())
        {
            try
            {
                var root = System.Text.Json.Nodes.JsonNode.Parse(book.LegacyArchive[name]);
                if (root?["Notes"] is not System.Text.Json.Nodes.JsonArray array) { book.LegacyArchive.Remove(name); continue; }
                foreach (var item in array.ToList()) if (item?["Id"]?.GetValue<string>() is string id && ids.Contains(id)) array.Remove(item);
                book.LegacyArchive[name] = root.ToJsonString();
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { book.LegacyArchive.Remove(name); }
        }
        return count;
    }
}

public sealed class Notebook
{
    // 2 added attachments; older builds refuse the file instead of silently dropping their keys.
    public const int CurrentSchema = 2;
    public int SchemaVersion { get; set; } = CurrentSchema;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public List<Note> Notes { get; set; } = [];
    public List<PurgeRecord> Purged { get; set; } = [];
    // Preserve the complete original files (including rich text) inside encryption.
    public Dictionary<string, string> LegacyArchive { get; set; } = [];
}

public static class NoteQuery
{
    public static List<Note> Find(Notebook book, string query, bool trash) => book.Notes
        .Where(n => n.Deleted == trash && (string.IsNullOrWhiteSpace(query) ||
            L10n.Culture.CompareInfo.IndexOf(n.Title + "\n" + n.Text + "\n" + string.Join("\n", n.Attachments.Select(a => a.Name)), query.Trim(), CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0))
        .OrderByDescending(n => n.Pinned).ThenByDescending(n => n.Updated).ToList();
}
