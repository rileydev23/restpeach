using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RestPeach.Core.Http;
using RestPeach.Core.Model;
using RestPeach.Core.Storage;

namespace RestPeach.Tests;

/// <summary>Connection settings: proxy, client certificate (mutual TLS) and HTTP version.</summary>
public class NetworkTests
{
    private static X509Certificate2 SelfSigned(string subject, bool server)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=" + subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") }, false));
        if (server)
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            req.CertificateExtensions.Add(san.Build());
        }
        using var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    [Fact]
    public void Config_round_trips_the_connection_settings()
    {
        var c = new WorkspaceConfig
        {
            Name = "n", HttpVersion = "2", Proxy = "http://{{PROXY}}:8080",
            ClientCert = new ClientCertSpec { Path = "certs/c.pem", Key = "certs/c.key", Password = "{{CERT_PASSWORD}}", Host = "api.bank.test" },
        };
        var back = Codec.ReadConfig(Codec.WriteConfig(c));
        Assert.Equal("2", back.HttpVersion);
        Assert.Equal("http://{{PROXY}}:8080", back.Proxy);
        Assert.Equal("certs/c.key", back.ClientCert.Key);
        Assert.Equal("{{CERT_PASSWORD}}", back.ClientCert.Password);
        Assert.Equal("api.bank.test", back.ClientCert.Host);
        // Defaults are not written, so an old config.json does not change.
        var plain = Codec.WriteConfig(new WorkspaceConfig { Name = "n" });
        Assert.False(plain.ContainsKey("proxy") || plain.ContainsKey("httpVersion") || plain.ContainsKey("clientCertificate"));
    }

    [Fact]
    public void Options_expand_variables_and_resolve_paths()
    {
        var scope = new VariableScope();
        scope.Set("PROXY", "corp", VariableSource.Environment);
        scope.Set("CERT_PASSWORD", "s3cret", VariableSource.Secret);
        var c = new WorkspaceConfig { Proxy = "http://{{PROXY}}:8080", ClientCert = new ClientCertSpec { Path = "certs/c.pfx", Password = "{{CERT_PASSWORD}}" } };
        var root = Path.GetTempPath();
        var o = HttpOptions.For(c, new ApiRequest { FollowRedirects = false }, scope, root);
        Assert.Equal("http://corp:8080", o.Proxy);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "certs/c.pfx")), o.CertPath);
        Assert.Equal("s3cret", o.CertPassword);
        Assert.False(o.FollowRedirects);
    }

    [Fact]
    public void Http_version_is_asked_with_a_fallback()
    {
        var m = new HttpRequestMessage();
        Network.ApplyVersion(m, "2");
        Assert.Equal(HttpVersion.Version20, m.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, m.VersionPolicy);
        var d = new HttpRequestMessage();
        Network.ApplyVersion(d, "");
        Assert.Equal(HttpVersion.Version11, d.Version);
    }

    [Fact]
    public async Task Requests_go_through_a_manual_proxy()
    {
        // A one-shot "proxy" that answers with the request line it got: an absolute URI means it was used as a proxy.
        var port = FreePort();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var served = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            var line = await reader.ReadLineAsync() ?? "";
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
            var body = Encoding.ASCII.GetBytes(line);
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head);
            await stream.WriteAsync(body);
        });
        try
        {
            using var engine = new HttpEngine();
            var p = RequestPreparer.Prepare(new ApiRequest { Url = "http://api.example.test/hello?x=1" }, AuthSpec.None(), new VariableScope());
            var res = await engine.SendAsync(p, new HttpOptions(TimeoutMs: 5000) { Proxy = "127.0.0.1:" + port }, CancellationToken.None);
            Assert.Null(res.Error);
            Assert.Equal("GET http://api.example.test/hello?x=1 HTTP/1.1", res.Text);
            await served;
        }
        finally { listener.Stop(); }

        using var engine2 = new HttpEngine();
        var bad = await engine2.SendAsync(RequestPreparer.Prepare(new ApiRequest { Url = "http://h.test/" }, AuthSpec.None(), new VariableScope()),
            new HttpOptions() { Proxy = "ftp://nope" }, CancellationToken.None);
        Assert.Contains("ftp://nope", bad.Error);
    }

    [Fact]
    public async Task Client_certificate_reaches_a_server_that_asks_for_it()
    {
        using var tmp = new TempDir();
        using var client = SelfSigned("restpeach-client", server: false);
        var pfx = Path.Combine(tmp.Path, "client.pfx");
        File.WriteAllBytes(pfx, client.Export(X509ContentType.Pkcs12, "pw"));
        // The same certificate as PEM files (certificate and key apart).
        var crt = Path.Combine(tmp.Path, "client.crt");
        var key = Path.Combine(tmp.Path, "client.key");
        File.WriteAllText(crt, client.ExportCertificatePem());
        File.WriteAllText(key, client.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());
        Assert.Equal(client.Thumbprint, Network.LoadCertificate(crt, key, "").Thumbprint);

        using var serverCert = SelfSigned("localhost", server: true);
        var port = FreePort();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        async Task<string?> ServeOnce()
        {
            using var tcp = await listener.AcceptTcpClientAsync();
            await using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = serverCert, ClientCertificateRequired = true });
            var got = ssl.RemoteCertificate is { } rc ? new X509Certificate2(rc).Subject : null;
            var reader = new StreamReader(ssl, Encoding.ASCII);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
            var body = Encoding.ASCII.GetBytes(got ?? "none");
            await ssl.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
            await ssl.WriteAsync(body);
            return got;
        }
        try
        {
            var p = RequestPreparer.Prepare(new ApiRequest { Url = $"https://localhost:{port}/" }, AuthSpec.None(), new VariableScope());
            using var engine = new HttpEngine();
            var served = ServeOnce();
            var res = await engine.SendAsync(p, new HttpOptions(TimeoutMs: 10000, VerifyTls: false) { CertPath = pfx, CertPassword = "pw" }, CancellationToken.None);
            Assert.Null(res.Error);
            Assert.Equal("CN=restpeach-client", res.Text);
            await served;

            // Limited to another host: not offered.
            served = ServeOnce();
            await engine.SendAsync(p, new HttpOptions(TimeoutMs: 10000, VerifyTls: false) { CertPath = pfx, CertPassword = "pw", CertHost = "api.other.test" }, CancellationToken.None);
            Assert.Null(await served);

            var wrong = await engine.SendAsync(p, new HttpOptions(VerifyTls: false) { CertPath = pfx, CertPassword = "bad" }, CancellationToken.None);
            Assert.Contains("client.pfx", wrong.Error);
        }
        finally { listener.Stop(); }
    }
}
