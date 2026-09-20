using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Notlar;

// Languages, the vault format, trash and archive rules, text files and everything else that needs no window.
static partial class Program
{
    static (VaultSession Session, string Path, Notebook Book) CoreChecks(string root)
    {
    // UI checks and README screenshots run in English regardless of the machine's display language.
    L10n.Use("en");
    SyncChecks.Run(root, Check);
    var english = L10n.Load("en");
    foreach (var language in L10n.Languages)
    {
        var table = L10n.Load(language.Code);
        var missing = english.Keys.Where(k => !table.ContainsKey(k)).ToList();
        var extra = table.Keys.Where(k => !english.ContainsKey(k)).ToList();
        var placeholders = english.Keys.Where(k => table.ContainsKey(k) && !k.EndsWith("One") && System.Text.RegularExpressions.Regex.Matches(english[k], @"\{\d\}").Count != System.Text.RegularExpressions.Regex.Matches(table[k], @"\{\d\}").Count).ToList();
        Check(missing.Count == 0 && extra.Count == 0 && placeholders.Count == 0 && table.Values.All(v => !string.IsNullOrWhiteSpace(v)) && !string.IsNullOrWhiteSpace(language.NativeName) && CultureInfo.GetCultureInfo(language.Culture) != null,
            "Language " + language.Code + " has every key with matching placeholders" + (missing.Count > 0 ? " (missing: " + string.Join(", ", missing) + ")" : "") + (placeholders.Count > 0 ? " (placeholders: " + string.Join(", ", placeholders) + ")" : ""));
    }
    Check(L10n.Languages.Length >= 12 && L10n.Languages.Select(l => l.Code).Distinct().Count() == L10n.Languages.Length, "At least twelve distinct languages are available");
    L10n.Use("tr"); Check(L10n.T("MyNotes") == "Notlarım" && L10n.Count("SelectedCountOne", "SelectedCountMany", 3) == "3 not seçildi" && L10n.Culture.Name == "tr-TR", "Switching language changes strings and culture");
    L10n.Use("ar"); Check(L10n.Current.RightToLeft && L10n.T("MyNotes") != "My notes", "Arabic is flagged right-to-left");
    L10n.Use("xx"); Check(L10n.Current.Code == "en", "Unknown language code falls back to English");
    L10n.Use("en"); Check(L10n.T("NoSuchKey") == "NoSuchKey" && L10n.T("UpdateReady", "2.0.0") == "Version 2.0.0 ready · Update", "Missing keys degrade to the key name; format arguments apply");
    string settingsDir = Path.Combine(root, "settings"); Directory.CreateDirectory(settingsDir);
    Check(L10n.Detect(settingsDir) == (L10n.Languages.Any(l => l.Code == CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : "en"), "Without a saved choice the Windows display language is used");
    L10n.Save(settingsDir, "ja"); Check(L10n.Detect(settingsDir) == "ja", "A saved language choice is honored on the next start");
    File.WriteAllText(Path.Combine(settingsDir, L10n.SettingsFile), "{broken"); Check(L10n.Detect(settingsDir) != null, "A corrupt settings file does not prevent startup");
    string path = Path.Combine(root, "notes.vault");
    var book = new Notebook { Notes = [new Note { Title = "Gizli başlık İstanbul", Text = "PRIVATE-CONTENT-98765", Pinned = true }] };
    var session = VaultSession.Create(path, Password, book);
    Check(!File.Exists(path), "Recovery acknowledgment precedes persistence");
    session.Save(); session.VerifySaved();
    string first = File.ReadAllText(path);
    Check(!first.Contains("Gizli") && !first.Contains("PRIVATE-CONTENT") && !first.Contains(Password), "No plaintext title, body, password or recovery secret on disk");
    using (var reopened = VaultSession.Open(path, Password)) Check(reopened.Book.Notes[0].Text == book.Notes[0].Text, "Encrypted round-trip");
    Reject(() => VaultSession.Open(path, "incorrect password"), "Wrong password rejected");
    session.Save(); Check(first != File.ReadAllText(path), "Every save uses a fresh nonce");
    Check(File.Exists(path + ".bak") && !File.ReadAllText(path + ".bak").Contains("PRIVATE-CONTENT"), "Backup is encrypted");
    var valid = File.ReadAllBytes(path);
    var envelope = JsonSerializer.Deserialize<VaultEnvelope>(valid)!; envelope.Content.Ciphertext[0] ^= 1;
    File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(envelope));
    Reject(() => VaultSession.Open(path, Password), "Ciphertext tampering rejected");
    envelope = JsonSerializer.Deserialize<VaultEnvelope>(valid)!; envelope.Content.Tag[0] ^= 1; File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(envelope));
    Reject(() => VaultSession.Open(path, Password), "Authentication tag tampering rejected");
    envelope = JsonSerializer.Deserialize<VaultEnvelope>(valid)!; envelope.Id = new string('a', 32); File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(envelope));
    Reject(() => VaultSession.Open(path, Password), "Envelope substitution rejected");
    using (var backup = VaultSession.Open(path + ".bak", Password)) Check(backup.Book.Notes.Count == 1, "Prior backup survives corrupt primary");
    File.WriteAllBytes(path, valid);
    var older = new Notebook { Id = "book-id", Notes = [new Note { Id = "n1", Title = "Eski", Text = "t", Created = DateTimeOffset.UnixEpoch, Updated = DateTimeOffset.UnixEpoch, Pinned = true }] };
    string olderJson = "{\"SchemaVersion\":1,\"Id\":\"book-id\",\"Notes\":[{\"Id\":\"n1\",\"Title\":\"Eski\",\"Text\":\"t\",\"Created\":\"1970-01-01T00:00:00+00:00\",\"Updated\":\"1970-01-01T00:00:00+00:00\",\"Pinned\":true,\"Deleted\":false,\"Revision\":1}],\"LegacyArchive\":{}}";
    Check(VaultSession.SameNotebook(Encoding.UTF8.GetBytes(olderJson), older), "Vault written before the DeletedAt field still verifies after upgrade");
    older.Notes[0].Title = "Değişti";
    Check(!VaultSession.SameNotebook(Encoding.UTF8.GetBytes(olderJson), older) && !VaultSession.SameNotebook(Encoding.UTF8.GetBytes("not json"), older), "Verification still detects a real content mismatch or garbage");
    string releaseJson = "{\"tag_name\":\"v9.4.1\",\"html_url\":\"https://github.com/x/notlar/releases/tag/v9.4.1\",\"assets\":[{\"name\":\"NoteBook-Setup-9.4.1.exe\",\"browser_download_url\":\"https://github.com/x/notlar/releases/download/v9.4.1/NoteBook-Setup-9.4.1.exe\"},{\"name\":\"NoteBook-Setup-9.4.1.exe.sha256\",\"browser_download_url\":\"https://github.com/x/notlar/releases/download/v9.4.1/NoteBook-Setup-9.4.1.exe.sha256\"}]}";
    var parsedRelease = Updater.Parse(releaseJson);
    Check(parsedRelease != null && parsedRelease.Version == new Version(9, 4, 1) && parsedRelease.InstallerUrl.EndsWith("9.4.1.exe") && parsedRelease.ChecksumUrl != null && parsedRelease.Version > Updater.Current, "GitHub release JSON yields installer, checksum and a comparable version");
    Check(Updater.Parse("{\"tag_name\":\"v9.4.1\",\"assets\":[{\"name\":\"NoteBook-Setup.exe\",\"browser_download_url\":\"http://evil/x.exe\"}]}") == null && Updater.Parse("{\"message\":\"Not Found\"}") == null, "Non-HTTPS installer links and error responses are ignored");
    string checksumFile = Path.Combine(root, "sum.bin"); File.WriteAllBytes(checksumFile, [1, 2, 3]);
    Check(Updater.VerifyChecksum(checksumFile, Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })).ToLowerInvariant()) && !Updater.VerifyChecksum(checksumFile, new string('0', 64)), "Downloaded installer is verified against its SHA-256");
    Check(Updater.Enabled(root), "Update check is on for the published repository");
    File.WriteAllText(Path.Combine(root, "guncelleme-kapali"), "");
    Check(!Updater.Enabled(root), "An opt-out marker file disables the update check");
    var now = DateTimeOffset.UtcNow;
    var stale = new Notebook { Notes = [new Note { Id = "old-id", Title = "Eski", Deleted = true, DeletedAt = now.AddDays(-31) }, new Note { Title = "Yeni silinen", Deleted = true, DeletedAt = now.AddDays(-29) }, new Note { Title = "Tarihsiz", Deleted = true }, new Note { Title = "Canlı" }] };
    Check(TrashPolicy.InitializeDates(stale, now) && stale.Notes[2].DeletedAt == now && !TrashPolicy.InitializeDates(stale, now), "Legacy deletions without a timestamp start their 30-day period now");
    Check(TrashPolicy.Expired(stale, now).Single().Title == "Eski" && TrashPolicy.Expired(stale, now.AddDays(2)).Count == 2, "Only deletions older than 30 days expire");
    Check(TrashPolicy.Purge(stale, TrashPolicy.Expired(stale, now)) == 1 && stale.Notes.Count == 3, "Purge removes the expired note");
    Check(TrashPolicy.Purge(stale, [stale.Notes.First(n => n.Title == "Canlı")]) == 0 && stale.Notes.Count == 3, "Purge never removes a live note");
    TrashPolicy.Delete([stale.Notes[2]], now); Check(stale.Notes[2].Deleted && stale.Notes[2].DeletedAt == now && stale.Notes[2].TrashLabel == L10n.T("TrashLabelDays", 30), "Delete records the deletion time and shows remaining days");
    TrashPolicy.Restore([stale.Notes[2]], now); Check(!stale.Notes[2].Deleted && stale.Notes[2].DeletedAt == null && stale.Notes[2].TrashLabel == "", "Restore clears the deletion time");
    Check(NoteQuery.Find(book, "istanbul", Folder.Notes).Count == 1 && NoteQuery.Find(book, "ISTANBUL", Folder.Notes).Count == 1, "Search ignores case and dotted/undotted i in any UI language");
    L10n.Use("tr"); Check(NoteQuery.Find(book, "istanbul", Folder.Notes).Count == 1 && NoteQuery.Find(book, "ıstanbul", Folder.Notes).Count == 0, "Turkish culture keeps dotted and dotless i distinct"); L10n.Use("en");
    book.Notes[0].Deleted = true;
    Check(NoteQuery.Find(book, "", Folder.Notes).Count == 0 && NoteQuery.Find(book, "", Folder.Trash).Count == 1, "Deleted tombstone filtered from live notes");
    book.Notes[0].Deleted = false;
    // Archive: a third folder beside the main list and the trash; restoring from the trash always lands in the main list.
    TrashPolicy.Archive([book.Notes[0]], true, now);
    Check(book.Notes[0].Archived && NoteQuery.Find(book, "", Folder.Notes).Count == 0 && NoteQuery.Find(book, "", Folder.Archive).Count == 1 && NoteQuery.Find(book, "", Folder.Trash).Count == 0, "Archived notes leave the main list for the archive");
    TrashPolicy.Delete([book.Notes[0]], now);
    Check(NoteQuery.Find(book, "", Folder.Archive).Count == 0 && NoteQuery.Find(book, "", Folder.Trash).Count == 1, "A deleted archived note shows only in the trash");
    TrashPolicy.Restore([book.Notes[0]], now);
    Check(!book.Notes[0].Archived && NoteQuery.Find(book, "", Folder.Notes).Count == 1, "Restoring from the trash brings the note back to the main list");
    var archivedJson = new Note { Title = "a", Archived = true };
    Check(System.Text.Json.JsonSerializer.Deserialize<Note>(System.Text.Json.JsonSerializer.Serialize(archivedJson))!.Archived && !System.Text.Json.JsonSerializer.Deserialize<Note>("{\"Title\":\"old\"}")!.Archived, "The archive flag is stored with the note and older notes read as not archived");
    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Notlar;component/Styles.xaml") });
    DeviceFlow(root);
    AttachmentFlow(root);
        return (session, path, book);
    }
}
