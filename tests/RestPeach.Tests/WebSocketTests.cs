using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using RestPeach.Core;
using RestPeach.Core.Http;
using RestPeach.Core.Model;
using RestPeach.Core.Storage;

namespace RestPeach.Tests;

/// <summary>WebSocket requests: the file format, the handshake and a real connection against a local HttpListener.</summary>
public sealed class WebSocketTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _base;
    private readonly CancellationTokenSource _stop = new();

    public WebSocketTests()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
        _base = $"http://localhost:{port}/";
        _listener.Prefixes.Add(_base);
        _listener.Start();
        _ = Task.Run(Serve);
    }

    private async Task Serve()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    /// <summary>/echo greets, echoes, and closes on "close" (1000) or "error" (1011); /private wants a token.</summary>
    private async Task Handle(HttpListenerContext ctx)
    {
        if (!ctx.Request.IsWebSocketRequest || ctx.Request.Url!.AbsolutePath == "/private" && ctx.Request.Headers["Authorization"] != "Bearer ok")
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.Close();
            return;
        }
        var wsCtx = await ctx.AcceptWebSocketAsync(null);
        var ws = wsCtx.WebSocket;
        async Task Send(string s) => await ws.SendAsync(Encoding.UTF8.GetBytes(s), WebSocketMessageType.Text, true, CancellationToken.None);
        try
        {
            await Send("{\"type\":\"welcome\",\"ua\":\"" + ctx.Request.UserAgent + "\"}");
            var buffer = new byte[4096];
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); return; }
                var text = Encoding.UTF8.GetString(buffer, 0, r.Count);
                if (text == "close") { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye", CancellationToken.None); return; }
                if (text == "error") { await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, "Boom", CancellationToken.None); return; }
                await Send(text);
            }
        }
        catch (WebSocketException) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _listener.Close();
    }

    private static ApiRequest Ws(string url, string message = "", params string[] tests) => new()
    {
        Name = "ws",
        Method = ApiRequest.WebSocket,
        Url = url,
        Body = message.Length > 0 ? new BodySpec { Type = BodyTypes.Text, Data = message } : new BodySpec(),
        Tests = tests.ToList(),
    };

    private static PreparedRequest Prepare(ApiRequest r, AuthSpec? auth = null) =>
        RequestPreparer.Prepare(r, auth ?? AuthSpec.None(), new VariableScope());

    [Fact]
    public void Kind_comes_from_the_method_or_a_ws_url()
    {
        Assert.Equal(ApiKind.WebSocket, ApiKinds.Of(new ApiRequest { Method = "WS", Url = "{{BASE}}/ws" }));
        Assert.Equal(ApiKind.WebSocket, ApiKinds.Of(new ApiRequest { Method = "GET", Url = "wss://h/feed" }));
        Assert.Equal(ApiKind.Rest, ApiKinds.Of(new ApiRequest { Method = "GET", Url = "https://h/ws" }));
        Assert.Equal("WS", ApiKinds.Tag(ApiKind.WebSocket));
    }

    [Fact]
    public void Saved_messages_round_trip_in_order()
    {
        var r = Ws("ws://h/chat", "{\"type\":\"ping\"}");
        r.Messages.Add(new WsMessage { Name = "who", Data = "{\"type\":\"who\"}" });
        r.Messages.Add(new WsMessage { Name = "join", Data = "{\"type\":\"join\",\"room\":\"dev\"}" });
        var text = Workspace.Serialize(r, "chat.req.json");
        Assert.Contains("\"method\": \"WS\"", text);
        var back = Workspace.ParseRequest(text, "chat.req.json");
        Assert.Equal(new[] { "who", "join" }, back.Messages.Select(m => m.Name));
        Assert.Equal("{\"type\":\"join\",\"room\":\"dev\"}", back.Messages[1].Data);
        Assert.Equal(text, Workspace.Serialize(back, "chat.req.json"));
    }

    [Fact]
    public void Prepared_handshake_uses_ws_scheme_and_no_content_type()
    {
        var p = Prepare(Ws("http://h:5000/ws/echo?room=a", "{\"a\":1}"), AuthSpec.BearerOf("tok"));
        Assert.Equal("ws://h:5000/ws/echo?room=a", p.Url);
        Assert.Equal("WS", p.Method);
        Assert.Equal(ApiKind.WebSocket, p.Kind);
        Assert.False(p.HasHeader("Content-Type"));
        Assert.Equal("Bearer tok", p.Header("Authorization"));
        Assert.Equal("{\"a\":1}", p.RawBody);
        Assert.Equal("wss://h/x", RequestPreparer.WebSocketUrl("https://h/x"));
    }

    [Fact]
    public async Task Session_sends_receives_and_sees_the_server_close()
    {
        using var engine = new HttpEngine();
        var (session, handshake) = await WebSocketSession.ConnectAsync(Prepare(Ws(_base + "echo")), new HttpOptions(), engine, CancellationToken.None);
        Assert.NotNull(session);
        Assert.Equal(101, handshake.Status);

        var events = new List<WsEvent>();
        var closed = new TaskCompletionSource<string>();
        session!.Event += e => { lock (events) events.Add(e); };
        session.Closed += t => closed.TrySetResult(t);
        session.Start();
        await session.SendAsync("hola", CancellationToken.None);
        await session.SendAsync("close", CancellationToken.None);

        Assert.Equal("1000 Bye", await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(session.IsOpen);
        lock (events)
        {
            Assert.Contains(events, e => e.Direction == WsDirection.Received && e.Text.Contains("welcome") && e.Text.Contains("RestPeach/"));
            Assert.Contains(events, e => e.Direction == WsDirection.Received && e.Text == "hola");
            Assert.Equal(2, events.Count(e => e.Direction == WsDirection.Sent));
        }
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Refused_handshake_reports_the_status()
    {
        using var engine = new HttpEngine();
        var (session, handshake) = await WebSocketSession.ConnectAsync(Prepare(Ws(_base + "private")), new HttpOptions(), engine, CancellationToken.None);
        Assert.Null(session);
        Assert.Equal(401, handshake.Status);
        Assert.NotNull(handshake.Error);

        var (ok, _) = await WebSocketSession.ConnectAsync(Prepare(Ws(_base + "private"), AuthSpec.BearerOf("ok")), new HttpOptions(), engine, CancellationToken.None);
        Assert.NotNull(ok);
        await ok!.DisposeAsync();
    }

    [Fact]
    public async Task Run_collects_the_messages_as_a_json_array()
    {
        var dir = Directory.CreateTempSubdirectory("rp-ws-").FullName;
        try
        {
            var ws = Workspace.Init(dir, "ws");
            ws.CreateRequest("", Ws(_base + "echo", "{\"n\": 1}", "status == 101", "$[0].type == \"welcome\"", "$[1].n == 1"));
            var noisy = Ws(_base + "echo", "error");
            noisy.Name = "noisy";
            ws.CreateRequest("", noisy);
            using var engine = new HttpEngine();
            var runner = new Runner(ws, engine, null);

            var good = await runner.RunAsync("ws.req.json", CancellationToken.None);
            Assert.True(good.Passed, string.Join("; ", good.Assertions.Select(a => a.Line + " " + a.Error + " " + a.Actual)));
            Assert.Contains(ProtocolInsight.Describe(ApiKind.WebSocket, good.Response!), i => i.Text.Contains('2'));

            // No tests, but the server hung up with 1011: that is a failure.
            var bad = await runner.RunAsync("noisy.req.json", CancellationToken.None);
            Assert.Equal("1011 Boom", bad.Response!.WebSocketClose);
            Assert.False(bad.Passed);
        }
        finally { Directory.Delete(dir, true); }
    }
}
