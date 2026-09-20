using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Notlar;

// Text files in and out, encrypted backups, and files dropped onto the window.
public partial class MainWindow
{
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
}
