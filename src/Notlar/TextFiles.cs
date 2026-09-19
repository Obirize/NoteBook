using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Notlar;

// Files that become notes and notes that become files. Everything is text in the end: plain files in any of the
// usual encodings, Markdown (task lists map onto checklists), HTML (tags stripped), RTF and Word documents (text
// only). Exports write plain text or Markdown; the note stays in the vault, the file is an unencrypted copy.
public static class TextFiles
{
    public static readonly string[] PlainExtensions = [".txt", ".text", ".md", ".markdown", ".log", ".json", ".csv", ".tsv", ".xml", ".ini", ".cfg", ".conf", ".yaml", ".yml", ".nfo"];
    public static readonly string[] MarkupExtensions = [".html", ".htm"];
    public static readonly string[] RichExtensions = [".rtf", ".docx"];
    public static IEnumerable<string> Extensions => PlainExtensions.Concat(MarkupExtensions).Concat(RichExtensions);
    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static string OpenPatterns => string.Join(";", Extensions.Select(x => "*" + x));
    public const long MaxBytes = 8 * 1024 * 1024;

    public static Note Read(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (!IsSupported(path)) throw new InvalidDataException(L10n.T("TxtSelectFile"));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxBytes) throw new InvalidDataException(L10n.T("TxtTooLarge"));
        string text = ext switch
        {
            ".docx" => DocxText(stream),
            ".rtf" => RtfText(ReadText(stream)),
            ".html" or ".htm" => HtmlText(ReadText(stream)),
            ".md" or ".markdown" => Markdown.ToNoteText(ReadText(stream)),
            _ => ReadText(stream),
        };
        if (text.Contains('\0')) throw new InvalidDataException(L10n.T("TxtNotText"));
        return new Note { Title = Path.GetFileNameWithoutExtension(path), Text = text.Replace("\r\n", "\n").Replace('\r', '\n') };
    }
    // UTF-8 (with or without BOM), UTF-16/32 with BOM, and otherwise the Turkish Windows code page older Notepad files use.
    private static string ReadText(Stream stream)
    {
        try { using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 4096, true); return reader.ReadToEnd(); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            stream.Position = 0;
            using var reader = new StreamReader(stream, Encoding.GetEncoding(1254), false, 4096, true);
            return reader.ReadToEnd();
        }
    }
    // WPF's own RTF reader, through a FlowDocument, gives the plain text of a WordPad or Notepad-era RTF file.
    private static string RtfText(string rtf)
    {
        var document = new System.Windows.Documents.FlowDocument();
        var range = new System.Windows.Documents.TextRange(document.ContentStart, document.ContentEnd);
        using var buffer = new MemoryStream(Encoding.UTF8.GetBytes(rtf));
        try { range.Load(buffer, System.Windows.DataFormats.Rtf); }
        catch (ArgumentException) { throw new InvalidDataException(L10n.T("TxtNotText")); }
        return range.Text.TrimEnd('\r', '\n');
    }
    // A .docx is a zip; word/document.xml holds paragraphs (w:p) of runs of text (w:t), tabs (w:tab) and breaks (w:br).
    private static string DocxText(Stream stream)
    {
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, true);
            var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException(L10n.T("TxtNotText"));
            var xml = new XmlDocument(); using (var entryStream = entry.Open()) xml.Load(entryStream);
            var sb = new StringBuilder();
            foreach (XmlNode paragraph in xml.GetElementsByTagName("w:p"))
            {
                foreach (XmlNode node in paragraph.SelectNodes(".//*")!)
                    switch (node.LocalName) { case "t": sb.Append(node.InnerText); break; case "tab": sb.Append('\t'); break; case "br": sb.Append('\n'); break; }
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException) { throw new InvalidDataException(L10n.T("TxtNotText")); }
    }
    // Scripts and styles dropped, block elements become line breaks, tags removed, entities decoded.
    public static string HtmlText(string html)
    {
        string s = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</(p|div|h[1-6]|li|tr|blockquote|pre|section|article|header|footer)\s*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", "");
        s = System.Net.WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"[ \t]+\n", "\n");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }
    public static string SuggestedName(Note note, string extension = ".txt")
    {
        string name = string.Concat(note.DisplayTitle.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        if (name.Length > 100) name = name[..100];
        return (string.IsNullOrWhiteSpace(name) ? "Not" : name) + extension;
    }
    // Plain text keeps the note's text as it is; Markdown adds the title as a heading and writes checklists as task lists.
    public static void Write(string path, Note note)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        // Only text extensions: a note must never be written over a vault, a backup or anything else by accident.
        if (!PlainExtensions.Contains(ext)) throw new InvalidDataException(L10n.T("TxtExtension"));
        string content = ext is ".md" or ".markdown" ? Markdown.FromNote(note) : note.Text;
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

// Markdown task lists ("- [ ] item" / "- [x] item") and the app's checklist lines translate into each other.
public static class Markdown
{
    private static readonly Regex Task = new(@"^(\s*)[-*+]\s+\[( |x|X)\]\s?(.*)$");
    public static string ToNoteText(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var m = Task.Match(lines[i]);
            if (m.Success) lines[i] = (m.Groups[2].Value == " " ? Checklist.OpenPrefix : Checklist.DonePrefix) + m.Groups[3].Value;
        }
        return string.Join("\n", lines);
    }
    public static string FromNote(Note note)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(note.Title)) sb.Append("# ").Append(note.Title.Trim()).Append("\n\n");
        foreach (var line in note.Text.Replace("\r\n", "\n").Split('\n'))
            sb.Append(Checklist.IsItem(line) ? (Checklist.IsDone(line) ? "- [x] " : "- [ ] ") + Checklist.Body(line) : line).Append('\n');
        return sb.ToString().TrimEnd('\n') + "\n";
    }
}
