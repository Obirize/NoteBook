using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Notlar;

// Photos and videos on a note: adding, pasting, the thumbnail tiles, opening, saving copies and removing.
public partial class MainWindow
{
    // ----- Photos and videos -----
    private void AttachClick(object sender, RoutedEventArgs e)
    {
        if (!CanAttach) return;
        string patterns = string.Join(";", AttachmentStore.ImageExtensions.Concat(AttachmentStore.VideoExtensions).Select(x => "*" + x));
        var dialog = new OpenFileDialog { Title = L10n.T("AttachAdd"), Filter = L10n.T("AttachFilterMedia") + " (" + patterns + ")|" + patterns + "|" + L10n.T("AllFiles") + " (*.*)|*.*", Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        _ = AttachFilesAsync(dialog.FileNames);
    }
    // Encrypts each file into the store off the UI thread, then adds it to the open note. Returns how many succeeded.
    public async Task<int> AttachFilesAsync(IEnumerable<string> paths)
    {
        if (!CanAttach || !SaveNow()) return 0;
        var note = current!; var files = paths.ToList(); var added = new List<Attachment>(); string? error = null;
        importing = true; UpdateState(); StatusText.Text = L10n.T("Saving");
        await Task.Run(() =>
        {
            foreach (string path in files)
            {
                try { added.Add(session.Attachments.Import(path)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.Cryptography.CryptographicException)
                { error = ex is InvalidDataException ? ex.Message : L10n.T("AttachmentFailed"); }
            }
        });
        // Resume explicitly on the UI thread: without a synchronization context (tests) the continuation may land elsewhere.
        await Dispatcher.InvokeAsync(() => { importing = false; note.Attachments.AddRange(added); FinishAttach(note, added.Count, error); });
        return added.Count;
    }
    private void FinishAttach(Note note, int count, string? error)
    {
        if (count > 0)
        {
            note.Updated = DateTimeOffset.UtcNow; note.Revision++; dirty = true;
            if (!SaveNow()) { UpdateState(); return; }
            if (current == note) RenderAttachments();
        }
        UpdateState();
        StatusText.Text = error ?? (count == 1 ? L10n.T("AttachmentAdded") : L10n.T("AttachmentAddedMany", count));
    }
    // A picture (or media files) on the clipboard becomes an attachment; text on the clipboard keeps normal paste.
    public bool PasteAttachment()
    {
        if (!CanAttach) return false;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().Where(AttachmentStore.IsSupported).ToArray();
                if (files.Length == 0) return false;
                _ = AttachFilesAsync(files); return true;
            }
            if (Clipboard.ContainsText() || !Clipboard.ContainsImage()) return false;
            var image = Clipboard.GetImage();
            if (image == null) return false;
            return AttachImage(image);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException) { return false; }
    }
    public bool AttachImage(BitmapSource image)
    {
        if (!CanAttach || !SaveNow()) return false;
        var note = current!;
        using var buffer = new MemoryStream();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(buffer);
        buffer.Position = 0;
        try { note.Attachments.Add(session.Attachments.Import(buffer, "Image " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss") + ".png", "image/png", (image.PixelWidth, image.PixelHeight))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { FinishAttach(note, 0, L10n.T("AttachmentFailed")); return false; }
        FinishAttach(note, 1, null); return true;
    }
    public bool RemoveAttachment(Attachment attachment)
    {
        if (!CanAttach || !current!.Attachments.Contains(attachment) || !SaveNow()) return false;
        if (!ConfirmRemoveAttachment(L10n.T("ConfirmRemoveAttachment", attachment.Name))) return false;
        current.Attachments.Remove(attachment); thumbnails.Remove(attachment.Id);
        Touch(); sweep = true;
        if (!SaveNow()) return false;
        RenderAttachments(); UpdateState();
        StatusText.Text = L10n.T("AttachmentRemoved");
        return true;
    }
    public bool SaveAttachmentCopy(Attachment attachment, string? targetPath = null)
    {
        if (targetPath == null)
        {
            var dialog = new SaveFileDialog { Title = L10n.T("AttachmentSaveTitle"), FileName = attachment.Name, Filter = L10n.T("AllFiles") + " (*.*)|*.*", DefaultExt = Path.GetExtension(attachment.Name) };
            if (dialog.ShowDialog(this) != true) return false;
            targetPath = dialog.FileName;
        }
        try { session.Attachments.Export(attachment, targetPath); StatusText.Text = L10n.T("AttachmentSaved"); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or ArgumentException)
        { StatusText.Text = L10n.T(ex is System.Security.Cryptography.CryptographicException or FileNotFoundException ? "AttachmentOpenFailed" : "AttachmentSaveFailed"); return false; }
    }
    // Every attachment of the open (or every checked) note goes into a folder of the user's choosing; several notes
    // get a subfolder each. Names are kept, with a counter when two files would collide.
    private void SaveAttachmentsClick(object sender, RoutedEventArgs e)
    {
        var notes = Targets().Where(n => n.Attachments.Count > 0).ToList();
        if (notes.Count == 0 || !SaveNow()) return;
        var dialog = new OpenFolderDialog { Title = L10n.T("SaveAllAttachments").TrimEnd('…', '.') };
        if (dialog.ShowDialog(this) != true) return;
        int saved = SaveAttachments(notes, dialog.FolderName);
        StatusText.Text = saved >= 0 ? L10n.T("AttachmentsSavedMany", saved, Path.GetFileName(dialog.FolderName)) : L10n.T("AttachmentSaveFailed");
    }
    public int SaveAttachments(IReadOnlyList<Note> notes, string folder)
    {
        int saved = 0;
        try
        {
            foreach (var note in notes)
            {
                string target = notes.Count == 1 ? folder : Path.Combine(folder, Path.GetFileNameWithoutExtension(TextFiles.SuggestedName(note)));
                saved += ExportInto(note.Attachments, target);
            }
            return saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or ArgumentException) { return -1; }
    }
    private int ExportInto(IEnumerable<Attachment> attachments, string folder)
    {
        Directory.CreateDirectory(folder); int saved = 0;
        foreach (var attachment in attachments)
        {
            if (!session.Attachments.Exists(attachment)) continue;
            string name = string.Concat(attachment.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            if (string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(name))) name = attachment.Id[..8] + Path.GetExtension(name);
            string path = Path.Combine(folder, name);
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, Path.GetFileNameWithoutExtension(name) + " (" + i + ")" + Path.GetExtension(name));
            session.Attachments.Export(attachment, path); saved++;
        }
        return saved;
    }

    // ----- Picking several attachments: the circle on a tile (or Ctrl+click) checks it; the bar above the grid acts on them -----
    private readonly HashSet<string> picked = [];
    public IReadOnlyCollection<string> PickedAttachments => picked;
    public void PickAttachment(Attachment attachment, bool? on = null)
    {
        if (on ?? !picked.Contains(attachment.Id)) picked.Add(attachment.Id); else picked.Remove(attachment.Id);
        RenderAttachments();
    }
    private void AttachmentCancelClick(object sender, RoutedEventArgs e) { picked.Clear(); RenderAttachments(); }
    private List<Attachment> PickedList() => current?.Attachments.Where(a => picked.Contains(a.Id)).ToList() ?? [];
    private void AttachmentSaveClick(object sender, RoutedEventArgs e)
    {
        var chosen = PickedList(); if (chosen.Count == 0 || !SaveNow()) return;
        var dialog = new OpenFolderDialog { Title = L10n.T("SaveSelectedAttachments").TrimEnd('…', '.') };
        if (dialog.ShowDialog(this) != true) return;
        SaveAttachmentCopies(chosen, dialog.FolderName);
    }
    public bool SaveAttachmentCopies(IReadOnlyList<Attachment> chosen, string folder)
    {
        try { int saved = ExportInto(chosen, folder); StatusText.Text = L10n.T("AttachmentsSavedMany", saved, Path.GetFileName(folder)); picked.Clear(); RenderAttachments(); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or ArgumentException) { StatusText.Text = L10n.T("AttachmentSaveFailed"); return false; }
    }
    private void AttachmentRemoveClick(object sender, RoutedEventArgs e) => RemovePicked();
    public bool RemovePicked()
    {
        var chosen = PickedList();
        if (!CanAttach || chosen.Count == 0 || !SaveNow()) return false;
        if (!ConfirmRemoveAttachment(L10n.T("ConfirmRemoveAttachments", chosen.Count))) return false;
        foreach (var a in chosen) { current!.Attachments.Remove(a); thumbnails.Remove(a.Id); }
        picked.Clear(); Touch(); sweep = true;
        if (!SaveNow()) return false;
        RenderAttachments(); UpdateState();
        StatusText.Text = L10n.T("AttachmentsRemoved", chosen.Count);
        return true;
    }
    private void OpenAttachment(Attachment attachment)
    {
        if (!SaveNow()) return;
        if (!session.Attachments.Exists(attachment)) { StatusText.Text = L10n.T("AttachmentOpenFailed"); return; }
        var viewer = new AttachmentWindow(this, session.Attachments, attachment) { SaveCopy = () => SaveAttachmentCopy(attachment) };
        viewer.ShowDialog();
    }
    private void RenderAttachments()
    {
        AttachmentPanel.Children.Clear();
        var note = current;
        if (note == null) picked.Clear(); else picked.RemoveWhere(id => !note.Attachments.Any(a => a.Id == id));
        AttachmentScroll.Visibility = note != null && note.Attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AttachmentBar.Visibility = picked.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AttachmentCount.Text = L10n.T("AttachmentsSelected", picked.Count);
        AttachmentRemoveButton.Visibility = trash ? Visibility.Collapsed : Visibility.Visible;
        if (note == null) return;
        foreach (var attachment in note.Attachments) AttachmentPanel.Children.Add(BuildTile(attachment));
    }
    private FrameworkElement BuildTile(Attachment attachment)
    {
        const double size = 132;
        bool present = session.Attachments.Exists(attachment);
        var icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        var content = new Grid { Clip = new RectangleGeometry(new Rect(0, 0, size, size), 10, 10) };
        var icon = new TextBlock { Text = !present ? "\uE7BA" : attachment.IsVideo ? "\uE714" : "\uEB9F", FontFamily = icons, FontSize = 30, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var picture = new Image { Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        var caption = new Border { VerticalAlignment = VerticalAlignment.Bottom, Padding = new Thickness(9, 14, 9, 7), Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(200, 0, 0, 0), 90) };
        var name = new TextBlock { Text = attachment.Name, FontSize = 11, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
        var detail = new TextBlock { Text = present ? attachment.SizeLabel : L10n.T("AttachmentMissing"), FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC7, 0xCC)) };
        caption.Child = new StackPanel { Children = { name, detail } };
        content.Children.Add(icon); content.Children.Add(picture); content.Children.Add(caption);
        bool isPicked = picked.Contains(attachment.Id);
        // The check circle: shown on hover and whenever a selection exists; filled with the accent when this tile is picked.
        var check = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1.5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8), Cursor = Cursors.Hand, Visibility = picked.Count > 0 ? Visibility.Visible : Visibility.Hidden, ToolTip = L10n.T("PickAttachmentHint"), Child = new TextBlock { Text = "\uE73E", FontFamily = icons, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0x29, 0x24, 0x1C)), Visibility = isPicked ? Visibility.Visible : Visibility.Collapsed } };
        check.BorderBrush = Brushes.White; check.Background = isPicked ? (Brush)FindResource("Accent") : new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));
        check.MouseLeftButtonUp += (_, e) => { PickAttachment(attachment); e.Handled = true; };
        content.Children.Add(check);
        var tile = new Border { Width = size, Height = size, Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(isPicked ? 2 : 1), Child = content, Cursor = Cursors.Hand, Focusable = true, Tag = attachment, ToolTip = attachment.Name + " · " + attachment.SizeLabel + (attachment.Width > 0 ? " · " + attachment.Width + "×" + attachment.Height : "") };
        tile.SetResourceReference(Border.BackgroundProperty, "Surface"); tile.SetResourceReference(Border.BorderBrushProperty, isPicked ? "Accent" : "Rule");
        System.Windows.Automation.AutomationProperties.SetName(tile, attachment.Name);
        tile.MouseEnter += (_, _) => { tile.SetResourceReference(Border.BorderBrushProperty, "Accent"); check.Visibility = Visibility.Visible; };
        tile.MouseLeave += (_, _) => { tile.SetResourceReference(Border.BorderBrushProperty, isPicked ? "Accent" : "Rule"); if (picked.Count == 0) check.Visibility = Visibility.Hidden; };
        tile.MouseLeftButtonUp += (_, e) => { if (Keyboard.Modifiers == ModifierKeys.Control || picked.Count > 0) PickAttachment(attachment); else OpenAttachment(attachment); };
        tile.KeyDown += (_, e) => { if (e.Key == Key.Enter) { OpenAttachment(attachment); e.Handled = true; } else if (e.Key == Key.Space) { PickAttachment(attachment); e.Handled = true; } else if (e.Key == Key.Delete && !trash) { RemoveAttachment(attachment); e.Handled = true; } };
        var menu = new ContextMenu();
        var open = new MenuItem { Header = L10n.T("AttachmentOpen") }; open.Click += (_, _) => OpenAttachment(attachment); menu.Items.Add(open);
        var save = new MenuItem { Header = L10n.T("AttachmentSaveAs") }; save.Click += (_, _) => SaveAttachmentCopy(attachment); menu.Items.Add(save);
        var pick = new MenuItem { Header = L10n.T(isPicked ? "UnpickAttachment" : "PickAttachment") }; pick.Click += (_, _) => PickAttachment(attachment); menu.Items.Add(pick);
        var saveAll = new MenuItem { Header = L10n.T("SaveAllAttachments") }; saveAll.Click += (_, _) => SaveAttachmentsClick(this, new RoutedEventArgs()); menu.Items.Add(saveAll);
        if (!trash)
        {
            menu.Items.Add(new Separator());
            var remove = new MenuItem { Header = L10n.T("AttachmentRemove"), Style = (Style)FindResource("DangerMenuItem") }; remove.Click += (_, _) => RemoveAttachment(attachment); menu.Items.Add(remove);
        }
        tile.ContextMenu = menu;
        if (present && attachment.IsImage) LoadThumbnail(attachment, picture, icon);
        return tile;
    }
    private void LoadThumbnail(Attachment attachment, Image target, TextBlock placeholder)
    {
        if (thumbnails.TryGetValue(attachment.Id, out var cached)) { target.Source = cached; placeholder.Visibility = Visibility.Collapsed; return; }
        _ = Task.Run(() =>
        {
            BitmapSource bitmap;
            try
            {
                using var stream = new MemoryStream(session.Attachments.ReadAll(attachment));
                var image = new BitmapImage();
                image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile; image.StreamSource = stream;
                if (attachment.Width >= attachment.Height) image.DecodePixelWidth = 264; else image.DecodePixelHeight = 264;
                image.EndInit(); image.Freeze();
                bitmap = image;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or ObjectDisposedException) { return; }
            // Decoding ran on the pool; the control belongs to the UI thread.
            Dispatcher.InvokeAsync(() =>
            {
                if (thumbnails.Count > 200) thumbnails.Clear();
                thumbnails[attachment.Id] = bitmap;
                target.Source = bitmap; placeholder.Visibility = Visibility.Collapsed;
            });
        });
    }
}
