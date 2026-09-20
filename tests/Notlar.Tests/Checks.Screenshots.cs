using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Notlar;
using Notlar.Sync;

// The README pictures: a notebook with readable sample notes, rendered in English. Only runs when NOTLAR_SHOT names
// a folder; `test.cmd` with NOTLAR_SHOT=docs refreshes the images that the READMEs embed.
static partial class Program
{
    static void Screenshots(string root)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NOTLAR_SHOT"))) return;
        string dir = Path.Combine(root, "shots"); Directory.CreateDirectory(dir);
        using var session = VaultSession.CreateDevice(Path.Combine(dir, "notes.vault")); session.Save();
        var today = DateTimeOffset.Now;
        Note Add(string title, string text, double daysAgo, bool pinned = false)
        {
            var n = new Note { Title = title, Text = text, Pinned = pinned, Created = today.AddDays(-daysAgo), Updated = today.AddDays(-daysAgo) };
            session.Book.Notes.Add(n); return n;
        }
        var lake = Add("Weekend at the lake", "Leave early on Saturday, before the traffic.\n\n" + Checklist.DonePrefix + "Book the cabin for two nights\n" + Checklist.OpenPrefix + "Ask whether the canoe is included\n" + Checklist.OpenPrefix + "Pack: swimsuits, the good coffee, a book each\n\nStop at the farm stand on the way back.", 0.02, pinned: true);
        lake.Attachments.Add(session.Attachments.Import(new MemoryStream(Sunset(1200, 800)), "Lake at sunset.jpg", "image/jpeg", (1200, 800)));
        lake.Attachments.Add(session.Attachments.Import(new MemoryStream(new byte[3_300_000]), "Canoe trip.mp4", "video/mp4"));
        Add("Ideas for the garden", "Move the herbs closer to the kitchen door. Try tomatoes along the south wall next spring; the fence gets sun until late afternoon.", 0.2);
        Add("Books to read", "The Left Hand of Darkness\nPiranesi\nA Gentleman in Moscow\nThe Overstory", 1);
        Add("Call the dentist", "Ask about Thursday afternoons.", 3);
        Add("Recipe: lentil soup", "Onion, carrot, celery, a bay leaf. Red lentils, stock, a squeeze of lemon at the end.", 6);
        session.Save();
        var window = new MainWindow(session) { Width = 1160, Height = 780 }; window.Show(); Pump();
        Find<ListBox>(window, "NoteList").SelectedItem = lake; Pump();
        Snapshot(window, "screenshot");
        Click(window, "SelectButton"); var list = Find<ListBox>(window, "NoteList");
        list.SelectedItems.Add(list.Items[1]); list.SelectedItems.Add(list.Items[3]); Pump();
        Snapshot(window, "screenshot-select");
        Click(window, "SelectButton"); Pump();
        using (var service = new SyncService(dir, window))
        {
            service.Settings.Port = 48731; service.Settings.Enabled = true; service.Start();
            var sync = new SyncWindow(window, service); sync.Show(); Pump();
            Snapshot(sync, "screenshot-phone-sync"); sync.Close(); service.Stop();
        }
        window.Close();
        L10n.Use("ar");
        var arabic = new MainWindow(session) { Width = 1160, Height = 780 }; arabic.Show(); Pump();
        Find<ListBox>(arabic, "NoteList").SelectedItem = lake; Pump();
        Snapshot(arabic, "screenshot-arabic"); arabic.Close();
        L10n.Use("en");
    }
    // A warm gradient with a sun: enough of a picture for a thumbnail.
    static byte[] Sunset(int width, int height)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x3A, 0x5A, 0x8C), Color.FromRgb(0xF2, 0xA8, 0x5A), 90), null, new Rect(0, 0, width, height));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0xE8, 0xA0)), null, new Point(width * 0.68, height * 0.42), width * 0.07, width * 0.07);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1E, 0x2E, 0x48)), null, new Rect(0, height * 0.72, width, height * 0.28));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 }; encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
}
