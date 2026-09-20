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

// Vault, gate and attachment flows that run without the main window.
static partial class Program
{
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
        using (var reopened = VaultSession.OpenDevice(path)) Check(reopened.Book.Notes.Any(n => n.Title == "Türkçe örnek" && n.Text == importedText.Replace("\r\n", "\n")), "Imported Unicode survives encrypted reopen; line endings are normalised for the phone");
        Find<TextBox>(window, "BodyInput").Text += "Düzenleme"; window.SaveNow();
        Check(File.ReadAllText(importPath) == importedText, "Editing imported note never overwrites original TXT");
        string exportPath = Path.Combine(root, "export.txt");
        TextFiles.Write(exportPath, new Note { Title = "Title", Text = importedText });
        Check(File.ReadAllBytes(exportPath).SequenceEqual(new UTF8Encoding(false).GetBytes(importedText)), "Export writes interoperable UTF-8 with exact body and line endings");
        File.WriteAllText(importPath, importedText, Encoding.Unicode);
        Check(TextFiles.Read(importPath).Text == importedText.Replace("\r\n", "\n"), "UTF-16 BOM Notepad file imports correctly");
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
}
