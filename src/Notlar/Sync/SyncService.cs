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
public sealed partial class SyncService : IDisposable
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
    // Per phone and note: the fingerprint of the version we last applied from it, and the conflict copy (with the
    // baseline it diverged from) its later submissions go to. A phone typing faster than our replies, or one that
    // never saw a reply before it went to sleep, then edits its own work rather than spawning copy after copy.
    private readonly Dictionary<(string Device, string Note), string> applied = [];
    private readonly Dictionary<(string Device, string Note), (string Baseline, string Copy)> conflictCopies = [];
    internal string? Applied(string device, string note) { lock (applied) return applied.TryGetValue((device, note), out var f) ? f : null; }
    internal void RecordApplied(string device, string note, string fingerprint) { lock (applied) applied[(device, note)] = fingerprint; }
    internal (string Baseline, string Copy)? ConflictCopy(string device, string note) { lock (applied) return conflictCopies.TryGetValue((device, note), out var c) ? c : null; }
    internal void RecordConflictCopy(string device, string note, string baseline, string copy) { lock (applied) conflictCopies[(device, note)] = (baseline, copy); }
    // What phones did here, newest first, dated, and mirrored to sync-log.txt so a gap can be read the next day.
    private readonly List<string> log = [];
    private const int LogLines = 60, LogFileLines = 300;
    private int fileLines;
    public IReadOnlyList<string> Log { get { lock (log) return log.ToList(); } }
    // Tests talk from this machine; the app leaves its own loopback traffic out.
    public bool LogLoopback { get; set; }
    public string LogPath => Path.Combine(SyncDirectory, "sync-log.txt");
    public void LogEvent(System.Net.IPAddress remote, string what)
    {
        if (System.Net.IPAddress.IsLoopback(remote) && !LogLoopback) return;
        LogNote(remote + "  " + what);
    }
    public void LogNote(string what)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + what;
        lock (log)
        {
            log.Insert(0, line); if (log.Count > LogLines) log.RemoveAt(log.Count - 1);
            // The file never grows past twice its size: once it does, only the newest lines are kept.
            try
            {
                Directory.CreateDirectory(SyncDirectory); File.AppendAllText(LogPath, line + Environment.NewLine);
                if (++fileLines > LogFileLines * 2) { var kept = File.ReadAllLines(LogPath)[^LogFileLines..]; File.WriteAllLines(LogPath, kept); fileLines = kept.Length; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    private void LoadLog()
    {
        try
        {
            if (!File.Exists(LogPath)) return;
            var lines = File.ReadAllLines(LogPath);
            if (lines.Length > LogFileLines) { lines = lines[^LogFileLines..]; File.WriteAllLines(LogPath, lines); }
            fileLines = lines.Length;
            lock (log) { log.Clear(); log.AddRange(lines.Reverse().Take(LogLines)); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    // A phone that keeps failing the TLS handshake has lost trust in the certificate; the window and the tray say so.
    public event Action? TrustProblem;
    private readonly Dictionary<System.Net.IPAddress, List<DateTime>> tlsFailures = []; private DateTime trustWarned;
    private void NoteTlsFailure(System.Net.IPAddress remote)
    {
        var now = DateTime.UtcNow;
        lock (tlsFailures)
        {
            foreach (var (address, old) in tlsFailures.ToList())
            { old.RemoveAll(t => now - t > TimeSpan.FromMinutes(5)); if (old.Count == 0) tlsFailures.Remove(address); }
            if (!tlsFailures.TryGetValue(remote, out var times)) tlsFailures[remote] = times = [];
            times.Add(now);
            if (times.Count < 3 || now - trustWarned < TimeSpan.FromMinutes(30)) return; trustWarned = now;
        }
        TrustProblem?.Invoke();
    }
    private void Trace(System.Net.IPAddress remote, string what)
    {
        if (what == "tls-failed" && !System.Net.IPAddress.IsLoopback(remote)) NoteTlsFailure(remote);
        // Static files and status probes are noise; what matters is whether the phone gets through and how far it gets.
        if ((what.StartsWith("GET /v", StringComparison.Ordinal) || what.StartsWith("GET /status", StringComparison.Ordinal)) && what.EndsWith(" 200", StringComparison.Ordinal)) return;
        LogEvent(remote, what switch
        {
            "tls-failed" => L10n.T("SyncLogTlsFailed"),
            _ when what.StartsWith("websocket ", StringComparison.Ordinal) => L10n.T("SyncLogSocketVia", what[10..]),
            "POST /pair 200" => L10n.T("SyncLogPaired"),
            "POST /pair 403" => L10n.T("SyncLogPairRefused"),
            _ => what,
        });
    }
    // A phone that stays silent this long is gone (or asleep); only phones that send heartbeats are held to it.
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);
    // The listener is torn down and rebuilt when the PC's addresses leave the certificate or it nears expiry; the
    // window owner runs Restart on the UI thread when this fires.
    public event Action? RestartNeeded;
    private System.Threading.Timer? addressTimer, renewalTimer;
    private string lastAddresses = "";
    private void AddressesChanged(object? sender, EventArgs e) { addressTimer?.Change(TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan); }
    private void CheckAddresses()
    {
        try
        {
            string now = string.Join(",", Certificates.LanAddresses());
            if (now != lastAddresses) { lastAddresses = now; LogNote(L10n.T("SyncLogAddresses", now.Length == 0 ? "-" : now)); }
            if (Certs != null && Running && !Certs.Covers()) RestartNeeded?.Invoke();
        }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or CryptographicException) { }
    }
    private void CheckCertificate()
    {
        try { if (Certs != null && Running && Certs.RenewServerIfNeeded()) { LogNote(L10n.T("SyncLogCertRenewed")); RestartNeeded?.Invoke(); } }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException) { }
    }
    public void Restart() { bool was = Running; Stop(); if (was || Settings.Enabled) Start(); }
    // The addresses a phone may use for this PC besides the name: the one it came in on first, then the rest, all
    // of them covered by the certificate.
    public List<string> AdvertisedAddresses(System.Net.IPAddress? servedOn)
    {
        var list = Certs?.Advertised().Select(a => a.ToString()).ToList() ?? [];
        if (servedOn != null && list.Remove(servedOn.ToString())) list.Insert(0, servedOn.ToString());
        return list;
    }
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
    public string? FirstAddress => Certs?.Advertised().FirstOrDefault()?.ToString();
    // The address on the QR code: a document path no earlier phone copy ever cached, so the newest app always loads.
    public string StartUrl => AppUrl + "start";
    public string SetupUrl => "http://" + (FirstAddress ?? LocalName) + ":" + (Port + 1) + "/";
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
            if (Certs == null) { Certs = new Certificates(SyncDirectory); LoadLog(); }
            else if (Certs.RenewServerIfNeeded()) LogNote(L10n.T("SyncLogCertRenewed"));
            Certs.ReloadServer();
            if (!Settings.HasKey) { Settings.NewKey(); Settings.Save(dataDirectory); }
            secure = new WebServer(Settings.Port, Certs.Server, HandleSecure, HandleSocket) { Trace = Trace };
            plain = new WebServer(Settings.Port + 1, null, HandlePlain) { Trace = Trace };
            secure.Failed += _ => { Error = L10n.T("SyncPortBusy"); Stop(); };
            lastAddresses = string.Join(",", Certificates.LanAddresses());
            addressTimer ??= new System.Threading.Timer(_ => CheckAddresses(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            renewalTimer ??= new System.Threading.Timer(_ => CheckCertificate(), null, TimeSpan.FromHours(24), TimeSpan.FromHours(24));
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += AddressesChanged;
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
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= AddressesChanged;
        addressTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        List<SyncSession> open; lock (sessions) { open = sessions.ToList(); sessions.Clear(); }
        foreach (var session in open) session.Close("stopped");
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
        lock (tlsFailures) tlsFailures.Remove(session.Remote);   // this address got in, so its failures were not about trust
        List<SyncSession> stale; lock (sessions) stale = sessions.Where(s => s != session && s.Device?.Id == id).ToList();
        foreach (var other in stale) other.Close("replaced");
        var device = Settings.Devices.FirstOrDefault(d => d.Id == id);
        if (device == null) { device = new PairedDevice { Id = id, Name = name }; Settings.Devices.Add(device); }
        device.Name = name; device.LastSeen = DateTimeOffset.UtcNow;
        try { Settings.Save(dataDirectory); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        StatusChanged?.Invoke();
    }
    internal void SessionEnded(SyncSession session, System.Net.IPAddress remote)
    {
        lock (sessions) sessions.Remove(session);
        if (session.Device != null && session.EndReason != "replaced")
            LogEvent(remote, L10n.T("SyncLogSessionEnded", session.Device.Name, session.EndReason, (int)session.Duration.TotalSeconds));
        StatusChanged?.Invoke();
    }

    // ---- WebSocket ----
    private async Task HandleSocket(HttpRequest request, WebSocket socket, CancellationToken stop)
    {
        if (request.Path != "/sync") { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "unknown path", CancellationToken.None); return; }
        var session = new SyncSession(this, host, socket, Settings.Key(), request.Remote, request.Local);
        lock (sessions) sessions.Add(session);
        try { await session.RunAsync(stop); }
        finally { SessionEnded(session, request.Remote); session.Dispose(); }
    }
    public void Dispose() { Stop(); addressTimer?.Dispose(); renewalTimer?.Dispose(); Certs?.Root.Dispose(); Certs?.Server.Dispose(); }
}
