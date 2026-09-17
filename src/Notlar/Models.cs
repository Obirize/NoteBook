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
    [JsonIgnore] public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "Yeni not" : Title;
    [JsonIgnore] public string Preview => string.IsNullOrWhiteSpace(Text) ? "Yazmaya başlayın…" : Text.Replace('\r', ' ').Replace('\n', ' ').Trim();
    [JsonIgnore] public string DateLabel => Updated.LocalDateTime.Date == DateTime.Today ? Updated.LocalDateTime.ToString("HH:mm") : Updated.LocalDateTime.ToString("d MMM", CultureInfo.GetCultureInfo("tr-TR"));
    [JsonIgnore] public string TrashLabel
    {
        get
        {
            if (!Deleted || DeletedAt == null) return "";
            int days = (int)Math.Ceiling((DeletedAt.Value.AddDays(TrashPolicy.RetentionDays) - DateTimeOffset.UtcNow).TotalDays);
            return days <= 0 ? "Bugün kalıcı silinecek" : days + " gün sonra kalıcı silinecek";
        }
    }
}

public static class TrashPolicy
{
    public const int RetentionDays = 30;
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
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public List<Note> Notes { get; set; } = [];
    // Preserve the complete original files (including rich text) inside encryption.
    public Dictionary<string, string> LegacyArchive { get; set; } = [];
}

public static class NoteQuery
{
    public static List<Note> Find(Notebook book, string query, bool trash) => book.Notes
        .Where(n => n.Deleted == trash && (string.IsNullOrWhiteSpace(query) ||
            CultureInfo.GetCultureInfo("tr-TR").CompareInfo.IndexOf(n.Title + "\n" + n.Text, query.Trim(), CompareOptions.IgnoreCase) >= 0))
        .OrderByDescending(n => n.Pinned).ThenByDescending(n => n.Updated).ToList();
}
