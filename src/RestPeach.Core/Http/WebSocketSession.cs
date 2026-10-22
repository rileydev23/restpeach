using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace RestPeach.Core.Http;

public enum WsDirection { Sent, Received, Info, Error }

/// <summary>One line of a WebSocket log: a message either way, or something that happened to the connection.</summary>
public sealed record WsEvent(DateTimeOffset At, WsDirection Direction, string Text, bool Binary = false, long Size = 0)
{
    public bool IsMessage => Direction is WsDirection.Sent or WsDirection.Received;
}

/// <summary>The HTTP part of a WebSocket: the upgrade request and what the server answered to it.</summary>
public sealed class WsHandshake
{
    /// <summary>101 when the connection was upgraded; the server's status when it refused (401, 404…); 0 when nothing answered.</summary>
    public int Status { get; init; }
    public List<KeyValuePair<string, string>> Headers { get; init; } = new();
    public string? SubProtocol { get; init; }
    public double ElapsedMs { get; init; }
    public string? Error { get; init; }
    public bool Ok => Error is null && Status == 101;
}

/// <summary>
/// An open WebSocket. Messages from the server and changes of the connection arrive through <see cref="Event"/>,
/// raised on a background thread; <see cref="Closed"/> fires once when it is over, whoever closed it.
/// </summary>
public sealed class WebSocketSession : IAsyncDisposable
{
    /// <summary>Longer messages are cut (a log line is not the place for a 100 MB frame).</summary>
    public const int MaxMessageBytes = 16 * 1024 * 1024;

    /// <summary>Handshake headers that <see cref="ClientWebSocket"/> writes itself and refuses from the caller.</summary>
    private static readonly HashSet<string> Managed = new(StringComparer.OrdinalIgnoreCase)
        { "Host", "Connection", "Upgrade", "Sec-WebSocket-Key", "Sec-WebSocket-Version", "Sec-WebSocket-Extensions", "Sec-WebSocket-Protocol", "Content-Length", "Content-Type" };

    private readonly ClientWebSocket _socket;
    private readonly CancellationTokenSource _stop = new();
    private readonly Stopwatch _since = Stopwatch.StartNew();
    private int _closed, _started;

    public event Action<WsEvent>? Event;
    /// <summary>The close code and reason ("1000 Bye"), or the error that ended the connection.</summary>
    public event Action<string>? Closed;

    public WsHandshake Handshake { get; }
    public int Sent { get; private set; }
    public int Received { get; private set; }
    public TimeSpan Uptime => _since.Elapsed;
    public bool IsOpen => _socket.State == WebSocketState.Open && Volatile.Read(ref _closed) == 0;
    /// <summary>Set once the connection is over: "1000 Bye", "1006"…</summary>
    public string? CloseText { get; private set; }

    private WebSocketSession(ClientWebSocket socket, WsHandshake handshake)
    {
        _socket = socket;
        Handshake = handshake;
    }

    /// <summary>
    /// Opens the connection. Returns the session (null when the handshake failed) and the handshake either way, so a
    /// refusal shows its status and headers like any HTTP response.
    /// </summary>
    public static async Task<(WebSocketSession? Session, WsHandshake Handshake)> ConnectAsync(PreparedRequest p, HttpOptions options,
        HttpEngine engine, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.TimeoutMs);
        try
        {
            if (p.Missing.Count > 0 && p.Url.Contains("{{"))
                throw new InvalidOperationException("{{" + p.Missing.First() + "}}");
            if (!Uri.TryCreate(p.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss"))
                throw new UriFormatException(p.Url);

            foreach (var (k, v) in p.Headers)
            {
                if (k.Equals("Sec-WebSocket-Protocol", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var proto in v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        socket.Options.AddSubProtocol(proto);
                }
                else if (!Managed.Contains(k)) socket.Options.SetRequestHeader(k, v);
            }
            if (!p.HasHeader("User-Agent")) socket.Options.SetRequestHeader("User-Agent", HttpEngine.UserAgent);
            if (p.Deferred is { Type: Model.AuthTypes.OAuth2 } oauth)
            {
                var token = await engine.OAuthTokenAsync(oauth, options, false, ct);
                socket.Options.SetRequestHeader("Authorization", "Bearer " + token.AccessToken);
            }

            await socket.ConnectAsync(uri, engine.WebSocketInvoker(options), timeout.Token);
            var handshake = new WsHandshake
            {
                Status = (int)socket.HttpStatusCode,
                Headers = Flatten(socket.HttpResponseHeaders),
                SubProtocol = socket.SubProtocol,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
            };
            socket.HttpResponseHeaders = null; // only needed once
            return (new WebSocketSession(socket, handshake), handshake);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var refused = Refused(socket, sw, $"Timeout ({options.TimeoutMs} ms)");
            socket.Dispose();
            return (null, refused);
        }
        catch (OperationCanceledException)
        {
            socket.Dispose();
            throw;
        }
        catch (Exception e)
        {
            var refused = Refused(socket, sw, HttpEngine.Describe(e));
            socket.Dispose();
            return (null, refused);
        }
    }

    /// <summary>Starts reading from the server. Call it after subscribing to <see cref="Event"/>, or the welcome message is lost.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) _ = Task.Run(ReceiveLoopAsync);
    }

    private static WsHandshake Refused(ClientWebSocket socket, Stopwatch sw, string error) => new()
    {
        Status = (int)socket.HttpStatusCode,
        Headers = Flatten(socket.HttpResponseHeaders),
        ElapsedMs = sw.Elapsed.TotalMilliseconds,
        Error = error,
    };

    private static List<KeyValuePair<string, string>> Flatten(IReadOnlyDictionary<string, IEnumerable<string>>? headers)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (headers is null) return list;
        foreach (var (k, values) in headers) foreach (var v in values) list.Add(new(k, v));
        return list;
    }

    public async Task SendAsync(string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        Sent++;
        Raise(new WsEvent(DateTimeOffset.Now, WsDirection.Sent, text, Size: bytes.Length));
    }

    /// <summary>Starts the closing handshake; <see cref="Closed"/> fires when the server answers (or after 3 s).</summary>
    public async Task CloseAsync(WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure, string reason = "")
    {
        if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) { Finish("1006"); return; }
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await _socket.CloseOutputAsync(status, reason, wait.Token); }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        // The receive loop sees the server's close frame and finishes; if it never comes, give up on it.
        _ = Task.Delay(3000).ContinueWith(_ => { if (Volatile.Read(ref _closed) == 0) { _stop.Cancel(); Finish($"{(int)status}"); } }, TaskScheduler.Default);
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (_socket.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                message.SetLength(0);
                bool cut = false;
                ValueWebSocketReceiveResult r;
                do
                {
                    r = await _socket.ReceiveAsync(buffer.AsMemory(), _stop.Token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    if (message.Length + r.Count <= MaxMessageBytes) message.Write(buffer, 0, r.Count);
                    else cut = true;
                }
                while (!r.EndOfMessage);

                if (r.MessageType == WebSocketMessageType.Close)
                {
                    var code = _socket.CloseStatus is { } s ? ((int)s).ToString() : "1005";
                    var text = _socket.CloseStatusDescription is { Length: > 0 } d ? code + " " + d : code;
                    // Answer the server's close so it can end the TCP connection cleanly.
                    if (_socket.State == WebSocketState.CloseReceived)
                    {
                        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        try { await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", wait.Token); }
                        catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
                    }
                    Finish(text);
                    return;
                }
                Received++;
                var bytes = message.ToArray();
                bool binary = r.MessageType == WebSocketMessageType.Binary;
                Raise(new WsEvent(DateTimeOffset.Now, WsDirection.Received, binary ? Hex(bytes) : Encoding.UTF8.GetString(bytes),
                    binary, cut ? MaxMessageBytes : bytes.Length));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException)
        {
            if (Volatile.Read(ref _closed) == 0) Raise(new WsEvent(DateTimeOffset.Now, WsDirection.Error, HttpEngine.Describe(e)));
        }
        Finish(_socket.CloseStatus is { } st ? ((int)st).ToString() : "1006");
    }

    private void Finish(string text)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        CloseText = text;
        Closed?.Invoke(text);
    }

    private void Raise(WsEvent e) => Event?.Invoke(e);

    /// <summary>A binary frame as hex, cut at 4 KB: "0a 1b 2c …".</summary>
    public static string Hex(byte[] bytes)
    {
        const int max = 4096;
        var hex = Convert.ToHexString(bytes, 0, Math.Min(bytes.Length, max)).ToLowerInvariant();
        var sb = new StringBuilder(hex.Length * 3 / 2);
        for (int i = 0; i < hex.Length; i += 2)
        {
            if (i > 0) sb.Append(i % 64 == 0 ? '\n' : ' ');
            sb.Append(hex, i, 2);
        }
        if (bytes.Length > max) sb.Append(" …");
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (IsOpen) await CloseAsync(WebSocketCloseStatus.NormalClosure, "");
        _stop.Cancel();
        Finish(CloseText ?? "1000");
        _socket.Dispose();
        _stop.Dispose();
    }

    // ------------------------------------------------------------ runs (CLI and "Run collection")

    /// <summary>
    /// A WebSocket inside a collection run: connects, sends the request's message (if it has one), collects what the
    /// server says until it is quiet for <paramref name="quietMs"/> (or closes, or the timeout ends), and closes.
    /// The result reads like an HTTP response, so the same tests work: <c>status == 101</c>, <c>$[0].type == "welcome"</c>.
    /// The body is a JSON array of the received messages (each parsed when it is JSON, else a string).
    /// </summary>
    public static async Task<ApiResponse> ProbeAsync(PreparedRequest p, HttpOptions options, HttpEngine engine, CancellationToken ct, int quietMs = 1000)
    {
        var sw = Stopwatch.StartNew();
        var (session, handshake) = await ConnectAsync(p, options, engine, ct);
        if (session is null)
        {
            // The server refused the upgrade with a real HTTP answer: that answer is the response (a 401 is testable).
            if (handshake.Status > 0)
                return new ApiResponse { Status = handshake.Status, Reason = handshake.Error ?? "", Headers = handshake.Headers, FinalUrl = p.Url, ElapsedMs = handshake.ElapsedMs };
            return new ApiResponse { Error = handshake.Error, FinalUrl = p.Url, ElapsedMs = handshake.ElapsedMs };
        }

        var received = new List<string>();
        var signal = new SemaphoreSlim(0);
        string? interrupted = null;
        session.Event += e =>
        {
            if (e.Direction == WsDirection.Received) lock (received) received.Add(e.Text);
            signal.Release();
        };
        // The socket stops being Open as soon as the server's close frame arrives, before the close code is known:
        // wait for this event, not for the state.
        var closed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Closed += t => { closed.TrySetResult(t); signal.Release(); };
        session.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.TimeoutMs);
        try
        {
            if (p.RawBody.Trim().Length > 0) await session.SendAsync(p.RawBody, timeout.Token);
            // Wait while messages keep coming; stop at the first quiet gap.
            while (!closed.Task.IsCompleted && await signal.WaitAsync(quietMs, timeout.Token)) { }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { interrupted = "timeout"; }
        catch (WebSocketException) { interrupted = "connection"; }

        var closedByServer = closed.Task.IsCompleted ? closed.Task.Result : null;
        await session.DisposeAsync();
        sw.Stop();

        var array = new JsonArray();
        lock (received)
            foreach (var m in received)
            {
                JsonNode? node;
                try { node = Text.CanonicalJson.Parse(m); }
                catch (System.Text.Json.JsonException) { node = JsonValue.Create(m); }
                array.Add(node);
            }
        return new ApiResponse
        {
            Status = handshake.Status,
            Reason = "Switching Protocols",
            HttpVersion = "1.1",
            FinalUrl = p.Url,
            Headers = handshake.Headers,
            Body = Encoding.UTF8.GetBytes(array.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })),
            ElapsedMs = sw.Elapsed.TotalMilliseconds,
            Interrupted = interrupted,
            WebSocketClose = closedByServer,
        };
    }
}
