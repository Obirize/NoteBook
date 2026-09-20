using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Notlar.Sync;

// Keys derived from the shared sync key: one authenticates devices, the other seals note contents.
// Neither the TLS layer nor anything on the network ever sees a note in the clear.
public sealed class SyncKeys : IDisposable
{
    public const int Protocol = 1;
    public byte[] Auth { get; }
    public byte[] Content { get; }
    public SyncKeys(byte[] syncKey)
    {
        if (syncKey.Length != 32) throw new ArgumentException("Sync key must be 256 bits.");
        Auth = HKDF.DeriveKey(HashAlgorithmName.SHA256, syncKey, 32, null, "notlar-sync-auth"u8.ToArray());
        Content = HKDF.DeriveKey(HashAlgorithmName.SHA256, syncKey, 32, null, "notlar-sync-content"u8.ToArray());
    }
    public byte[] Mac(string role, byte[] first, byte[] second)
    {
        using var hmac = new HMACSHA256(Auth);
        hmac.TransformBlock(Encoding.UTF8.GetBytes(role), 0, role.Length, null, 0);
        hmac.TransformBlock(first, 0, first.Length, null, 0);
        hmac.TransformFinalBlock(second, 0, second.Length);
        return hmac.Hash!;
    }
    // A note as it travels and as the phone stores it: nonce | ciphertext | tag over the note's JSON, bound to id and revision.
    public byte[] Seal(Note note)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(note);
        var nonce = RandomNumberGenerator.GetBytes(12); var cipher = new byte[plain.Length]; var tag = new byte[16];
        try { using var aes = new AesGcm(Content, 16); aes.Encrypt(nonce, plain, cipher, tag, Aad(note.Id, note.Revision)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        return [.. nonce, .. cipher, .. tag];
    }
    public Note Open(string id, long revision, byte[] blob)
    {
        if (blob.Length < 28) throw new CryptographicException("Note blob too short.");
        var plain = new byte[blob.Length - 28];
        try
        {
            using var aes = new AesGcm(Content, 16);
            aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(12, plain.Length), blob.AsSpan(blob.Length - 16), plain, Aad(id, revision));
            var note = JsonSerializer.Deserialize<Note>(plain) ?? throw new CryptographicException("Empty note.");
            if (note.Id != id || note.Revision != revision) throw new CryptographicException("Note identity mismatch.");
            if (id.Length != 32 || !id.All(char.IsAsciiHexDigitLower) || revision < 1 || revision > 9007199254740991 || note.Title == null || note.Text == null) throw new CryptographicException("Invalid note.");
            note.Attachments ??= [];
            if (note.Attachments.Any(a => a == null || a.Id.Length != 32 || !a.Id.All(char.IsAsciiHexDigitLower) || a.Key.Length != 32 || a.Size < 0)) throw new CryptographicException("Invalid attachment.");
            return note;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private static byte[] Aad(string id, long revision) => Encoding.UTF8.GetBytes("Notlar/note/" + id + "/" + revision);
    public void Dispose() { CryptographicOperations.ZeroMemory(Auth); CryptographicOperations.ZeroMemory(Content); }
}

public sealed record NoteStamp(string Id, long Revision, DateTimeOffset Updated);
public sealed record PurgeStamp(string Id, long Revision);
public sealed class Manifest
{
    public List<NoteStamp> Notes { get; set; } = [];
    public List<PurgeStamp> Purged { get; set; } = [];
    public HashSet<string> Files { get; set; } = [];

    public static Manifest Of(Notebook book, AttachmentStore store) => new()
    {
        Notes = book.Notes.Select(n => new NoteStamp(n.Id, n.Revision, n.Updated)).ToList(),
        Purged = book.Purged.Select(p => new PurgeStamp(p.Id, p.Revision)).ToList(),
        Files = book.Notes.SelectMany(n => n.Attachments).Where(store.Exists).Select(a => a.Id).ToHashSet(),
    };
    public JsonObject ToJson() => new()
    {
        ["t"] = "manifest",
        ["notes"] = new JsonArray(Notes.Select(n => (JsonNode)new JsonObject { ["id"] = n.Id, ["rev"] = n.Revision, ["updated"] = n.Updated.ToUnixTimeMilliseconds() }).ToArray()),
        ["purged"] = new JsonArray(Purged.Select(p => (JsonNode)new JsonObject { ["id"] = p.Id, ["rev"] = p.Revision }).ToArray()),
        ["files"] = new JsonArray(Files.Select(f => (JsonNode)f).ToArray()),
    };
    public static Manifest Parse(JsonObject json) => new()
    {
        Notes = json["notes"]?.AsArray().Select(n => new NoteStamp(n!["id"]!.GetValue<string>(), n["rev"]!.GetValue<long>(), DateTimeOffset.FromUnixTimeMilliseconds(n["updated"]!.GetValue<long>()))).ToList() ?? [],
        Purged = json["purged"]?.AsArray().Select(p => new PurgeStamp(p!["id"]!.GetValue<string>(), p["rev"]!.GetValue<long>())).ToList() ?? [],
        Files = json["files"]?.AsArray().Select(f => f!.GetValue<string>()).ToHashSet() ?? [],
    };
}

// Applies notes that arrived from another device. Rules: the higher revision wins; an equal revision with different
// content means both devices edited the same version, so the older one is kept as a separate "conflict" note rather
// than lost; a purge removes a note only if that device did not edit it afterwards.
public static class SyncMerge
{
    public sealed class Result { public int Added, Updated, Conflicts, Purged; public List<string> Changed = []; }
    public static Result Apply(Notebook book, IEnumerable<Note> incoming, IEnumerable<PurgeStamp> purges, string deviceName)
    {
        var result = new Result();
        var now = DateTimeOffset.UtcNow;
        foreach (var note in incoming)
        {
            var purged = book.Purged.FirstOrDefault(p => p.Id == note.Id);
            if (purged != null && note.Revision <= purged.Revision) continue;
            var existing = book.Notes.FirstOrDefault(n => n.Id == note.Id);
            if (existing == null) { book.Notes.Add(note); result.Added++; result.Changed.Add(note.Id); continue; }
            if (note.Revision < existing.Revision || (note.Revision == existing.Revision && note.Updated <= existing.Updated && SameContent(note, existing))) continue;
            if (note.Revision == existing.Revision && !SameContent(note, existing))
            {
                // Same base edited on both sides: keep the newer as the note and the older as a copy nobody has to look for.
                var older = note.Updated >= existing.Updated ? Clone(existing) : Clone(note);
                var newer = note.Updated >= existing.Updated ? note : existing;
                older.Id = Guid.NewGuid().ToString("N"); older.Revision = 1; older.Updated = now;
                older.Title = L10n.T("ConflictCopyTitle", older.DisplayTitle, note.Updated >= existing.Updated ? L10n.T("OnThisPc") : deviceName);
                older.Deleted = false; older.DeletedAt = null; older.Pinned = false; older.Archived = false;
                book.Notes.Add(older); result.Conflicts++; result.Changed.Add(older.Id);
                if (!ReferenceEquals(newer, existing)) Copy(newer, existing);
                existing.Revision++; existing.Updated = now;
                result.Changed.Add(existing.Id);
                continue;
            }
            Copy(note, existing); result.Updated++; result.Changed.Add(existing.Id);
        }
        foreach (var purge in purges)
        {
            var existing = book.Notes.FirstOrDefault(n => n.Id == purge.Id);
            if (existing != null && existing.Revision <= purge.Revision) { book.Notes.Remove(existing); result.Purged++; result.Changed.Add(existing.Id); }
            if (!book.Purged.Any(p => p.Id == purge.Id && p.Revision >= purge.Revision))
            { book.Purged.RemoveAll(p => p.Id == purge.Id); book.Purged.Add(new PurgeRecord { Id = purge.Id, Revision = purge.Revision, At = now }); }
        }
        return result;
    }
    public static bool SameContent(Note a, Note b) => a.Title == b.Title && a.Text == b.Text && a.Pinned == b.Pinned && a.Archived == b.Archived && a.Deleted == b.Deleted
        && a.Attachments.Select(x => x.Id).SequenceEqual(b.Attachments.Select(x => x.Id));
    private static void Copy(Note from, Note to)
    {
        to.Title = from.Title; to.Text = from.Text; to.Created = from.Created; to.Updated = from.Updated; to.Pinned = from.Pinned; to.Archived = from.Archived;
        to.Deleted = from.Deleted; to.DeletedAt = from.DeletedAt; to.Revision = from.Revision;
        to.Attachments = from.Attachments.Select(Clone).ToList();
    }
    public static Note Clone(Note n) => new() { Id = n.Id, Title = n.Title, Text = n.Text, Created = n.Created, Updated = n.Updated, Pinned = n.Pinned, Archived = n.Archived, Deleted = n.Deleted, DeletedAt = n.DeletedAt, Revision = n.Revision, Attachments = n.Attachments.Select(Clone).ToList() };
    private static Attachment Clone(Attachment a) => new() { Id = a.Id, Name = a.Name, MediaType = a.MediaType, Size = a.Size, Key = (byte[])a.Key.Clone(), Sha256 = (byte[])a.Sha256.Clone(), Width = a.Width, Height = a.Height, Added = a.Added };
}
