using System.IO;
using System.Text.Json;
using System.Windows;
using Notlar.Sync;

namespace Notlar;

public partial class MainWindow : ISyncHost
{
    private SyncService? phoneSync;
    private Dictionary<string, string> syncVersions = [];
    private bool applyingSync;
    private void InitializeSync()
    {
        phoneSync = new SyncService(Path.GetDirectoryName(session.FilePath)!, this);
        RememberSyncVersions();
        phoneSync.TrustProblem += () => Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = L10n.T("SyncTrustWarning");
            if (!IsVisible && tray != null) tray.ShowBalloonTip(8000, L10n.T("PhoneSync"), L10n.T("SyncTrustWarning"), System.Windows.Forms.ToolTipIcon.Warning);
        });
        // Not on Loaded: a window started into the tray is never shown, yet the phone link must run.
        if (phoneSync.Settings.Enabled) Dispatcher.BeginInvoke(() => phoneSync?.Start());
    }
    private void RememberSyncVersions() => syncVersions = session.Book.Notes.ToDictionary(n => n.Id, n => JsonSerializer.Serialize(n));
    private void PublishSyncChanges()
    {
        if (phoneSync == null || applyingSync) return;
        var changed = session.Book.Notes.Where(n => !syncVersions.TryGetValue(n.Id, out var old) || old != JsonSerializer.Serialize(n)).Select(n => n.Id).ToList();
        RememberSyncVersions();
        phoneSync.NotifyChanged(changed, session.Book.Purged.Select(p => new PurgeStamp(p.Id, p.Revision)).ToList());
    }
    private void PhoneSyncClick(object sender, RoutedEventArgs e)
    {
        if (SaveNow() && phoneSync != null) new SyncWindow(this, phoneSync).ShowDialog();
    }
    AttachmentStore ISyncHost.Attachments => session.Attachments;
    public Task<(Manifest Manifest, string NotebookId)> ManifestAsync() => Dispatcher.InvokeAsync(() =>
    {
        if (!SaveNow()) throw new IOException("Save failed.");
        return (Manifest.Of(session.Book, session.Attachments), session.Book.Id);
    }).Task;
    public Task<List<Note>> NotesAsync(IReadOnlyList<string> ids) => Dispatcher.InvokeAsync(() => session.Book.Notes.Where(n => ids.Contains(n.Id)).Select(SyncMerge.Clone).ToList()).Task;
    public Task<Attachment?> AttachmentAsync(string id) => Dispatcher.InvokeAsync(() => session.Book.Notes.SelectMany(n => n.Attachments).FirstOrDefault(a => a.Id == id)).Task;
    public Task DeviceSeenAsync(string id, string name) => Dispatcher.InvokeAsync(() => { if (current != null && !dirty) OpenNote(current); }).Task;
    public Task<SyncMerge.Result> ApplyAsync(List<Note> notes, List<PurgeStamp> purges, string deviceName) => Dispatcher.InvokeAsync(() =>
    {
        if (!SaveNow()) throw new IOException("Save failed.");
        var before = session.Book.Notes.Select(SyncMerge.Clone).ToList();
        var beforePurges = session.Book.Purged.ToList();
        string? selected = current?.Id;
        applyingSync = true;
        try
        {
            var result = SyncMerge.Apply(session.Book, notes, purges, deviceName);
            session.Save(purges.Count > 0);
            RememberSyncVersions();
            RefreshList(selected);
            if (selected != null) OpenNote(session.Book.Notes.FirstOrDefault(n => n.Id == selected));
            return result;
        }
        catch { session.Book.Notes = before; session.Book.Purged = beforePurges; RefreshList(selected); throw; }
        finally { applyingSync = false; }
    }).Task;
}
