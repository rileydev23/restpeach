using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RestPeach.Core.Http;

/// <summary>The connection settings of a collection applied to a handler or a message: proxy, client certificate, HTTP version.</summary>
public static class Network
{
    /// <summary>
    /// "" keeps .NET's default (the system proxy, or HTTP_PROXY / HTTPS_PROXY / NO_PROXY), "none" connects directly,
    /// anything else is the proxy's URL; credentials in it (<c>http://user:pass@host:8080</c>) are sent to the proxy.
    /// </summary>
    public static void ApplyProxy(SocketsHttpHandler handler, string proxy)
    {
        if (proxy.Length == 0) return;
        if (proxy.Equals("none", StringComparison.OrdinalIgnoreCase)) { handler.UseProxy = false; return; }
        var text = proxy.Contains("://") ? proxy : "http://" + proxy;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5"))
            throw new InvalidOperationException(Loc.F("net.badProxy", proxy));
        var address = new UriBuilder(uri) { UserName = "", Password = "" }.Uri;
        var web = new WebProxy(address) { BypassProxyOnLocal = true };
        if (uri.UserInfo.Length > 0)
        {
            var parts = uri.UserInfo.Split(':', 2);
            web.Credentials = new NetworkCredential(Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
        }
        handler.Proxy = web;
        handler.UseProxy = true;
    }

    /// <summary>Offers the certificate to the servers that ask for one (only to <see cref="HttpOptions.CertHost"/> when set).</summary>
    public static void ApplyClientCertificate(SocketsHttpHandler handler, HttpOptions o)
    {
        X509Certificate2 cert;
        try { cert = LoadCertificate(o.CertPath, o.CertKey, o.CertPassword); }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new InvalidOperationException(Loc.F("net.badCert", Path.GetFileName(o.CertPath), e.Message), e);
        }
        var host = o.CertHost.Trim().TrimStart('*', '.');
        handler.SslOptions.ClientCertificates = new X509CertificateCollection { cert };
        handler.SslOptions.LocalCertificateSelectionCallback = (_, target, _, _, _) =>
            host.Length == 0 || target.Equals(host, StringComparison.OrdinalIgnoreCase) || target.EndsWith("." + host, StringComparison.OrdinalIgnoreCase)
                ? cert
                : null!;
    }

    /// <summary>A .pfx / .p12, or a PEM certificate (with its key inside, or in <paramref name="keyPath"/>; encrypted when there is a password).</summary>
    public static X509Certificate2 LoadCertificate(string path, string keyPath, string password)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".pfx" or ".p12")
            return X509CertificateLoader.LoadPkcs12FromFile(path, password.Length > 0 ? password : null);
        var key = keyPath.Length > 0 ? keyPath : null;
        using var pem = password.Length > 0 ? X509Certificate2.CreateFromEncryptedPemFile(path, password, key) : X509Certificate2.CreateFromPemFile(path, key);
        // Windows' TLS cannot use the ephemeral key a PEM file gives; a round trip through PKCS#12 makes it usable.
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
    }

    /// <summary>"" keeps HTTP/1.1; "2" and "3" ask for that version and fall back when the server does not speak it.</summary>
    public static void ApplyVersion(HttpRequestMessage msg, string version)
    {
        switch (version)
        {
            case "1.1":
                msg.Version = HttpVersion.Version11;
                msg.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
                break;
            case "2":
                msg.Version = HttpVersion.Version20;
                msg.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
                break;
            case "3":
                msg.Version = HttpVersion.Version30;
                msg.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
                break;
        }
    }
}
