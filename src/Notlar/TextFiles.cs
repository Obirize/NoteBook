using System.IO;
using System.Text;

namespace Notlar;

public static class TextFiles
{
    public static Note Read(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(L10n.T("TxtSelectFile"));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 8 * 1024 * 1024) throw new InvalidDataException(L10n.T("TxtTooLarge"));
        string text;
        try { using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 4096, true); text = reader.ReadToEnd(); }
        catch (DecoderFallbackException)
        {
            // Older Turkish Windows Notepad files commonly use Windows-1254.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            stream.Position = 0;
            using var reader = new StreamReader(stream, Encoding.GetEncoding(1254), false, 4096, true);
            text = reader.ReadToEnd();
        }
        if (text.Contains('\0')) throw new InvalidDataException(L10n.T("TxtNotText"));
        return new Note { Title = Path.GetFileNameWithoutExtension(path), Text = text };
    }
    public static string SuggestedName(Note note)
    {
        string name = string.Concat(note.DisplayTitle.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        if (name.Length > 100) name = name[..100];
        return (string.IsNullOrWhiteSpace(name) ? "Not" : name) + ".txt";
    }
    public static void Write(string path, Note note)
    {
        if (!string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(L10n.T("TxtExtension"));
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, note.Text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
