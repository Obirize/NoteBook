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

static class Program
{
    static int checks;
    const string Password = "test-only long passphrase 927";
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    static void Reject(Action action, string name) { bool rejected = false; try { action(); } catch { rejected = true; } Check(rejected, name); }
    static void dirty(MainWindow w) => w.MarkChanged();
    static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    static T Find<T>(Window window, string name) where T : class => (T)window.FindName(name);
    static void Click(Window window, string name) { Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
    static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new Exception("UI operation timed out"); await Task.Delay(30); }
    }
    static void Wheel(UIElement target, int delta) => target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
    static double Settle(Func<double> offset)
    {
        // Smooth scrolling animates over a few frames; wait until the offset stops changing.
        var watch = Stopwatch.StartNew(); double last = double.NaN; int stable = 0;
        while (watch.ElapsedMilliseconds < 3000) { Pump(); Thread.Sleep(16); double value = offset(); if (value == last && ++stable >= 8) break; if (value != last) stable = 0; last = value; }
        return last;
    }
    static IEnumerable<T> Visuals<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in Visuals<T>(child)) yield return descendant;
        }
    }
    static void DeviceFlow(string root)
    {
        string path = Path.Combine(root, "automatic.vault");
        using var device = VaultSession.CreateDevice(path, new Notebook { Notes = [new Note { Title = "Kendime küçük bir not", Text = "PASSWORDLESS-PRIVATE-TEXT" }] });
        device.Save(); device.VerifySaved(); device.Save();
        using (var opened = VaultSession.OpenDevice(path)) Check(opened.Book.Notes[0].Text == "PASSWORDLESS-PRIVATE-TEXT", "Windows-protected vault opens without application password");
        Check(!File.ReadAllText(path).Contains("PASSWORDLESS-PRIVATE-TEXT") && !File.ReadAllText(path + ".bak").Contains("PASSWORDLESS-PRIVATE-TEXT"), "Automatic vault and backup contain no plaintext notes");
        var valid = File.ReadAllBytes(path);
        var e = JsonSerializer.Deserialize<VaultEnvelope>(valid)!; e.DeviceKey[e.DeviceKey.Length / 2] ^= 1; File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(e));
        Reject(() => VaultSession.OpenDevice(path), "Tampered Windows key fails closed");
        Reject(device.VerifySaved, "Migration verification checks persisted Windows key");
        e = JsonSerializer.Deserialize<VaultEnvelope>(valid)!; e.Content.Ciphertext[0] ^= 1; File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(e));
        Reject(() => VaultSession.OpenDevice(path), "Tampered automatic vault content rejected");
        File.WriteAllBytes(path, valid);
        var legacy = VaultSession.Create(Path.Combine(root, "convert.vault"), Password, device.Book); using var previous = legacy.Session;
        previous.Save(); previous.UseDeviceProtection();
        using (var converted = VaultSession.OpenDevice(previous.FilePath)) Check(converted.Book.Notes[0].Text == "PASSWORDLESS-PRIVATE-TEXT", "Existing password vault converts without losing notes");
        using (var convertedBackup = VaultSession.OpenDevice(previous.FilePath + ".bak")) Check(convertedBackup.IsDeviceProtected, "Converted rolling backup also opens without password");
        Reject(() => VaultSession.Open(previous.FilePath, Password), "Converted vault no longer uses password wrapper");
        for (int i = 0; i < 50; i++) device.Book.Notes.Add(new Note { Title = "Not " + (i + 1), Text = "Kısa bir düşünce." });
        var longNote = device.Book.Notes[0]; longNote.Updated = DateTimeOffset.UtcNow.AddMinutes(1);
        longNote.Text = string.Join("\n\n", Enumerable.Range(1, 45).Select(i => i + ". Aklımdakileri yazmak için küçük bir alan.\nSadece notlarım, hepsi bu."));
        device.Book.Notes.Add(new Note { Title = "Süresi dolan", Text = "EXPIRED-TRASH-TEXT", Deleted = true, DeletedAt = DateTimeOffset.UtcNow.AddDays(-31) });
        device.Book.Notes.Add(new Note { Title = "Yeni silinen", Deleted = true, DeletedAt = DateTimeOffset.UtcNow.AddDays(-2) });
        device.Book.Notes.Add(new Note { Title = "Eski sürümden silinen", Deleted = true });
        device.Save();
        var window = new MainWindow(device); window.Show(); Pump();
        Check(device.Book.Notes.All(n => n.Title != "Süresi dolan") && device.Book.Notes.Count(n => n.Deleted) == 2, "Trash older than 30 days is purged automatically at startup");
        Check(device.Book.Notes.First(n => n.Title == "Eski sürümden silinen").DeletedAt != null, "Deletions without a timestamp get a full grace period instead of instant purge");
        using (var purgedOnDisk = VaultSession.OpenDevice(path)) Check(purgedOnDisk.Book.Notes.All(n => n.Title != "Süresi dolan"), "Automatic purge is persisted");
        using (var purgedBackup = VaultSession.OpenDevice(path + ".bak")) Check(purgedBackup.Book.Notes.All(n => n.Title != "Süresi dolan"), "Automatic purge also rewrites the rolling backup");
        Check(Find<Button>(window, "LockButton").Visibility == Visibility.Collapsed, "Automatic mode has no unnecessary lock button");
        Check(window.WindowStyle == WindowStyle.None, "Main window has no native white title bar");
        var body = Find<TextBox>(window, "BodyInput");
        var bars = Visuals<ScrollBar>(body).Where(b => b.IsVisible && b.Orientation == Orientation.Vertical).ToList();
        Check(bars.Count == 1 && Math.Abs(bars[0].ActualWidth - 10) < 1, "Long note uses narrow themed scrollbar");
        var scrollbar = bars[0];
        ScrollBar.PageDownCommand.Execute(null, scrollbar); Pump();
        Check(body.VerticalOffset > 0, "Styled scrollbar page command actually scrolls text");
        ScrollBar.PageUpCommand.Execute(null, scrollbar); Pump();
        Check(Settle(() => body.VerticalOffset) == 0, "Styled scrollbar can return to the start");
        var list = Find<ListBox>(window, "NoteList");
        Check(Visuals<ScrollBar>(list).Any(b => b.IsVisible && Math.Abs(b.ActualWidth - 10) < 1), "Long note list uses same narrow scrollbar");
        var listScroll = Visuals<ScrollViewer>(list).First();
        double listStep = SmoothScroll.NotchPixels * SmoothScroll.GetScale(listScroll), listLead = SmoothScroll.MaxLead * SmoothScroll.GetScale(listScroll);
        Check(listStep < SmoothScroll.NotchPixels && listStep > SmoothScroll.NotchPixels / 2, "Note list scrolls a little slower per notch than the editor");
        listScroll.ScrollToTop(); Pump();
        Wheel(listScroll, -120);
        Check(listScroll.VerticalOffset < listStep - 1, "List wheel input animates over frames instead of jumping synchronously");
        double settled = Settle(() => listScroll.VerticalOffset);
        Check(Math.Abs(settled - Math.Round(listStep)) < 1, "One wheel notch settles at the list step, not three note cards");
        Wheel(listScroll, 60); settled = Settle(() => listScroll.VerticalOffset);
        Check(Math.Abs(settled - listStep / 2) < 1.5, "High-resolution wheel delta is proportional");
        for (int i = 0; i < 12; i++) Wheel(listScroll, -120);
        Check(listScroll.VerticalOffset < listLead + listStep, "Rapid wheel input does not queue a long overshoot");
        settled = Settle(() => listScroll.VerticalOffset);
        Check(settled > listStep && settled <= listStep / 2 + listLead + 1, "Rapid wheel input is capped, so releasing the wheel stops promptly");
        listScroll.ScrollToTop(); Pump();
        var bodyScroll = Visuals<ScrollViewer>(body).First();
        bodyScroll.ScrollToTop(); Pump();
        Wheel(bodyScroll, -120); settled = Settle(() => body.VerticalOffset);
        Check(Math.Abs(settled - SmoothScroll.NotchPixels) < 1, "Editor text uses the same smooth wheel behavior as the list");
        Wheel(bodyScroll, 120); settled = Settle(() => body.VerticalOffset);
        Check(settled == 0, "Editor smooth scroll returns exactly to the top");
        string importPath = Path.Combine(root, "Türkçe örnek.txt");
        string importedText = "İstanbul, ıhlamur, çığ, şeker, öykü, güneş. 🌿\r\n\r\nSon satır\n";
        File.WriteAllText(importPath, importedText, new UTF8Encoding(false));
        Check(window.ImportTextFile(importPath), "TXT imports through editor into encrypted storage");
        using (var reopened = VaultSession.OpenDevice(path)) Check(reopened.Book.Notes.Any(n => n.Title == "Türkçe örnek" && n.Text == importedText), "Imported Unicode and newlines survive encrypted reopen");
        Find<TextBox>(window, "BodyInput").Text += "Düzenleme"; window.SaveNow();
        Check(File.ReadAllText(importPath) == importedText, "Editing imported note never overwrites original TXT");
        string exportPath = Path.Combine(root, "export.txt");
        TextFiles.Write(exportPath, new Note { Title = "Title", Text = importedText });
        Check(File.ReadAllBytes(exportPath).SequenceEqual(new UTF8Encoding(false).GetBytes(importedText)), "Export writes interoperable UTF-8 with exact body and line endings");
        File.WriteAllText(importPath, importedText, Encoding.Unicode);
        Check(TextFiles.Read(importPath).Text == importedText, "UTF-16 BOM Notepad file imports correctly");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        File.WriteAllText(importPath, "İıĞğŞşÇçÖöÜü", Encoding.GetEncoding(1254));
        Check(TextFiles.Read(importPath).Text == "İıĞğŞşÇçÖöÜü", "Legacy Turkish Windows-1254 TXT imports correctly");
        File.WriteAllBytes(importPath, [0, 1, 0, 2]); Reject(() => TextFiles.Read(importPath), "Binary masquerading as TXT is rejected");
        Reject(() => TextFiles.Write(path, new Note()), "TXT export cannot overwrite encrypted vault");
        list.SelectedItem = longNote; Pump();
        window.Close();
    }
    static BitmapSource Bitmap(int width, int height, Color color)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) { dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, width, height)); dc.DrawEllipse(Brushes.White, null, new Point(width / 2.0, height / 2.0), width / 4.0, height / 4.0); }
        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bmp.Render(visual); return bmp;
    }
    static byte[] Png(int width, int height, Color color)
    {
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(Bitmap(width, height, color)));
        using var stream = new MemoryStream(); png.Save(stream); return stream.ToArray();
    }
    static void AttachmentFlow(string root)
    {
        string dir = Path.Combine(root, "attachments-store");
        var store = new AttachmentStore(Path.Combine(dir, "attachments"));
        byte[] photo = Png(640, 400, Colors.SteelBlue);
        string photoPath = Path.Combine(dir, "Tatil fotoğrafı.png"); Directory.CreateDirectory(dir); File.WriteAllBytes(photoPath, photo);
        var big = new byte[(int)(2.5 * AttachmentStore.ChunkSize)]; RandomNumberGenerator.Fill(big);
        string videoPath = Path.Combine(dir, "klip.mp4"); File.WriteAllBytes(videoPath, big);
        var a = store.Import(photoPath); var v = store.Import(videoPath);
        Check(a.IsImage && a.Width == 640 && a.Height == 400 && a.Size == photo.Length && a.MediaType == "image/png" && a.Key.Length == 32 && a.Sha256.SequenceEqual(SHA256.HashData(photo)), "Imported photo records size, pixel dimensions, media type, hash and its own key");
        Check(v.IsVideo && v.Size == big.Length && v.MediaType == "video/mp4" && !v.Key.SequenceEqual(a.Key), "Imported video gets a different random key");
        var stored = File.ReadAllBytes(store.PathFor(a));
        Check(stored.Length == photo.Length + 24 + 16 && stored.AsSpan().IndexOf(photo.AsSpan(0, 8)) < 0, "Encrypted attachment has only a header and tag of overhead and no plaintext signature");
        Check(store.ReadAll(a).SequenceEqual(photo) && store.ReadAll(v).SequenceEqual(big), "Attachments decrypt back to the original bytes across several chunks");
        string exported = Path.Combine(dir, "kopya.mp4"); store.Export(v, exported);
        Check(File.ReadAllBytes(exported).SequenceEqual(big), "Exported copy equals the original file");
        string temp = store.WriteTemporary(a);
        Check(File.Exists(temp) && File.ReadAllBytes(temp).SequenceEqual(photo) && Path.GetExtension(temp) == ".png", "Temporary decrypted copy for the player keeps the extension");
        Check(AttachmentStore.DeleteTemporary(temp) && !File.Exists(temp), "Temporary copy is deleted after viewing");
        string leftover = store.WriteTemporary(a); AttachmentStore.CleanTemporary(); Check(!File.Exists(leftover), "Leftover temporary copies are cleaned at startup");
        var empty = store.Import(new MemoryStream(), "boş.jpg", "image/jpeg");
        Check(empty.Size == 0 && store.ReadAll(empty).Length == 0, "An empty file round-trips");
        byte[] valid = File.ReadAllBytes(store.PathFor(v));
        var tampered = (byte[])valid.Clone(); tampered[24 + AttachmentStore.ChunkSize + 100] ^= 1; File.WriteAllBytes(store.PathFor(v), tampered);
        Reject(() => store.ReadAll(v), "A flipped byte in a middle chunk is rejected");
        File.WriteAllBytes(store.PathFor(v), valid[..(24 + 2 * (AttachmentStore.ChunkSize + 16))]);
        Reject(() => store.ReadAll(v), "A truncated attachment (last chunk missing) is rejected");
        File.WriteAllBytes(store.PathFor(v), valid.Concat(new byte[] { 7 }).ToArray());
        Reject(() => store.ReadAll(v), "Trailing data after the last chunk is rejected");
        File.WriteAllBytes(store.PathFor(v), valid);
        var wrongKey = new Attachment { Id = v.Id, Key = RandomNumberGenerator.GetBytes(32), Size = v.Size };
        Reject(() => store.ReadAll(wrongKey), "Wrong key is rejected");
        var wrongId = new Attachment { Id = "other-id", Key = v.Key, Size = v.Size }; File.Copy(store.PathFor(v), store.PathFor(wrongId));
        Reject(() => store.ReadAll(wrongId), "A file swapped under another attachment id is rejected");
        Check(store.ReadAll(v).SequenceEqual(big), "Original attachment still decrypts after tamper checks");
        var book = new Notebook { Notes = [new Note { Title = "Ekli", Attachments = [a, v] }, new Note { Title = "Silinmiş ama ekli", Deleted = true, DeletedAt = DateTimeOffset.UtcNow, Attachments = [empty] }] };
        int swept = store.Sweep(book);
        Check(swept == 1 && !File.Exists(store.PathFor(wrongId)) && File.Exists(store.PathFor(a)) && File.Exists(store.PathFor(v)) && File.Exists(store.PathFor(empty)), "Sweep removes only files no note (not even a trashed one) refers to");
        string vaultPath = Path.Combine(dir, "notes.vault");
        using (var vault = VaultSession.CreateDevice(vaultPath, book)) { vault.Save(); vault.VerifySaved(); }
        string vaultText = File.ReadAllText(vaultPath);
        Check(!vaultText.Contains(Convert.ToBase64String(a.Key)) && !vaultText.Contains("Tatil"), "Attachment keys and names are only inside the encrypted notebook");
        using (var vault = VaultSession.OpenDevice(vaultPath))
        {
            var loaded = vault.Book.Notes[0].Attachments;
            Check(loaded.Count == 2 && loaded[0].Key.SequenceEqual(a.Key) && loaded[0].Name == "Tatil fotoğrafı.png" && vault.Attachments.Directory == store.Directory, "Attachment metadata survives the vault round-trip and the store sits beside the vault");
            Check(vault.Attachments.ReadAll(loaded[0]).SequenceEqual(photo) && vault.Book.SchemaVersion == Notebook.CurrentSchema, "A reopened vault decrypts its attachments; the schema version is current");
        }
        var other = new AttachmentStore(Path.Combine(dir, "other"));
        Check(other.CopyMissing(book, store) == 3 && other.CopyMissing(book, store) == 0 && other.ReadAll(v).SequenceEqual(big), "Copying encrypted files between stores needs no re-encryption and is idempotent");
        var merged = new Notebook { Notes = [new Note { Id = book.Notes[0].Id, Title = "old", Revision = 0 }] };
        VaultSession.Merge(merged, book);
        Check(merged.Notes[0].Attachments.Count == 2 && merged.Notes[0].Attachments[1].Key.SequenceEqual(v.Key) && !ReferenceEquals(merged.Notes[0].Attachments[1], v), "Merge carries attachments (as copies) with the winning revision");
    }
    static void GateFlow(string root)
    {
        var folder = Path.Combine(root, "gate");
        string recovery = "";
        Exception? failure = null;
        var gate = new GateWindow(folder);
        gate.Loaded += async (_, _) =>
        {
            try
            {
                Find<PasswordBox>(gate, "Password").Password = Password;
                Find<PasswordBox>(gate, "ConfirmPassword").Password = Password;
                Click(gate, "Submit");
                await Until(() => Find<StackPanel>(gate, "RecoveryPanel").Visibility == Visibility.Visible);
                recovery = Find<TextBox>(gate, "RecoveryCode").Text;
                Check(!File.Exists(Path.Combine(folder, "notes.vault")), "Setup UI waits for recovery acknowledgment");
                Find<CheckBox>(gate, "Acknowledged").IsChecked = true; Click(gate, "Finish");
            }
            catch (Exception ex) { failure = ex; gate.Close(); }
        };
        Check(gate.ShowDialog() == true && gate.Session != null && failure == null, "Full setup UI opens a persisted vault");
        gate.Session!.Dispose();
        var unlock = new GateWindow(folder);
        unlock.Loaded += async (_, _) =>
        {
            try
            {
                Find<PasswordBox>(unlock, "Password").Password = "wrong"; Click(unlock, "Submit");
                await Until(() => Find<Button>(unlock, "Submit").IsEnabled);
                Check(Find<TextBlock>(unlock, "Error").Text.Contains("Kilit açılamadı"), "Wrong password stays at lock screen");
                Find<PasswordBox>(unlock, "Password").Password = Password; Click(unlock, "Submit");
            }
            catch (Exception ex) { failure = ex; unlock.Close(); }
        };
        Check(unlock.ShowDialog() == true && unlock.Session != null && failure == null, "Unlock UI returns original session"); unlock.Session!.Dispose();
        var reset = new GateWindow(folder);
        reset.Loaded += async (_, _) =>
        {
            try
            {
                Click(reset, "Recover"); Find<PasswordBox>(reset, "Password").Password = recovery; Click(reset, "Submit");
                await Until(() => Find<StackPanel>(reset, "ConfirmPanel").Visibility == Visibility.Visible);
                Find<PasswordBox>(reset, "Password").Password = "another long test password";
                Find<PasswordBox>(reset, "ConfirmPassword").Password = "another long test password"; Click(reset, "Submit");
            }
            catch (Exception ex) { failure = ex; reset.Close(); }
        };
        Check(reset.ShowDialog() == true && reset.Session != null && failure == null, "Recovery UI resets password and opens vault"); reset.Session!.Dispose();
        var restore = new GateWindow(folder);
        File.WriteAllText(Path.Combine(folder, "notes.vault"), "corrupt test data");
        restore.Loaded += async (_, _) =>
        {
            try
            {
                Find<PasswordBox>(restore, "Password").Password = "another long test password"; Click(restore, "Submit");
                await Until(() => Find<CheckBox>(restore, "UseBackup").Visibility == Visibility.Visible);
                Find<CheckBox>(restore, "UseBackup").IsChecked = true; Click(restore, "Submit");
            }
            catch (Exception ex) { failure = ex; restore.Close(); }
        };
        Check(restore.ShowDialog() == true && restore.Session != null && failure == null, "Backup recovery UI restores authenticated file"); restore.Session!.Dispose();
        Check(Directory.GetFiles(folder, "*.damaged-*").Length == 1, "Backup recovery retains damaged encrypted source");
    }
    [STAThread] static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "Notlar-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
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
            var created = VaultSession.Create(path, Password, book); using var session = created.Session;
            Check(!File.Exists(path), "Recovery acknowledgment precedes persistence");
            session.Save(); session.VerifySaved();
            string first = File.ReadAllText(path);
            Check(!first.Contains("Gizli") && !first.Contains("PRIVATE-CONTENT") && !first.Contains(Password) && !first.Contains(created.RecoveryCode), "No plaintext title, body, password or recovery secret on disk");
            using (var reopened = VaultSession.Open(path, Password)) Check(reopened.Book.Notes[0].Text == book.Notes[0].Text, "Encrypted round-trip");
            Reject(() => VaultSession.Open(path, "incorrect password"), "Wrong password rejected");
            using (var recovered = VaultSession.Open(path, created.RecoveryCode, true)) Check(recovered.Book.Id == book.Id, "Recovery key unlocks original vault");
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
            session.ChangePassword("replacement test passphrase");
            Reject(() => VaultSession.Open(path, Password), "Old password invalid after reset");
            Reject(() => VaultSession.Open(path + ".bak", Password), "Rolling backup also uses new password");
            using (var changed = VaultSession.Open(path, "replacement test passphrase")) Check(changed.Book.Notes.Count == 1, "Password reset preserves notes");
            using (var recovered = VaultSession.Open(path, created.RecoveryCode, true)) Check(recovered.Book.Notes.Count == 1, "Recovery remains valid after reset");
            var migrationDir = Path.Combine(root, "migration"); Directory.CreateDirectory(migrationDir);
            string legacy = "{\"Notes\":[{\"Id\":\"legacy-id\",\"Title\":\"Eski not\",\"Text\":\"Mevcut içerik\",\"Rtf\":\"rich-data-preserved\",\"Created\":\"/Date(1700000000000)/\",\"Updated\":\"/Date(1700000000000)/\",\"Pinned\":true,\"Deleted\":false}]}";
            File.WriteAllText(Path.Combine(migrationDir, "notes.json"), legacy); File.WriteAllText(Path.Combine(migrationDir, "notes.backup.json"), legacy);
            var imported = LegacyImport.Read(migrationDir);
            Check(imported.Notes.Count == 1 && imported.Notes[0].Id == "legacy-id" && imported.Notes[0].Created.Year == 2023 && imported.LegacyArchive.Count == 2, "Legacy ids, dates, content and raw rich-text archives retained");
            var importedSession = VaultSession.Create(Path.Combine(migrationDir, "notes.vault"), Password, imported); using var migrated = importedSession.Session;
            migrated.Save(); migrated.VerifySaved(); LegacyImport.RemoveVerifiedOriginals(migrationDir, migrated);
            Check(!File.Exists(Path.Combine(migrationDir, "notes.json")) && !File.Exists(Path.Combine(migrationDir, "notes.backup.json")), "Only verified legacy plaintext removed");
            using (var roundtrip = VaultSession.Open(migrated.FilePath, Password)) Check(roundtrip.Book.LegacyArchive["notes.json"].Contains("rich-data-preserved"), "Rich source retained inside encrypted archive");
            File.WriteAllText(Path.Combine(migrationDir, "notes.json"), "changed concurrently");
            Reject(() => LegacyImport.RemoveVerifiedOriginals(migrationDir, migrated), "Concurrent legacy edits are never deleted");
            Reject(() => LegacyImport.Read(migrationDir), "Unreadable legacy files do not silently become empty notes");
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
            stale.LegacyArchive["notes.json"] = "{\"Notes\":[{\"Id\":\"old-id\",\"Text\":\"legacy-secret\"},{\"Id\":\"keep\",\"Text\":\"kept\"}]}";
            Check(TrashPolicy.InitializeDates(stale, now) && stale.Notes[2].DeletedAt == now && !TrashPolicy.InitializeDates(stale, now), "Legacy deletions without a timestamp start their 30-day period now");
            Check(TrashPolicy.Expired(stale, now).Single().Title == "Eski" && TrashPolicy.Expired(stale, now.AddDays(2)).Count == 2, "Only deletions older than 30 days expire");
            Check(TrashPolicy.Purge(stale, TrashPolicy.Expired(stale, now)) == 1 && stale.Notes.Count == 3 && !stale.LegacyArchive["notes.json"].Contains("legacy-secret") && stale.LegacyArchive["notes.json"].Contains("kept"), "Purge removes the note and its copy inside the legacy archive");
            Check(TrashPolicy.Purge(stale, [stale.Notes.First(n => n.Title == "Canlı")]) == 0 && stale.Notes.Count == 3, "Purge never removes a live note");
            TrashPolicy.Delete([stale.Notes[2]], now); Check(stale.Notes[2].Deleted && stale.Notes[2].DeletedAt == now && stale.Notes[2].TrashLabel == L10n.T("TrashLabelDays", 30), "Delete records the deletion time and shows remaining days");
            TrashPolicy.Restore([stale.Notes[2]], now); Check(!stale.Notes[2].Deleted && stale.Notes[2].DeletedAt == null && stale.Notes[2].TrashLabel == "", "Restore clears the deletion time");
            Check(NoteQuery.Find(book, "istanbul", false).Count == 1 && NoteQuery.Find(book, "ISTANBUL", false).Count == 1, "Search ignores case and dotted/undotted i in any UI language");
            L10n.Use("tr"); Check(NoteQuery.Find(book, "istanbul", false).Count == 1 && NoteQuery.Find(book, "ıstanbul", false).Count == 0, "Turkish culture keeps dotted and dotless i distinct"); L10n.Use("en");
            book.Notes[0].Deleted = true;
            Check(NoteQuery.Find(book, "", false).Count == 0 && NoteQuery.Find(book, "", true).Count == 1, "Deleted tombstone filtered from live notes");
            book.Notes[0].Deleted = false;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Notlar;component/Styles.xaml") });
            DeviceFlow(root);
            GateFlow(root);
            AttachmentFlow(root);
            var window = new MainWindow(session); window.Show(); Pump();
            window.Activate();
            var title = Find<TextBox>(window, "TitleInput"); var body = Find<TextBox>(window, "BodyInput");
            title.Text = "Yarın için birkaç fikir"; body.Text = "Her şeyi aynı anda yapmak zorunda değilim.\n\nÖnce önemli olanı seç.\nBiraz yavaşla.\nBaşlamak için küçük bir adım yeter.";
            Check(window.SaveNow(), "Editor saves title and text");
            using (var reopened = VaultSession.Open(path, "replacement test passphrase")) Check(reopened.Book.Notes[0].Title == title.Text && reopened.Book.Notes[0].Text == body.Text, "Actual editor content survives reopen");
            Click(window, "NewButton");
            Check(title.IsKeyboardFocused, "New note focuses title for immediate typing");
            title.Text = "Hafta sonu"; body.Text = "Uzun bir yürüyüş.\nYarım kalan kitaba dön.\nKahveyi acele etmeden iç.";
            var autoSaveWatch = Stopwatch.StartNew();
            while (Find<TextBlock>(window, "StatusText").Text == L10n.T("Saving") && autoSaveWatch.ElapsedMilliseconds < 3000) { Pump(); Thread.Sleep(10); }
            Check(Find<TextBlock>(window, "StatusText").Text == L10n.T("SavedEncrypted"), "Debounced automatic save completes without save command");
            Check(session.Book.Notes.Count == 2, "New note via real UI handler");
            var search = Find<TextBox>(window, "SearchInput"); search.Text = "yürüyüş"; Pump();
            Check(Find<ListBox>(window, "NoteList").Items.Count == 1, "UI searches note bodies");
            search.Text = "unmatched-query"; Pump();
            Check(Find<ListBox>(window, "NoteList").Items.Count == 0 && Find<Grid>(window, "EditorArea").Visibility == Visibility.Collapsed, "Empty search hides unrelated editor content");
            search.Clear(); Pump();
            Click(window, "DeleteButton");
            Check(session.Book.Notes.Count(n => n.Deleted) == 1, "Soft delete retains note");
            Click(window, "UndoDelete"); Check(session.Book.Notes.All(n => !n.Deleted), "Delete undo restores note");
            Click(window, "DeleteButton"); Click(window, "TrashFilter");
            Check(body.IsReadOnly, "Trash content cannot be accidentally edited");
            Click(window, "RestoreButton"); Check(session.Book.Notes.All(n => !n.Deleted), "Trash restore returns note");
            var pinTarget = (Note)Find<ListBox>(window, "NoteList").SelectedItem; bool wasPinned = pinTarget.Pinned;
            Click(window, "PinButton"); Check(pinTarget.Pinned != wasPinned, "Pin toggles from UI");
            Click(window, "NewButton"); title.Text = "Toplu 1"; body.Text = "PURGE-ME-4411"; window.SaveNow();
            Click(window, "NewButton"); title.Text = "Toplu 2"; window.SaveNow();
            var noteList = Find<ListBox>(window, "NoteList");
            Click(window, "SelectButton");
            Check(noteList.SelectionMode == SelectionMode.Multiple && Find<Grid>(window, "EditorArea").Visibility == Visibility.Collapsed && Find<StackPanel>(window, "SelectionState").Visibility == Visibility.Visible, "Select mode shows checkboxes and hides the editor");
            Check(Visuals<CheckBox>(noteList).Count(c => c.IsVisible) == 4 && !Find<Button>(window, "BulkDeleteButton").IsEnabled, "Every card gets a checkbox; bulk delete waits for a selection");
            Click(window, "SelectAllButton");
            Check(noteList.SelectedItems.Count == 4 && Find<TextBlock>(window, "SelectionCount").Text == L10n.Count("SelectedCountOne", "SelectedCountMany", 4), "Select all checks every listed note");
            Click(window, "SelectAllButton"); Check(noteList.SelectedItems.Count == 0, "Select all toggles back to none");
            noteList.SelectedItems.Add(noteList.Items[0]); noteList.SelectedItems.Add(noteList.Items[1]); Pump();
            Check(Find<Button>(window, "BulkDeleteButton").IsEnabled && Find<TextBlock>(window, "SelectionHeading").Text == L10n.Count("SelectedCountOne", "SelectedCountMany", 2), "Checking cards enables bulk actions");
            Click(window, "BulkDeleteButton");
            Check(session.Book.Notes.Count(n => n.Deleted) == 2 && session.Book.Notes.Where(n => n.Deleted).All(n => n.DeletedAt != null) && noteList.SelectionMode == SelectionMode.Single, "Bulk delete moves the checked notes to trash with timestamps and leaves select mode");
            Click(window, "UndoDelete"); Check(session.Book.Notes.All(n => !n.Deleted), "Undo restores every bulk-deleted note");
            Click(window, "SelectButton"); Click(window, "SelectAllButton"); Find<Button>(window, "SelectAllButton").Focus(); Pump();
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Delete) { RoutedEvent = UIElement.PreviewKeyDownEvent }); Pump();
            Check(session.Book.Notes.Count(n => n.Deleted) == 4 && noteList.SelectionMode == SelectionMode.Single, "Delete key removes the checked notes even when a toolbar button has focus");
            Click(window, "UndoDelete"); Check(session.Book.Notes.All(n => !n.Deleted), "Undo after keyboard bulk delete restores everything");
            Check(Find<Button>(window, "BulkDeleteButton").Content as string == L10n.T("Delete") && Find<Button>(window, "DeleteButton").ToolTip as string == L10n.T("Delete"), "The primary bulk action is plainly labeled Delete");
            Click(window, "NewButton"); title.Text = "Geçici"; window.SaveNow();
            Click(window, "SelectButton"); noteList.SelectedItems.Add(session.Book.Notes.First(n => n.Title == "Geçici")); Pump();
            Check(Find<Button>(window, "SelectionDeleteButton").IsVisible && Find<Button>(window, "BulkPurgeButton").IsVisible && Find<Button>(window, "BulkPurgeButton").IsEnabled, "Select mode offers Delete in the sidebar and permanent delete from All notes too");
            window.ConfirmDestructive = _ => true; Click(window, "BulkPurgeButton");
            Check(session.Book.Notes.Count == 4 && session.Book.Notes.All(n => n.Title != "Geçici") && noteList.SelectionMode == SelectionMode.Single, "Permanent delete from All notes removes the checked note outright");
            body.Focus(); Pump();
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Delete) { RoutedEvent = UIElement.PreviewKeyDownEvent }); Pump();
            Check(session.Book.Notes.All(n => !n.Deleted), "Delete key inside the editor edits text instead of deleting the note");
            Click(window, "SelectButton"); Click(window, "SelectAllButton"); Click(window, "BulkDeleteButton");
            Check(session.Book.Notes.Count(n => n.Deleted) == 4, "Select all then delete empties the list");
            Click(window, "TrashFilter");
            Check(Find<TextBlock>(window, "TrashNotice").Visibility == Visibility.Visible && Find<Button>(window, "PurgeButton").Visibility == Visibility.Visible && Find<Button>(window, "ExportButton").Visibility == Visibility.Collapsed, "Trash explains the 30-day limit and offers permanent delete");
            var purgeTarget = session.Book.Notes.First(n => n.Text == "PURGE-ME-4411"); noteList.SelectedItem = purgeTarget; Pump();
            Check(purgeTarget.TrashLabel == L10n.T("TrashLabelDays", 30), "Trash card shows remaining days");
            bool asked = false; window.ConfirmDestructive = _ => { asked = true; return false; };
            Click(window, "PurgeButton"); Check(asked && session.Book.Notes.Contains(purgeTarget), "Permanent delete asks first and a declined dialog keeps the note");
            window.ConfirmDestructive = _ => true;
            Click(window, "PurgeButton");
            Check(!session.Book.Notes.Contains(purgeTarget) && session.Book.Notes.Count == 3, "Confirmed permanent delete removes the note from the notebook");
            using (var reopened = VaultSession.Open(path, "replacement test passphrase")) Check(reopened.Book.Notes.All(n => n.Id != purgeTarget.Id), "Purged note is gone after reopen");
            using (var reopenedBackup = VaultSession.Open(path + ".bak", "replacement test passphrase")) Check(reopenedBackup.Book.Notes.All(n => n.Id != purgeTarget.Id), "Purged note is also gone from the rolling backup");
            Click(window, "SelectButton"); noteList.SelectedItems.Add(noteList.Items[0]); noteList.SelectedItems.Add(noteList.Items[1]); Pump();
            Click(window, "BulkPurgeButton");
            Check(session.Book.Notes.Count == 1 && session.Book.Notes[0].Deleted, "Bulk permanent delete removes the checked trash notes");
            Click(window, "SelectButton"); Click(window, "SelectAllButton"); Click(window, "BulkRestoreButton");
            Check(session.Book.Notes.All(n => !n.Deleted && n.DeletedAt == null) && Find<TextBlock>(window, "ListHeading").Text == L10n.T("MyNotes") && Find<Grid>(window, "EditorArea").Visibility == Visibility.Visible, "Bulk restore returns notes and reopens the editor");
            // Portable backup: password-protected, independent of this Windows account.
            string portable = Path.Combine(root, "portable.vault");
            window.AskPassword = _ => "portable backup passphrase 42";
            Check(window.ExportBackup(portable, "portable backup passphrase 42") && File.Exists(portable) && !File.ReadAllText(portable).Contains("Yarın"), "Portable backup is written encrypted");
            Reject(() => VaultSession.OpenBackup(portable, "wrong passphrase 12345"), "Portable backup rejects a wrong password");
            Reject(() => VaultSession.OpenBackup(portable, null), "Portable backup requires a password");
            using (var opened = VaultSession.OpenBackup(portable, "portable backup passphrase 42")) Check(opened.Book.Notes.Count == session.Book.Notes.Count && opened.Book.Id == session.Book.Id, "Portable backup opens with its password alone (no DPAPI)");
            var before = session.Book.Notes.Count;
            var edited = session.Book.Notes[0]; string keepTitle = edited.Title;
            edited.Title = "Sonradan düzenlendi"; edited.Revision++; edited.Updated = DateTimeOffset.UtcNow; window.SaveNow();
            Check(window.RestoreBackup(portable, "portable backup passphrase 42") && session.Book.Notes.Count == before && session.Book.Notes[0].Title == "Sonradan düzenlendi" && Find<TextBlock>(window, "StatusText").Text == L10n.T("RestoreNothing"), "Restoring an older backup never overwrites newer local edits");
            session.Book.Notes.Remove(edited); dirty(window);
            Check(window.RestoreBackup(portable, "portable backup passphrase 42") && session.Book.Notes.Any(n => n.Id == edited.Id && n.Title == keepTitle) && Find<TextBlock>(window, "StatusText").Text == L10n.T("RestoreResult", 1, 0), "Restoring adds notes missing locally and reports the count");
            Check(!window.RestoreBackup(portable, "wrong passphrase 12345") && Find<TextBlock>(window, "StatusText").Text == L10n.T("PasswordWrong"), "Wrong restore password is reported, nothing changes");
            var merged = new Notebook { Notes = [new Note { Id = "a", Title = "old", Revision = 1 }, new Note { Id = "b", Title = "local newer", Revision = 3 }] };
            var incoming = new Notebook { Notes = [new Note { Id = "a", Title = "new", Revision = 2 }, new Note { Id = "b", Title = "backup older", Revision = 2 }, new Note { Id = "c", Title = "only in backup", Revision = 1 }] };
            var stats = VaultSession.Merge(merged, incoming);
            Check(stats == (1, 1) && merged.Notes.Single(n => n.Id == "a").Title == "new" && merged.Notes.Single(n => n.Id == "b").Title == "local newer" && merged.Notes.Count == 3, "Merge keeps the higher revision per note and adds unknown notes");
            // Drag and drop of TXT files.
            string drop1 = Path.Combine(root, "Bırakılan 1.txt"), drop2 = Path.Combine(root, "Bırakılan 2.txt"), dropBad = Path.Combine(root, "resim.png");
            File.WriteAllText(drop1, "ilk", new UTF8Encoding(false)); File.WriteAllText(drop2, "ikinci", new UTF8Encoding(false)); File.WriteAllBytes(dropBad, [1, 2]);
            int dropped = window.ImportDropped([drop1, drop2]);
            Check(dropped == 2 && session.Book.Notes.Count(n => n.Title.StartsWith("Bırakılan")) == 2 && Find<TextBlock>(window, "StatusText").Text == L10n.T("TxtImportedMany", 2), "Dropping TXT files imports each one as a note");
            Check(window.AllowDrop, "Main window accepts file drops");
            // Photos and videos on a note.
            var photoNote = (Note)Find<ListBox>(window, "NoteList").SelectedItem;
            string photoFile = Path.Combine(root, "Deniz kenarı.png"); File.WriteAllBytes(photoFile, Png(800, 500, Colors.DarkOrange));
            string videoFile = Path.Combine(root, "yürüyüş.mp4"); var clip = new byte[3 * 1024 * 1024 + 123]; RandomNumberGenerator.Fill(clip); File.WriteAllBytes(videoFile, clip);
            var attaching = window.AttachFilesAsync([photoFile, videoFile, drop1]);
            while (!attaching.IsCompleted) { Pump(); Thread.Sleep(10); }
            Check(attaching.Result == 2 && photoNote.Attachments.Count == 2 && Find<TextBlock>(window, "StatusText").Text == L10n.T("AttachmentSelectFile"), "Attaching a photo and a video reports the unsupported file and keeps the two media files");
            var panel = Find<WrapPanel>(window, "AttachmentPanel");
            Check(Find<ScrollViewer>(window, "AttachmentScroll").Visibility == Visibility.Visible && panel.Children.Count == 2, "Attachment strip shows one tile per file");
            var thumbnail = Visuals<Image>(panel).First();
            var thumbWatch = Stopwatch.StartNew(); while (thumbnail.Source == null && thumbWatch.ElapsedMilliseconds < 5000) { Pump(); Thread.Sleep(20); }
            Check(thumbnail.Source is BitmapSource thumb && thumb.PixelWidth == 264, "Photo tile decodes a small thumbnail from the encrypted file");
            Check(session.Attachments.Exists(photoNote.Attachments[0]) && session.Attachments.Directory == Path.Combine(Path.GetDirectoryName(path)!, "attachments"), "Encrypted attachment files live in the attachments folder beside the vault");
            using (var reopened = VaultSession.Open(path, "replacement test passphrase")) Check(reopened.Book.Notes.Single(n => n.Id == photoNote.Id).Attachments.Count == 2, "Attachment metadata is saved with the note");
            search.Text = "yürüyüş.mp4"; Pump();
            Check(Find<ListBox>(window, "NoteList").Items.Count == 1 && photoNote.AttachmentLabel == L10n.T("AttachmentCountMany", 2), "Search finds notes by attachment name; the card shows the attachment count");
            search.Clear(); Pump();
            var viewer = new AttachmentWindow(window, session.Attachments, photoNote.Attachments[0]); viewer.Show(); Pump();
            Check(Find<Image>(viewer, "Picture").Source is BitmapSource full && full.PixelWidth == 800 && Find<TextBlock>(viewer, "NameText").Text == "Deniz kenarı.png", "Viewer shows the full decrypted photo");
            viewer.Close(); Pump();
            string copy = Path.Combine(root, "kopya.mp4");
            Check(window.SaveAttachmentCopy(photoNote.Attachments[1], copy) && File.ReadAllBytes(copy).SequenceEqual(clip), "Save a copy writes the decrypted original");
            Check(window.AttachImage(Bitmap(200, 120, Colors.Teal)) && photoNote.Attachments.Count == 3 && photoNote.Attachments[2].MediaType == "image/png" && photoNote.Attachments[2].Width == 200, "A pasted picture becomes a PNG attachment");
            string attachedBackup = Path.Combine(root, "with-files.vault");
            Check(window.ExportBackup(attachedBackup, "portable backup passphrase 42") && Directory.GetFiles(VaultSession.BackupFilesDirectory(attachedBackup)).Length == 3, "Portable backup carries the encrypted attachment files beside it");
            string restoreRoot = Path.Combine(root, "restore-target"); Directory.CreateDirectory(restoreRoot);
            using (var target = VaultSession.CreateDevice(Path.Combine(restoreRoot, "notes.vault")))
            {
                target.Save();
                var targetWindow = new MainWindow(target); targetWindow.Show(); Pump();
                Check(targetWindow.RestoreBackup(attachedBackup, "portable backup passphrase 42") && target.Attachments.ReadAll(target.Book.Notes.Single(n => n.Id == photoNote.Id).Attachments[1]).SequenceEqual(clip), "Restoring a backup on another vault brings the attachments along and decrypts them there");
                targetWindow.Close();
            }
            var removed = photoNote.Attachments[1]; string removedPath = session.Attachments.PathFor(removed);
            window.ConfirmRemoveAttachment = _ => false;
            Check(!window.RemoveAttachment(removed) && photoNote.Attachments.Count == 3 && File.Exists(removedPath), "Declining the removal prompt keeps the attachment");
            window.ConfirmRemoveAttachment = _ => true;
            Check(window.RemoveAttachment(removed) && photoNote.Attachments.Count == 2 && !File.Exists(removedPath) && panel.Children.Count == 2 && Find<TextBlock>(window, "StatusText").Text == L10n.T("AttachmentRemoved"), "Removing an attachment deletes its encrypted file");
            Click(window, "DeleteButton"); Click(window, "TrashFilter"); Pump();
            Check(Find<Button>(window, "AttachButton").Visibility == Visibility.Collapsed && File.Exists(session.Attachments.PathFor(photoNote.Attachments[0])), "A note in Recently deleted cannot take attachments but keeps its files");
            window.ConfirmDestructive = _ => true; Click(window, "PurgeButton");
            Check(!session.Book.Notes.Contains(photoNote) && !File.Exists(session.Attachments.PathFor(photoNote.Attachments[0])) && Directory.GetFiles(session.Attachments.Directory, "*.bin").Length == 0, "Permanently deleting a note removes its attachment files");
            Click(window, "AllFilter"); Pump();
            var passwordDialog = new PasswordDialog(window, L10n.T("BackupPasswordTitle"), L10n.T("BackupPasswordCreate"), true); passwordDialog.Show(); Pump();
            Find<PasswordBox>(passwordDialog, "Password").Password = "short"; Click(passwordDialog, "Submit");
            Check(passwordDialog.IsVisible && Find<TextBlock>(passwordDialog, "Error").Visibility == Visibility.Visible, "Password dialog refuses short passwords");
            Find<PasswordBox>(passwordDialog, "Password").Password = "portable backup passphrase 42"; Find<PasswordBox>(passwordDialog, "Confirm").Password = "different passphrase 4242"; Click(passwordDialog, "Submit");
            Check(passwordDialog.IsVisible && Find<TextBlock>(passwordDialog, "Error").Text == L10n.T("PasswordMismatch"), "Password dialog refuses mismatched confirmation");
            passwordDialog.Close();
            Sounds.Enabled = false;
            var confirm = new MessageDialog(window, L10n.T("DeletePermanently"), L10n.T("ConfirmPurgeOne", "Deneme"), L10n.T("DeletePermanently"), L10n.T("Cancel"), danger: true); confirm.Show(); Pump();
            Check(Find<Button>(confirm, "Primary").Style == confirm.FindResource("DangerButton") && Find<Button>(confirm, "Secondary").IsKeyboardFocused && Visuals<Button>(confirm).Count() == 2, "Themed confirmation uses a red primary action and focuses Cancel by default");
            confirm.Close();
            Check(Assembly.GetAssembly(typeof(Sounds))!.GetManifestResourceStream("Notlar.Sounds.click.wav") is { Length: > 1000 }, "The click sound is embedded in the application");
            Sounds.Enabled = true; Sounds.Click();
            Check(!Directory.Exists("src") || Directory.GetFiles(Path.Combine("src", "Notlar"), "*.cs").Where(f => !f.EndsWith("GateWindow.xaml.cs")).All(f => !File.ReadAllText(f).Contains("MessageBox.Show")), "No system message boxes remain outside the legacy gate window");
            var backupMenu = Find<ContextMenu>(window, "BackupMenu");
            Check(backupMenu.Items.Count == 2 && TextFiles.SuggestedName(new Note { Title = "Alışveriş: süt/ekmek?" }) == "Alışveriş_ süt_ekmek_.txt", "Backup menu offers create and restore; TXT export suggests the note title as file name");
            title.Text = "Kaydedilemeyen değişiklik";
            string moved = path + ".held"; File.Move(path, moved); Directory.CreateDirectory(path);
            Check(!window.SaveNow(), "Failed write is reported, never falsely marked saved");
            Directory.Delete(path); File.Move(moved, path);
            Check(window.SaveNow(), "Failed save can be retried without losing editor text");
            title.Text = "Yarın için birkaç fikir"; window.SaveNow();
            window.Width = 800; window.Height = 560; Pump();
            Check(body.ActualWidth > 300 && body.ActualHeight > 200, "Editor remains usable at minimum window size");
            window.WindowState = WindowState.Maximized; Pump(); Thread.Sleep(300); Pump();
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            bool placed = WindowPlacement.GetWindowRect(hwnd, out var rect) & WindowPlacement.TryGetWorkArea(hwnd, out var work, out _);
            Check(placed && rect.Left >= work.Left && rect.Top >= work.Top && rect.Right <= work.Right && rect.Bottom <= work.Bottom
                && rect.Bottom - rect.Top >= work.Bottom - work.Top - 2, "Maximized window fills exactly the work area: nothing hidden under the taskbar or off-screen");
            var countText = Find<TextBlock>(window, "CountText");
            var countBottom = countText.PointToScreen(new Point(0, countText.ActualHeight));
            Check(countText.IsVisible && countBottom.Y <= work.Bottom && Find<Button>(window, "LanguageButton").PointToScreen(new Point(0, 0)).Y < work.Bottom, "Sidebar bottom row stays on screen when maximized");
            window.WindowState = WindowState.Normal; Pump();
            window.Width = 1900; window.Height = 1000; Pump();
            var editorArea = Find<Grid>(window, "EditorArea");
            var editorLeft = editorArea.TranslatePoint(new Point(0, 0), window).X;
            double bodyRight = body.TranslatePoint(new Point(body.ActualWidth, 0), window).X;
            Check(body.ActualWidth > window.ActualWidth - 304 - 48 - 12 - 2 && editorLeft < 400 && window.ActualWidth - bodyRight < 20, "Wide window stretches the editor to the right edge with its scrollbar at the edge");
            var textMenu = body.ContextMenu!; textMenu.PlacementTarget = body; textMenu.IsOpen = true; Pump();
            Check(textMenu.Items.Count == 4 && textMenu.Items.OfType<Separator>().Count() == 0 && textMenu.Items.OfType<MenuItem>().Any(m => (string)m.Header == L10n.T("Paste")) && textMenu.ActualWidth > 100, "Editor text boxes get the themed cut/copy/paste menu");
            var menuBmp = new RenderTargetBitmap((int)Math.Ceiling(textMenu.ActualWidth), (int)Math.Ceiling(textMenu.ActualHeight), 96, 96, PixelFormats.Pbgra32); menuBmp.Render(textMenu);
            using (var stream = File.Create(Path.Combine("artifacts", "notlar-menu.png"))) { var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(menuBmp)); png.Save(stream); }
            textMenu.IsOpen = false; Pump();
            var languageMenu = Find<ContextMenu>(window, "LanguageMenu");
            Check(languageMenu.Items.Count == L10n.Languages.Length && languageMenu.Items.OfType<MenuItem>().Single(m => m.IsChecked).Tag as string == "en", "Language menu lists every language and marks the current one");
            var listMenu = Find<ListBox>(window, "NoteList").ContextMenu!;
            Check(listMenu.Items.OfType<MenuItem>().Count() == 5, "Note cards get a themed action menu");
            window.Width = 1160; window.Height = 780; Pump();
            Find<ListBox>(window, "NoteList").SelectedIndex = 0; Pump();
            // Closing only hides the window: the app stays in the notification area so the phone can keep syncing.
            window.Close(); Pump();
            Check(!window.IsVisible && new System.Windows.Interop.WindowInteropHelper(window).Handle != IntPtr.Zero && session.Book.Notes.Count > 0, "Closing the window hides it to the tray instead of quitting");
            window.ShowFromTray(); Pump();
            Check(window.IsVisible, "The tray brings the window back");
            var appSettings = AppSettings.Load(Path.GetDirectoryName(path)!); appSettings.TrayHintShown = true; appSettings.Language = "tr"; appSettings.Save();
            var reloaded = AppSettings.Load(Path.GetDirectoryName(path)!);
            Check(reloaded.TrayHintShown && reloaded.Language == "tr" && L10n.Detect(Path.GetDirectoryName(path)!) == "tr", "Settings keep the language and the tray hint flag together");
            Check(File.ReadAllText("setup/Notlar.iss").Contains(@"CurrentVersion\Run") && File.ReadAllText("setup/Notlar.iss").Contains("--minimized"), "The installer offers start-with-Windows into the tray");
            Check(Find<Button>(window, "DeleteButton").Focusable == false, "Toolbar icon buttons do not keep a focus ring after a click");
            Click(window, "LockButton"); Check(window.LockRequested && !window.IsVisible && body.Text == "", "Lock closes and clears visible plaintext");
            session.Dispose(); Reject(session.Save, "Disposed session cannot save");
            var large = new Notebook { Notes = Enumerable.Range(0, 1000).Select(i => new Note { Title = "Not " + i, Text = new string('x', 1000) }).ToList() };
            var bulk = VaultSession.Create(Path.Combine(root, "bulk.vault"), Password, large); using var bulkSession = bulk.Session;
            bulkSession.Save(); var results = NoteQuery.Find(large, "Not 99", false);
            Check(results.Count == 11, "1000-note search returns correct matches");
            var empty = VaultSession.Create(Path.Combine(root, "empty.vault"), Password); using var emptySession = empty.Session; emptySession.Save();
            var emptyWindow = new MainWindow(emptySession); emptyWindow.Show(); Pump();
            Check(Find<StackPanel>(emptyWindow, "EmptyState").Visibility == Visibility.Visible, "Fresh vault has a real empty state, no fake notes");
            emptyWindow.ExitFromTray();
            {
                L10n.Use("ar");
                var rtl = new MainWindow(emptySession); rtl.Show(); Pump();
                Check(rtl.FlowDirection == FlowDirection.RightToLeft && Find<TextBlock>(rtl, "ListHeading").Text == L10n.T("MyNotes"), "Arabic window mirrors the layout and shows Arabic labels");
                rtl.ExitFromTray();
                L10n.Use("ja");
                var ja = new MainWindow(emptySession); ja.Show(); Pump();
                Check(ja.FlowDirection == FlowDirection.LeftToRight && Find<TextBlock>(ja, "ListHeading").Text == "マイノート", "Japanese window shows Japanese labels");
                ja.ChangeLanguage("de");
                Check(ja.RestartRequested && !ja.IsVisible && L10n.Detect(Path.GetDirectoryName(emptySession.FilePath)!) == "de", "Choosing a language saves it and asks for a restart");
                L10n.Use("en");
            }
            Console.WriteLine("PASS TOTAL: " + checks);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}

