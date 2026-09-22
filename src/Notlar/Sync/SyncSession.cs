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
    private readonly System.Net.IPAddress remote, local;
    internal System.Net.IPAddress Remote => remote;
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly CancellationTokenSource closed = new();
    private readonly List<Note> pendingNotes = []; private readonly List<PurgeStamp> pendingPurges = [];
    private readonly Dictionary<string, Note> bases = [];
    private Manifest? theirs;
    private (string Id, long Size, FileStream Stream)? receiving;
    private const int FileChunk = 256 * 1024, MaxText = 8 * 1024 * 1024;
    private static readonly TimeSpan SendDeadline = TimeSpan.FromSeconds(30), CloseGrace = TimeSpan.FromSeconds(2);
    public ConnectedDevice? Device { get; private set; }
    // A phone that sends "hb" in its hello pings while it is on screen; only such sessions are dropped for silence.
    public bool Heartbeat { get; private set; }
    public string EndReason { get; private set; } = "closed by phone";
    private readonly DateTime started = DateTime.UtcNow;
    public TimeSpan Duration => DateTime.UtcNow - started;
    private string closeReason = "stopped";

    public SyncSession(SyncService service, ISyncHost host, WebSocket socket, byte[] syncKey, System.Net.IPAddress? remote = null, System.Net.IPAddress? local = null)
    { this.service = service; this.host = host; this.socket = socket; this.remote = remote ?? System.Net.IPAddress.None; this.local = local ?? System.Net.IPAddress.None; keys = new SyncKeys(syncKey); CryptographicOperations.ZeroMemory(syncKey); }
    public void Close(string reason = "stopped") { closeReason = reason; closed.Cancel(); }

    public async Task RunAsync(CancellationToken stop)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, closed.Token);
        var token = linked.Token;
        try
        {
            if (!await Handshake(token)) return;
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                string? text; byte[]? binary;
                if (Heartbeat)
                {
                    // The silence clock runs only while we wait for the phone, never while we work for it.
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(token); idle.CancelAfter(service.IdleTimeout);
                    try { (text, binary) = await Receive(idle.Token); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { EndReason = "idle"; break; }
                }
                else (text, binary) = await Receive(token);
                if (text == null && binary == null) break;
                if (binary != null) { await ReceiveChunk(binary); continue; }
                var message = JsonNode.Parse(text!) as JsonObject;
                if (message == null) continue;
                await Handle(message, token);
            }
        }
        catch (OperationCanceledException) { EndReason = closed.IsCancellationRequested ? closeReason : "stopped"; }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or System.Text.Json.JsonException or CryptographicException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
        { EndReason = EndReason == "closed by phone" ? "error " + ex.GetType().Name : EndReason; }
        finally
        {
            if (receiving != null) { receiving.Value.Stream.Dispose(); TryDelete(receiving.Value.Stream.Name); receiving = null; }
            await CloseBounded(WebSocketCloseStatus.NormalClosure, "bye");
        }
    }
    // A close that always returns: say goodbye, give the phone two seconds to answer, then cut the socket.
    private async Task CloseBounded(WebSocketCloseStatus status, string reason)
    {
        using var grace = new CancellationTokenSource(CloseGrace);
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(status, reason, grace.Token);
                var drain = new byte[4096];
                while (socket.State == WebSocketState.CloseSent) { var r = await socket.ReceiveAsync(drain, grace.Token); if (r.MessageType == WebSocketMessageType.Close) break; }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { }
        finally { try { socket.Abort(); } catch (ObjectDisposedException) { } }
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
        if (clientNonce.Length != 32 || !CryptographicOperations.FixedTimeEquals(mac, keys.Mac("client", serverNonce, clientNonce))) { service.LogEvent(remote, L10n.T("SyncLogKeyMismatch")); await Reject("key"); return false; }
        Heartbeat = hello["hb"]?.GetValue<bool>() == true;
        // The phone tells us why its previous link ended and how long it was away: the one line that explains a gap.
        var diag = hello["diag"] as JsonObject; var summary = new List<string>();
        if (diag?["prev"]?.GetValue<string>() is { Length: > 0 } prev) summary.Add("prev " + prev);
        if (diag?["hidden"]?.GetValue<double>() is double hidden and > 0) summary.Add("hidden " + Math.Round(hidden / 1000) + " s");
        if (diag?["tries"]?.GetValue<int>() is int tries and > 0) summary.Add("tries " + tries);
        if (diag?["boot"]?.GetValue<bool>() == true) summary.Add("boot");
        if (!Heartbeat) summary.Add(L10n.T("SyncLogOldCopy"));
        service.LogEvent(remote, L10n.T("SyncLogAuthOk", deviceName) + (summary.Count > 0 ? " (" + string.Join(", ", summary) + ")" : ""));
        var (manifest, notebookId) = await host.ManifestAsync();
        var addresses = new JsonArray(service.AdvertisedAddresses(local).Select(a => (JsonNode)a).ToArray());
        await Send(new JsonObject { ["t"] = "welcome", ["mac"] = Convert.ToBase64String(keys.Mac("server", clientNonce, serverNonce)), ["name"] = service.PcName, ["notebook"] = notebookId, ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["host"] = service.LocalName, ["port"] = service.Port, ["addresses"] = addresses, ["idle"] = (long)service.IdleTimeout.TotalMilliseconds }, limit.Token);
        Device = new ConnectedDevice { Id = deviceId, Name = deviceName };
        service.DeviceSeen(this, deviceId, deviceName);
        await host.DeviceSeenAsync(deviceId, deviceName);
        return true;
    }
    private async Task Reject(string reason)
    {
        EndReason = "rejected " + reason;
        try { await Send(new JsonObject { ["t"] = "rejected", ["reason"] = reason }, CancellationToken.None); } catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException) { }
        await CloseBounded(WebSocketCloseStatus.PolicyViolation, reason);
    }
    private async Task Handle(JsonObject message, CancellationToken token)
    {
        switch (message["t"]?.GetValue<string>())
        {
            case "ping":
                await Send(new JsonObject { ["t"] = "pong", ["id"] = message["id"]?.DeepClone() }, token);
                break;
            case "manifest":
                theirs = Manifest.Parse(message);
                var (mine, _) = await host.ManifestAsync();
                await Send(mine.ToJson(), token);
                await SendNotes(await NotesToSend(theirs, mine), token);
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
        string device = Device?.Id ?? "?";
        foreach (var note in notes)
        {
            var existing = current.FirstOrDefault(n => n.Id == note.Id);
            if (existing == null || SyncMerge.SameContent(existing, note)) continue;
            // The PC's version is what this very phone gave us earlier: the phone is simply continuing its own work.
            string existingPrint = SyncMerge.Fingerprint(existing);
            if (existingPrint == service.Applied(device, note.Id)) continue;
            // No baseline, or the PC still holds the version the phone started from: a plain update.
            if (!bases.TryGetValue(note.Id, out var baseline) || SyncMerge.SameContent(existing, baseline)) continue;
            // Someone else changed the note meanwhile. The phone's version becomes a copy; while the phone keeps
            // sending edits built on that same stale baseline, they all go into that one copy.
            string baselinePrint = SyncMerge.Fingerprint(baseline);
            var prior = service.ConflictCopy(device, note.Id);
            var copy = prior?.Baseline == baselinePrint ? (await host.NotesAsync([prior.Value.Copy])).FirstOrDefault() : null;
            string original = note.Id;
            note.Title = L10n.T("ConflictCopyTitle", note.DisplayTitle, Device?.Name ?? "phone");
            if (copy != null) { note.Id = copy.Id; note.Revision = copy.Revision + 1; }
            else { note.Id = Guid.NewGuid().ToString("N"); note.Revision = 1; service.RecordConflictCopy(device, original, baselinePrint, note.Id); }
            note.Deleted = false; note.DeletedAt = null; note.Archived = false;
            current = current.Where(n => n.Id != note.Id).Append(SyncMerge.Clone(note)).ToList();
        }
        bases.Clear();
        var result = await host.ApplyAsync(notes, purges, Device?.Name ?? "?");
        foreach (var note in notes) service.RecordApplied(device, note.Id, SyncMerge.Fingerprint(note));
        // Other phones (not this one) learn about the change; conflict copies go back to this phone too.
        if (result.Changed.Count > 0) service.NotifyChanged(result.Changed, purges, this);
        await SendNotes(result.Changed.Concat(originals).Distinct().ToList(), token, submitted);
        await Send(new JsonObject { ["t"] = "flush" }, token);
    }
    private async Task<List<string>> NotesToSend(Manifest theirs, Manifest mine)
    {
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
        while ((read = await stream.ReadAsync(buffer, token)) > 0) await SendFrame(buffer.AsMemory(0, read), WebSocketMessageType.Binary, token);
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

    private Task Send(JsonObject message, CancellationToken token) => SendFrame(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, token);
    // No single frame may wait forever on a phone that stopped reading: 30 s and the session ends.
    private async Task SendFrame(ReadOnlyMemory<byte> bytes, WebSocketMessageType type, CancellationToken token)
    {
        await sendLock.WaitAsync(token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(SendDeadline);
            try { await socket.SendAsync(bytes, type, true, deadline.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { EndReason = "send stalled"; socket.Abort(); throw new WebSocketException("Send stalled."); }
        }
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
