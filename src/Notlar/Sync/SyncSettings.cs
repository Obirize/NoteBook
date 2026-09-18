using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Notlar.Sync;

public sealed class PairedDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset PairedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeen { get; set; }
}

// data/sync/settings.json: whether the phone link is on, the port, the paired phones and the shared sync key
// (protected with DPAPI like the vault key). The sync key never leaves this file except inside the pairing QR code.
public sealed class SyncSettings
{
    public const int DefaultPort = 47831;
    public bool Enabled { get; set; }
    public int Port { get; set; } = DefaultPort;
    public byte[] ProtectedKey { get; set; } = [];
    public List<PairedDevice> Devices { get; set; } = [];

    public static string DirectoryFor(string dataDirectory) => Path.Combine(dataDirectory, "sync");
    public static string PathFor(string dataDirectory) => Path.Combine(DirectoryFor(dataDirectory), "settings.json");
    private static byte[] Aad => Encoding.UTF8.GetBytes("Notlar/sync/key");

    public static SyncSettings Load(string dataDirectory)
    {
        try
        {
            string path = PathFor(dataDirectory);
            if (File.Exists(path) && JsonSerializer.Deserialize<SyncSettings>(File.ReadAllBytes(path)) is SyncSettings settings)
            {
                settings.Devices ??= []; settings.ProtectedKey ??= [];
                if (settings.Port is < 1024 or > 65534) settings.Port = DefaultPort;
                return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new SyncSettings();
    }
    public void Save(string dataDirectory)
    {
        Directory.CreateDirectory(DirectoryFor(dataDirectory));
        string path = PathFor(dataDirectory), temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
    public bool HasKey => ProtectedKey.Length > 0;
    // The 256-bit sync key; created on first use. Callers zero the returned copy when done.
    public byte[] Key()
    {
        if (ProtectedKey.Length == 0) throw new InvalidOperationException("No sync key yet.");
        return ProtectedData.Unprotect(ProtectedKey, Aad, DataProtectionScope.CurrentUser);
    }
    public void NewKey()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        try { ProtectedKey = ProtectedData.Protect(key, Aad, DataProtectionScope.CurrentUser); Devices.Clear(); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
