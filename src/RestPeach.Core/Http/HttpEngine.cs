using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using RestPeach.Core.Model;

namespace RestPeach.Core.Http;

public sealed record HttpOptions(int TimeoutMs = 30000, bool FollowRedirects = true, bool VerifyTls = true)
{
    public static HttpOptions From(WorkspaceConfig c) => new(c.TimeoutMs, c.FollowRedirects, c.VerifyTls);

    /// <summary>The collection's settings with what <paramref name="r"/> overrides for itself.</summary>
    public static HttpOptions For(WorkspaceConfig c, ApiRequest r) => From(c) with { FollowRedirects = r.FollowRedirects ?? c.FollowRedirects };
}

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
/// Sends prepared requests. One engine per open collection keeps its own cookie jar in memory (never written to disk,
/// never sent anywhere else) and reuses connections. No telemetry, no proxies of its own: only the request you sent.
/// </summary>
public sealed class HttpEngine : IDisposable
{
    public const int MaxBodyBytes = 64 * 1024 * 1024;
    public static readonly string UserAgent = "RestPeach/" + (typeof(HttpEngine).Assembly.GetName().Version?.ToString(3) ?? "1.0");

    public CookieContainer Cookies { get; private set; } = new();
    private readonly Dictionary<(bool Redirects, bool Tls), HttpClient> _clients = new();
    private readonly object _lock = new();

    public void ClearCookies()
    {
        lock (_lock)
        {
            foreach (var c in _clients.Values) c.Dispose();
            _clients.Clear();
            Cookies = new CookieContainer();
        }
    }

    public IReadOnlyList<Cookie> AllCookies() => Cookies.GetAllCookies().Cast<Cookie>().OrderBy(c => c.Domain).ThenBy(c => c.Name).ToList();

    private HttpClient ClientFor(HttpOptions o)
    {
        lock (_lock)
        {
            var key = (o.FollowRedirects, o.VerifyTls);
            if (_clients.TryGetValue(key, out var c)) return c;
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
            c = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            _clients[key] = c;
            return c;
        }
    }

    public async Task<ApiResponse> SendAsync(PreparedRequest p, HttpOptions options, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.TimeoutMs);
        try
        {
            if (p.Missing.Count > 0 && p.Url.Contains("{{"))
                throw new InvalidOperationException("{{" + p.Missing.First() + "}}");
            if (!Uri.TryCreate(p.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new UriFormatException(p.Url);

            using var msg = Build(p, uri);
            using var res = await ClientFor(options).SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var (body, truncated, interrupted) = await ReadBodyAsync(res, timeout.Token, ct);
            sw.Stop();
            var headers = new List<KeyValuePair<string, string>>();
            foreach (var h in res.Headers) foreach (var v in h.Value) headers.Add(new(h.Key, v));
            foreach (var h in res.Content.Headers) foreach (var v in h.Value) headers.Add(new(h.Key, v));
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
    }

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
    private static string Describe(Exception e)
    {
        var inner = e;
        while (inner.InnerException is not null && inner is HttpRequestException or AggregateException) inner = inner.InnerException;
        return inner.Message;
    }

    private static HttpRequestMessage Build(PreparedRequest p, Uri uri)
    {
        var msg = new HttpRequestMessage(new HttpMethod(p.Method), uri);
        HttpContent? content = p.BodyType switch
        {
            BodyTypes.Json or BodyTypes.Text or BodyTypes.Xml or BodyTypes.Html => new ByteArrayContent(Encoding.UTF8.GetBytes(p.RawBody)),
            BodyTypes.UrlEncoded => new ByteArrayContent(Encoding.ASCII.GetBytes(FormEncode(p.Fields))),
            BodyTypes.FormData => Multipart(p.Fields),
            _ => null,
        };
        var contentHeaders = new List<KeyValuePair<string, string>>();
        foreach (var (k, v) in p.Headers)
        {
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
                var file = new ByteArrayContent(File.ReadAllBytes(f.Value));
                file.Headers.ContentType = new MediaTypeHeaderValue(MimeFor(f.Value));
                m.Add(file, f.Key, Path.GetFileName(f.Value));
            }
            else m.Add(new StringContent(f.Value, Encoding.UTF8), f.Key);
        }
        return m;
    }

    private static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" => "application/json",
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
    private static async Task<(byte[] Body, bool Truncated, string? Interrupted)> ReadBodyAsync(HttpResponseMessage res, CancellationToken timeout, CancellationToken user)
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
            }
        }
        catch (OperationCanceledException) { return (ms.ToArray(), false, user.IsCancellationRequested ? "cancelled" : "timeout"); }
        catch (Exception e) when (e is IOException or HttpRequestException) { return (ms.ToArray(), false, "connection"); }
        return (ms.ToArray(), false, null);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var c in _clients.Values) c.Dispose();
            _clients.Clear();
        }
    }
}
