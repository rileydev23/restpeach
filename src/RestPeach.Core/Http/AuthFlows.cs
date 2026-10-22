using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RestPeach.Core.Model;

namespace RestPeach.Core.Http;

/// <summary>HTTP Digest (RFC 7616): answers the server's challenge with a hash of the credentials.</summary>
public static partial class DigestAuth
{
    /// <summary>The Digest challenge of a 401, if there is one.</summary>
    public static Dictionary<string, string>? Challenge(HttpResponseMessage res)
    {
        foreach (var h in res.Headers.WwwAuthenticate)
            if (h.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase) && h.Parameter is { } p)
                return Parse(p);
        return null;
    }

    public static Dictionary<string, string> Parse(string parameters)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in ParamRegex().Matches(parameters))
            d[m.Groups[1].Value] = m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value.Trim();
        return d;
    }

    [GeneratedRegex(@"([\w-]+)\s*=\s*(""([^""]*)""|([^,]*))")]
    private static partial Regex ParamRegex();

    /// <summary>The Authorization header value. <paramref name="cnonce"/> is random unless a test fixes it.</summary>
    public static string Header(Dictionary<string, string> challenge, string user, string password, string method, string uri, string? cnonce = null)
    {
        string Get(string k) => challenge.TryGetValue(k, out var v) ? v : "";
        var realm = Get("realm");
        var nonce = Get("nonce");
        var algorithm = Get("algorithm").Length > 0 ? Get("algorithm") : "MD5";
        bool sha256 = algorithm.StartsWith("SHA-256", StringComparison.OrdinalIgnoreCase);
        bool sess = algorithm.EndsWith("-sess", StringComparison.OrdinalIgnoreCase);
        string H(string s) => Convert.ToHexStringLower(sha256 ? SHA256.HashData(Encoding.UTF8.GetBytes(s)) : MD5.HashData(Encoding.UTF8.GetBytes(s)));

        var qops = Get("qop").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var qop = qops.Contains("auth", StringComparer.OrdinalIgnoreCase) ? "auth" : null;
        cnonce ??= Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        const string nc = "00000001";

        var ha1 = H($"{user}:{realm}:{password}");
        if (sess) ha1 = H($"{ha1}:{nonce}:{cnonce}");
        var ha2 = H($"{method}:{uri}");
        var response = qop is null ? H($"{ha1}:{nonce}:{ha2}") : H($"{ha1}:{nonce}:{nc}:{cnonce}:{qop}:{ha2}");

        var sb = new StringBuilder($"Digest username=\"{user}\", realm=\"{realm}\", nonce=\"{nonce}\", uri=\"{uri}\"");
        if (challenge.ContainsKey("algorithm")) sb.Append($", algorithm={algorithm}");
        if (qop is not null) sb.Append($", qop={qop}, nc={nc}, cnonce=\"{cnonce}\"");
        sb.Append($", response=\"{response}\"");
        if (challenge.TryGetValue("opaque", out var opaque)) sb.Append($", opaque=\"{opaque}\"");
        return sb.ToString();
    }
}

/// <summary>AWS Signature Version 4: signs the method, URL, a few headers and the body with the secret key.</summary>
public static class AwsSigV4
{
    public static async Task SignAsync(HttpRequestMessage msg, AuthSpec a, DateTime utcNow, CancellationToken ct)
    {
        var uri = msg.RequestUri!;
        byte[] body = Array.Empty<byte>();
        if (msg.Content is not null)
        {
            await msg.Content.LoadIntoBufferAsync(ct);
            body = await msg.Content.ReadAsByteArrayAsync(ct);
        }
        bool s3 = a.Service.Equals("s3", StringComparison.OrdinalIgnoreCase);
        var amzDate = utcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = amzDate[..8];
        var payloadHash = Hex(SHA256.HashData(body));

        msg.Headers.Remove("X-Amz-Date");
        msg.Headers.TryAddWithoutValidation("X-Amz-Date", amzDate);
        if (a.SessionToken.Length > 0) msg.Headers.TryAddWithoutValidation("X-Amz-Security-Token", a.SessionToken);
        if (s3) msg.Headers.TryAddWithoutValidation("X-Amz-Content-Sha256", payloadHash);

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = uri.IsDefaultPort ? uri.IdnHost : uri.IdnHost + ":" + uri.Port,
            ["x-amz-date"] = amzDate,
        };
        if (a.SessionToken.Length > 0) headers["x-amz-security-token"] = a.SessionToken;
        if (s3) headers["x-amz-content-sha256"] = payloadHash;
        if (msg.Content?.Headers.ContentType is { } contentType) headers["content-type"] = contentType.ToString();

        var signed = string.Join(';', headers.Keys);
        var canonical = string.Join('\n',
            msg.Method.Method,
            CanonicalPath(uri.AbsolutePath, s3),
            CanonicalQuery(uri.Query),
            string.Concat(headers.Select(h => h.Key + ":" + Regex.Replace(h.Value.Trim(), @"\s+", " ") + "\n")),
            signed,
            payloadHash);
        var scope = $"{date}/{a.Region}/{a.Service}/aws4_request";
        var toSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";

        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + a.SecretKey), date);
        key = Hmac(key, a.Region);
        key = Hmac(key, a.Service);
        key = Hmac(key, "aws4_request");
        var signature = Hex(Hmac(key, toSign));
        msg.Headers.TryAddWithoutValidation("Authorization", $"AWS4-HMAC-SHA256 Credential={a.AccessKey}/{scope}, SignedHeaders={signed}, Signature={signature}");
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
    private static string Hex(byte[] b) => Convert.ToHexStringLower(b);

    /// <summary>RFC 3986 encoding, the only one AWS accepts (spaces as %20, "~" left alone).</summary>
    private static string Encode(string s) => Uri.EscapeDataString(s);

    /// <summary>Every segment encoded; outside S3, encoded a second time (the path is already encoded once in the URL).</summary>
    private static string CanonicalPath(string path, bool s3)
    {
        if (path.Length == 0) return "/";
        if (s3) return path;
        return string.Join('/', path.Split('/').Select(Encode));
    }

    private static string CanonicalQuery(string query)
    {
        if (query.Length <= 1) return "";
        return string.Join('&', query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p =>
            {
                var eq = p.IndexOf('=');
                var k = Uri.UnescapeDataString(eq < 0 ? p : p[..eq]);
                var v = eq < 0 ? "" : Uri.UnescapeDataString(p[(eq + 1)..]);
                return (K: Encode(k), V: Encode(v));
            })
            .OrderBy(x => x.K, StringComparer.Ordinal).ThenBy(x => x.V, StringComparer.Ordinal)
            .Select(x => x.K + "=" + x.V));
    }
}

/// <summary>An access token from an OAuth 2.0 server, and until when it is good.</summary>
public sealed record OAuthToken(string AccessToken, string TokenType, DateTimeOffset? Expires, string? RefreshToken, string? Scope)
{
    /// <summary>Thirty seconds of margin, so a token does not expire on its way to the server.</summary>
    public bool IsExpired(DateTimeOffset now) => Expires is { } e && e - TimeSpan.FromSeconds(30) <= now;
}

/// <summary>
/// OAuth 2.0 grants: client credentials, password, and authorization code with PKCE (the browser opens the login and
/// a listener on 127.0.0.1 gets the code). Tokens are kept in memory by the engine, never written anywhere, and
/// renewed with the refresh token (or asked for again) when they expire.
/// </summary>
public static class OAuth2
{
    /// <summary>How the login page is opened (tests replace it). Returns false when there is no browser to open.</summary>
    public static Func<string, bool> OpenBrowser { get; set; } = url =>
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return true; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    };

    /// <summary>The longest wait for the user to log in in the browser.</summary>
    public static TimeSpan LoginTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public static string CacheKey(AuthSpec a) => string.Join('\n', a.Grant, a.TokenUrl, a.AuthUrl, a.ClientId, a.Scope, a.Username);

    public static async Task<OAuthToken> RequestAsync(AuthSpec a, HttpMessageInvoker http, OAuthToken? previous, CancellationToken ct)
    {
        if (a.TokenUrl.Length == 0) throw new OAuthException(Loc.T("oauth.noTokenUrl"));
        if (previous?.RefreshToken is { Length: > 0 } refresh)
        {
            try { return await TokenAsync(a, http, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh }, ct, previous); }
            catch (OAuthException) { } // the refresh token is no longer good: start over
        }
        switch (a.Grant)
        {
            case OAuthGrants.Password:
                return await TokenAsync(a, http, new() { ["grant_type"] = "password", ["username"] = a.Username, ["password"] = a.Password }, ct);
            case OAuthGrants.AuthorizationCode:
                return await AuthorizationCodeAsync(a, http, ct);
            default:
                return await TokenAsync(a, http, new() { ["grant_type"] = "client_credentials" }, ct);
        }
    }

    private static async Task<OAuthToken> AuthorizationCodeAsync(AuthSpec a, HttpMessageInvoker http, CancellationToken ct)
    {
        if (a.AuthUrl.Length == 0) throw new OAuthException(Loc.T("oauth.noAuthUrl"));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var redirect = a.RedirectUri.Length > 0 ? a.RedirectUri : $"http://127.0.0.1:{FreePort()}/callback";
        if (!Uri.TryCreate(redirect, UriKind.Absolute, out var redirectUri) || redirectUri.Scheme != "http" || !redirectUri.IsLoopback)
            throw new OAuthException(Loc.F("oauth.badRedirect", redirect));
        using var listener = new HttpListener();
        var path = redirectUri.AbsolutePath.EndsWith('/') ? redirectUri.AbsolutePath : redirectUri.AbsolutePath + "/";
        listener.Prefixes.Add($"http://{redirectUri.Host}:{redirectUri.Port}{path}");
        try { listener.Start(); }
        catch (HttpListenerException e) { throw new OAuthException(Loc.F("oauth.listen", redirect, e.Message)); }

        var query = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"), new("client_id", a.ClientId), new("redirect_uri", redirect), new("state", state),
            new("code_challenge", challenge), new("code_challenge_method", "S256"),
        };
        if (a.Scope.Length > 0) query.Add(new("scope", a.Scope));
        var url = a.AuthUrl + (a.AuthUrl.Contains('?') ? "&" : "?") + string.Join('&', query.Select(q => q.Key + "=" + Uri.EscapeDataString(q.Value)));
        if (!OpenBrowser(url)) throw new OAuthException(Loc.F("oauth.noBrowser", url));

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(LoginTimeout);
        using var reg = wait.Token.Register(() => { try { listener.Stop(); } catch (ObjectDisposedException) { } });
        string? code = null, error = null;
        while (code is null && error is null)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                ct.ThrowIfCancellationRequested();
                throw new OAuthException(Loc.T("oauth.loginTimeout"));
            }
            var q = ctx.Request.QueryString;
            if (q["state"] is null && q["error"] is null && q["code"] is null) { ctx.Response.StatusCode = 404; ctx.Response.Close(); continue; }
            if (q["error"] is { } e2) error = e2 + (q["error_description"] is { } d ? ": " + d : "");
            else if (q["state"] != state) error = Loc.T("oauth.badState");
            else code = q["code"];
            var page = Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><title>RestPeach</title><body style=\"font-family:sans-serif;background:#1e1f22;color:#ddd;padding:40px\"><h2>"
                + WebUtility.HtmlEncode(error is null ? Loc.T("oauth.page.ok") : Loc.T("oauth.page.error")) + "</h2><p>" + WebUtility.HtmlEncode(error ?? Loc.T("oauth.page.close")) + "</p>");
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.OutputStream.WriteAsync(page, ct);
            ctx.Response.Close();
        }
        if (error is not null) throw new OAuthException(error);
        return await TokenAsync(a, http, new()
        {
            ["grant_type"] = "authorization_code", ["code"] = code!, ["redirect_uri"] = redirect, ["code_verifier"] = verifier,
        }, ct);
    }

    private static async Task<OAuthToken> TokenAsync(AuthSpec a, HttpMessageInvoker http, Dictionary<string, string> form, CancellationToken ct, OAuthToken? previous = null)
    {
        if (a.Scope.Length > 0 && form["grant_type"] is "client_credentials" or "password") form["scope"] = a.Scope;
        using var msg = new HttpRequestMessage(HttpMethod.Post, a.TokenUrl);
        if (a.ClientAuth == "body" || a.ClientSecret.Length == 0)
        {
            form["client_id"] = a.ClientId;
            if (a.ClientSecret.Length > 0) form["client_secret"] = a.ClientSecret;
        }
        else
        {
            // RFC 6749 §2.3.1: both form-encoded before going into Basic.
            var pair = Uri.EscapeDataString(a.ClientId) + ":" + Uri.EscapeDataString(a.ClientSecret);
            msg.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(pair)));
        }
        msg.Content = new FormUrlEncodedContent(form);
        msg.Headers.Accept.ParseAdd("application/json");
        msg.Headers.TryAddWithoutValidation("User-Agent", HttpEngine.UserAgent);

        using var res = await http.SendAsync(msg, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        var fields = ParseTokenResponse(text);
        string F(string k) => fields.TryGetValue(k, out var v) ? v : "";
        if (!res.IsSuccessStatusCode || F("access_token").Length == 0)
        {
            var why = F("error").Length > 0 ? F("error") + (F("error_description").Length > 0 ? ": " + F("error_description") : "")
                : $"{(int)res.StatusCode} {res.ReasonPhrase}" + (text.Length > 0 ? " — " + (text.Length > 200 ? text[..200] : text) : "");
            throw new OAuthException(Loc.F("oauth.failed", why));
        }
        DateTimeOffset? expires = int.TryParse(F("expires_in"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs) ? DateTimeOffset.Now.AddSeconds(secs) : null;
        return new OAuthToken(F("access_token"), F("token_type").Length > 0 ? F("token_type") : "Bearer", expires,
            F("refresh_token").Length > 0 ? F("refresh_token") : previous?.RefreshToken, F("scope").Length > 0 ? F("scope") : null);
    }

    /// <summary>JSON (the standard) or form-encoded (what some older servers answer).</summary>
    private static Dictionary<string, string> ParseTokenResponse(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var p in doc.RootElement.EnumerateObject())
                    d[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
            return d;
        }
        catch (JsonException) { }
        foreach (var part in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0) d[Uri.UnescapeDataString(part[..eq])] = Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
        }
        return d;
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}

public sealed class OAuthException(string message) : Exception(message);
