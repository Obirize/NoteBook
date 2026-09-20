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
    // The address on the QR code: a document path no earlier phone copy ever cached, so the newest app always loads.
    public string StartUrl => AppUrl + "start";
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
        string path = request.Path is "/" or "/start" ? "/index.html" : request.Path.StartsWith("/v2/", StringComparison.Ordinal) ? request.Path[3..] : request.Path;
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
        var steps = new[] { L10n.T("SetupStep1"), L10n.T("SetupStep2"), L10n.T("SetupStep3"), L10n.T("SetupStep4", StartUrl) };
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"").Append(L10n.Current.Code).Append("\" dir=\"").Append(dir).Append("\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1,viewport-fit=cover\"><title>").Append(E(L10n.T("AppName"))).Append("</title>");
        sb.Append("<style>body{margin:0;background:#202022;color:#F1F0ED;font:17px/1.5 -apple-system,'Segoe UI',sans-serif;padding:32px 22px calc(32px + env(safe-area-inset-bottom))}h1{font-size:26px;margin:0 0 6px}p{color:#A3A2A7;margin:0 0 22px}ol{padding-inline-start:22px}li{margin:0 0 16px}a.b{display:block;text-align:center;background:#E7BB62;color:#29241C;font-weight:600;border-radius:12px;padding:15px;text-decoration:none;margin:8px 0 6px}code{background:#333337;border-radius:6px;padding:2px 6px;font-size:15px;word-break:break-all}button.b{width:100%;border:0;font:inherit;font-size:17px;cursor:pointer}.r{min-height:1.5em;line-height:1.5}.r.ok{color:#8fd19e}.r.bad{color:#e27d7d}.f{font-size:12px;color:#A3A2A7;word-break:break-all;margin-top:26px}</style></head><body>");
        sb.Append("<h1>").Append(E(L10n.T("SetupTitle"))).Append("</h1><p>").Append(E(L10n.T("SetupIntro", PcName))).Append("</p><ol>");
        sb.Append("<li>").Append(E(steps[0])).Append("<a class=\"b\" href=\"/ca.mobileconfig\">").Append(E(L10n.T("SetupInstallButton"))).Append("</a></li>");
        sb.Append("<li>").Append(E(steps[1])).Append("</li><li>").Append(E(steps[2])).Append("</li>");
        sb.Append("<li>").Append(E(steps[3]).Replace(E(StartUrl), "<a href=\"" + E(StartUrl) + "\"><code>" + E(StartUrl) + "</code></a>")).Append("</li></ol>");
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
