using System.IO;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Notlar.Sync;

// What the servers answer: the phone app files and pairing over HTTPS, the certificate setup page over plain HTTP.
public sealed partial class SyncService
{
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
            if (!OriginAllowed(request.Header("Origin"))) return new HttpResponse { Status = 403, ContentType = "application/json", Body = "{\"error\":\"origin\"}"u8.ToArray() };
            string code = "";
            try { code = (JsonNode.Parse(request.Body) as JsonObject)?["code"]?.GetValue<string>() ?? ""; } catch (System.Text.Json.JsonException) { }
            var key = TryPair(code);
            if (key == null) return new HttpResponse { Status = 403, ContentType = "application/json", Body = "{\"error\":\"code\"}"u8.ToArray() };
            try { return HttpResponse.Text(new JsonObject { ["k"] = Base64Url(key), ["name"] = PcName }.ToJsonString(), "application/json"); }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        if (request.Method is not ("GET" or "HEAD")) return new HttpResponse { Status = 405, Body = "Method not allowed"u8.ToArray() };
        string path = request.Path is "/" or "/start" ? "/index.html" : System.Text.RegularExpressions.Regex.Replace(request.Path, "^/v[0-9]+/", "/");
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
    private bool OriginAllowed(string? origin)
    {
        if (string.IsNullOrEmpty(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != Port) return false;
        var hosts = new List<string> { LocalName, Certs?.HostName ?? "" };
        hosts.AddRange(Certs?.Advertised().Select(a => a.ToString()) ?? []);
        return hosts.Any(h => h.Length > 0 && string.Equals(h, uri.Host, StringComparison.OrdinalIgnoreCase));
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
        sb.Append("<p id=\"fix\" hidden><a class=\"b\" href=\"/ca.mobileconfig\">").Append(E(L10n.T("SetupReinstallButton"))).Append("</a><br><span class=\"f\">").Append(E(L10n.T("SetupReinstallHelp"))).Append("</span></p>");
        sb.Append("<p id=\"go\" hidden><a class=\"b\" href=\"").Append(E(StartUrl)).Append("\">").Append(E(L10n.T("SetupOpenApp"))).Append("</a></p>");
        sb.Append("<p id=\"nameOnly\" hidden><button class=\"b\" id=\"again\">").Append(E(L10n.T("SetupTryAgain"))).Append("</button></p>");
        sb.Append("<div class=\"f\">").Append(E(L10n.T("SetupFingerprint"))).Append("<br>").Append(E(Certs!.RootFingerprint)).Append("</div>");
        // The check runs by itself when the page opens and again on demand. A phone that gets this page but fails the
        // HTTPS check has lost trust in the certificate: the page then leads straight to reinstalling it, so the fix is
        // one tap plus the iOS trust switch rather than a search for what went wrong.
        string J(string value) => System.Text.Json.JsonSerializer.Serialize(value);
        string byName = AppUrl + "status", byAddress = FirstAddress != null ? "https://" + FirstAddress + ":" + Port + "/status" : "";
        sb.Append("<script>const r=document.getElementById('result'),fix=document.getElementById('fix'),go=document.getElementById('go'),nameOnly=document.getElementById('nameOnly');")
          .Append("async function probe(u){if(!u)return false;try{const c=new AbortController();setTimeout(()=>c.abort(),6000);const s=await fetch(u,{cache:'no-store',signal:c.signal});return s.ok;}catch(e){return false;}}")
          .Append("async function check(){r.textContent='…';r.className='r';fix.hidden=go.hidden=nameOnly.hidden=true;const [n,a]=await Promise.all([probe(").Append(J(byName)).Append("),probe(").Append(J(byAddress)).Append(")]);")
          .Append("if(n){r.textContent=").Append(J(L10n.T("SetupCheckOk"))).Append(";r.className='r ok';go.hidden=false;}")
          .Append("else if(a){r.textContent=").Append(J(L10n.T("SetupNameOnly"))).Append(";r.className='r bad';nameOnly.hidden=false;}")
          .Append("else{r.textContent=").Append(J(L10n.T("SetupCheckFail"))).Append(";r.className='r bad';fix.hidden=false;}}")
          .Append("document.getElementById('check').onclick=check;document.getElementById('again').onclick=check;check();</script></body></html>");
        return sb.ToString();
    }
}
