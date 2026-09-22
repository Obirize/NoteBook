using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Notlar;
using Notlar.Sync;

// The PC side of the link: a phone that goes silent is dropped, the same phone coming back evicts its old session,
// a broken frame ends a session promptly, the welcome carries the addresses, the log persists, and the certificate
// follows the PC's addresses.
static class LinkChecks
{
    // A minimal phone: handshake through the real protocol, then whatever the check needs.
    sealed class Phone : IDisposable
    {
        public ClientWebSocket Socket = new();
        public JsonObject? Welcome;
        public async Task<Phone> Connect(SyncService service, string device, bool hb)
        {
            Socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            await Socket.ConnectAsync(new Uri("wss://localhost:" + service.Port + "/sync"), CancellationToken.None);
            var hello = new JsonObject { ["t"] = "hello", ["protocol"] = SyncKeys.Protocol, ["device"] = device, ["name"] = "Test phone" };
            if (hb) { hello["hb"] = true; hello["diag"] = new JsonObject { ["prev"] = "no-pong", ["hidden"] = 61000, ["tries"] = 2 }; }
            await Send(hello);
            var challenge = await Next();
            using var keys = new SyncKeys(service.Settings.Key());
            var serverNonce = Convert.FromBase64String(challenge!["nonce"]!.GetValue<string>()); var clientNonce = RandomNumberGenerator.GetBytes(32);
            await Send(new JsonObject { ["t"] = "auth", ["nonce"] = Convert.ToBase64String(clientNonce), ["mac"] = Convert.ToBase64String(keys.Mac("client", serverNonce, clientNonce)) });
            Welcome = await Next();
            return this;
        }
        public Task Send(JsonObject m) => Socket.SendAsync(Encoding.UTF8.GetBytes(m.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
        public async Task<JsonObject?> Next(int timeoutMs = 5000)
        {
            var buffer = new byte[1 << 20]; using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                var r = await Socket.ReceiveAsync(buffer, cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) return new JsonObject { ["t"] = "closed", ["code"] = (int?)r.CloseStatus };
                return JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, r.Count)) as JsonObject;
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { return new JsonObject { ["t"] = "gone" }; }
        }
        public void Dispose() => Socket.Dispose();
    }
    static async Task Until(Func<bool> condition, int ms = 5000) { var end = DateTime.UtcNow.AddMilliseconds(ms); while (!condition() && DateTime.UtcNow < end) await Task.Delay(50); }

    public static void Run(string root, SyncService service, Action<bool, string> check) => RunAsync(root, service, check).GetAwaiter().GetResult();
    static async Task RunAsync(string root, SyncService service, Action<bool, string> check)
    {
        service.LogLoopback = true; service.IdleTimeout = TimeSpan.FromSeconds(2);
        // Welcome fields and the hello diagnostics in the log.
        using (var phone = await new Phone().Connect(service, "phone-a", hb: true))
        {
            var w = phone.Welcome!;
            var addresses = w["addresses"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
            var san = service.Certs!.Server.Extensions.OfType<X509SubjectAlternativeNameExtension>().First().EnumerateIPAddresses().Select(a => a.ToString()).ToHashSet();
            check(w["t"]?.GetValue<string>() == "welcome" && w["host"]!.GetValue<string>().EndsWith(".local") && w["port"]!.GetValue<int>() == service.Port && w["idle"]!.GetValue<long>() == 2000
                && addresses.All(san.Contains) && addresses.All(a => !a.StartsWith("169.254.")), "The welcome names the PC, its port, its idle limit and only addresses the certificate covers");
            check(service.Log[0].Contains(L10n.T("SyncLogAuthOk", "Test phone")) && service.Log[0].Contains("prev no-pong") && service.Log[0].Contains("hidden 61 s") && service.Log[0].Contains("tries 2"), "The phone's own account of its last link appears in the auth line");
            // Ping is answered with the same id.
            await phone.Send(new JsonObject { ["t"] = "ping", ["id"] = 7 });
            var pong = await phone.Next();
            check(pong?["t"]?.GetValue<string>() == "pong" && pong["id"]?.GetValue<int>() == 7, "A ping is answered with a pong carrying its id");
            // Silence: a heartbeat phone is dropped after the idle limit and the log says so.
            await Until(() => service.Connected.Count == 0, 6000);
            var end = await phone.Next(500);
            check(service.Connected.Count == 0 && service.Log.Any(l => l.Contains("idle") && l.Contains("Test phone")), "A silent heartbeat phone is dropped at the idle limit, with a log line");
        }
        // A phone without heartbeats is left alone in silence.
        using (var old = await new Phone().Connect(service, "phone-old", hb: false))
        {
            await Task.Delay(3000);
            check(service.Connected.Count == 1 && service.Log.Any(l => l.Contains(L10n.T("SyncLogOldCopy"))), "An old phone copy (no heartbeat) is never dropped for silence and is marked in the log");
        }
        await Until(() => service.Connected.Count == 0);
        // Same device twice: the earlier session is evicted without a log line of its own.
        using (var first = await new Phone().Connect(service, "phone-b", hb: true))
        using (var again = await new Phone().Connect(service, "phone-b", hb: true))
        {
            var closed = await first.Next(3000);
            await Until(() => service.Connected.Count == 1);
            check(closed?["t"]?.GetValue<string>() is "closed" or "gone" && service.Connected.Count == 1 && !service.Log.Any(l => l.Contains("replaced")), "The same phone connecting again replaces its earlier session quietly");
            await again.Send(new JsonObject { ["t"] = "ping", ["id"] = 1 });
        }
        await Until(() => service.Connected.Count == 0);
        // A broken frame ends the session within the close grace and is named in the log.
        using (var bad = await new Phone().Connect(service, "phone-c", hb: true))
        {
            await bad.Send(new JsonObject { ["t"] = "note", ["id"] = "x", ["rev"] = 1, ["blob"] = "not-base64" });
            await Until(() => service.Connected.Count == 0, 4000);
            check(service.Connected.Count == 0 && service.Log.Any(l => l.Contains("error FormatException")), "A malformed frame ends the session at once and the reason is logged");
        }
        // The log is dated and mirrored to a file that survives restarts.
        service.LogNote("marker line");
        check(service.Log[0].StartsWith(DateTime.Now.ToString("yyyy-MM-dd")) && File.ReadAllText(service.LogPath).Contains("marker line"), "Log lines carry the date and are written to sync-log.txt");
        // Pairing only answers the app's own pages.
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
        var evil = new HttpRequestMessage(HttpMethod.Post, "https://localhost:" + service.Port + "/pair") { Content = new StringContent("{\"code\":\"000000\"}", Encoding.UTF8, "application/json") };
        evil.Headers.Add("Origin", "https://evil.example");
        var refused = await http.SendAsync(evil);
        var own = new HttpRequestMessage(HttpMethod.Post, "https://localhost:" + service.Port + "/pair") { Content = new StringContent("{\"code\":\"000000\"}", Encoding.UTF8, "application/json") };
        own.Headers.Add("Origin", "https://" + service.LocalName + ":" + service.Port);
        var ownReply = await http.SendAsync(own);
        check(refused.StatusCode == HttpStatusCode.Forbidden && (await refused.Content.ReadAsStringAsync()).Contains("origin") && (await ownReply.Content.ReadAsStringAsync()).Contains("code"), "Pairing refuses a foreign page's origin and answers the app's own");
        // Restart keeps the port and the phones can come back.
        service.Restart();
        check(service.Running && service.Error == null, "Restart brings the listener back (error: " + service.Error + ")");
        using (var back = await new Phone().Connect(service, "phone-d", hb: true)) check(service.Running && back.Welcome?["t"]?.GetValue<string>() == "welcome", "After a restart the listener is back on the same port");
        service.IdleTimeout = TimeSpan.FromSeconds(60);
        // Certificates follow the PC's addresses: a new address renews the leaf under the same root; noise does not.
        var real = Certificates.AddressSource;
        try
        {
            var dir = Path.Combine(root, "certs"); Directory.CreateDirectory(dir);
            Certificates.AddressSource = () => [IPAddress.Parse("192.168.1.80")];
            var certs = new Certificates(dir);
            check(certs.Covers() && certs.Advertised().Select(a => a.ToString()).SequenceEqual(["192.168.1.80"]), "A fresh server certificate covers and advertises the current address");
            Certificates.AddressSource = () => [IPAddress.Parse("192.168.1.8")];
            string before = certs.Server.Thumbprint;
            check(!certs.Covers() && certs.RenewServerIfNeeded() && certs.Server.Thumbprint == before, "A changed address is detected exactly (no substring match) and renewal writes a new leaf without touching the served one");
            certs.ReloadServer();
            var ips = certs.Server.Extensions.OfType<X509SubjectAlternativeNameExtension>().First().EnumerateIPAddresses().Select(a => a.ToString()).ToList();
            check(certs.Server.Thumbprint != before && ips.SequenceEqual(["192.168.1.8"]) && certs.Server.Issuer == certs.Root.Subject, "The reloaded leaf names the new address and is signed by the same root");
            string renewed = certs.Server.Thumbprint;
            foreach (var churn in new[] { new string[0], ["169.254.5.5"], ["169.254.5.5", "192.168.1.8"], ["192.168.1.8"] })
            { Certificates.AddressSource = () => churn.Select(IPAddress.Parse).ToList(); if (certs.RenewServerIfNeeded()) certs.ReloadServer(); }
            check(certs.Server.Thumbprint == renewed, "Losing the address or gaining a self-assigned one never renews the certificate");
        }
        finally { Certificates.AddressSource = real; }
    }
}
