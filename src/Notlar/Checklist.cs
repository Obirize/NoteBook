using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Notlar;

// Checklist items are ordinary lines that start with "○" (open) or "●" (done) followed by an em space: two symbols
// of equal width that still read naturally in a plain-text export, with room for the drawn check box. The text stays plain (it syncs, exports and
// searches like any other), and the editor draws the markers as round check boxes, striking through done items.
public static class Checklist
{
    public const char Open = '○', Done = '●';
    public const char Gap = ' ';
    public const string OpenPrefix = "○ ", DonePrefix = "● ";
    public static bool IsItem(string line) => line.Length >= 1 && (line[0] == Open || line[0] == Done);
    public static bool IsDone(string line) => line.Length >= 1 && line[0] == Done;
    public static string Body(string line) => IsItem(line) ? (line.Length >= 2 && (line[1] == Gap || line[1] == ' ') ? line[2..] : line[1..]) : line;
    public static string Toggle(string line) => IsItem(line) ? (IsDone(line) ? OpenPrefix : DonePrefix) + Body(line) : line;
    // Selected lines all become items; if every one already is, the markers are removed instead.
    public static string ToggleLines(string text, int from, int to, out int newFrom, out int newTo)
    {
        int lineStart = LineStart(text, from), lineEnd = LineEnd(text, Math.Max(from, to));
        var lines = text[lineStart..lineEnd].Split('\n');
        bool all = lines.All(IsItem);
        var changed = lines.Select(l => all ? Body(l) : IsItem(l) ? l : OpenPrefix + l).ToArray();
        string replaced = string.Join("\n", changed);
        newFrom = lineStart + (all ? Math.Max(0, from - lineStart - 2) : from - lineStart + 2);
        newTo = lineStart + replaced.Length;
        return text[..lineStart] + replaced + text[lineEnd..];
    }
    public static int LineStart(string text, int index) { int i = Math.Clamp(index, 0, text.Length); int n = text.LastIndexOf('\n', Math.Max(0, i - 1)); return i == 0 ? 0 : n + 1; }
    public static int LineEnd(string text, int index) { int n = text.IndexOf('\n', Math.Clamp(index, 0, text.Length)); return n < 0 ? text.Length : n; }
    public static string LineAt(string text, int index) => text[LineStart(text, index)..LineEnd(text, index)];

    // Enter on an item continues the list; Enter on an empty item ends it. Returns the new text and caret, or null.
    public static (string Text, int Caret)? Enter(string text, int caret)
    {
        int start = LineStart(text, caret); string line = LineAt(text, caret);
        if (!IsItem(line)) return null;
        if (Body(line).Trim().Length == 0) return (text[..start] + text[LineEnd(text, caret)..], start);
        string inserted = "\n" + OpenPrefix;
        return (text[..caret] + inserted + text[caret..], caret + inserted.Length);
    }
    // Backspace right after a marker removes the marker (the line becomes plain text).
    public static (string Text, int Caret)? Backspace(string text, int caret)
    {
        int start = LineStart(text, caret); string line = LineAt(text, caret);
        if (!IsItem(line) || caret - start != Math.Min(2, line.Length)) return null;
        return (text[..start] + Body(line) + text[LineEnd(text, caret)..], start);
    }
    // Text with the markers turned into words for previews and exports that should read naturally.
    public static string Preview(string text) => text.Replace(DonePrefix, "✓ ").Replace("● ", "✓ ").Replace(OpenPrefix, "").Replace("○ ", "");
}

// Paints over the marker characters of a TextBox: a ring for open items, a filled accent circle with a tick for done
// ones, and a line through finished text. Purely visual; the TextBox keeps owning the text and the caret.
public sealed class ChecklistAdorner : Adorner
{
    private readonly TextBox box;
    // Repaint only when the text, the scroll position or the size changes. (Repainting on every LayoutUpdated
    // would itself schedule a layout pass and keep a CPU core busy for as long as a note is open.)
    public ChecklistAdorner(TextBox box) : base(box)
    {
        this.box = box; IsHitTestVisible = false;
        box.TextChanged += (_, _) => InvalidateVisual();
        box.SizeChanged += (_, _) => InvalidateVisual();
        box.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => InvalidateVisual()));
    }
    public static void Attach(TextBox box)
    {
        box.Loaded += (_, _) => { var layer = AdornerLayer.GetAdornerLayer(box); if (layer != null && (layer.GetAdorners(box)?.OfType<ChecklistAdorner>().Any() != true)) layer.Add(new ChecklistAdorner(box)); };
    }
    // The marker of the item under a point, or -1: the caller toggles the line when the check box itself is clicked.
    public static int MarkerAt(TextBox box, Point point)
    {
        int index = box.GetCharacterIndexFromPoint(point, true);
        if (index < 0) return -1;
        string text = box.Text; int start = Checklist.LineStart(text, index);
        if (!Checklist.IsItem(Checklist.LineAt(text, index))) return -1;
        var rect = box.GetRectFromCharacterIndex(start);
        return point.X <= rect.Left + rect.Height + 4 && point.Y >= rect.Top - 2 && point.Y <= rect.Bottom + 2 ? start : -1;
    }
    protected override void OnRender(DrawingContext dc)
    {
        string text = box.Text;
        if (text.IndexOf(Checklist.Open) < 0 && text.IndexOf(Checklist.Done) < 0) return;
        var accent = (Brush)box.FindResource("Accent"); var muted = (Brush)box.FindResource("Muted"); var canvas = (Brush)box.FindResource("Canvas");
        var ring = new Pen(muted, 1.5); var tick = new Pen(new SolidColorBrush(Color.FromRgb(0x29, 0x24, 0x1C)), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        // Before the first layout pass the TextBox reports no lines at all.
        if (box.LineCount <= 0 || box.ActualWidth <= 0) return;
        int first = Math.Max(0, box.GetFirstVisibleLineIndex()), last = Math.Min(box.LineCount - 1, Math.Max(first, box.GetLastVisibleLineIndex()));
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, box.ActualWidth, box.ActualHeight)));
        for (int visual = first; visual <= last; visual++)
        {
            int at = box.GetCharacterIndexFromLineIndex(visual);
            if (at < 0 || at > text.Length) continue;
            int start = Checklist.LineStart(text, at);
            string line = Checklist.LineAt(text, at);
            if (!Checklist.IsItem(line)) continue;
            bool done = Checklist.IsDone(line);
            if (at == start)
            {
                // Hide the marker glyph and its gap, and draw the check box in their place. Positions come from the
                // TextBox in its own coordinate frame (mirrored for right-to-left text, as is this drawing), and the
                // text begins at the trailing edge of the gap character whichever way the text runs.
                var glyph = box.GetRectFromCharacterIndex(start);
                double textEdge = box.GetRectFromCharacterIndex(start + Math.Min(1, line.Length - 1), true).Left;
                var cover = new Rect(glyph.Left - 1, glyph.Top, Math.Max(0, textEdge - glyph.Left), glyph.Height);
                dc.DrawRectangle(canvas, null, cover);
                double d = Math.Min(16, Math.Max(10, cover.Width - 8)), cx = glyph.Left + d / 2 + 1, cy = glyph.Top + glyph.Height / 2;
                if (done) { dc.DrawEllipse(accent, null, new Point(cx, cy), d / 2, d / 2); dc.DrawLine(tick, new Point(cx - d * 0.26, cy), new Point(cx - d * 0.07, cy + d * 0.2)); dc.DrawLine(tick, new Point(cx - d * 0.07, cy + d * 0.2), new Point(cx + d * 0.28, cy - d * 0.22)); }
                else dc.DrawEllipse(null, ring, new Point(cx, cy), d / 2, d / 2);
            }
            if (!done) continue;
            // A line through the finished text on this visual row.
            int rowEnd = Math.Min(Checklist.LineEnd(text, at), visual + 1 <= box.LineCount - 1 ? box.GetCharacterIndexFromLineIndex(visual + 1) : text.Length);
            int textStart = at == start ? Math.Min(start + 2, rowEnd) : at;
            if (rowEnd <= textStart) continue;
            var a = box.GetRectFromCharacterIndex(textStart); var b = box.GetRectFromCharacterIndex(rowEnd - 1, true);
            double y = a.Top + a.Height * 0.55, x1 = Math.Min(a.Left, b.Left), x2 = Math.Max(a.Left, b.Left);
            if (x2 - x1 > 1) dc.DrawLine(new Pen(muted, 1.2), new Point(x1, y), new Point(x2 - 1, y));
        }
        dc.Pop();
    }
}
