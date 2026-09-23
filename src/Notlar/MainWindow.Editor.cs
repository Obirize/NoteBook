using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Notlar;

// The editor: autosave, undo and the checklist behaviour of the body text box.
public partial class MainWindow
{
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
}
