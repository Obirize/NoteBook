using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        ConfirmDestructive = message => MessageDialog.Ask(this, L10n.T("DeletePermanently"), message, L10n.T("DeletePermanently"), danger: true);
        AskPassword = confirm =>
        {
            var dialog = new PasswordDialog(this, L10n.T("BackupPasswordTitle"), L10n.T(confirm ? "BackupPasswordCreate" : "BackupPasswordOpen"), confirm);
            return dialog.ShowDialog() == true ? dialog.Result : null;
        };
        if (L10n.Current.RightToLeft) FlowDirection = FlowDirection.RightToLeft;
        InitializeComponent();
        BuildLanguageMenu();
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
        WindowPlacement.Attach(this);
        SystemEvents.SessionSwitch += SessionSwitch; SystemEvents.PowerModeChanged += PowerChanged;
        PurgeExpired();
        RefreshList(session.Book.Notes.Where(n => !n.Deleted).OrderByDescending(n => n.Updated).FirstOrDefault()?.Id);
        if (!session.IsDeviceProtected) idleTimer.Start();
        VersionText.Text = L10n.T("OnThisPc") + " · " + Updater.CurrentLabel;
        if (Updater.Enabled(Path.GetDirectoryName(session.FilePath)!)) Loaded += (_, _) => _ = CheckUpdates();
    }
    private async Task CheckUpdates()
    {
        try
        {
            update = await Updater.CheckAsync();
            if (update == null || !IsLoaded) return;
            UpdateButton.Content = L10n.T("UpdateReady", update.Version.ToString(3));
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
            var progress = new Progress<double>(p => StatusText.Text = L10n.T("UpdateDownloading", (int)(p * 100)));
            string installer = await Updater.DownloadAsync(update, progress);
            StatusText.Text = L10n.T("UpdateInstalling");
            Updater.Install(installer);
            Close();
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { StatusText.Text = ex is InvalidDataException ? ex.Message : L10n.T("UpdateFailed"); UpdateButton.IsEnabled = true; }
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
        // Delete acts on the list unless a text box (search, title, body) owns the keyboard.
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Delete && Keyboard.FocusedElement is not TextBoxBase && (selecting || NoteList.IsKeyboardFocusWithin || current != null))
        { if (trash) PurgeClick(this, e); else DeleteClick(this, e); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Shift && e.Key == Key.Delete && Keyboard.FocusedElement is not TextBoxBase && (selecting || current != null)) { PurgeClick(this, e); e.Handled = true; return; }
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
        CountText.Text = L10n.Count("NoteCountOne", "NoteCountMany", notes.Count);
        NoResults.Visibility = notes.Count == 0 && SearchInput.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AllFilter.SetResourceReference(BackgroundProperty, trash ? "Side" : "Surface");
        TrashFilter.SetResourceReference(BackgroundProperty, trash ? "Surface" : "Side");
        ListHeading.Text = L10n.T(trash ? "RecentlyDeleted" : "MyNotes");
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
        PinButton.ToolTip = L10n.T(current?.Pinned == true ? "UnpinNote" : "PinNote");
        System.Windows.Automation.AutomationProperties.SetName(PinButton, (string)PinButton.ToolTip);
        PinButton.SetResourceReference(ForegroundProperty, current?.Pinned == true ? "Accent" : "Muted");
        DateText.Text = current?.Updated.LocalDateTime.ToString("f", L10n.Culture) ?? "";
        EmptyHeading.Text = L10n.T(trash ? "EmptyTrashHeading" : SearchInput.Text.Length > 0 ? "EmptySearchHeading" : "EmptyHeading");
        EmptyDescription.Text = trash ? L10n.T("EmptyTrashDescription", TrashPolicy.RetentionDays) : L10n.T(SearchInput.Text.Length > 0 ? "EmptySearchDescription" : "EmptyDescription");
        EmptyNew.Content = L10n.T(session.Book.Notes.Any(n => !n.Deleted) ? "WriteNewNote" : "WriteFirstNote");
        EmptyNew.Visibility = trash ? Visibility.Collapsed : Visibility.Visible;
        TrashNotice.Text = L10n.T("TrashNotice", TrashPolicy.RetentionDays);
        TrashNotice.Visibility = trash ? Visibility.Visible : Visibility.Collapsed;
        SelectButton.Content = L10n.T(selecting ? "Cancel" : "Select");
        SelectButton.Visibility = selecting || NoteList.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectAllButton.Visibility = selecting && NoteList.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectAllButton.Content = L10n.T(count > 0 && count == NoteList.Items.Count ? "DeselectAll" : "SelectAll");
        SelectionCount.Text = selecting ? count == 0 ? L10n.T("NoSelection") : L10n.Count("SelectedCountOne", "SelectedCountMany", count) : "";
        SelectionHeading.Text = count == 0 ? L10n.T("SelectionPrompt") : L10n.Count("SelectedCountOne", "SelectedCountMany", count);
        SelectionDescription.Text = trash ? L10n.T("SelectionDescriptionTrash") : L10n.T("SelectionDescription", TrashPolicy.RetentionDays);
        BulkDeleteButton.Visibility = trash ? Visibility.Collapsed : Visibility.Visible;
        BulkRestoreButton.Visibility = trash ? Visibility.Visible : Visibility.Collapsed;
        SelectionDeleteButton.Visibility = selecting && !trash ? Visibility.Visible : Visibility.Collapsed;
        SelectionDeleteButton.IsEnabled = count > 0;
        BulkDeleteButton.IsEnabled = BulkRestoreButton.IsEnabled = BulkPurgeButton.IsEnabled = count > 0;
    }
    private void Touch()
    {
        if (current == null) return;
        current.Updated = DateTimeOffset.UtcNow; current.Revision++;
        dirty = true; saveTimer.Stop(); saveTimer.Start(); StatusText.Text = L10n.T("Saving");
    }
    private static void ClearUndo(TextBox box) { box.IsUndoEnabled = false; box.IsUndoEnabled = true; }
    private void EditorChanged(object sender, TextChangedEventArgs e)
    {
        if (loading || current == null || trash) return;
        current.Title = TitleInput.Text; current.Text = BodyInput.Text;
        Touch(); UpdateState();
    }
    // For callers that edited the notebook directly (restore, tests).
    public void MarkChanged() { dirty = true; SaveNow(); }
    public bool SaveNow()
    {
        saveTimer.Stop();
        if (!dirty) return true;
        try { session.Save(purged); purged = false; dirty = false; StatusText.Text = L10n.T("SavedEncrypted"); RefreshList(); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { StatusText.Text = L10n.T("SaveFailed"); return false; }
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
        MenuPin.Header = L10n.T(targets[0].Pinned ? "UnpinNote" : "Pin");
        MenuDelete.Visibility = trash ? Visibility.Collapsed : Visibility.Visible;
        MenuRestore.Visibility = trash ? Visibility.Visible : Visibility.Collapsed;
        MenuDelete.Header = targets.Count == 1 ? L10n.T("Delete") : L10n.T("MoveManyToTrash", targets.Count);
        MenuRestore.Header = targets.Count == 1 ? L10n.T("Restore") : L10n.T("RestoreMany", targets.Count);
        MenuPurge.Header = targets.Count == 1 ? L10n.T("DeletePermanentlyMenu") : L10n.T("PurgeMany", targets.Count);
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
        StatusText.Text = L10n.Count("MovedToTrashOne", "MovedToTrashMany", notes.Count);
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
        StatusText.Text = L10n.Count("RestoredOne", "RestoredMany", notes.Count);
        lastDeleted = []; UndoDelete.Visibility = Visibility.Collapsed;
    }
    private void PurgeClick(object sender, RoutedEventArgs e)
    {
        var notes = Targets();
        if (notes.Count == 0 || !SaveNow()) return;
        string message = notes.Count == 1 ? L10n.T("ConfirmPurgeOne", notes[0].DisplayTitle) : L10n.T("ConfirmPurgeMany", notes.Count);
        if (!ConfirmDestructive(message)) return;
        TrashPolicy.Delete(notes, DateTimeOffset.UtcNow); TrashPolicy.Purge(session.Book, notes); purged = true; dirty = true;
        if (!SaveNow()) return;
        StatusText.Text = L10n.Count("PurgedOne", "PurgedMany", notes.Count);
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
        if (saved) StatusText.Text = L10n.T("TxtImported");
        return saved;
    }
    private void ImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = L10n.T("TxtOpenTitle"), Filter = L10n.T("TxtFilter"), CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try { ImportTextFile(dialog.FileName); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { StatusText.Text = ex is InvalidDataException ? ex.Message : L10n.T("TxtOpenFailed"); }
    }
    private void ExportClick(object sender, RoutedEventArgs e)
    {
        if (current == null || selecting || !SaveNow()) return;
        var note = current;
        var dialog = new SaveFileDialog { Title = L10n.T("TxtSaveTitle"), Filter = L10n.T("TxtSaveFilter"), DefaultExt = ".txt", FileName = TextFiles.SuggestedName(note) };
        if (dialog.ShowDialog(this) != true) return;
        try { TextFiles.Write(dialog.FileName, note); StatusText.Text = L10n.T("TxtSaved"); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { StatusText.Text = L10n.T("TxtSaveFailed"); }
    }
    // Backup password prompts go through here so tests can supply a password without a dialog.
    public Func<bool, string?> AskPassword { get; set; }
    private void BackupMenuClick(object sender, RoutedEventArgs e)
    {
        BackupMenu.PlacementTarget = BackupButton;
        BackupMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        BackupMenu.IsOpen = true;
    }
    private void BackupCreateClick(object sender, RoutedEventArgs e)
    {
        if (!SaveNow()) return;
        var dialog = new SaveFileDialog { Title = L10n.T("BackupTitle"), Filter = L10n.T("BackupFilter"), DefaultExt = ".vault", FileName = L10n.T("BackupFileName", DateTime.Now.ToString("yyyy-MM-dd")) + ".vault" };
        if (dialog.ShowDialog(this) != true) return;
        string target = Path.GetFullPath(dialog.FileName);
        if (string.Equals(target, session.FilePath, StringComparison.OrdinalIgnoreCase) || target.StartsWith(session.FilePath + ".", StringComparison.OrdinalIgnoreCase))
        { StatusText.Text = L10n.T("BackupSameLocation"); return; }
        string? password = AskPassword(true);
        if (password == null) return;
        ExportBackup(target, password);
    }
    public bool ExportBackup(string target, string password)
    {
        try { session.ExportPortable(target, password); StatusText.Text = L10n.T("BackupSaved"); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { StatusText.Text = L10n.T("BackupFailed"); return false; }
    }
    private void BackupRestoreClick(object sender, RoutedEventArgs e)
    {
        if (!SaveNow()) return;
        var dialog = new OpenFileDialog { Title = L10n.T("RestoreTitle"), Filter = L10n.T("BackupFilter"), CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        string path = Path.GetFullPath(dialog.FileName);
        string? password = null;
        try { if (VaultSession.ReadVersion(path) == 1) { password = AskPassword(false); if (password == null) return; } }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException or UnauthorizedAccessException) { StatusText.Text = L10n.T("RestoreFailed"); return; }
        RestoreBackup(path, password);
    }
    // Merges a backup into the open notebook: newer revisions win, nothing is removed.
    public bool RestoreBackup(string path, string? password)
    {
        try
        {
            (int added, int updated) result;
            using (var backup = VaultSession.OpenBackup(path, password)) result = VaultSession.Merge(session.Book, backup.Book);
            if (result.added == 0 && result.updated == 0) { StatusText.Text = L10n.T("RestoreNothing"); return true; }
            dirty = true;
            if (!SaveNow()) return false;
            if (selecting) SetSelecting(false);
            RefreshList();
            StatusText.Text = L10n.T("RestoreResult", result.added, result.updated);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or FormatException)
        { StatusText.Text = L10n.T(ex is System.Security.Cryptography.CryptographicException ? "PasswordWrong" : "RestoreFailed"); return false; }
    }
    private static string[] DroppedTextFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)).ToArray() : [];
    private void FileDragOver(object sender, DragEventArgs e)
    {
        if (DroppedTextFiles(e).Length == 0) return;
        e.Effects = DragDropEffects.Copy; e.Handled = true;
    }
    private void FileDrop(object sender, DragEventArgs e)
    {
        var files = DroppedTextFiles(e);
        if (files.Length == 0) return;
        e.Handled = true; ImportDropped(files);
    }
    // Imports each dropped .txt as a note; reports how many succeeded.
    public int ImportDropped(string[] files)
    {
        int count = 0; string? error = null;
        foreach (string file in files)
        {
            try { if (ImportTextFile(file)) count++; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { error = ex is InvalidDataException ? ex.Message : L10n.T("TxtOpenFailed"); }
        }
        StatusText.Text = error ?? (count == 1 ? L10n.T("TxtImported") : L10n.T("TxtImportedMany", count));
        return count;
    }
    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (!SaveNow()) { e.Cancel = true; LockRequested = false; MessageDialog.Info(this, L10n.T("CloseSaveFailed"), L10n.T("CloseSaveFailedTitle")); }
    }
    private void Lock() { if (SaveNow()) { LockRequested = true; Close(); } }
    // Language is applied at window creation; choosing another one saves it and restarts the application.
    public bool RestartRequested { get; private set; }
    private void BuildLanguageMenu()
    {
        LanguageMenu.Items.Clear();
        foreach (var language in L10n.Languages)
        {
            var item = new MenuItem { Header = language.NativeName, Tag = language.Code, IsCheckable = true, IsChecked = language.Code == L10n.Current.Code };
            item.Click += (_, _) => ChangeLanguage(language.Code);
            LanguageMenu.Items.Add(item);
        }
    }
    public void ChangeLanguage(string code)
    {
        if (code == L10n.Current.Code || !SaveNow()) return;
        try { L10n.Save(Path.GetDirectoryName(session.FilePath)!, code); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusText.Text = L10n.T("SaveFailed"); return; }
        RestartRequested = true; Close();
    }
    private void LanguageClick(object sender, RoutedEventArgs e)
    {
        LanguageMenu.PlacementTarget = LanguageButton;
        LanguageMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        LanguageMenu.IsOpen = true;
    }
    private void LockClick(object sender, RoutedEventArgs e) => Lock();
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
