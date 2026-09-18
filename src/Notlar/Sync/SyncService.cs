using System.IO;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Notlar.Sync;

// What the sync layer needs from the notebook. Every call runs on the UI thread (the notebook is not thread-safe).
public interface ISyncHost
{
    Task<(Manifest Manifest, string NotebookId)> ManifestAsync();
    Task<List<Note>> NotesAsync(IReadOnlyList<string> ids);
    Task<Attachment?> AttachmentAsync(string id);
    Task<SyncMerge.Result> ApplyAsync(List<Note> notes, List<PurgeStamp> purges, string deviceName);
    Task DeviceSeenAsync(string id, string name);
    AttachmentStore Attachments { get; }
}

public sealed class ConnectedDevice { public string Id = ""; public string Name = ""; public DateTimeOffset Since = DateTimeOffset.UtcNow; }

// The phone link: an HTTPS + WebSocket server on the LAN, a plain HTTP page that hands out the root certificate,
// and one session per connected phone. Notes travel sealed with the content key; files travel as they are stored.
public sealed class SyncService : IDisposable
{
    private readonly string dataDirectory;
    private readonly ISyncHost host;
    private WebServer? secure, plain;
    private readonly List<SyncSession> sessions = [];
    public SyncSettings Settings { get; }
    public Certificates? Certs { get; private set; }
    public bool Running => secure != null;
    public string? Error { get; private set; }
    public event Action? StatusChanged;
    public IReadOnlyList<ConnectedDevice> Connected { get { lock (sessions) return sessions.Where(s => s.Device != null).Select(s => s.Device!).ToList(); } }
    public string PcName => Environment.MachineName;

    public SyncService(string dataDirectory, ISyncHost host)
    {
        this.dataDirectory = dataDirectory; this.host = host;
        Settings = SyncSettings.Load(dataDirectory);
    }
    public string SyncDirectory => SyncSettings.DirectoryFor(dataDirectory);
    public int Port => secure?.Port ?? Settings.Port;
    public string LocalName => Certs?.LocalName ?? (Environment.MachineName.ToLowerInvariant() + ".local");
    public string AppUrl => "https://" + LocalName + ":" + Port + "/";
    public string SetupUrl => "http://" + LocalName + ":" + (Port + 1) + "/";
    // The home-screen app has its own storage and no camera access, so pairing happens with a short code typed by
    // hand. The code is shown on the PC and changes every minute (the previous one is still accepted briefly, for
    // someone who is mid-typing); five wrong tries rotate it early; it only exists while the sync window is open.
    // The key itself travels once, over TLS, in exchange for the code.
    private string? pairCode, previousCode; private DateTimeOffset pairCodeExpires, previousExpires; private int pairAttempts;
    private readonly object pairLock = new();
    public const int PairCodeSeconds = 60, PairCodeGraceSeconds = 20, PairCodeAttempts = 5;
    // Tests move the clock; the app uses real time.
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    public string? PairCode { get { lock (pairLock) return pairCode != null && Clock() < pairCodeExpires ? pairCode : null; } }
    public int PairCodeSecondsLeft { get { lock (pairLock) return pairCode == null ? 0 : Math.Max(0, (int)Math.Ceiling((pairCodeExpires - Clock()).TotalSeconds)); } }
    public string NewPairCode()
    {
        lock (pairLock)
        {
            var now = Clock();
            // The code that just ran out stays valid a little longer: the person may still be typing it.
            if (pairCode != null && now < pairCodeExpires.AddSeconds(PairCodeGraceSeconds)) { previousCode = pairCode; previousExpires = now.AddSeconds(PairCodeGraceSeconds); }
            else previousCode = null;
            do pairCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("000000"); while (pairCode == previousCode);
            pairCodeExpires = now.AddSeconds(PairCodeSeconds); pairAttempts = 0;
            return pairCode;
        }
    }
    // Called by the sync window's clock: hands out the current code, replacing it once its minute is over.
    public string CurrentPairCode() => PairCode ?? NewPairCode();
    public void ClearPairCode() { lock (pairLock) pairCode = previousCode = null; }
    // The sync key for a correct code (caller zeroes it), null otherwise.
    public byte[]? TryPair(string code)
    {
        lock (pairLock)
        {
            var now = Clock();
            if (pairCode == null || now >= pairCodeExpires || !Settings.HasKey) return null;
            var typed = Encoding.UTF8.GetBytes(code.Trim().Replace(" ", ""));
            bool ok = CryptographicOperations.FixedTimeEquals(typed, Encoding.UTF8.GetBytes(pairCode))
                || (previousCode != null && now < previousExpires && CryptographicOperations.FixedTimeEquals(typed, Encoding.UTF8.GetBytes(previousCode)));
            if (!ok) { if (++pairAttempts >= PairCodeAttempts) pairCode = previousCode = null; return null; }
            pairCode = previousCode = null;
        }
        return Settings.Key();
    }
    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Start()
    {
        if (Running) return;
        Error = null;
        try
        {
            Certs ??= new Certificates(SyncDirectory);
            if (!Settings.HasKey) { Settings.NewKey(); Settings.Save(dataDirectory); }
            secure = new WebServer(Settings.Port, Certs.Server, HandleSecure, HandleSocket);
            plain = new WebServer(Settings.Port + 1, null, HandlePlain);
            secure.Failed += _ => { Error = L10n.T("SyncPortBusy"); Stop(); };
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or CryptographicException or UnauthorizedAccessException)
        {
            Error = ex is System.Net.Sockets.SocketException ? L10n.T("SyncPortBusy") : L10n.T("SyncStartFailed");
            Stop();
        }
        StatusChanged?.Invoke();
    }
    public void Stop()
    {
        List<SyncSession> open; lock (sessions) { open = sessions.ToList(); sessions.Clear(); }
        foreach (var session in open) session.Close();
        secure?.Dispose(); plain?.Dispose(); secure = plain = null;
        StatusChanged?.Invoke();
    }
    public void SetEnabled(bool enabled)
    {
        Settings.Enabled = enabled; Settings.Save(dataDirectory);
        if (enabled) Start(); else Stop();
    }
    // A fresh sync key: every phone must scan the pairing code again.
    public void ResetKey() { Settings.NewKey(); Settings.Save(dataDirectory); Stop(); if (Settings.Enabled) Start(); StatusChanged?.Invoke(); }
    public void ForgetDevice(string id) { Settings.Devices.RemoveAll(d => d.Id == id); Settings.Save(dataDirectory); StatusChanged?.Invoke(); }
    // Called after a local save: connected phones receive the changed notes right away.
    public void NotifyChanged(IReadOnlyList<string> noteIds, IReadOnlyList<PurgeStamp> purges, SyncSession? origin = null)
    {
        if (noteIds.Count == 0 && purges.Count == 0) return;
        List<SyncSession> open; lock (sessions) open = sessions.Where(s => s != origin && s.Device != null).ToList();
        foreach (var session in open) _ = session.PushAsync(noteIds, purges);
    }
    internal void DeviceSeen(SyncSession session, string id, string name)
    {
        var device = Settings.Devices.FirstOrDefault(d => d.Id == id);
        if (device == null) { device = new PairedDevice { Id = id, Name = name }; Settings.Devices.Add(device); }
        device.Name = name; device.LastSeen = DateTimeOffset.UtcNow;
        try { Settings.Save(dataDirectory); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        StatusChanged?.Invoke();
    }
    internal void SessionEnded(SyncSession session) { lock (sessions) sessions.Remove(session); StatusChanged?.Invoke(); }

    // ---- HTTP ----
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    { [".html"] = "text/html; charset=utf-8", [".js"] = "text/javascript; charset=utf-8", [".css"] = "text/css; charset=utf-8", [".webmanifest"] = "application/manifest+json", [".png"] = "image/png", [".svg"] = "image/svg+xml", [".json"] = "application/json" };
    private static byte[]? WebFile(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Notlar.Web." + name);
        if (stream == null) return null;
        var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray();
    }
    private HttpResponse? HandleSecure(HttpRequest request)
    {
        if (request.Method == "POST" && request.Path == "/pair")
        {
            string code = "";
            try { code = (JsonNode.Parse(request.Body) as JsonObject)?["code"]?.GetValue<string>() ?? ""; } catch (System.Text.Json.JsonException) { }
            var key = TryPair(code);
            if (key == null) return new HttpResponse { Status = 403, ContentType = "application/json", Body = "{\"error\":\"code\"}"u8.ToArray() };
            try { return HttpResponse.Text(new JsonObject { ["k"] = Base64Url(key), ["name"] = PcName }.ToJsonString(), "application/json"); }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        if (request.Method is not ("GET" or "HEAD")) return new HttpResponse { Status = 405, Body = "Method not allowed"u8.ToArray() };
        string path = request.Path == "/" ? "/index.html" : request.Path;
        switch (path)
        {
            case "/ca.mobileconfig": return Profile();
            case "/status":
                var status = new JsonObject { ["app"] = "Notlar", ["protocol"] = SyncKeys.Protocol, ["name"] = PcName, ["host"] = LocalName, ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["version"] = Updater.CurrentLabel };
                var statusResponse = HttpResponse.Text(status.ToJsonString(), "application/json");
                statusResponse.Headers["Access-Control-Allow-Origin"] = "*";
                return statusResponse;
        }
        string name = path.TrimStart('/');
        if (name.Contains('/') || name.Contains("..")) return HttpResponse.NotFound();
        var bytes = WebFile(name);
        if (bytes == null) return HttpResponse.NotFound();
        var response = HttpResponse.File(bytes, Types.TryGetValue(Path.GetExtension(name), out var type) ? type : "application/octet-stream");
        if (name == "sw.js") response.Headers["Service-Worker-Allowed"] = "/";
        return response;
    }
    private HttpResponse? HandlePlain(HttpRequest request)
    {
        if (request.Method is not ("GET" or "HEAD")) return new HttpResponse { Status = 405, Body = "Method not allowed"u8.ToArray() };
        return request.Path switch
        {
            "/" or "/index.html" => HttpResponse.Html(SetupPage()),
            "/ca.mobileconfig" => Profile(),
            "/ca.cer" => HttpResponse.File(Certs!.RootDer, "application/x-x509-ca-cert", "notlar-" + Certs.HostName + ".cer"),
            "/icon.png" => WebFile("icon.png") is byte[] icon ? HttpResponse.File(icon, "image/png") : HttpResponse.NotFound(),
            _ => HttpResponse.NotFound(),
        };
    }
    private HttpResponse Profile() => HttpResponse.File(Encoding.UTF8.GetBytes(Certs!.MobileConfig()), "application/x-apple-aspen-config", "notlar-" + Certs.HostName + ".mobileconfig");
    // The page the phone opens first, over plain HTTP: it installs the certificate and explains the next steps.
    private string SetupPage()
    {
        string E(string s) => System.Net.WebUtility.HtmlEncode(s);
        string dir = L10n.Current.RightToLeft ? "rtl" : "ltr";
        var steps = new[] { L10n.T("SetupStep1"), L10n.T("SetupStep2"), L10n.T("SetupStep3"), L10n.T("SetupStep4", AppUrl) };
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"").Append(L10n.Current.Code).Append("\" dir=\"").Append(dir).Append("\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1,viewport-fit=cover\"><title>").Append(E(L10n.T("AppName"))).Append("</title>");
        sb.Append("<style>body{margin:0;background:#202022;color:#F1F0ED;font:17px/1.5 -apple-system,'Segoe UI',sans-serif;padding:32px 22px calc(32px + env(safe-area-inset-bottom))}h1{font-size:26px;margin:0 0 6px}p{color:#A3A2A7;margin:0 0 22px}ol{padding-inline-start:22px}li{margin:0 0 16px}a.b{display:block;text-align:center;background:#E7BB62;color:#29241C;font-weight:600;border-radius:12px;padding:15px;text-decoration:none;margin:8px 0 6px}code{background:#333337;border-radius:6px;padding:2px 6px;font-size:15px;word-break:break-all}button.b{width:100%;border:0;font:inherit;font-size:17px;cursor:pointer}.r{min-height:1.5em;line-height:1.5}.r.ok{color:#8fd19e}.r.bad{color:#e27d7d}.f{font-size:12px;color:#A3A2A7;word-break:break-all;margin-top:26px}</style></head><body>");
        sb.Append("<h1>").Append(E(L10n.T("SetupTitle"))).Append("</h1><p>").Append(E(L10n.T("SetupIntro", PcName))).Append("</p><ol>");
        sb.Append("<li>").Append(E(steps[0])).Append("<a class=\"b\" href=\"/ca.mobileconfig\">").Append(E(L10n.T("SetupInstallButton"))).Append("</a></li>");
        sb.Append("<li>").Append(E(steps[1])).Append("</li><li>").Append(E(steps[2])).Append("</li>");
        sb.Append("<li>").Append(E(steps[3]).Replace(E(AppUrl), "<a href=\"" + E(AppUrl) + "\"><code>" + E(AppUrl) + "</code></a>")).Append("</li></ol>");
        // The check fetches /status over HTTPS: it only succeeds once the certificate is installed and fully trusted.
        sb.Append("<button class=\"b\" id=\"check\">").Append(E(L10n.T("SetupCheckButton"))).Append("</button><p id=\"result\" class=\"r\"></p>");
        sb.Append("<div class=\"f\">").Append(E(L10n.T("SetupFingerprint"))).Append("<br>").Append(E(Certs!.RootFingerprint)).Append("</div>");
        sb.Append("<script>const r=document.getElementById('result');document.getElementById('check').onclick=async()=>{r.textContent='…';r.className='r';try{const s=await fetch(").Append(System.Text.Json.JsonSerializer.Serialize(AppUrl + "status")).Append(",{cache:'no-store'});if(!s.ok)throw 0;r.textContent=").Append(System.Text.Json.JsonSerializer.Serialize(L10n.T("SetupCheckOk"))).Append(";r.className='r ok';}catch(e){r.textContent=").Append(System.Text.Json.JsonSerializer.Serialize(L10n.T("SetupCheckFail"))).Append(";r.className='r bad';}};</script></body></html>");
        return sb.ToString();
    }

    // ---- WebSocket ----
    private async Task HandleSocket(HttpRequest request, WebSocket socket, CancellationToken stop)
    {
        if (request.Path != "/sync") { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "unknown path", CancellationToken.None); return; }
        var session = new SyncSession(this, host, socket, Settings.Key());
        lock (sessions) sessions.Add(session);
        try { await session.RunAsync(stop); }
        finally { SessionEnded(session); session.Dispose(); }
    }
    public void Dispose() { Stop(); Certs?.Root.Dispose(); Certs?.Server.Dispose(); }
}

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
            if (existing != null && bases.TryGetValue(note.Id, out var baseline) && !SyncMerge.SameContent(existing, baseline) && !SyncMerge.SameContent(existing, note))
            {
                note.Id = Guid.NewGuid().ToString("N"); note.Revision = 1;
                note.Title = L10n.T("ConflictCopyTitle", note.DisplayTitle, Device?.Name ?? "phone");
                note.Deleted = false; note.DeletedAt = null;
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
