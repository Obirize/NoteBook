using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Notlar.Sync;

// Attachment files between the PC and one phone. They travel as they are stored (encrypted), 256 KB per binary frame.
public sealed partial class SyncSession
{
    private (string Id, long Size, FileStream Stream)? receiving;
    private int receivedPercent;
    // Files asked of this phone that have not arrived yet. Each is asked for once, and the PC's tile shows it as on its
    // way (SyncService.Incoming) rather than missing.
    private readonly HashSet<string> awaited = [];
    private const int FileChunk = 256 * 1024;

    // The files of these notes that the PC lacks. A photo the phone has just added is asked for along with the note
    // that carries it, instead of after the phone's next full manifest.
    private async Task<List<string>> MissingFiles(IReadOnlyList<string> noteIds) =>
        (await host.NotesAsync(noteIds)).SelectMany(n => n.Attachments).Where(a => !host.Attachments.Exists(a)).Select(a => a.Id).Distinct().ToList();
    private async Task RequestFiles(IEnumerable<string> ids, CancellationToken token)
    {
        var wanted = ids.Where(awaited.Add).ToList();
        if (wanted.Count == 0) return;
        foreach (var id in wanted) service.Expect(id, 0);
        await Send(new JsonObject { ["t"] = "want-files", ["ids"] = new JsonArray(wanted.Select(w => (JsonNode)w).ToArray()) }, token);
    }
    // The file is no longer coming from this phone: it arrived, it was refused, or the phone has no copy either.
    private void Settle(string id) { if (awaited.Remove(id)) service.Settle(id); }
    private void SettleAll() { DropReceiving(); foreach (var id in awaited.ToList()) Settle(id); }

    private async Task SendFile(string id, CancellationToken token)
    {
        var attachment = await host.AttachmentAsync(id);
        if (attachment == null || !host.Attachments.Exists(attachment)) return;
        string path = host.Attachments.PathFor(attachment);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        await Send(new JsonObject { ["t"] = "file", ["id"] = id, ["size"] = stream.Length }, token);
        var buffer = new byte[FileChunk]; int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0) await SendFrame(buffer.AsMemory(0, read), WebSocketMessageType.Binary, token);
        await Send(new JsonObject { ["t"] = "file-end", ["id"] = id }, token);
    }
    private async Task StartFile(JsonObject message)
    {
        DropReceiving();
        string id = message["id"]!.GetValue<string>(); long size = message["size"]!.GetValue<long>();
        if (id.Length != 32 || !id.All(char.IsAsciiHexDigitLower) || size < 24 || size > 257L * 1024 * 1024 || await host.AttachmentAsync(id) == null) { Settle(id); return; }
        Directory.CreateDirectory(host.Attachments.Directory);
        receiving = (id, size, new FileStream(Path.Combine(host.Attachments.Directory, id + "." + Guid.NewGuid().ToString("N") + ".bin.part"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16));
        receivedPercent = 0;
    }
    private void DropReceiving()
    {
        if (receiving == null) return;
        var (id, _, stream) = receiving.Value; receiving = null;
        stream.Dispose(); TryDelete(stream.Name); Settle(id);
    }
    private async Task ReceiveChunk(byte[] chunk)
    {
        if (receiving == null) return;
        var (id, size, stream) = receiving.Value;
        if (stream.Length + chunk.Length > size) { DropReceiving(); return; }
        await stream.WriteAsync(chunk);
        // The tile counts along in whole percents; a frame that moves no number tells nobody.
        int percent = (int)(stream.Length * 100 / size);
        if (percent != receivedPercent && awaited.Contains(id)) { receivedPercent = percent; service.Expect(id, percent); }
    }
    // The file is only kept when it decrypts under the key the note carries: a wrong or damaged file never lands.
    private async Task FinishFile()
    {
        if (receiving == null) return;
        var (id, size, stream) = receiving.Value; receiving = null;
        string part = stream.Name, final = Path.Combine(host.Attachments.Directory, id + ".bin");
        try
        {
            if (stream.Length != size) return;
            var attachment = await host.AttachmentAsync(id);
            if (attachment == null) return;
            stream.Position = 0;
            AttachmentStore.Decrypt(stream, Stream.Null, attachment.Key, id);
            stream.Dispose();
            File.Move(part, final, true);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException) { }
        finally { stream.Dispose(); TryDelete(part); Settle(id); }
    }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
}
