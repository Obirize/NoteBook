using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Notlar;

public partial class MainWindow : Window
{
    private readonly VaultSession session;
    private Note? current;
    private List<Note> lastDeleted = [];
    private bool loading, dirty, trash, selecting, purged;
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly DispatcherTimer idleTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private DateTime lastInput = DateTime.UtcNow;
    public bool LockRequested { get; private set; }
    // Permanent deletion asks before proceeding; tests replace this to avoid a modal dialog.
    public Func<string, bool> ConfirmDestructive { get; set; }
    private Updater.Release? update;
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    public MainWindow(VaultSession vault)
    {
        session = vault;
        ConfirmDestructive = message => MessageBox.Show(this, message, "Kalıcı sil", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        InitializeComponent();
        LockButton.Visibility = session.IsDeviceProtected ? Visibility.Collapsed : Visibility.Visible;
        saveTimer.Tick += (_, _) => SaveNow();
        idleTimer.Tick += (_, _) => { if (DateTime.UtcNow - lastInput >= TimeSpan.FromMinutes(5)) Lock(); };
        PreviewKeyDown += Shortcut;
        PreviewMouseDown += (_, _) => lastInput = DateTime.UtcNow;
        PreviewMouseMove += (_, _) => lastInput = DateTime.UtcNow;
        Closing += WindowClosing;
        Closed += (_, _) =>
        {
            saveTimer.Stop(); idleTimer.Stop(); SystemEvents.SessionSwitch -= SessionSwitch; SystemEvents.PowerModeChanged -= PowerChanged;
            loading = true; TitleInput.Clear(); BodyInput.Clear(); ClearUndo(BodyInput); ClearUndo(TitleInput); SearchInput.Clear(); NoteList.ItemsSource = null;
            current = null; lastDeleted = [];
        };
        SourceInitialized += (_, _) => { int rounded = 2; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref rounded, 4); };
        SystemEvents.SessionSwitch += SessionSwitch; SystemEvents.PowerModeChanged += PowerChanged;
        PurgeExpired();
        RefreshList(session.Book.Notes.Where(n => !n.Deleted).OrderByDescending(n => n.Updated).FirstOrDefault()?.Id);
        if (!session.IsDeviceProtected) idleTimer.Start();
        VersionText.Text = "Bu bilgisayarda · " + Updater.CurrentLabel;
        if (Updater.Enabled(Path.GetDirectoryName(session.FilePath)!)) Loaded += (_, _) => _ = CheckUpdates();
    }
    private async Task CheckUpdates()
    {
        try
        {
            update = await Updater.CheckAsync();
            if (update == null || !IsLoaded) return;
            UpdateButton.Content = "Sürüm " + update.Version.ToString(3) + " hazır · Güncelle";
            UpdateButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException) { }
    }
    private async void UpdateClick(object sender, RoutedEventArgs e)
    {
        if (update == null || !SaveNow()) return;
        UpdateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(p => StatusText.Text = "Güncelleme indiriliyor… %" + (int)(p * 100));
            string installer = await Updater.DownloadAsync(update, progress);
            StatusText.Text = "Güncelleme kuruluyor; uygulama yeniden açılacak.";
            Updater.Install(installer);
            Close();
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { StatusText.Text = ex is InvalidDataException ? ex.Message : "Güncelleme indirilemedi. Daha sonra yeniden deneyin."; UpdateButton.IsEnabled = true; }
    }
    private void PurgeExpired()
    {
        var now = DateTimeOffset.UtcNow;
        bool changed = TrashPolicy.InitializeDates(session.Book, now);
        var expired = TrashPolicy.Expired(session.Book, now);
        if (expired.Count > 0) { TrashPolicy.Purge(session.Book, expired); purged = true; changed = true; }
        if (changed) { dirty = true; SaveNow(); }
    }
    private void SessionSwitch(object sender, SessionSwitchEventArgs e) { if (e.Reason == SessionSwitchReason.SessionLock) Dispatcher.BeginInvoke(() => { if (session.IsDeviceProtected) SaveNow(); else Lock(); }); }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Suspend) Dispatcher.BeginInvoke(() => { if (session.IsDeviceProtected) SaveNow(); else Lock(); }); }
    private void Shortcut(object sender, KeyEventArgs e)
    {
        lastInput = DateTime.UtcNow;
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Delete && NoteList.IsKeyboardFocusWithin)
        { if (trash) PurgeClick(this, e); else DeleteClick(this, e); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Escape && selecting) { SetSelecting(false); e.Handled = true; return; }
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.S) { ExportClick(this, e); e.Handled = true; return; }
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.O) { ImportClick(this, e); e.Handled = true; }
        if (e.Key == Key.N) { NewNote(); e.Handled = true; }
        if (e.Key == Key.A && selecting) { SelectAllClick(this, e); e.Handled = true; }
        if (e.Key == Key.K || e.Key == Key.F) { SearchInput.Focus(); SearchInput.SelectAll(); e.Handled = true; }
        if (e.Key == Key.S) { SaveNow(); e.Handled = true; }
        if (e.Key == Key.L && !session.IsDeviceProtected) { Lock(); e.Handled = true; }
    }
    private void RefreshList(string? selectedId = null)
    {
        if (NoteList == null) return;
        selectedId ??= current?.Id;
        var notes = NoteQuery.Find(session.Book, SearchInput.Text, trash);
        var scroll = FindVisual<ScrollViewer>(NoteList);
        double offset = scroll?.VerticalOffset ?? 0;
        loading = true;
        NoteList.ItemsSource = notes;
        Note? selected = null;
        if (selecting) NoteList.UnselectAll();
        else { selected = notes.FirstOrDefault(n => n.Id == selectedId) ?? notes.FirstOrDefault(); NoteList.SelectedItem = selected; }
        loading = false;
        // Autosave refreshes the list while reading; keep the reader's place instead of jumping to the top.
        if (scroll != null && offset > 0) { NoteList.UpdateLayout(); scroll.ScrollToVerticalOffset(offset); }
        if (current != selected) OpenNote(selected);
        CountText.Text = notes.Count + " not";
        NoResults.Visibility = notes.Count == 0 && SearchInput.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AllFilter.SetResourceReference(BackgroundProperty, trash ? "Side" : "Surface");
        TrashFilter.SetResourceReference(BackgroundProperty, trash ? "Surface" : "Side");
        ListHeading.Text = trash ? "Son silinenler" : "Notlarım";
        UpdateState();
    }
    private void OpenNote(Note? note)
    {
        loading = true; current = note;
        TitleInput.Text = note?.Title ?? ""; BodyInput.Text = note?.Text ?? "";
        ClearUndo(TitleInput); ClearUndo(BodyInput);
        BodyInput.CaretIndex = 0;
        loading = false; UpdateState();
    }
    private void UpdateState()
    {
        bool exists = current != null && !selecting;
        int count = selecting ? NoteList.SelectedItems.Count : 0;
        EditorArea.Visibility = exists ? Visibility.Visible : Visibility.Collapsed;
        SelectionState.Visibility = selecting ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = exists || selecting ? Visibility.Collapsed : Visibility.Visible;
        NoteActions.Visibility = exists ? Visibility.Visible : Visibility.Collapsed;
        TitleInput.IsReadOnly = BodyInput.IsReadOnly = trash;
        TitleHint.Visibility = TitleInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        BodyHint.Visibility = BodyInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PinButton.Visibility = DeleteButton.Visibility = ExportButton.Visibility = trash ? Visibility.Collapsed : Visibility.Visible;
        RestoreButton.Visibility = PurgeButton.Visibility = trash ? Visibility.Visible : Visibility.Collapsed;
        PinButton.ToolTip = current?.Pinned == true ? "Sabitlemeyi kaldır" : "Notu sabitle";
        System.Windows.Automation.AutomationProperties.SetName(PinButton, (string)PinButton.ToolTip);
        PinButton.SetResourceReference(ForegroundProperty, current?.Pinned == true ? "Accent" : "Muted");
        DateText.Text = current?.Updated.LocalDateTime.ToString("d MMMM yyyy, HH:mm", CultureInfo.GetCultureInfo("tr-TR")) ?? "";
        EmptyHeading.Text = trash ? "Burada hiçbir şey yok." : SearchInput.Text.Length > 0 ? "Bir başka kelime deneyin." : "Bir düşünceyle başlar.";
        EmptyDescription.Text = trash ? "Sildiğiniz notlar " + TrashPolicy.RetentionDays + " gün boyunca burada kalır ve geri yüklenebilir." : SearchInput.Text.Length > 0 ? "Başlıklarda ve not içeriklerinde arama yapabilirsiniz." : "Küçük bir fikir, uzun bir gün, unutmamak istediğiniz bir şey.";
        EmptyNew.Content = session.Book.Notes.Any(n => !n.Deleted) ? "Yeni not yazın" : "İlk notunuzu yazın";
        EmptyNew.Visibility = trash ? Visibility.Collapsed : Visibility.Visible;
        TrashNotice.Text = "Silinen notlar " + TrashPolicy.RetentionDays + " gün sonra kalıcı olarak silinir.";
        TrashNotice.Visibility = trash ? Visibility.Visible : Visibility.Collapsed;
        SelectButton.Content = selecting ? "Vazgeç" : "Seç";
        SelectButton.Visibility = selecting || NoteList.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectAllButton.Visibility = selecting && NoteList.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectAllButton.Content = count > 0 && count == NoteList.Items.Count ? "Seçimi kaldır" : "Tümünü seç";
        SelectionCount.Text = selecting ? count == 0 ? "Seçili not yok" : count + " not seçildi" : "";
        SelectionHeading.Text = count == 0 ? "Not seçin" : count + " not seçildi";
        SelectionDescription.Text = trash ? "Seçili notları geri yükleyebilir veya geri alınamaz şekilde kalıcı olarak silebilirsiniz." : "Seçili notlar son silinenlere taşınır; " + TrashPolicy.RetentionDays + " gün içinde geri alabilirsiniz.";
        BulkDeleteButton.Visibility = trash ? Visibility.Collapsed : Visibility.Visible;
        BulkRestoreButton.Visibility = BulkPurgeButton.Visibility = trash ? Visibility.Visible : Visibility.Collapsed;
        BulkDeleteButton.IsEnabled = BulkRestoreButton.IsEnabled = BulkPurgeButton.IsEnabled = count > 0;
    }
    private void Touch()
    {
        if (current == null) return;
        current.Updated = DateTimeOffset.UtcNow; current.Revision++;
        dirty = true; saveTimer.Stop(); saveTimer.Start(); StatusText.Text = "Kaydediliyor…";
    }
    private static void ClearUndo(TextBox box) { box.IsUndoEnabled = false; box.IsUndoEnabled = true; }
    private void EditorChanged(object sender, TextChangedEventArgs e)
    {
        if (loading || current == null || trash) return;
        current.Title = TitleInput.Text; current.Text = BodyInput.Text;
        Touch(); UpdateState();
    }
    public bool SaveNow()
    {
        saveTimer.Stop();
        if (!dirty) return true;
        try { session.Save(purged); purged = false; dirty = false; StatusText.Text = "Şifreli olarak kaydedildi"; RefreshList(); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { StatusText.Text = "Kaydedilemedi. Tekrar denemek için Ctrl+S."; return false; }
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        if (selecting) { UpdateState(); return; }
        var selected = NoteList.SelectedItem as Note;
        if (!SaveNow()) { loading = true; NoteList.SelectedItem = current; loading = false; return; }
        OpenNote(selected);
        // Saving can refresh the list before this selection is committed.
        loading = true; NoteList.SelectedItem = selected; loading = false;
    }
    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchHint == null || loading) return;
        SearchHint.Visibility = SearchInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearSearch.Visibility = SearchInput.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (SaveNow()) RefreshList();
    }
    // The notes an action applies to: the checked cards while selecting, otherwise the open note.
    private List<Note> Targets() => selecting ? NoteList.SelectedItems.Cast<Note>().ToList() : current != null ? [current] : [];
    private void SetSelecting(bool on)
    {
        if (selecting == on) return;
        selecting = on;
        loading = true;
        NoteList.SelectionMode = on ? SelectionMode.Multiple : SelectionMode.Single;
        NoteList.UnselectAll();
        loading = false;
        if (on) { OpenNote(null); UpdateState(); }
        else RefreshList();
    }
    private void SelectModeClick(object sender, RoutedEventArgs e) { if (SaveNow()) SetSelecting(!selecting); }
    private void SelectAllClick(object sender, RoutedEventArgs e)
    {
        if (!selecting) return;
        loading = true;
        if (NoteList.SelectedItems.Count == NoteList.Items.Count) NoteList.UnselectAll(); else NoteList.SelectAll();
        loading = false; UpdateState();
    }
    private void NewNote()
    {
        if (!SaveNow()) return;
        if (selecting) SetSelecting(false);
        trash = false; loading = true; SearchInput.Clear(); loading = false;
        SearchHint.Visibility = Visibility.Visible; ClearSearch.Visibility = Visibility.Collapsed;
        var note = new Note(); session.Book.Notes.Add(note);
        dirty = true; RefreshList(note.Id); SaveNow(); TitleInput.Focus();
    }
    private void NewClick(object sender, RoutedEventArgs e) => NewNote();
    // Used by the shell "new note" entry and by a second launch forwarding --new.
    public void CreateNote() => NewNote();
    private void ListMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Act on the card under the pointer, unless several cards are checked in select mode.
        var item = (e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>();
        if (item?.DataContext is Note note && !(selecting && NoteList.SelectedItems.Count > 1))
        {
            loading = true;
            if (selecting) { NoteList.UnselectAll(); item.IsSelected = true; }
            else NoteList.SelectedItem = note;
            loading = false;
            if (selecting) UpdateState(); else { if (!SaveNow()) { e.Handled = true; return; } OpenNote(note); }
        }
        var targets = Targets();
        if (targets.Count == 0) { e.Handled = true; return; }
        bool single = targets.Count == 1 && !trash;
        MenuPin.Visibility = MenuExport.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
        MenuPin.Header = targets[0].Pinned ? "Sabitlemeyi kaldır" : "Sabitle";
        MenuDelete.Visibility = trash ? Visibility.Collapsed : Visibility.Visible;
        MenuRestore.Visibility = MenuPurge.Visibility = trash ? Visibility.Visible : Visibility.Collapsed;
        MenuDelete.Header = targets.Count == 1 ? "Son silinenlere taşı" : targets.Count + " notu son silinenlere taşı";
        MenuRestore.Header = targets.Count == 1 ? "Geri yükle" : targets.Count + " notu geri yükle";
        MenuPurge.Header = targets.Count == 1 ? "Kalıcı sil…" : targets.Count + " notu kalıcı sil…";
    }
    private void ClearSearchClick(object sender, RoutedEventArgs e) { SearchInput.Clear(); SearchInput.Focus(); }
    private void AllClick(object sender, RoutedEventArgs e) { if (!SaveNow()) return; trash = false; RefreshList(); }
    private void TrashClick(object sender, RoutedEventArgs e) { if (!SaveNow()) return; trash = true; RefreshList(); }
    private void PinClick(object sender, RoutedEventArgs e) { if (current == null || selecting) return; current.Pinned = !current.Pinned; Touch(); SaveNow(); }

    private void DeleteClick(object sender, RoutedEventArgs e)
    {
        var notes = Targets();
        if (notes.Count == 0 || trash || !SaveNow()) return;
        TrashPolicy.Delete(notes, DateTimeOffset.UtcNow); dirty = true;
        if (!SaveNow()) return;
        lastDeleted = notes; UndoDelete.Visibility = Visibility.Visible;
        StatusText.Text = notes.Count == 1 ? "Not, son silinenlere taşındı." : notes.Count + " not son silinenlere taşındı.";
        if (selecting) SetSelecting(false);
    }
    private void RestoreClick(object sender, RoutedEventArgs e)
    {
        var notes = Targets();
        if (notes.Count == 0 || !trash || !SaveNow()) return;
        TrashPolicy.Restore(notes, DateTimeOffset.UtcNow); dirty = true;
        if (!SaveNow()) return;
        trash = false; selecting = false; NoteList.SelectionMode = SelectionMode.Single;
        loading = true; SearchInput.Clear(); loading = false;
        SearchHint.Visibility = Visibility.Visible; ClearSearch.Visibility = Visibility.Collapsed;
        RefreshList(notes[0].Id);
        StatusText.Text = notes.Count == 1 ? "Not geri yüklendi." : notes.Count + " not geri yüklendi.";
        lastDeleted = []; UndoDelete.Visibility = Visibility.Collapsed;
    }
    private void PurgeClick(object sender, RoutedEventArgs e)
    {
        var notes = Targets();
        if (notes.Count == 0 || !trash || !SaveNow()) return;
        string message = notes.Count == 1
            ? "“" + notes[0].DisplayTitle + "” kalıcı olarak silinsin mi?\n\nBu işlem geri alınamaz."
            : notes.Count + " not kalıcı olarak silinsin mi?\n\nBu işlem geri alınamaz.";
        if (!ConfirmDestructive(message)) return;
        TrashPolicy.Purge(session.Book, notes); purged = true; dirty = true;
        if (!SaveNow()) return;
        StatusText.Text = notes.Count == 1 ? "Not kalıcı olarak silindi." : notes.Count + " not kalıcı olarak silindi.";
        lastDeleted.RemoveAll(notes.Contains);
        if (lastDeleted.Count == 0) UndoDelete.Visibility = Visibility.Collapsed;
        if (selecting) SetSelecting(false);
    }
    private void UndoDeleteClick(object sender, RoutedEventArgs e)
    {
        var notes = lastDeleted.Where(n => n.Deleted && session.Book.Notes.Contains(n)).ToList();
        if (notes.Count == 0) { UndoDelete.Visibility = Visibility.Collapsed; return; }
        TrashPolicy.Restore(notes, DateTimeOffset.UtcNow); dirty = true;
        if (!SaveNow()) return;
        trash = false; if (selecting) SetSelecting(false);
        loading = true; SearchInput.Clear(); loading = false;
        SearchHint.Visibility = Visibility.Visible; ClearSearch.Visibility = Visibility.Collapsed;
        RefreshList(notes[0].Id); UndoDelete.Visibility = Visibility.Collapsed; lastDeleted = [];
    }
    private void TitleKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { BodyInput.Focus(); e.Handled = true; } }
    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) return result;
            var nested = FindVisual<T>(child); if (nested != null) return nested;
        }
        return null;
    }
    public bool ImportTextFile(string path)
    {
        if (!SaveNow()) return false;
        var note = TextFiles.Read(path);
        session.Book.Notes.Add(note); dirty = true;
        if (selecting) SetSelecting(false);
        trash = false; loading = true; SearchInput.Clear(); loading = false;
        SearchHint.Visibility = Visibility.Visible; ClearSearch.Visibility = Visibility.Collapsed;
        RefreshList(note.Id);
        bool saved = SaveNow();
        if (saved) StatusText.Text = "TXT dosyası notlara eklendi. Orijinal dosya değişmedi.";
        return saved;
    }
    private void ImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "TXT dosyasını notlara ekle", Filter = "Metin dosyası (*.txt)|*.txt", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try { ImportTextFile(dialog.FileName); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { StatusText.Text = ex is InvalidDataException ? ex.Message : "Dosya açılamadı. Dosyayı ve erişim izinlerini kontrol edin."; }
    }
    private void ExportClick(object sender, RoutedEventArgs e)
    {
        if (current == null || selecting || !SaveNow()) return;
        var note = current;
        var dialog = new SaveFileDialog { Title = "TXT olarak kaydet (şifresiz kopya)", Filter = "UTF-8 metin dosyası (*.txt)|*.txt", DefaultExt = ".txt", FileName = TextFiles.SuggestedName(note) };
        if (dialog.ShowDialog(this) != true) return;
        try { TextFiles.Write(dialog.FileName, note); StatusText.Text = "TXT kopyası kaydedildi (şifresiz). Notunuz kasada korunuyor."; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { StatusText.Text = "TXT kaydedilemedi. Konumu ve dosya uzantısını kontrol edin."; }
    }
    private void BackupClick(object sender, RoutedEventArgs e)
    {
        if (!SaveNow()) return;
        var dialog = new SaveFileDialog { Title = session.IsDeviceProtected ? "Bu Windows hesabında açılabilen şifreli yedeği kaydet" : "Şifreli yedeği kaydet", Filter = "Şifreli Notlar kasası (*.vault)|*.vault", FileName = "Notlar-" + DateTime.Now.ToString("yyyy-MM-dd") + ".vault" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            string target = Path.GetFullPath(dialog.FileName);
            if (string.Equals(target, session.FilePath, StringComparison.OrdinalIgnoreCase) || target.StartsWith(session.FilePath + ".", StringComparison.OrdinalIgnoreCase))
            { StatusText.Text = "Yedek için kasadan farklı bir konum seçin."; return; }
            File.Copy(session.FilePath, target, true); StatusText.Text = "Şifreli yedek kaydedildi.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusText.Text = "Yedek kaydedilemedi. Başka bir konum deneyin."; }
    }
    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (!SaveNow()) { e.Cancel = true; LockRequested = false; MessageBox.Show(this, "Son değişiklik kaydedilemedi. Notlarınız açık tutuluyor. Disk alanını ve klasör izinlerini kontrol edip Ctrl+S ile yeniden deneyin.", "Notlar kaydedilemedi"); }
    }
    private void Lock() { if (SaveNow()) { LockRequested = true; Close(); } }
    private void LockClick(object sender, RoutedEventArgs e) => Lock();
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
