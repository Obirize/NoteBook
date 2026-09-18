using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Notlar.Sync;

public sealed class HttpRequest
{
    public string Method { get; init; } = "";
    public string Path { get; init; } = "";
    public string Query { get; init; } = "";
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; init; } = [];
    public IPAddress Remote { get; init; } = IPAddress.None;
    public bool IsWebSocketUpgrade => Headers.TryGetValue("Upgrade", out var u) && u.Equals("websocket", StringComparison.OrdinalIgnoreCase) && Headers.ContainsKey("Sec-WebSocket-Key");
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
}
public sealed class HttpResponse
{
    public int Status { get; init; } = 200;
    public string ContentType { get; init; } = "text/plain; charset=utf-8";
    public byte[] Body { get; init; } = [];
    public Dictionary<string, string> Headers { get; } = [];
    public static HttpResponse Text(string text, string contentType = "text/plain; charset=utf-8") => new() { ContentType = contentType, Body = Encoding.UTF8.GetBytes(text) };
    public static HttpResponse Html(string html) => Text(html, "text/html; charset=utf-8");
    public static HttpResponse NotFound() => new() { Status = 404, Body = "Not found"u8.ToArray() };
    public static HttpResponse File(byte[] bytes, string contentType, string? downloadName = null)
    {
        var response = new HttpResponse { ContentType = contentType, Body = bytes };
        if (downloadName != null) response.Headers["Content-Disposition"] = "attachment; filename=\"" + downloadName + "\"";
        return response;
    }
}

// A deliberately small HTTP/1.1 server: TLS (or plain for the setup page), static responses and WebSocket upgrades.
// It only answers devices on the local network, and it carries a few files and one WebSocket, nothing more.
public sealed class WebServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly X509Certificate2? certificate;
    private readonly Func<HttpRequest, HttpResponse?> handler;
    private readonly Func<HttpRequest, WebSocket, CancellationToken, Task>? socketHandler;
    private readonly CancellationTokenSource stop = new();
    private const int MaxHead = 16 * 1024, MaxBody = 1024 * 1024;
    public int Port { get; }
    public Exception? Failure { get; private set; }
    public event Action<Exception>? Failed;

    public WebServer(int port, X509Certificate2? certificate, Func<HttpRequest, HttpResponse?> handler, Func<HttpRequest, WebSocket, CancellationToken, Task>? socketHandler = null)
    {
        this.certificate = certificate; this.handler = handler; this.socketHandler = socketHandler;
        listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Accept();
    }
    private async Task Accept()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException) { if (stop.IsCancellationRequested) return; Failure = ex; Failed?.Invoke(ex); return; }
            _ = Serve(client);
        }
    }
    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
            if (!Certificates.IsPrivate(remote)) return;
            client.NoDelay = true;
            Stream stream = client.GetStream();
            try
            {
                if (certificate != null)
                {
                    var tls = new SslStream(stream, false);
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = certificate, ClientCertificateRequired = false,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    }, stop.Token);
                    stream = tls;
                }
                using (stream)
                {
                    while (!stop.IsCancellationRequested)
                    {
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                        idle.CancelAfter(TimeSpan.FromSeconds(60));
                        var request = await ReadRequest(stream, remote, idle.Token);
                        if (request == null) return;
                        if (request.IsWebSocketUpgrade && socketHandler != null)
                        {
                            string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(request.Header("Sec-WebSocket-Key")!.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                            await Write(stream, "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n", stop.Token);
                            using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(20) });
                            await socketHandler(request, socket, stop.Token);
                            return;
                        }
                        var response = handler(request) ?? HttpResponse.NotFound();
                        bool close = string.Equals(request.Header("Connection"), "close", StringComparison.OrdinalIgnoreCase);
                        var head = new StringBuilder();
                        head.Append("HTTP/1.1 ").Append(response.Status).Append(' ').Append(Reason(response.Status)).Append("\r\n");
                        head.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
                        head.Append("Content-Length: ").Append(response.Body.Length).Append("\r\n");
                        head.Append("Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n");
                        foreach (var pair in response.Headers) head.Append(pair.Key).Append(": ").Append(pair.Value).Append("\r\n");
                        head.Append(close ? "Connection: close\r\n\r\n" : "Connection: keep-alive\r\n\r\n");
                        await Write(stream, head.ToString(), stop.Token);
                        if (request.Method != "HEAD") await stream.WriteAsync(response.Body, stop.Token);
                        await stream.FlushAsync(stop.Token);
                        if (close) return;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException or WebSocketException or ObjectDisposedException or InvalidOperationException) { }
        }
    }
    private static string Reason(int status) => status switch { 200 => "OK", 204 => "No Content", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Error" };
    private static async Task Write(Stream stream, string text, CancellationToken token) => await stream.WriteAsync(Encoding.ASCII.GetBytes(text), token);
    private static async Task<HttpRequest?> ReadRequest(Stream stream, IPAddress remote, CancellationToken token)
    {
        var buffer = new byte[MaxHead]; int length = 0, headEnd = -1;
        while (headEnd < 0)
        {
            if (length == buffer.Length) return null;
            int read = await stream.ReadAsync(buffer.AsMemory(length), token);
            if (read == 0) return null;
            length += read;
            headEnd = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
        }
        var lines = Encoding.ASCII.GetString(buffer, 0, headEnd).Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.")) return null;
        string target = parts[1]; int q = target.IndexOf('?');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1)) { int colon = line.IndexOf(':'); if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim(); }
        byte[] body = [];
        int bodyStart = headEnd + 4, have = length - bodyStart;
        if (headers.TryGetValue("Content-Length", out var lengthText) && int.TryParse(lengthText, out int bodyLength) && bodyLength > 0)
        {
            if (bodyLength > MaxBody) return null;
            body = new byte[bodyLength];
            Array.Copy(buffer, bodyStart, body, 0, Math.Min(have, bodyLength));
            if (have < bodyLength) await stream.ReadExactlyAsync(body.AsMemory(have, bodyLength - have), token);
        }
        var request = new HttpRequest { Method = parts[0], Path = Uri.UnescapeDataString(q < 0 ? target : target[..q]), Query = q < 0 ? "" : target[(q + 1)..], Remote = remote, Body = body };
        foreach (var pair in headers) request.Headers[pair.Key] = pair.Value;
        return request;
    }
    public void Dispose()
    {
        stop.Cancel();
        try { listener.Stop(); } catch (SocketException) { }
        stop.Dispose();
    }
}
