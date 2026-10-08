using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Notlar.Sync;

// data/sync/memory.bin: per phone and note, the fingerprint of the version last applied from that phone, and the
// conflict copy (with the baseline it diverged from) its later edits on that baseline go to. It survives a restart of
// the PC: a phone that was mid-edit then still continues its own work instead of spawning copies. Protected with DPAPI
// like the sync key, since a fingerprint is a hash of the note's text.
public sealed class SyncMemory
{
    private readonly string path;
    private readonly Dictionary<(string Device, string Note), string> applied = [];
    private readonly Dictionary<(string Device, string Note), (string Baseline, string Copy)> copies = [];
    private bool changed;
    private static byte[] Aad => Encoding.UTF8.GetBytes("Notlar/sync/memory");
    private sealed class Stored { public List<string[]> Applied { get; set; } = []; public List<string[]> Copies { get; set; } = []; }

    public SyncMemory(string path) { this.path = path; Load(); }
    public string? Applied(string device, string note) { lock (applied) return applied.TryGetValue((device, note), out var f) ? f : null; }
    public void RecordApplied(string device, string note, string fingerprint)
    { lock (applied) { if (applied.TryGetValue((device, note), out var f) && f == fingerprint) return; applied[(device, note)] = fingerprint; changed = true; } }
    public (string Baseline, string Copy)? ConflictCopy(string device, string note) { lock (applied) return copies.TryGetValue((device, note), out var c) ? c : null; }
    public void RecordConflictCopy(string device, string note, string baseline, string copy) { lock (applied) { copies[(device, note)] = (baseline, copy); changed = true; } }

    // Written after each batch of edits a phone sends, and only when something new was learned.
    public void Save()
    {
        byte[] bytes;
        lock (applied)
        {
            if (!changed) return;
            var stored = new Stored
            {
                Applied = applied.Select(e => new[] { e.Key.Device, e.Key.Note, e.Value }).ToList(),
                Copies = copies.Select(e => new[] { e.Key.Device, e.Key.Note, e.Value.Baseline, e.Value.Copy }).ToList(),
            };
            bytes = JsonSerializer.SerializeToUtf8Bytes(stored); changed = false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, ProtectedData.Protect(bytes, Aad, DataProtectionScope.CurrentUser));
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { lock (applied) changed = true; }
    }
    // A missing or unreadable file only means starting without memory, as before this file existed.
    private void Load()
    {
        try
        {
            if (!File.Exists(path)) return;
            var stored = JsonSerializer.Deserialize<Stored>(ProtectedData.Unprotect(File.ReadAllBytes(path), Aad, DataProtectionScope.CurrentUser));
            foreach (var e in stored?.Applied ?? []) if (e is [var device, var note, var print]) applied[(device, note)] = print;
            foreach (var e in stored?.Copies ?? []) if (e is [var device, var note, var baseline, var copy]) copies[(device, note)] = (baseline, copy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException) { }
    }
}
