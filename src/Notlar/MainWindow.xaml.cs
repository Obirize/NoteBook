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

// The main window: session, timers, shortcuts, updates and the window chrome. The list, the editor, files and
// attachments live in the other MainWindow.*.cs files.
public partial class MainWindow
{
    private readonly VaultSession session;
    private Note? current;
    private List<Note> lastDeleted = [];
    private bool loading, dirty, trash, archive, selecting, purged, sweep, importing;
    private Folder CurrentFolder => trash ? Folder.Trash : archive ? Folder.Archive : Folder.Notes;
    private readonly Dictionary<string, BitmapSource> thumbnails = [];
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
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
        saveTimer.Tick += (_, _) => SaveNow();
        PreviewKeyDown += Shortcut;
        Closing += WindowClosing;
        Closed += (_, _) =>
        {
            phoneSync?.Dispose();
            saveTimer.Stop(); SystemEvents.SessionSwitch -= SessionSwitch; SystemEvents.PowerModeChanged -= PowerChanged;
            loading = true; TitleInput.Clear(); BodyInput.Clear(); ClearUndo(BodyInput); ClearUndo(TitleInput); SearchInput.Clear(); NoteList.ItemsSource = null;
            current = null; lastDeleted = []; thumbnails.Clear(); noThumb.Clear(); AttachmentPanel.Children.Clear();
        };
        SourceInitialized += (_, _) => { int rounded = 2; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref rounded, 4); };
        WindowPlacement.Attach(this);
        SystemEvents.SessionSwitch += SessionSwitch; SystemEvents.PowerModeChanged += PowerChanged;
        PurgeExpired();
        // Files left behind by a crash or by an older save no note refers to any more.
        session.Attachments.Sweep(session.Book);
        RefreshList(session.Book.Notes.Where(n => !n.Deleted).OrderByDescending(n => n.Updated).FirstOrDefault()?.Id);
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
    // Locking the PC or putting it to sleep is a moment to make sure everything is on disk.
    private void SessionSwitch(object sender, SessionSwitchEventArgs e) { if (e.Reason == SessionSwitchReason.SessionLock) Dispatcher.BeginInvoke(SaveNow); }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) Dispatcher.BeginInvoke(SaveNow);
        // A mark in the phone log: a gap that starts here was the PC sleeping, not the phone failing.
        else if (e.Mode == PowerModes.Resume) phoneSync?.LogNote(L10n.T("SyncLogPcResumed"));
    }
    private void Shortcut(object sender, KeyEventArgs e)
    {
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
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (!SaveNow()) { e.Cancel = true; MessageDialog.Info(this, L10n.T("CloseSaveFailed"), L10n.T("CloseSaveFailedTitle")); return; }
        // The X only hides the window; the phone link keeps working from the notification area.
        if (CloseHides) { e.Cancel = true; HideToTray(); }
    }
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
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
