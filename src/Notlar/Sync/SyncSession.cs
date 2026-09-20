using System.IO;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Notlar.Sync;


// One connected phone. Messages are JSON text frames ("t" names the type); attachment bytes are binary frames.
public sealed class SyncSession : IDisposable
{
    private readonly SyncService service;
    private readonly ISyncHost host;
    private readonly WebSocket socket;
    private readonly SyncKeys keys;
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly CancellationTokenSource closed = new();
    private readonly List<Note> pendingNotes = []; private readonly List<PurgeStamp> pendingPurges = [];
    private readonly Dictionary<string, Note> bases = [];
    // The last version of each note this phone sent us. A note the PC holds that equals it was written by this very
    // phone (it typed faster than our replies came back), so it is not someone else's edit and not a conflict.
    private readonly Dictionary<string, Note> lastSubmitted = [];
    private Manifest? theirs;
    private (string Id, long Size, FileStream Stream)? receiving;
    private const int FileChunk = 256 * 1024, MaxText = 8 * 1024 * 1024;
    public ConnectedDevice? Device { get; private set; }

    public SyncSession(SyncService service, ISyncHost host, WebSocket socket, byte[] syncKey)
    { this.service = service; this.host = host; this.socket = socket; keys = new SyncKeys(syncKey); CryptographicOperations.ZeroMemory(syncKey); }
    public void Close() => closed.Cancel();

    public async Task RunAsync(CancellationToken stop)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, closed.Token);
        var token = linked.Token;
        try
        {
            if (!await Handshake(token)) return;
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var (text, binary) = await Receive(token);
                if (text == null && binary == null) break;
                if (binary != null) { await ReceiveChunk(binary); continue; }
                var message = JsonNode.Parse(text!) as JsonObject;
                if (message == null) continue;
                await Handle(message, token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException or System.Text.Json.JsonException or CryptographicException or InvalidOperationException or ArgumentException or FormatException or OverflowException) { }
        finally
        {
            if (receiving != null) { receiving.Value.Stream.Dispose(); TryDelete(receiving.Value.Stream.Name); receiving = null; }
            try { if (socket.State == WebSocketState.Open) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException) { }
        }
    }
    private async Task<bool> Handshake(CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(20));
        var hello = await ReceiveObject(limit.Token);
        if (hello?["t"]?.GetValue<string>() != "hello" || hello["protocol"]?.GetValue<int>() != SyncKeys.Protocol) { await Reject("protocol"); return false; }
        string deviceId = hello["device"]?.GetValue<string>() ?? "", deviceName = hello["name"]?.GetValue<string>() ?? "?";
        if (deviceId.Length is 0 or > 64 || deviceName.Length > 64) { await Reject("device"); return false; }
        var serverNonce = RandomNumberGenerator.GetBytes(32);
        await Send(new JsonObject { ["t"] = "challenge", ["nonce"] = Convert.ToBase64String(serverNonce) }, limit.Token);
        var auth = await ReceiveObject(limit.Token);
        if (auth?["t"]?.GetValue<string>() != "auth") { await Reject("auth"); return false; }
        byte[] clientNonce, mac;
        try { clientNonce = Convert.FromBase64String(auth["nonce"]?.GetValue<string>() ?? ""); mac = Convert.FromBase64String(auth["mac"]?.GetValue<string>() ?? ""); }
        catch (FormatException) { await Reject("auth"); return false; }
        if (clientNonce.Length != 32 || !CryptographicOperations.FixedTimeEquals(mac, keys.Mac("client", serverNonce, clientNonce))) { await Reject("key"); return false; }
        var (manifest, notebookId) = await host.ManifestAsync();
        await Send(new JsonObject { ["t"] = "welcome", ["mac"] = Convert.ToBase64String(keys.Mac("server", clientNonce, serverNonce)), ["name"] = service.PcName, ["notebook"] = notebookId, ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, limit.Token);
        Device = new ConnectedDevice { Id = deviceId, Name = deviceName };
        service.DeviceSeen(this, deviceId, deviceName);
        await host.DeviceSeenAsync(deviceId, deviceName);
        return true;
    }
    private async Task Reject(string reason)
    {
        try { await Send(new JsonObject { ["t"] = "rejected", ["reason"] = reason }, CancellationToken.None); await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, CancellationToken.None); }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException) { }
    }
    private async Task Handle(JsonObject message, CancellationToken token)
    {
        switch (message["t"]?.GetValue<string>())
        {
            case "manifest":
                theirs = Manifest.Parse(message);
                var (mine, _) = await host.ManifestAsync();
                await Send(mine.ToJson(), token);
                var (book, _) = await host.ManifestAsync();
                await SendNotes(await NotesToSend(theirs), token);
                foreach (var purge in mine.Purged) await Send(new JsonObject { ["t"] = "purge", ["id"] = purge.Id, ["rev"] = purge.Revision }, token);
                await Send(new JsonObject { ["t"] = "flush" }, token);
                await Send(new JsonObject { ["t"] = "done" }, token);
                break;
            case "note":
                string id = message["id"]!.GetValue<string>(); long rev = message["rev"]!.GetValue<long>();
                byte[] blob = Convert.FromBase64String(message["blob"]!.GetValue<string>());
                pendingNotes.Add(keys.Open(id, rev, blob));
                if (message["base"] is JsonObject baseline)
                    bases[id] = keys.Open(id, baseline["rev"]!.GetValue<long>(), Convert.FromBase64String(baseline["blob"]!.GetValue<string>()));
                if (pendingNotes.Count >= 200) await Flush(token);
                break;
            case "purge":
                pendingPurges.Add(new PurgeStamp(message["id"]!.GetValue<string>(), message["rev"]!.GetValue<long>()));
                break;
            case "flush":
                await Flush(token);
                break;
            case "done":
                await Flush(token);
                // Now that every note is known, ask for the attachment files we lack and the phone has.
                if (theirs != null)
                {
                    var (current, _) = await host.ManifestAsync();
                    var wanted = theirs.Files.Where(f => !current.Files.Contains(f)).ToList();
                    if (wanted.Count > 0) await Send(new JsonObject { ["t"] = "want-files", ["ids"] = new JsonArray(wanted.Select(w => (JsonNode)w).ToArray()) }, token);
                }
                break;
            case "want-files":
                foreach (var node in message["ids"]?.AsArray() ?? []) if (node?.GetValue<string>() is string fileId) await SendFile(fileId, token);
                break;
            case "file":
                if (receiving != null) { receiving.Value.Stream.Dispose(); TryDelete(receiving.Value.Stream.Name); }
                string fileId2 = message["id"]!.GetValue<string>(); long size = message["size"]!.GetValue<long>();
                receiving = null;
                if (fileId2.Length != 32 || !fileId2.All(char.IsAsciiHexDigitLower) || size < 24 || size > 257L * 1024 * 1024 || await host.AttachmentAsync(fileId2) == null) break;
                Directory.CreateDirectory(host.Attachments.Directory);
                receiving = (fileId2, size, new FileStream(Path.Combine(host.Attachments.Directory, fileId2 + "." + Guid.NewGuid().ToString("N") + ".bin.part"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16));
                break;
            case "file-end":
                await FinishFile();
                break;
        }
    }
    private async Task Flush(CancellationToken token)
    {
        if (pendingNotes.Count == 0 && pendingPurges.Count == 0) return;
        var notes = pendingNotes.ToList(); var purges = pendingPurges.ToList(); pendingNotes.Clear(); pendingPurges.Clear();
        var originals = notes.Select(n => n.Id).ToList();
        var submitted = notes.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.Max(n => n.Revision));
        var current = await host.NotesAsync(originals);
        foreach (var note in notes)
        {
            var existing = current.FirstOrDefault(n => n.Id == note.Id);
            bool ownEarlierVersion = existing != null && lastSubmitted.TryGetValue(note.Id, out var mine) && SyncMerge.SameContent(existing, mine);
            lastSubmitted[note.Id] = note;
            if (existing != null && !ownEarlierVersion && bases.TryGetValue(note.Id, out var baseline) && !SyncMerge.SameContent(existing, baseline) && !SyncMerge.SameContent(existing, note))
            {
                note.Id = Guid.NewGuid().ToString("N"); note.Revision = 1;
                note.Title = L10n.T("ConflictCopyTitle", note.DisplayTitle, Device?.Name ?? "phone");
                note.Deleted = false; note.DeletedAt = null; note.Archived = false;
            }
        }
        bases.Clear();
        var result = await host.ApplyAsync(notes, purges, Device?.Name ?? "?");
        // Other phones (not this one) learn about the change; conflict copies go back to this phone too.
        if (result.Changed.Count > 0) service.NotifyChanged(result.Changed, purges, this);
        await SendNotes(result.Changed.Concat(originals).Distinct().ToList(), token, submitted);
        await Send(new JsonObject { ["t"] = "flush" }, token);
    }
    private async Task<List<string>> NotesToSend(Manifest theirs)
    {
        var (mine, _) = await host.ManifestAsync();
        var known = theirs.Notes.ToDictionary(n => n.Id); var purged = theirs.Purged.ToDictionary(p => p.Id, p => p.Revision);
        return mine.Notes.Where(n => !(purged.TryGetValue(n.Id, out long pr) && n.Revision <= pr) && (!known.TryGetValue(n.Id, out var s) || s.Revision < n.Revision || (s.Revision == n.Revision && s.Updated < n.Updated))).Select(n => n.Id).ToList();
    }
    private async Task SendNotes(IReadOnlyList<string> ids, CancellationToken token, Dictionary<string, long>? submitted = null)
    {
        if (ids.Count == 0) return;
        foreach (var note in await host.NotesAsync(ids))
            await Send(new JsonObject { ["t"] = "note", ["id"] = note.Id, ["rev"] = note.Revision, ["updated"] = note.Updated.ToUnixTimeMilliseconds(), ["deleted"] = note.Deleted, ["blob"] = Convert.ToBase64String(keys.Seal(note)), ["replyRev"] = submitted != null && submitted.TryGetValue(note.Id, out var revision) ? revision : null }, token);
    }
    public async Task PushAsync(IReadOnlyList<string> noteIds, IReadOnlyList<PurgeStamp> purges)
    {
        try
        {
            await SendNotes(noteIds, closed.Token);
            foreach (var purge in purges) await Send(new JsonObject { ["t"] = "purge", ["id"] = purge.Id, ["rev"] = purge.Revision }, closed.Token);
            await Send(new JsonObject { ["t"] = "flush" }, closed.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
    }
    private async Task SendFile(string id, CancellationToken token)
    {
        var attachment = await host.AttachmentAsync(id);
        if (attachment == null || !host.Attachments.Exists(attachment)) return;
        string path = host.Attachments.PathFor(attachment);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        await Send(new JsonObject { ["t"] = "file", ["id"] = id, ["size"] = stream.Length }, token);
        var buffer = new byte[FileChunk]; int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        {
            await sendLock.WaitAsync(token);
            try { await socket.SendAsync(buffer.AsMemory(0, read), WebSocketMessageType.Binary, true, token); }
            finally { sendLock.Release(); }
        }
        await Send(new JsonObject { ["t"] = "file-end", ["id"] = id }, token);
    }
    private async Task ReceiveChunk(byte[] chunk)
    {
        if (receiving == null) return;
        if (receiving.Value.Stream.Length + chunk.Length > receiving.Value.Size) { receiving.Value.Stream.Dispose(); TryDelete(receiving.Value.Stream.Name); receiving = null; return; }
        await receiving.Value.Stream.WriteAsync(chunk);
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
            await host.DeviceSeenAsync(Device?.Id ?? "", Device?.Name ?? "");
        }
        catch (Exception ex) when (ex is CryptographicException or IOException) { }
        finally { stream.Dispose(); TryDelete(part); }
    }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    private async Task Send(JsonObject message, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await sendLock.WaitAsync(token);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token); }
        finally { sendLock.Release(); }
    }
    private async Task<JsonObject?> ReceiveObject(CancellationToken token)
    {
        var (text, _) = await Receive(token);
        return text == null ? null : JsonNode.Parse(text) as JsonObject;
    }
    private async Task<(string? Text, byte[]? Binary)> Receive(CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        using var whole = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close) return (null, null);
            whole.Write(buffer, 0, result.Count);
            if (whole.Length > MaxText) throw new WebSocketException("Message too large.");
            if (result.EndOfMessage) return result.MessageType == WebSocketMessageType.Text ? (Encoding.UTF8.GetString(whole.GetBuffer(), 0, (int)whole.Length), null) : (null, whole.ToArray());
        }
    }
    public void Dispose() { closed.Cancel(); keys.Dispose(); sendLock.Dispose(); closed.Dispose(); }
}
