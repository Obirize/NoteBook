using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Notlar.Sync;

// The app is its own certificate authority: a root created once on this PC (private key protected by DPAPI) and a
// server certificate for "<computer>.local" plus the current LAN addresses, renewed automatically. The phone installs
// the root once (as a configuration profile) so Safari treats the connection as secure — no public CA involved.
public sealed class Certificates
{
    private readonly string directory;
    public X509Certificate2 Root { get; private set; } = null!;
    public X509Certificate2 Server { get; private set; } = null!;
    public string HostName { get; } = (Dns.GetHostName().Split('.')[0]).ToLowerInvariant();
    public string LocalName => HostName + ".local";
    public Certificates(string directory) { this.directory = directory; Directory.CreateDirectory(directory); Root = LoadOrCreateRoot(); Server = LoadOrCreateServer(); }

    public string RootFingerprint => FormatFingerprint(Root.GetCertHash(HashAlgorithmName.SHA256));
    public static string FormatFingerprint(byte[] hash) => string.Join(" ", Enumerable.Range(0, hash.Length / 2).Select(i => Convert.ToHexString(hash, i * 2, 2)));
    public byte[] RootDer => Root.Export(X509ContentType.Cert);

    public static List<IPAddress> LanAddresses()
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(unicast.Address)) result.Add(unicast.Address);
            }
        }
        catch (NetworkInformationException) { }
        return result;
    }
    // Only devices on the local network may talk to the server at all.
    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    private string RootPath => Path.Combine(directory, "ca.bin");
    private string ServerPath => Path.Combine(directory, "server.bin");
    private static byte[] Aad(string purpose) => Encoding.UTF8.GetBytes("Notlar/sync/" + purpose);

    private X509Certificate2 LoadOrCreateRoot()
    {
        var existing = Load(RootPath, "ca");
        if (existing != null && existing.NotAfter > DateTime.UtcNow.AddDays(30)) return existing;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Notlar on " + HostName + ", O=Notlar", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var now = DateTimeOffset.UtcNow;
        var root = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
        Save(RootPath, root, "ca");
        return Load(RootPath, "ca")!;
    }
    private X509Certificate2 LoadOrCreateServer()
    {
        var existing = Load(ServerPath, "server");
        if (existing != null && existing.NotAfter > DateTime.UtcNow.AddDays(30) && Covers(existing) && existing.Issuer == Root.Subject) return existing;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + LocalName, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(LocalName); names.AddDnsName(HostName);
        foreach (var address in LanAddresses()) names.AddIpAddress(address);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Root, true, false));
        var now = DateTimeOffset.UtcNow;
        var serial = RandomNumberGenerator.GetBytes(16); serial[0] &= 0x7F;
        using var signed = request.Create(Root, now.AddDays(-1), now.AddDays(365), serial);
        using var withKey = signed.CopyWithPrivateKey(key);
        Save(ServerPath, withKey, "server");
        return Load(ServerPath, "server")!;
    }
    private bool Covers(X509Certificate2 certificate)
    {
        string text = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault()?.Format(false) ?? "";
        return text.Contains(LocalName, StringComparison.OrdinalIgnoreCase) && LanAddresses().All(a => text.Contains(a.ToString()));
    }
    private void Save(string path, X509Certificate2 certificate, string purpose)
    {
        byte[] pfx = certificate.Export(X509ContentType.Pfx);
        try
        {
            byte[] protectedBytes = ProtectedData.Protect(pfx, Aad(purpose), DataProtectionScope.CurrentUser);
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, protectedBytes); File.Move(temp, path, true);
        }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }
    private static X509Certificate2? Load(string path, string purpose)
    {
        if (!File.Exists(path)) return null;
        byte[]? pfx = null;
        try
        {
            pfx = ProtectedData.Unprotect(File.ReadAllBytes(path), Aad(purpose), DataProtectionScope.CurrentUser);
            // SChannel needs a persisted (not ephemeral) private key to serve TLS.
            return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException) { return null; }
        finally { if (pfx != null) CryptographicOperations.ZeroMemory(pfx); }
    }

    // An iOS/macOS configuration profile that installs the root certificate. The user still enables full trust by hand.
    public string MobileConfig()
    {
        string id = Convert.ToHexString(Root.GetCertHash(HashAlgorithmName.SHA256))[..16].ToLowerInvariant();
        string display = "Notlar – " + HostName;
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
            "<plist version=\"1.0\"><dict>\n" +
            "<key>PayloadContent</key><array><dict>\n" +
            "<key>PayloadCertificateFileName</key><string>notlar-" + HostName + ".cer</string>\n" +
            "<key>PayloadContent</key><data>" + Convert.ToBase64String(RootDer) + "</data>\n" +
            "<key>PayloadDescription</key><string>" + Escape(display) + "</string>\n" +
            "<key>PayloadDisplayName</key><string>" + Escape(display) + "</string>\n" +
            "<key>PayloadIdentifier</key><string>com.wallece.notlar.ca." + id + ".cert</string>\n" +
            "<key>PayloadType</key><string>com.apple.security.root</string>\n" +
            "<key>PayloadUUID</key><string>" + Uuid(id + "cert") + "</string>\n" +
            "<key>PayloadVersion</key><integer>1</integer>\n" +
            "</dict></array>\n" +
            "<key>PayloadDescription</key><string>" + Escape(display) + "</string>\n" +
            "<key>PayloadDisplayName</key><string>" + Escape(display) + "</string>\n" +
            "<key>PayloadIdentifier</key><string>com.wallece.notlar.ca." + id + "</string>\n" +
            "<key>PayloadOrganization</key><string>Notlar</string>\n" +
            "<key>PayloadRemovalDisallowed</key><false/>\n" +
            "<key>PayloadType</key><string>Configuration</string>\n" +
            "<key>PayloadUUID</key><string>" + Uuid(id + "profile") + "</string>\n" +
            "<key>PayloadVersion</key><integer>1</integer>\n" +
            "</dict></plist>\n";
    }
    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    private static string Uuid(string seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return new Guid(hash.AsSpan(0, 16)).ToString().ToUpperInvariant();
    }
}
