using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RestPeach.Core.Formats;
using RestPeach.Core.Http;
using RestPeach.Core.Model;
using RestPeach.Core.Storage;

namespace RestPeach.Tests;

/// <summary>Digest, AWS Signature V4 and OAuth 2.0: known test vectors, and real exchanges against a local HttpListener.</summary>
public sealed class AuthTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _base;
    private int _tokenCalls;
    private readonly List<string> _tokenForms = new();
    private string _currentToken = "t1";
    private int _expiresIn = 3600;

    public AuthTests()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
        _base = $"http://localhost:{port}/";
        _listener.Prefixes.Add(_base);
        _listener.Start();
        _ = Task.Run(Serve);
    }

    private static string Md5(string s) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)));

    private async Task Serve()
    {
        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            var req = ctx.Request;
            var res = ctx.Response;
            var body = await new StreamReader(req.InputStream).ReadToEndAsync();
            var auth = req.Headers["Authorization"] ?? "";
            string reply = "";
            switch (req.Url!.AbsolutePath)
            {
                case "/digest":
                    // Checks the answer with its own arithmetic (RFC 2617, qop=auth).
                    var p = DigestAuth.Parse(auth.StartsWith("Digest ") ? auth[7..] : "");
                    var expected = p.Count == 0 ? "" : Md5($"{Md5("ana:shop:pw")}:abc123:{p["nc"]}:{p["cnonce"]}:auth:{Md5("GET:" + p["uri"])}");
                    if (p.Count > 0 && p["response"] == expected && p["uri"] == "/digest?x=1" && p["opaque"] == "op") { reply = "welcome ana"; break; }
                    res.StatusCode = 401;
                    res.AddHeader("WWW-Authenticate", "Digest realm=\"shop\", qop=\"auth\", nonce=\"abc123\", opaque=\"op\"");
                    break;
                case "/token":
                    Interlocked.Increment(ref _tokenCalls);
                    lock (_tokenForms) _tokenForms.Add(auth + " | " + body);
                    var form = body.Split('&').Select(x => x.Split('=')).ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]));
                    if (form.GetValueOrDefault("client_secret") == "bad" || auth == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("app:bad")))
                    {
                        res.StatusCode = 401;
                        reply = "{\"error\":\"invalid_client\",\"error_description\":\"Client authentication failed\"}";
                        break;
                    }
                    if (form["grant_type"] == "authorization_code" && (form.GetValueOrDefault("code") != "the-code" || form.GetValueOrDefault("code_verifier") is not { Length: 43 }))
                    {
                        res.StatusCode = 400;
                        reply = "{\"error\":\"invalid_grant\"}";
                        break;
                    }
                    res.ContentType = "application/json";
                    reply = JsonSerializer.Serialize(new { access_token = _currentToken, token_type = "bearer", expires_in = _expiresIn, refresh_token = "r-" + _currentToken, scope = form.GetValueOrDefault("scope") ?? "" });
                    break;
                case "/api":
                    res.StatusCode = auth == "Bearer " + _currentToken ? 200 : 401;
                    reply = auth;
                    break;
            }
            var bytes = Encoding.UTF8.GetBytes(reply);
            await res.OutputStream.WriteAsync(bytes);
            res.Close();
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
    }

    private static PreparedRequest Prepare(string url, AuthSpec auth) =>
        RequestPreparer.Prepare(new ApiRequest { Url = url }, auth, new VariableScope());

    // ------------------------------------------------------------ Digest

    [Fact]
    public void Digest_matches_the_rfc_2617_example()
    {
        var challenge = DigestAuth.Parse("realm=\"testrealm@host.com\", qop=\"auth,auth-int\", nonce=\"dcd98b7102dd2f0e8b11d0f600bfb0c093\", opaque=\"5ccc069c403ebaf9f0171e9517f40e41\"");
        var header = DigestAuth.Header(challenge, "Mufasa", "Circle Of Life", "GET", "/dir/index.html", cnonce: "0a4f113b");
        Assert.Contains("response=\"6629fae49393a05397450978507c4ef1\"", header);
        Assert.Contains("qop=auth, nc=00000001, cnonce=\"0a4f113b\"", header);
        Assert.Contains("opaque=\"5ccc069c403ebaf9f0171e9517f40e41\"", header);
    }

    [Fact]
    public async Task Digest_answers_the_challenge_with_a_second_try()
    {
        using var engine = new HttpEngine();
        var ok = await engine.SendAsync(Prepare(_base + "digest?x=1", new AuthSpec { Type = AuthTypes.Digest, Username = "ana", Password = "pw" }), new HttpOptions(), CancellationToken.None);
        Assert.Equal(200, ok.Status);
        Assert.Equal("welcome ana", ok.Text);
        var bad = await engine.SendAsync(Prepare(_base + "digest?x=1", new AuthSpec { Type = AuthTypes.Digest, Username = "ana", Password = "nope" }), new HttpOptions(), CancellationToken.None);
        Assert.Equal(401, bad.Status);
    }

    // ------------------------------------------------------------ AWS

    [Fact]
    public async Task Aws_signature_matches_the_get_vanilla_test_vector()
    {
        // aws-sig-v4-test-suite/get-vanilla
        var msg = new HttpRequestMessage(HttpMethod.Get, "https://example.amazonaws.com/");
        var a = new AuthSpec { AccessKey = "AKIDEXAMPLE", SecretKey = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", Region = "us-east-1", Service = "service" };
        await AwsSigV4.SignAsync(msg, a, new DateTime(2015, 8, 30, 12, 36, 0, DateTimeKind.Utc), CancellationToken.None);
        Assert.Equal("20150830T123600Z", msg.Headers.GetValues("X-Amz-Date").Single());
        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, SignedHeaders=host;x-amz-date, Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31",
            msg.Headers.GetValues("Authorization").Single());
    }

    // ------------------------------------------------------------ OAuth 2.0

    private AuthSpec OAuth(string grant = OAuthGrants.ClientCredentials, string secret = "s3cret") => new()
    {
        Type = AuthTypes.OAuth2, Grant = grant, TokenUrl = _base + "token", AuthUrl = _base + "authorize", ClientId = "app", ClientSecret = secret,
        Scope = "read write", Username = "ana", Password = "pw",
    };

    [Fact]
    public async Task OAuth_client_credentials_gets_one_token_and_reuses_it()
    {
        using var engine = new HttpEngine();
        var p = Prepare(_base + "api", OAuth());
        var first = await engine.SendAsync(p, new HttpOptions(), CancellationToken.None);
        var second = await engine.SendAsync(p, new HttpOptions(), CancellationToken.None);
        Assert.Equal(200, first.Status);
        Assert.Equal(200, second.Status);
        Assert.Equal(1, _tokenCalls);
        lock (_tokenForms)
        {
            Assert.StartsWith("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("app:s3cret")), _tokenForms[0]);
            Assert.Contains("grant_type=client_credentials", _tokenForms[0]);
            Assert.Contains("scope=read+write", _tokenForms[0]);
        }
        Assert.Equal("t1", engine.CachedToken(p.Deferred!)!.AccessToken);
    }

    [Fact]
    public async Task OAuth_expired_token_is_refreshed_and_a_revoked_one_replaced()
    {
        using var engine = new HttpEngine();
        _expiresIn = 10; // inside the 30 s margin: already "expired" when used again
        var p = Prepare(_base + "api", OAuth(OAuthGrants.Password));
        Assert.Equal(200, (await engine.SendAsync(p, new HttpOptions(), CancellationToken.None)).Status);
        _currentToken = "t2";
        Assert.Equal(200, (await engine.SendAsync(p, new HttpOptions(), CancellationToken.None)).Status);
        lock (_tokenForms)
        {
            Assert.Contains("grant_type=password", _tokenForms[0]);
            Assert.Contains("username=ana", _tokenForms[0]);
            Assert.Contains("grant_type=refresh_token", _tokenForms[1]);
            Assert.Contains("refresh_token=r-t1", _tokenForms[1]);
        }

        // The server revokes it: a 401 gets one new token and one more try.
        _expiresIn = 3600;
        _currentToken = "t3";
        Assert.Equal(200, (await engine.SendAsync(p, new HttpOptions(), CancellationToken.None)).Status);
        _currentToken = "t4";
        var res = await engine.SendAsync(p, new HttpOptions(), CancellationToken.None);
        Assert.Equal(200, res.Status);
        Assert.Equal("Bearer t4", res.Text);
    }

    [Fact]
    public async Task OAuth_error_from_the_server_is_reported()
    {
        using var engine = new HttpEngine();
        var res = await engine.SendAsync(Prepare(_base + "api", OAuth(secret: "bad")), new HttpOptions(), CancellationToken.None);
        Assert.NotNull(res.Error);
        Assert.Contains("invalid_client: Client authentication failed", res.Error);
    }

    [Fact]
    public async Task OAuth_authorization_code_with_pkce_goes_through_the_browser()
    {
        var old = OAuth2.OpenBrowser;
        string? opened = null;
        // The "browser": logs in and comes back to the redirect URI with the code.
        OAuth2.OpenBrowser = url =>
        {
            opened = url;
            var q = url[(url.IndexOf('?') + 1)..].Split('&').Select(x => x.Split('=')).ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]));
            _ = Task.Run(async () =>
            {
                using var http = new HttpClient();
                await http.GetStringAsync(q["redirect_uri"] + "?code=the-code&state=" + Uri.EscapeDataString(q["state"]));
            });
            return true;
        };
        try
        {
            using var engine = new HttpEngine();
            var res = await engine.SendAsync(Prepare(_base + "api", OAuth(OAuthGrants.AuthorizationCode, secret: "")), new HttpOptions(), CancellationToken.None);
            Assert.Null(res.Error);
            Assert.Equal(200, res.Status);
            Assert.Contains("code_challenge_method=S256", opened);
            Assert.Contains("response_type=code", opened);
            lock (_tokenForms)
            {
                Assert.Contains("grant_type=authorization_code", _tokenForms[0]);
                Assert.Contains("client_id=app", _tokenForms[0]); // public client: id in the body
            }
        }
        finally { OAuth2.OpenBrowser = old; }
    }

    // ------------------------------------------------------------ files and formats

    [Fact]
    public void Each_auth_type_round_trips_and_writes_only_its_fields()
    {
        var aws = new AuthSpec { Type = AuthTypes.AwsSigV4, AccessKey = "{{AK}}", SecretKey = "{{SK}}", Region = "eu-west-1", Service = "execute-api" };
        var back = Codec.ReadAuth(Codec.WriteAuth(aws));
        Assert.Equal(("{{AK}}", "eu-west-1", "execute-api"), (back.AccessKey, back.Region, back.Service));
        Assert.False(Codec.WriteAuth(aws).ContainsKey("sessionToken"));

        var oauth = OAuth(OAuthGrants.ClientCredentials);
        var json = Codec.WriteAuth(oauth);
        Assert.False(json.ContainsKey("username") || json.ContainsKey("authUrl")); // not used by client credentials
        Assert.Equal("read write", Codec.ReadAuth(json).Scope);
        Assert.Equal(OAuthGrants.Password, Codec.ReadAuth(Codec.WriteAuth(OAuth(OAuthGrants.Password))).Grant);
        Assert.Equal("ana", Codec.ReadAuth(Codec.WriteAuth(OAuth(OAuthGrants.Password))).Username);
    }

    [Fact]
    public void Curl_writes_and_reads_digest_and_aws()
    {
        var digest = Curl.Write(Prepare("https://h.test/x", new AuthSpec { Type = AuthTypes.Digest, Username = "ana", Password = "pw" }));
        Assert.Contains("--digest -u ana:pw", digest);
        Assert.Equal(AuthTypes.Digest, Curl.Parse(digest)!.Auth.Type);

        var aws = Curl.Write(Prepare("https://h.test/x", new AuthSpec { Type = AuthTypes.AwsSigV4, AccessKey = "AK", SecretKey = "SK", Region = "us-east-1", Service = "s3", SessionToken = "tok" }));
        var r = Curl.Parse(aws)!;
        Assert.Equal((AuthTypes.AwsSigV4, "AK", "SK", "us-east-1", "s3", "tok"), (r.Auth.Type, r.Auth.AccessKey, r.Auth.SecretKey, r.Auth.Region, r.Auth.Service, r.Auth.SessionToken));
        Assert.Empty(r.Headers);
    }
}
