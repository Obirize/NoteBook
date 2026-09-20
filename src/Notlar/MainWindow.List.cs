using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Notlar;

// The note list: folders (all, archive, trash), search, selection, and moving notes between the folders.
public partial class MainWindow
{
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
}
