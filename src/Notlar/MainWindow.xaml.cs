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
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Notlar;

public partial class MainWindow : Window
{
    private readonly VaultSession session;
    private Note? current;
    private List<Note> lastDeleted = [];
    private bool loading, dirty, trash, archive, selecting, purged, sweep, importing;
    private Folder CurrentFolder => trash ? Folder.Trash : archive ? Folder.Archive : Folder.Notes;
    private readonly Dictionary<string, BitmapSource> thumbnails = [];
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly DispatcherTimer idleTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private DateTime lastInput = DateTime.UtcNow;
    public bool LockRequested { get; private set; }
    // Permanent deletion asks before proceeding; tests replace this to avoid a modal dialog.
    public Func<string, bool> ConfirmDestructive { get; set; }
    public Func<string, bool> ConfirmRemoveAttachment { get; set; }
    private Updater.Release? update;
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    public MainWindow(VaultSession vault)
    {
        session = vault;
        ConfirmDestructive = message => MessageDialog.Ask(this, L10n.T("DeletePermanently"), message, L10n.T("DeletePermanently"), danger: true);
        ConfirmRemoveAttachment = message => MessageDialog.Ask(this, L10n.T("RemoveAttachmentTitle"), message, L10n.T("RemoveAttachmentTitle"), danger: true);
        AskPassword = confirm =>
        {
            var dialog = new PasswordDialog(this, L10n.T("BackupPasswordTitle"), L10n.T(confirm ? "BackupPasswordCreate" : "BackupPasswordOpen"), confirm);
            return dialog.ShowDialog() == true ? dialog.Result : null;
        };
        if (L10n.Current.RightToLeft) FlowDirection = FlowDirection.RightToLeft;
        InitializeComponent();
        InitializeSync();
        InitializeTray();
        InitializeChecklist();
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
            phoneSync?.Dispose();
            saveTimer.Stop(); idleTimer.Stop(); SystemEvents.SessionSwitch -= SessionSwitch; SystemEvents.PowerModeChanged -= PowerChanged;
            loading = true; TitleInput.Clear(); BodyInput.Clear(); ClearUndo(BodyInput); ClearUndo(TitleInput); SearchInput.Clear(); NoteList.ItemsSource = null;
            current = null; lastDeleted = []; thumbnails.Clear(); AttachmentPanel.Children.Clear();
        };
        SourceInitialized += (_, _) => { int rounded = 2; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref rounded, 4); };
        WindowPlacement.Attach(this);
        SystemEvents.SessionSwitch += SessionSwitch; SystemEvents.PowerModeChanged += PowerChanged;
        PurgeExpired();
        // Files left behind by a crash or by an older save no note refers to any more.
        session.Attachments.Sweep(session.Book);
        RefreshList(session.Book.Notes.Where(n => !n.Deleted).OrderByDescending(n => n.Updated).FirstOrDefault()?.Id);
        if (!session.IsDeviceProtected) idleTimer.Start();
        VersionText.Text = L10n.T("OnThisPc") + " · " + Updater.CurrentLabel;
        // The app may live in the tray for days: look for updates shortly after start, every hour, and whenever the
        // window comes to the front after a while. Each check is one small request to the releases page.
        if (Updater.Enabled(Path.GetDirectoryName(session.FilePath)!))
        {
            updateTimer.Tick += (_, _) => { updateTimer.Interval = TimeSpan.FromHours(1); _ = CheckUpdates(); };
            updateTimer.Start();
            Activated += (_, _) => CheckUpdatesIfStale();
        }
    }
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private DateTime lastUpdateCheck = DateTime.MinValue;
    private bool updateAnnounced;
    public void CheckUpdatesIfStale() { if (updateTimer.IsEnabled && update == null && DateTime.UtcNow - lastUpdateCheck > TimeSpan.FromMinutes(10)) _ = CheckUpdates(); }
    private async Task CheckUpdates()
    {
        lastUpdateCheck = DateTime.UtcNow;
        try
        {
            update = await Updater.CheckAsync();
            if (update == null) return;
            UpdateButton.Content = L10n.T("UpdateReady", update.Version.ToString(3));
            UpdateButton.Visibility = Visibility.Visible;
            if (!IsVisible && !updateAnnounced && tray != null) { updateAnnounced = true; tray.ShowBalloonTip(5000, L10n.T("AppName"), L10n.T("UpdateReady", update.Version.ToString(3)), System.Windows.Forms.ToolTipIcon.None); }
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException or UriFormatException) { }
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
        if (expired.Count > 0) { TrashPolicy.Purge(session.Book, expired); purged = true; sweep = true; changed = true; }
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
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.A) { AttachClick(this, e); e.Handled = true; return; }
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.L) { ChecklistClick(this, e); e.Handled = true; return; }
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.E) { ArchiveClick(this, e); e.Handled = true; return; }
        // Ctrl+V with a picture or media files on the clipboard (and no text) attaches instead of pasting nothing.
        if (e.Key == Key.V && PasteAttachment()) { e.Handled = true; return; }
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
        var notes = NoteQuery.Find(session.Book, SearchInput.Text, CurrentFolder);
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
        AllFilter.SetResourceReference(BackgroundProperty, CurrentFolder == Folder.Notes ? "Surface" : "Side");
        ArchiveFilter.SetResourceReference(BackgroundProperty, CurrentFolder == Folder.Archive ? "Surface" : "Side");
        TrashFilter.SetResourceReference(BackgroundProperty, CurrentFolder == Folder.Trash ? "Surface" : "Side");
        ListHeading.Text = L10n.T(trash ? "RecentlyDeleted" : archive ? "Archive" : "MyNotes");
        UpdateState();
    }
    private void OpenNote(Note? note)
    {
        loading = true; current = note;
        TitleInput.Text = note?.Title ?? ""; BodyInput.Text = note?.Text ?? "";
        ClearUndo(TitleInput); ClearUndo(BodyInput);
        BodyInput.CaretIndex = 0;
        RenderAttachments();
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
        PinButton.Visibility = ArchiveButton.Visibility = DeleteButton.Visibility = ExportButton.Visibility = AttachButton.Visibility = ChecklistButton.Visibility = trash ? Visibility.Collapsed : Visibility.Visible;
        ArchiveButton.ToolTip = L10n.T(current?.Archived == true ? "UnarchiveNote" : "ArchiveNote");
        System.Windows.Automation.AutomationProperties.SetName(ArchiveButton, (string)ArchiveButton.ToolTip);
        AttachButton.IsEnabled = !importing;
        RestoreButton.Visibility = PurgeButton.Visibility = trash ? Visibility.Visible : Visibility.Collapsed;
        PinButton.ToolTip = L10n.T(current?.Pinned == true ? "UnpinNote" : "PinNote");
        System.Windows.Automation.AutomationProperties.SetName(PinButton, (string)PinButton.ToolTip);
        PinButton.SetResourceReference(ForegroundProperty, current?.Pinned == true ? "Accent" : "Muted");
        DateText.Text = current?.Updated.LocalDateTime.ToString("f", L10n.Culture) ?? "";
        EmptyHeading.Text = L10n.T(SearchInput.Text.Length > 0 ? "EmptySearchHeading" : trash ? "EmptyTrashHeading" : archive ? "EmptyArchiveHeading" : "EmptyHeading");
        EmptyDescription.Text = SearchInput.Text.Length > 0 ? L10n.T("EmptySearchDescription") : trash ? L10n.T("EmptyTrashDescription", TrashPolicy.RetentionDays) : L10n.T(archive ? "EmptyArchiveDescription" : "EmptyDescription");
        EmptyNew.Content = L10n.T(session.Book.Notes.Any(n => !n.Deleted) ? "WriteNewNote" : "WriteFirstNote");
        EmptyNew.Visibility = trash || archive ? Visibility.Collapsed : Visibility.Visible;
        TrashNotice.Text = L10n.T("TrashNotice", TrashPolicy.RetentionDays);
        TrashNotice.Visibility = trash ? Visibility.Visible : Visibility.Collapsed;
        SelectButton.Content = L10n.T(selecting ? "Cancel" : "Select");
        SelectButton.Visibility = selecting || NoteList.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectAllButton.Visibility = selecting && NoteList.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectAllButton.Content = L10n.T(count > 0 && count == NoteList.Items.Count ? "DeselectAll" : "SelectAll");
        SelectionCount.Text = selecting ? count == 0 ? L10n.T("NoSelection") : L10n.Count("SelectedCountOne", "SelectedCountMany", count) : "";
        SelectionHeading.Text = count == 0 ? L10n.T("SelectionPrompt") : L10n.Count("SelectedCountOne", "SelectedCountMany", count);
        SelectionDescription.Text = trash ? L10n.T("SelectionDescriptionTrash") : L10n.T("SelectionDescription", TrashPolicy.RetentionDays);
        BulkDeleteButton.Visibility = BulkArchiveButton.Visibility = trash ? Visibility.Collapsed : Visibility.Visible;
        BulkArchiveButton.Content = L10n.T(archive ? "UnarchiveNote" : "ArchiveNote");
        BulkRestoreButton.Visibility = trash ? Visibility.Visible : Visibility.Collapsed;
        SelectionDeleteButton.Visibility = selecting && !trash ? Visibility.Visible : Visibility.Collapsed;
        SelectionDeleteButton.IsEnabled = count > 0;
        BulkDeleteButton.IsEnabled = BulkRestoreButton.IsEnabled = BulkPurgeButton.IsEnabled = BulkArchiveButton.IsEnabled = count > 0;
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
        try
        {
            session.Save(purged); purged = false; dirty = false; StatusText.Text = L10n.T("SavedEncrypted");
            PublishSyncChanges();
            if (sweep) { session.Attachments.Sweep(session.Book); sweep = false; }
            RefreshList(); return true;
        }
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
        trash = archive = false; loading = true; SearchInput.Clear(); loading = false;
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
        MenuSaveAttachments.Visibility = targets.Any(n => n.Attachments.Count > 0) ? Visibility.Visible : Visibility.Collapsed;
        MenuPin.Header = L10n.T(targets[0].Pinned ? "UnpinNote" : "Pin");
        MenuArchive.Visibility = trash ? Visibility.Collapsed : Visibility.Visible;
        MenuArchive.Header = targets.Count == 1 ? L10n.T(archive ? "UnarchiveNote" : "ArchiveNote") : L10n.T(archive ? "UnarchiveMany" : "ArchiveMany", targets.Count);
        MenuDelete.Visibility = trash ? Visibility.Collapsed : Visibility.Visible;
        MenuRestore.Visibility = trash ? Visibility.Visible : Visibility.Collapsed;
        MenuDelete.Header = targets.Count == 1 ? L10n.T("Delete") : L10n.T("MoveManyToTrash", targets.Count);
        MenuRestore.Header = targets.Count == 1 ? L10n.T("Restore") : L10n.T("RestoreMany", targets.Count);
        MenuPurge.Header = targets.Count == 1 ? L10n.T("DeletePermanentlyMenu") : L10n.T("PurgeMany", targets.Count);
    }
    private void ClearSearchClick(object sender, RoutedEventArgs e) { SearchInput.Clear(); SearchInput.Focus(); }
    private void AllClick(object sender, RoutedEventArgs e) { if (!SaveNow()) return; trash = archive = false; RefreshList(); }
    private void ArchiveFilterClick(object sender, RoutedEventArgs e) { if (!SaveNow()) return; trash = false; archive = true; RefreshList(); }
    private void TrashClick(object sender, RoutedEventArgs e) { if (!SaveNow()) return; trash = true; archive = false; RefreshList(); }
    // Archiving moves the note (or every checked note) between the main list and the archive; nothing is lost either way.
    private void ArchiveClick(object sender, RoutedEventArgs e)
    {
        var notes = Targets();
        if (notes.Count == 0 || trash || !SaveNow()) return;
        TrashPolicy.Archive(notes, !archive, DateTimeOffset.UtcNow); dirty = true;
        if (!SaveNow()) return;
        StatusText.Text = L10n.Count(archive ? "UnarchivedOne" : "ArchivedOne", archive ? "UnarchivedMany" : "ArchivedMany", notes.Count);
        if (selecting) SetSelecting(false);
    }
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
        trash = archive = false; selecting = false; NoteList.SelectionMode = SelectionMode.Single;
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
        TrashPolicy.Delete(notes, DateTimeOffset.UtcNow); TrashPolicy.Purge(session.Book, notes); purged = true; sweep = true; dirty = true;
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
        trash = archive = false; if (selecting) SetSelecting(false);
        loading = true; SearchInput.Clear(); loading = false;
        SearchHint.Visibility = Visibility.Visible; ClearSearch.Visibility = Visibility.Collapsed;
        RefreshList(notes[0].Id); UndoDelete.Visibility = Visibility.Collapsed; lastDeleted = [];
    }
    private void TitleKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { BodyInput.Focus(); e.Handled = true; } }

    // ----- Checklists -----
    private bool movingCaret;
    private void InitializeChecklist()
    {
        ChecklistAdorner.Attach(BodyInput);
        BodyInput.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (trash || current == null) return;
            int start = ChecklistAdorner.MarkerAt(BodyInput, e.GetPosition(BodyInput));
            if (start >= 0) { ToggleItem(start); e.Handled = true; }
        };
        BodyInput.PreviewKeyDown += (_, e) =>
        {
            if (trash || current == null || BodyInput.SelectionLength > 0 || Keyboard.Modifiers != ModifierKeys.None) return;
            var change = e.Key == Key.Enter ? Checklist.Enter(BodyInput.Text, BodyInput.CaretIndex) : e.Key == Key.Back ? Checklist.Backspace(BodyInput.Text, BodyInput.CaretIndex) : null;
            if (change == null) return;
            ReplaceBody(change.Value.Text, change.Value.Caret); e.Handled = true;
        };
        // The caret never sits inside a marker: clicking or arrowing into it lands after the box.
        BodyInput.SelectionChanged += (_, _) =>
        {
            if (movingCaret || BodyInput.SelectionLength > 0) return;
            string text = BodyInput.Text; int caret = BodyInput.CaretIndex, start = Checklist.LineStart(text, caret);
            string line = Checklist.LineAt(text, caret);
            if (Checklist.IsItem(line) && caret - start < 2 && caret - start < line.Length)
            { movingCaret = true; BodyInput.CaretIndex = Math.Min(start + 2, text.Length); movingCaret = false; }
        };
    }
    // Applies a whole-text change through the selection so the TextBox's undo stack and scroll position survive.
    private void ReplaceBody(string text, int caret)
    {
        string old = BodyInput.Text;
        int prefix = 0; while (prefix < old.Length && prefix < text.Length && old[prefix] == text[prefix]) prefix++;
        int suffix = 0; while (suffix < old.Length - prefix && suffix < text.Length - prefix && old[old.Length - 1 - suffix] == text[text.Length - 1 - suffix]) suffix++;
        movingCaret = true;
        BodyInput.Select(prefix, old.Length - prefix - suffix);
        BodyInput.SelectedText = text.Substring(prefix, text.Length - prefix - suffix);
        BodyInput.CaretIndex = Math.Clamp(caret, 0, BodyInput.Text.Length);
        movingCaret = false;
    }
    public void ToggleItem(int lineStart)
    {
        string text = BodyInput.Text; int end = Checklist.LineEnd(text, lineStart);
        string line = text[lineStart..end];
        if (!Checklist.IsItem(line)) return;
        ReplaceBody(text[..lineStart] + Checklist.Toggle(line) + text[end..], BodyInput.CaretIndex);
    }
    // The toolbar button or Ctrl+Shift+L: the current line, or every selected line, becomes a checklist item (or stops being one).
    private void ChecklistClick(object sender, RoutedEventArgs e)
    {
        if (trash || current == null) return;
        string text = BodyInput.Text; int from = BodyInput.SelectionStart, to = from + BodyInput.SelectionLength;
        string changed = Checklist.ToggleLines(text, from, to, out int newFrom, out int newTo);
        ReplaceBody(changed, newFrom);
        if (to > from) { int lineStart = Checklist.LineStart(changed, newFrom); movingCaret = true; BodyInput.Select(lineStart, newTo - lineStart); movingCaret = false; }
        BodyInput.Focus();
    }
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
        trash = archive = false; loading = true; SearchInput.Clear(); loading = false;
        SearchHint.Visibility = Visibility.Visible; ClearSearch.Visibility = Visibility.Collapsed;
        RefreshList(note.Id);
        bool saved = SaveNow();
        if (saved) StatusText.Text = L10n.T("TxtImported");
        return saved;
    }
    private void ImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = L10n.T("TxtOpenTitle"), Filter = L10n.T("TextFilesLabel") + " (" + TextFiles.OpenPatterns + ")|" + TextFiles.OpenPatterns + "|" + L10n.T("AllFiles") + " (*.*)|*.*", CheckFileExists = true, Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        if (dialog.FileNames.Length > 1) { ImportDropped(dialog.FileNames); return; }
        try { ImportTextFile(dialog.FileName); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { StatusText.Text = ex is InvalidDataException ? ex.Message : L10n.T("TxtOpenFailed"); }
    }
    private void ExportClick(object sender, RoutedEventArgs e)
    {
        if (current == null || selecting || !SaveNow()) return;
        var note = current;
        var dialog = new SaveFileDialog { Title = L10n.T("TxtSaveTitle"), Filter = L10n.T("TxtSaveFilter"), DefaultExt = ".txt", AddExtension = true, FileName = TextFiles.SuggestedName(note) };
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
            int copied;
            using (var backup = VaultSession.OpenBackup(path, password))
            {
                result = VaultSession.Merge(session.Book, backup.Book);
                // Encrypted attachment files travel as they are; their keys arrived inside the merged notes.
                copied = session.Attachments.CopyMissing(session.Book, backup.Attachments);
            }
            if (result.added == 0 && result.updated == 0 && copied == 0) { StatusText.Text = L10n.T("RestoreNothing"); return true; }
            dirty = true;
            if (!SaveNow()) return false;
            RenderAttachments();
            if (selecting) SetSelecting(false);
            RefreshList();
            StatusText.Text = L10n.T("RestoreResult", result.added, result.updated);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or FormatException)
        { StatusText.Text = L10n.T(ex is System.Security.Cryptography.CryptographicException ? "PasswordWrong" : "RestoreFailed"); return false; }
    }
    private static string[] DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files ? files : [];
    private static string[] TextFilesOf(string[] files) => files.Where(TextFiles.IsSupported).ToArray();
    private string[] MediaFilesOf(string[] files) => CanAttach ? files.Where(AttachmentStore.IsSupported).ToArray() : [];
    private bool CanAttach => current != null && !trash && !selecting && !importing;
    private void FileDragOver(object sender, DragEventArgs e)
    {
        var files = DroppedFiles(e);
        if (TextFilesOf(files).Length == 0 && MediaFilesOf(files).Length == 0) return;
        e.Effects = DragDropEffects.Copy; e.Handled = true;
    }
    private void FileDrop(object sender, DragEventArgs e)
    {
        var files = DroppedFiles(e);
        var media = MediaFilesOf(files); var text = TextFilesOf(files);
        if (media.Length == 0 && text.Length == 0) return;
        e.Handled = true;
        if (media.Length > 0) _ = AttachFilesAsync(media);
        if (text.Length > 0) ImportDropped(text);
    }
    // Imports each dropped text file as a note; reports how many succeeded.
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
                Directory.CreateDirectory(target);
                foreach (var attachment in note.Attachments)
                {
                    if (!session.Attachments.Exists(attachment)) continue;
                    string name = string.Concat(attachment.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                    if (string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(name))) name = attachment.Id[..8] + Path.GetExtension(name);
                    string path = Path.Combine(target, name);
                    for (int i = 2; File.Exists(path); i++) path = Path.Combine(target, Path.GetFileNameWithoutExtension(name) + " (" + i + ")" + Path.GetExtension(name));
                    session.Attachments.Export(attachment, path); saved++;
                }
            }
            return saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or ArgumentException) { return -1; }
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
        AttachmentScroll.Visibility = note != null && note.Attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        var tile = new Border { Width = size, Height = size, Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = content, Cursor = Cursors.Hand, Focusable = true, Tag = attachment, ToolTip = attachment.Name + " · " + attachment.SizeLabel + (attachment.Width > 0 ? " · " + attachment.Width + "×" + attachment.Height : "") };
        tile.SetResourceReference(Border.BackgroundProperty, "Surface"); tile.SetResourceReference(Border.BorderBrushProperty, "Rule");
        System.Windows.Automation.AutomationProperties.SetName(tile, attachment.Name);
        tile.MouseEnter += (_, _) => tile.SetResourceReference(Border.BorderBrushProperty, "Accent");
        tile.MouseLeave += (_, _) => tile.SetResourceReference(Border.BorderBrushProperty, "Rule");
        tile.MouseLeftButtonUp += (_, _) => OpenAttachment(attachment);
        tile.KeyDown += (_, e) => { if (e.Key == Key.Enter) { OpenAttachment(attachment); e.Handled = true; } else if (e.Key == Key.Delete && !trash) { RemoveAttachment(attachment); e.Handled = true; } };
        var menu = new ContextMenu();
        var open = new MenuItem { Header = L10n.T("AttachmentOpen") }; open.Click += (_, _) => OpenAttachment(attachment); menu.Items.Add(open);
        var save = new MenuItem { Header = L10n.T("AttachmentSaveAs") }; save.Click += (_, _) => SaveAttachmentCopy(attachment); menu.Items.Add(save);
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
    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (!SaveNow()) { e.Cancel = true; LockRequested = false; MessageDialog.Info(this, L10n.T("CloseSaveFailed"), L10n.T("CloseSaveFailedTitle")); return; }
        // The X only hides the window; the phone link keeps working from the notification area.
        if (CloseHides) { e.Cancel = true; HideToTray(); }
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
