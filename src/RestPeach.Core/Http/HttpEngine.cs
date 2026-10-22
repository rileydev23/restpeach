using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using RestPeach.Core.Model;

namespace RestPeach.Core.Http;

/// <summary>
/// How to send: everything here is already expanded. <see cref="Proxy"/>: "" = the system's, "none", or a URL.
/// <see cref="CertPath"/> and <see cref="CertKey"/> are absolute; <see cref="CertHost"/> limits who gets the certificate.
/// </summary>
public sealed record HttpOptions(int TimeoutMs = 30000, bool FollowRedirects = true, bool VerifyTls = true)
{
    public string HttpVersion { get; init; } = "";
    public string Proxy { get; init; } = "";
    public string CertPath { get; init; } = "";
    public string CertKey { get; init; } = "";
    public string CertPassword { get; init; } = "";
    public string CertHost { get; init; } = "";

    /// <summary>
    /// The collection's settings with what <paramref name="r"/> overrides for itself. <paramref name="scope"/> expands
    /// the proxy and the certificate fields; <paramref name="root"/> is what relative certificate paths start from.
    /// </summary>
    public static HttpOptions For(WorkspaceConfig c, ApiRequest? r = null, VariableScope? scope = null, string root = "")
    {
        scope ??= new VariableScope();
        string X(string s) => Interpolator.Expand(s, scope).Trim();
        string Abs(string s) => s.Length == 0 || Path.IsPathRooted(s) || root.Length == 0 ? s : Path.GetFullPath(Path.Combine(root, s));
        var cert = c.ClientCert;
        return new HttpOptions(c.TimeoutMs, r?.FollowRedirects ?? c.FollowRedirects, c.VerifyTls)
        {
            HttpVersion = c.HttpVersion,
            Proxy = X(c.Proxy),
            CertPath = cert.IsEmpty ? "" : Abs(X(cert.Path)),
            CertKey = cert.IsEmpty ? "" : Abs(X(cert.Key)),
            CertPassword = cert.IsEmpty ? "" : Interpolator.Expand(cert.Password, scope),
            CertHost = cert.IsEmpty ? "" : X(cert.Host),
        };
    }

    /// <summary>The part of the options a connection pool depends on (the timeout is per request).</summary>
    internal HttpOptions PoolKey => this with { TimeoutMs = 0 };
}

/// <summary>One redirect that was followed: the status, where it pointed and how long that hop took.</summary>
public sealed record RedirectHop(int Status, string From, string To, double ElapsedMs);

/// <summary>What came back. <see cref="Error"/> is set (and <see cref="Status"/> is 0) when no response arrived.</summary>
public sealed class ApiResponse
{
    public int Status { get; init; }
    public string Reason { get; init; } = "";
    public string HttpVersion { get; init; } = "";
    public string FinalUrl { get; init; } = "";
    public List<KeyValuePair<string, string>> Headers { get; init; } = new();
    public byte[] Body { get; init; } = Array.Empty<byte>();
    /// <summary>The body was longer than <see cref="HttpEngine.MaxBodyBytes"/> and was cut.</summary>
    public bool Truncated { get; init; }
    /// <summary>From sending until the last byte of the body.</summary>
    public double ElapsedMs { get; init; }
    public string? Error { get; init; }
    /// <summary>
    /// The body stopped before its end: "timeout", "cancelled" or "connection". What arrived until then is kept, so a
    /// stream that never ends (SSE, long polling) can still be read.
    /// </summary>
    public string? Interrupted { get; init; }
    /// <summary>WebSocket runs: the server closed the connection itself, with this code and reason ("1008 Unauthorized").</summary>
    public string? WebSocketClose { get; init; }
    /// <summary>The redirects followed before this response, in order (empty when there were none).</summary>
    public List<RedirectHop> Redirects { get; init; } = new();
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;

    public bool Ok => Error is null && Status is >= 200 and < 400;
    public long Size => Body.LongLength;
    public string? ContentType => Header("Content-Type");

    public string? Header(string name) =>
        Headers.Where(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).FirstOrDefault();

    public IEnumerable<string> HeaderValues(string name) =>
        Headers.Where(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value);

    private string? _text;

    /// <summary>The body decoded with the charset the server declared (UTF-8 otherwise).</summary>
    public string Text => _text ??= Decode(Body, ContentType);

    public static string Decode(byte[] body, string? contentType)
    {
        var enc = Encoding.UTF8;
        try
        {
            if (contentType is not null && MediaTypeHeaderValue.TryParse(contentType, out var mt) && !string.IsNullOrEmpty(mt.CharSet))
                enc = Encoding.GetEncoding(mt.CharSet.Trim('"'));
        }
        catch (ArgumentException) { }
        var text = enc.GetString(body);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }
}

/// <summary>
/// Watches an event stream (<c>text/event-stream</c>) while it arrives: the app's live view. With one, such a stream
/// has no overall timeout, only one between chunks (the collection's timeout), and runs until it ends or is cancelled.
/// </summary>
public interface IStreamListener
{
    /// <summary>The headers arrived: <paramref name="head"/> has the status and headers, no body yet.</summary>
    void Started(ApiResponse head);
    /// <summary>Called from a background thread; the array is the listener's to keep.</summary>
    void Data(byte[] chunk);
}

/// <summary>
/// Sends prepared requests. One engine per open collection keeps its own cookie jar in memory (never written to disk,
/// never sent anywhere else) and reuses connections. No telemetry, no proxies of its own: only the request you sent.
/// </summary>
public sealed class HttpEngine : IDisposable
{
    public const int MaxBodyBytes = 64 * 1024 * 1024;
    public static readonly string UserAgent = "RestPeach/" + (typeof(HttpEngine).Assembly.GetName().Version?.ToString(3) ?? "1.0");

    public CookieContainer Cookies { get; private set; } = new();
    private readonly Dictionary<HttpOptions, HttpClient> _clients = new();
    private readonly Dictionary<HttpOptions, HttpMessageInvoker> _webSockets = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, OAuthToken> _tokens = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _tokenGate = new(1, 1);

    /// <summary>The OAuth 2.0 token for <paramref name="a"/> (expanded), from memory while it is good, else asked for.</summary>
    public async Task<OAuthToken> OAuthTokenAsync(AuthSpec a, HttpOptions options, bool fresh, CancellationToken ct)
    {
        var key = OAuth2.CacheKey(a);
        // One at a time: a run of ten requests asks for one token, not ten (and opens one browser, not ten).
        await _tokenGate.WaitAsync(ct);
        try
        {
            OAuthToken? cached;
            lock (_lock) _tokens.TryGetValue(key, out cached);
            if (!fresh && cached is not null && !cached.IsExpired(DateTimeOffset.Now)) return cached;
            var token = await OAuth2.RequestAsync(a, ClientFor(options with { FollowRedirects = true }), fresh ? null : cached, ct);
            lock (_lock) _tokens[key] = token;
            return token;
        }
        finally { _tokenGate.Release(); }
    }

    public OAuthToken? CachedToken(AuthSpec a)
    {
        lock (_lock) return _tokens.TryGetValue(OAuth2.CacheKey(a), out var t) ? t : null;
    }

    public void ForgetToken(AuthSpec a)
    {
        lock (_lock) _tokens.Remove(OAuth2.CacheKey(a));
    }

    public void ClearCookies()
    {
        lock (_lock)
        {
            _tokens.Clear();
            foreach (var c in _clients.Values) c.Dispose();
            _clients.Clear();
            foreach (var w in _webSockets.Values) w.Dispose();
            _webSockets.Clear();
            Cookies = new CookieContainer();
        }
    }

    public IReadOnlyList<Cookie> AllCookies() => Cookies.GetAllCookies().Cast<Cookie>().OrderBy(c => c.Domain).ThenBy(c => c.Name).ToList();

    private HttpClient ClientFor(HttpOptions o)
    {
        lock (_lock)
        {
            var key = o.PoolKey;
            if (_clients.TryGetValue(key, out var c)) return c;
            c = new HttpClient(Handler(key)) { Timeout = Timeout.InfiniteTimeSpan };
            _clients[key] = c;
            return c;
        }
    }

    /// <summary>
    /// What WebSocket handshakes go through: the same cookie jar, TLS setting and IPv4/IPv6 connect as the requests,
    /// so a session cookie from a login reaches the socket. A handshake never follows redirects.
    /// </summary>
    public HttpMessageInvoker WebSocketInvoker(HttpOptions o)
    {
        lock (_lock)
        {
            var key = o.PoolKey with { FollowRedirects = false };
            if (_webSockets.TryGetValue(key, out var w)) return w;
            w = new HttpMessageInvoker(Handler(key));
            _webSockets[key] = w;
            return w;
        }
    }

    private SocketsHttpHandler Handler(HttpOptions o)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = o.FollowRedirects,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = Cookies,
            UseCookies = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            ConnectCallback = ConnectAnyAsync,
        };
        if (!o.VerifyTls) handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        Network.ApplyProxy(handler, o.Proxy);
        if (o.CertPath.Length > 0) Network.ApplyClientCertificate(handler, o);
        return handler;
    }

    public async Task<ApiResponse> SendAsync(PreparedRequest p, HttpOptions options, CancellationToken ct, IStreamListener? listener = null)
    {
        var sw = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sent = new List<HttpRequestMessage>();
        var hops = new List<RedirectHop>();
        HttpResponseMessage? res = null;
        try
        {
            if (p.Missing.Count > 0 && p.Url.Contains("{{"))
                throw new InvalidOperationException("{{" + p.Missing.First() + "}}");
            if (!Uri.TryCreate(p.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new UriFormatException(p.Url);

            // An OAuth token may need a login in the browser: that wait is not part of the request's time.
            OAuthToken? token = null;
            if (p.Deferred is { Type: AuthTypes.OAuth2 } oauth)
            {
                token = await OAuthTokenAsync(oauth, options, false, ct);
                sw.Restart();
            }
            timeout.CancelAfter(options.TimeoutMs);

            // Redirects are followed here, not by the handler, so every hop can be shown.
            var client = ClientFor(options with { FollowRedirects = false });
            async Task<HttpResponseMessage> SendOnce(HttpRequestMessage m, string? authorization, bool sign)
            {
                sent.Add(m);
                Network.ApplyVersion(m, options.HttpVersion);
                if (authorization is not null) m.Headers.TryAddWithoutValidation("Authorization", authorization);
                if (sign && p.Deferred is { Type: AuthTypes.AwsSigV4 } aws) await AwsSigV4.SignAsync(m, aws, DateTime.UtcNow, timeout.Token);
                return await client.SendAsync(m, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            }

            var authorization = token is null ? null : "Bearer " + token.AccessToken;
            res = await SendOnce(Build(p, uri), authorization, sign: true);
            if (res.StatusCode == HttpStatusCode.Unauthorized && p.Deferred is { } deferred)
            {
                string? answer = null;
                if (deferred.Type == AuthTypes.Digest && DigestAuth.Challenge(res) is { } challenge)
                    answer = DigestAuth.Header(challenge, deferred.Username, deferred.Password, p.Method, uri.PathAndQuery);
                else if (deferred.Type == AuthTypes.OAuth2)
                    answer = "Bearer " + (await OAuthTokenAsync(deferred, options, true, timeout.Token)).AccessToken; // revoked: one new token, one more try
                if (answer is not null)
                {
                    res.Dispose();
                    authorization = answer;
                    res = await SendOnce(Build(p, uri), answer, sign: true);
                }
            }

            var current = uri;
            var method = p.Method;
            bool withBody = true;
            double hopStart = 0;
            while (options.FollowRedirects && IsRedirect(res.StatusCode) && res.Headers.Location is { } location)
            {
                if (hops.Count >= MaxRedirects) throw new HttpRequestException(Loc.F("net.tooManyRedirects", MaxRedirects));
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                hops.Add(new RedirectHop((int)res.StatusCode, current.ToString(), next.ToString(), sw.Elapsed.TotalMilliseconds - hopStart));
                hopStart = sw.Elapsed.TotalMilliseconds;
                // What browsers and curl do: 303 (and 301/302 after a POST) go on as GET without a body; 307/308 repeat as is.
                if (res.StatusCode == HttpStatusCode.SeeOther && method != "HEAD"
                    || res.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found && method == "POST")
                {
                    method = "GET";
                    withBody = false;
                }
                res.Dispose();
                // Credentials stay with the origin they were meant for.
                bool sameOrigin = next.Scheme == uri.Scheme && next.IdnHost.Equals(uri.IdnHost, StringComparison.OrdinalIgnoreCase) && next.Port == uri.Port;
                res = await SendOnce(Build(p, next, method, withBody, sameOrigin), sameOrigin ? authorization : null, sign: sameOrigin);
                current = next;
            }
            var headers = new List<KeyValuePair<string, string>>();
            foreach (var h in res.Headers) foreach (var v in h.Value) headers.Add(new(h.Key, v));
            foreach (var h in res.Content.Headers) foreach (var v in h.Value) headers.Add(new(h.Key, v));
            var live = listener is not null && SseParser.IsEventStream(res.Content.Headers.ContentType?.ToString());
            if (live)
            {
                listener!.Started(new ApiResponse
                {
                    Status = (int)res.StatusCode, Reason = res.ReasonPhrase ?? res.StatusCode.ToString(), HttpVersion = res.Version.ToString(),
                    FinalUrl = res.RequestMessage?.RequestUri?.ToString() ?? p.Url, Headers = headers, ElapsedMs = sw.Elapsed.TotalMilliseconds,
                });
                timeout.CancelAfter(options.TimeoutMs); // from here on: the time allowed between two chunks
            }
            var (body, truncated, interrupted) = await ReadBodyAsync(res, timeout.Token, ct, live ? listener : null, live ? timeout : null, options.TimeoutMs);
            sw.Stop();
            return new ApiResponse
            {
                Status = (int)res.StatusCode,
                Reason = res.ReasonPhrase ?? res.StatusCode.ToString(),
                HttpVersion = res.Version.ToString(),
                FinalUrl = res.RequestMessage?.RequestUri?.ToString() ?? p.Url,
                Headers = headers,
                Body = body,
                Truncated = truncated,
                Interrupted = interrupted,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Redirects = hops,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failure(p, sw, $"Timeout ({options.TimeoutMs} ms)");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            return Failure(p, sw, Describe(e));
        }
        finally
        {
            res?.Dispose();
            foreach (var m in sent) m.Dispose();
        }
    }

    private const int MaxRedirects = 10;

    private static bool IsRedirect(HttpStatusCode code) => (int)code is 301 or 302 or 303 or 307 or 308;

    /// <summary>
    /// Connects to every address of the host at once and keeps the first that answers. "localhost" is ::1 and
    /// 127.0.0.1; a dev server listening only on IPv4 made each request wait ~2 s on Windows while the IPv6 attempt
    /// timed out, before .NET tried the next address.
    /// </summary>
    private static async ValueTask<Stream> ConnectAnyAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var ep = context.DnsEndPoint;
        var addresses = IPAddress.TryParse(ep.Host, out var ip) ? new[] { ip } : await Dns.GetHostAddressesAsync(ep.Host, ct);
        if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = addresses.Distinct().Select(a => AttemptAsync(a, ep.Port, cts.Token)).ToList();
        Exception? error = null;
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending);
            pending.Remove(done);
            try
            {
                var socket = await done;
                cts.Cancel();
                // An attempt that still wins after this one is closed, not leaked.
                foreach (var loser in pending)
                    _ = loser.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException) { error = e; }
        }
        ct.ThrowIfCancellationRequested();
        throw error ?? new SocketException((int)SocketError.HostUnreachable);
    }

    private static async Task<Socket> AttemptAsync(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static ApiResponse Failure(PreparedRequest p, Stopwatch sw, string error) =>
        new() { Error = error, ElapsedMs = sw.Elapsed.TotalMilliseconds, FinalUrl = p.Url };

    /// <summary>The innermost useful message ("No such host is known. (api.local:443)").</summary>
    internal static string Describe(Exception e)
    {
        var inner = e;
        while (inner.InnerException is not null && inner is HttpRequestException or AggregateException or System.Net.WebSockets.WebSocketException) inner = inner.InnerException;
        return inner.Message;
    }

    /// <summary>
    /// The message for <paramref name="uri"/>. A redirect passes the method it goes on with, whether the body goes
    /// too, and whether it stays on the same origin (if not, the Authorization and Cookie headers stay behind).
    /// </summary>
    private static HttpRequestMessage Build(PreparedRequest p, Uri uri, string? method = null, bool withBody = true, bool sameOrigin = true)
    {
        var msg = new HttpRequestMessage(new HttpMethod(method ?? p.Method), uri);
        HttpContent? content = !withBody ? null : p.BodyType switch
        {
            BodyTypes.Json or BodyTypes.Text or BodyTypes.Xml or BodyTypes.Html => new ByteArrayContent(Encoding.UTF8.GetBytes(p.RawBody)),
            BodyTypes.UrlEncoded => new ByteArrayContent(Encoding.ASCII.GetBytes(FormEncode(p.Fields))),
            BodyTypes.FormData => Multipart(p.Fields),
            BodyTypes.Binary when p.RawBody.Length > 0 => new ByteArrayContent(File.ReadAllBytes(p.RawBody)),
            _ => null,
        };
        var contentHeaders = new List<KeyValuePair<string, string>>();
        foreach (var (k, v) in p.Headers)
        {
            if (!sameOrigin && (k.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || k.Equals("Cookie", StringComparison.OrdinalIgnoreCase))) continue;
            if (!withBody && k.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)) continue;
            if (msg.Headers.TryAddWithoutValidation(k, v)) continue;
            contentHeaders.Add(new(k, v)); // Content-Type, Content-Language… belong to the content
        }
        if (content is null && contentHeaders.Count > 0) content = new ByteArrayContent(Array.Empty<byte>());
        if (content is not null)
        {
            foreach (var (k, v) in contentHeaders)
            {
                if (k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && content is MultipartFormDataContent) continue;
                content.Headers.Remove(k);
                content.Headers.TryAddWithoutValidation(k, v);
            }
            msg.Content = content;
        }
        if (!msg.Headers.Contains("User-Agent")) msg.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (!msg.Headers.Contains("Accept")) msg.Headers.TryAddWithoutValidation("Accept", "*/*");
        return msg;
    }

    public static string FormEncode(IEnumerable<KeyValue> fields) =>
        string.Join('&', fields.Select(f => Uri.EscapeDataString(f.Key) + "=" + Uri.EscapeDataString(f.Value)));

    private static MultipartFormDataContent Multipart(IEnumerable<KeyValue> fields)
    {
        var m = new MultipartFormDataContent();
        foreach (var f in fields)
        {
            if (f.IsFile)
            {
                if (f.Value.Length == 0) throw new IOException(Loc.F("net.noFileChosen", f.Key));
                var file = new ByteArrayContent(File.ReadAllBytes(f.Value));
                file.Headers.ContentType = new MediaTypeHeaderValue(MimeFor(f.Value));
                m.Add(file, f.Key, Path.GetFileName(f.Value));
            }
            else m.Add(new StringContent(f.Value, Encoding.UTF8), f.Key);
        }
        return m;
    }

    /// <summary>The Content-Type of a file sent as a body or a form-data part, from its extension.</summary>
    public static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" => "application/json",
        ".html" or ".htm" => "text/html",
        ".svg" => "image/svg+xml",
        ".mp3" => "audio/mpeg",
        ".mp4" => "video/mp4",
        ".wav" => "audio/wav",
        ".gz" => "application/gzip",
        ".yaml" or ".yml" => "application/yaml",
        ".txt" => "text/plain",
        ".csv" => "text/csv",
        ".xml" => "application/xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// The headers have arrived: from here on a timeout, a cancel or a dropped connection keeps the bytes read so far
    /// (marked <see cref="ApiResponse.Interrupted"/>) instead of throwing them away.
    /// </summary>
    private static async Task<(byte[] Body, bool Truncated, string? Interrupted)> ReadBodyAsync(HttpResponseMessage res, CancellationToken timeout, CancellationToken user,
        IStreamListener? listener = null, CancellationTokenSource? idle = null, int idleMs = 0)
    {
        using var ms = new MemoryStream();
        try
        {
            await using var stream = await res.Content.ReadAsStreamAsync(timeout);
            var buffer = new byte[81920];
            int n;
            while ((n = await stream.ReadAsync(buffer, timeout)) > 0)
            {
                if (ms.Length + n > MaxBodyBytes)
                {
                    ms.Write(buffer, 0, (int)(MaxBodyBytes - ms.Length));
                    return (ms.ToArray(), true, null);
                }
                ms.Write(buffer, 0, n);
                idle?.CancelAfter(idleMs);
                listener?.Data(buffer.AsSpan(0, n).ToArray());
            }
        }
        catch (OperationCanceledException) { return (ms.ToArray(), false, user.IsCancellationRequested ? "cancelled" : idle is not null ? "idle" : "timeout"); }
        catch (Exception e) when (e is IOException or HttpRequestException) { return (ms.ToArray(), false, "connection"); }
        return (ms.ToArray(), false, null);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var c in _clients.Values) c.Dispose();
            _clients.Clear();
            foreach (var w in _webSockets.Values) w.Dispose();
            _webSockets.Clear();
        }
    }
}
