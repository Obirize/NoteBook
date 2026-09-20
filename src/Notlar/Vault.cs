using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Notlar;

public sealed class SealedData
{
    public byte[] Nonce { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
    public byte[] Tag { get; set; } = [];
}

public sealed class VaultEnvelope
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Iterations { get; set; } = 600_000;
    public byte[] Salt { get; set; } = RandomNumberGenerator.GetBytes(32);
    public SealedData PasswordKey { get; set; } = new();
    public SealedData Content { get; set; } = new();
    public byte[] DeviceKey { get; set; } = [];
}

public sealed class VaultSession : IDisposable
{
    private byte[] key;
    private VaultEnvelope envelope;
    public Notebook Book { get; private set; }
    public string FilePath { get; }
    public bool Disposed { get; private set; }
    // Encrypted photo and video files: "attachments" beside the vault, or "<backup>.files" beside a portable backup.
    public AttachmentStore Attachments { get; private set; }
    private VaultSession(string path, VaultEnvelope header, byte[] secret, Notebook book)
    { FilePath = path; envelope = header; key = secret; Book = book; Attachments = new AttachmentStore(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "attachments")); }
    private static Notebook ParseNotebook(byte[] plain)
    {
        var book = JsonSerializer.Deserialize<Notebook>(plain) ?? throw new InvalidDataException();
        if (book.SchemaVersion is < 1 or > Notebook.CurrentSchema || book.Notes == null) throw new InvalidDataException();
        foreach (var note in book.Notes) { note.Attachments ??= []; note.Attachments.RemoveAll(a => a == null || a.Id.Length == 0 || a.Key.Length != 32); }
        book.Purged ??= [];
        return book;
    }

    private static byte[] Aad(VaultEnvelope e, string purpose) => Encoding.UTF8.GetBytes("Notlar/v" + e.Version + "/" + e.Id + "/" + purpose);
    public static int ReadVersion(string path) => (JsonSerializer.Deserialize<VaultEnvelope>(File.ReadAllBytes(path)) ?? throw new InvalidDataException()).Version;
    public static VaultSession CreateDevice(string path, Notebook? book = null)
    {
        if (File.Exists(path) || File.Exists(path + ".bak")) throw new IOException("Notes already exist at this location.");
        var e = new VaultEnvelope { Version = 2, Salt = [], Iterations = 0 };
        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            e.DeviceKey = ProtectedData.Protect(secret, Aad(e, "device"), DataProtectionScope.CurrentUser);
            return new VaultSession(path, e, secret, book ?? new());
        }
        catch { CryptographicOperations.ZeroMemory(secret); throw; }
    }
    public static VaultSession OpenDevice(string path, string? targetPath = null)
    {
        var e = JsonSerializer.Deserialize<VaultEnvelope>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("The vault could not be read.");
        if (e.Version != 2 || e.Id.Length != 32 || e.DeviceKey.Length == 0) throw new InvalidDataException("Unsupported vault format.");
        byte[]? secret = null; byte[]? plain = null;
        try
        {
            secret = ProtectedData.Unprotect(e.DeviceKey, Aad(e, "device"), DataProtectionScope.CurrentUser);
            plain = Unseal(secret, e.Content, Aad(e, "content"));
            var book = ParseNotebook(plain);
            var session = new VaultSession(targetPath ?? path, e, secret, book); secret = null; return session;
        }
        finally { if (secret != null) CryptographicOperations.ZeroMemory(secret); if (plain != null) CryptographicOperations.ZeroMemory(plain); }
    }
    private static byte[] Derive(string password, VaultEnvelope e) => Rfc2898DeriveBytes.Pbkdf2(password, e.Salt, e.Iterations, HashAlgorithmName.SHA256, 32);
    private static SealedData Seal(byte[] secret, byte[] bytes, byte[] aad)
    {
        var result = new SealedData { Nonce = RandomNumberGenerator.GetBytes(12), Ciphertext = new byte[bytes.Length], Tag = new byte[16] };
        using var aes = new AesGcm(secret, 16);
        aes.Encrypt(result.Nonce, bytes, result.Ciphertext, result.Tag, aad);
        return result;
    }
    private static byte[] Unseal(byte[] secret, SealedData data, byte[] aad)
    {
        if (data.Nonce.Length != 12 || data.Tag.Length != 16) throw new CryptographicException("Invalid envelope.");
        var bytes = new byte[data.Ciphertext.Length];
        try { using var aes = new AesGcm(secret, 16); aes.Decrypt(data.Nonce, data.Ciphertext, data.Tag, bytes, aad); return bytes; }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }
    // A password-protected vault: the portable backup format. The password wraps the content key through PBKDF2.
    public static VaultSession Create(string path, string password, Notebook? book = null)
    {
        if (password.Length < 12) throw new ArgumentException("Use a password of at least 12 characters.");
        if (File.Exists(path) || File.Exists(path + ".bak")) throw new IOException("A vault already exists at this location.");
        var e = new VaultEnvelope();
        var secret = RandomNumberGenerator.GetBytes(32);
        var passwordKey = Derive(password, e);
        try { e.PasswordKey = Seal(passwordKey, secret, Aad(e, "password")); return new VaultSession(path, e, secret, book ?? new()); }
        catch { CryptographicOperations.ZeroMemory(secret); throw; }
        finally { CryptographicOperations.ZeroMemory(passwordKey); }
    }
    public static VaultSession Open(string path, string password, string? targetPath = null)
    {
        var e = JsonSerializer.Deserialize<VaultEnvelope>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("The vault could not be read.");
        if (e.Version != 1 || e.Salt.Length != 32 || e.Iterations != 600_000 || e.Id.Length != 32) throw new InvalidDataException("Unsupported vault format.");
        byte[] wrapping = Derive(password, e);
        byte[]? secret = null;
        byte[]? plain = null;
        try
        {
            if (wrapping.Length != 32) throw new CryptographicException();
            secret = Unseal(wrapping, e.PasswordKey, Aad(e, "password"));
            plain = Unseal(secret, e.Content, Aad(e, "content"));
            var book = ParseNotebook(plain);
            var session = new VaultSession(targetPath ?? path, e, secret, book);
            secret = null;
            return session;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapping);
            if (plain != null) CryptographicOperations.ZeroMemory(plain);
            if (secret != null) CryptographicOperations.ZeroMemory(secret);
        }
    }
    public static string BackupFilesDirectory(string backupPath) => backupPath + ".files";
    // A password-protected copy with its own random content key: opens on any PC, independent of Windows DPAPI.
    // Attachment files go to "<backup>.files" beside it, unchanged: their keys travel inside the encrypted notebook.
    public void ExportPortable(string path, string password)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (password.Length < 12) throw new ArgumentException("Use a password of at least 12 characters.");
        var e = new VaultEnvelope();
        var secret = RandomNumberGenerator.GetBytes(32);
        var wrapping = Derive(password, e);
        Book.SchemaVersion = Notebook.CurrentSchema;
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(Book);
        try
        {
            e.PasswordKey = Seal(wrapping, secret, Aad(e, "password"));
            e.Content = Seal(secret, plain, Aad(e, "content"));
        }
        finally { CryptographicOperations.ZeroMemory(secret); CryptographicOperations.ZeroMemory(wrapping); CryptographicOperations.ZeroMemory(plain); }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(e);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, null, true); else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        var files = new AttachmentStore(BackupFilesDirectory(path));
        if (Book.Notes.Any(n => n.Attachments.Count > 0)) files.CopyMissing(Book, Attachments);
        files.Sweep(Book);
    }
    // Opens either kind of backup read-only: a portable (password) file or a same-account (DPAPI) copy.
    public static VaultSession OpenBackup(string path, string? password)
    {
        VaultSession session;
        if (ReadVersion(path) == 2) session = OpenDevice(path);
        else if (string.IsNullOrEmpty(password)) throw new CryptographicException("Password required.");
        else session = Open(path, password);
        if (Directory.Exists(BackupFilesDirectory(path))) session.Attachments = new AttachmentStore(BackupFilesDirectory(path));
        return session;
    }
    // Merges notes by id; the higher revision (then the later update) wins, nothing is ever dropped.
    public static (int Added, int Updated) Merge(Notebook into, Notebook from)
    {
        int added = 0, updated = 0;
        foreach (var note in from.Notes)
        {
            var existing = into.Notes.FirstOrDefault(n => n.Id == note.Id);
            if (existing == null) { into.Notes.Add(Clone(note)); added++; continue; }
            if (note.Revision > existing.Revision || (note.Revision == existing.Revision && note.Updated > existing.Updated))
            {
                existing.Title = note.Title; existing.Text = note.Text; existing.Created = note.Created; existing.Updated = note.Updated;
                existing.Pinned = note.Pinned; existing.Archived = note.Archived; existing.Deleted = note.Deleted; existing.DeletedAt = note.DeletedAt; existing.Revision = note.Revision;
                existing.Attachments = note.Attachments.Select(Clone).ToList();
                updated++;
            }
        }
        foreach (var purge in from.Purged) if (!into.Purged.Any(p => p.Id == purge.Id && p.Revision >= purge.Revision)) { into.Purged.RemoveAll(p => p.Id == purge.Id); into.Purged.Add(new PurgeRecord { Id = purge.Id, Revision = purge.Revision, At = purge.At }); }
        return (added, updated);
    }
    private static Note Clone(Note n) => new() { Id = n.Id, Title = n.Title, Text = n.Text, Created = n.Created, Updated = n.Updated, Pinned = n.Pinned, Archived = n.Archived, Deleted = n.Deleted, DeletedAt = n.DeletedAt, Revision = n.Revision, Attachments = n.Attachments.Select(Clone).ToList() };
    private static Attachment Clone(Attachment a) => new() { Id = a.Id, Name = a.Name, MediaType = a.MediaType, Size = a.Size, Key = (byte[])a.Key.Clone(), Sha256 = (byte[])a.Sha256.Clone(), Width = a.Width, Height = a.Height, Added = a.Added };
    public void Save() => Save(false);
    public void Save(bool redactBackup)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        Book.SchemaVersion = Notebook.CurrentSchema;
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(Book);
        try { envelope.Content = Seal(key, plain, Aad(envelope, "content")); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { stream.Write(bytes); stream.Flush(true); }
        if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak", true);
        else File.Move(temp, FilePath);
        if (redactBackup)
        {
            // Purged contents must not survive in the local rolling backup.
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            if (File.Exists(FilePath + ".bak")) File.Replace(temp, FilePath + ".bak", null, true);
            else File.Move(temp, FilePath + ".bak");
        }
    }
    public void VerifySaved()
    {
        var stored = JsonSerializer.Deserialize<VaultEnvelope>(File.ReadAllBytes(FilePath)) ?? throw new InvalidDataException();
        if (stored.Version == 2)
        {
            var restoredKey = ProtectedData.Unprotect(stored.DeviceKey, Aad(stored, "device"), DataProtectionScope.CurrentUser);
            try { if (!CryptographicOperations.FixedTimeEquals(restoredKey, key)) throw new CryptographicException("Key verification failed."); }
            finally { CryptographicOperations.ZeroMemory(restoredKey); }
        }
        byte[] plain = Unseal(key, stored.Content, Aad(stored, "content"));
        try { if (!SameNotebook(plain, Book)) throw new IOException("Saved data verification failed."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    // Compare through the current model so a vault written by an older schema (missing newer fields) still verifies.
    public static bool SameNotebook(byte[] storedPlain, Notebook book)
    {
        byte[]? normalized = null; byte[]? expected = null;
        int schema = book.SchemaVersion;
        try
        {
            var parsed = JsonSerializer.Deserialize<Notebook>(storedPlain);
            if (parsed == null) return false;
            // The schema number only says which build wrote the file; the contents are what must match.
            parsed.SchemaVersion = book.SchemaVersion = Notebook.CurrentSchema;
            normalized = JsonSerializer.SerializeToUtf8Bytes(parsed); expected = JsonSerializer.SerializeToUtf8Bytes(book);
            return CryptographicOperations.FixedTimeEquals(normalized, expected);
        }
        catch (JsonException) { return false; }
        finally { book.SchemaVersion = schema; if (normalized != null) CryptographicOperations.ZeroMemory(normalized); if (expected != null) CryptographicOperations.ZeroMemory(expected); }
    }
    public void Dispose()
    {
        if (Disposed) return;
        CryptographicOperations.ZeroMemory(key); key = []; Book = new(); Disposed = true;
    }
}
