using System.Net;
using System.Net.Sockets;
using System.Text;
using RestPeach.Core.Http;
using RestPeach.Core.Model;
using RestPeach.Core.Testing;

namespace RestPeach.Tests;

/// <summary>Server-Sent Events: the parser, tests over the events, and a live stream against a local HttpListener.</summary>
public sealed class SseTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _base;

    public SseTests()
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
        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            _ = Task.Run(async () =>
            {
                var res = ctx.Response;
                res.ContentType = "text/event-stream; charset=utf-8";
                res.SendChunked = true;
                async Task Write(string s)
                {
                    var b = Encoding.UTF8.GetBytes(s);
                    await res.OutputStream.WriteAsync(b);
                    await res.OutputStream.FlushAsync();
                }
                try
                {
                    // /tokens: three events and the end; /forever: one event, then silence.
                    await Write(": hello\n\nevent: start\ndata: {\"n\": 0}\n\n");
                    if (ctx.Request.Url!.AbsolutePath == "/forever") { await Task.Delay(10_000); return; }
                    for (int i = 1; i <= 2; i++)
                    {
                        await Task.Delay(150);
                        await Write($"id: {i}\ndata: {{\"n\": {i}, \"text\": \"ñ{i}\"}}\n\n");
                    }
                    await Write("event: done\ndata: [DONE]\n\n");
                }
                catch (HttpListenerException) { }
                finally { try { res.Close(); } catch { } }
            });
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
    }

    [Fact]
    public void Parser_handles_chunks_line_endings_comments_and_split_characters()
    {
        var bytes = Encoding.UTF8.GetBytes("﻿: comment\r\nevent: greet\r\ndata: hola\r\ndata: señor\r\n\r\nid: 7\rdata:x\r\rdata: lost");
        var p = new SseParser();
        var events = new List<SseEvent>();
        foreach (var b in bytes) events.AddRange(p.Feed(new[] { b })); // one byte at a time: "ñ" arrives in two halves
        Assert.Equal(2, events.Count);
        Assert.Equal(new SseEvent("greet", "hola\nseñor", null), events[0]);
        Assert.Equal(new SseEvent("message", "x", "7"), events[1]); // the last one never ended
    }

    [Fact]
    public void Tests_see_the_events_as_a_json_array()
    {
        var r = new ApiResponse
        {
            Status = 200,
            Headers = { new("Content-Type", "text/event-stream") },
            Body = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\nevent: done\ndata: [DONE]\n\n"),
        };
        var results = Assertions.Evaluate(new[]
        {
            "$.length == 2", "$[0].data.choices[0].delta.content == \"Hi\"", "$[1].event == \"done\"", "$[1].data == \"[DONE]\"", "body contains \"[DONE]\"",
        }, r);
        Assert.All(results, a => Assert.True(a.Passed, a.Line + " → " + a.Actual + " " + a.Error));
    }

    private sealed class Recorder : IStreamListener
    {
        public ApiResponse? Head;
        public readonly SseParser Parser = new();
        public readonly List<(SseEvent Event, DateTime At)> Events = new();
        public Action? OnStarted;
        public void Started(ApiResponse head) { Head = head; OnStarted?.Invoke(); }
        public void Data(byte[] chunk) { lock (Events) foreach (var e in Parser.Feed(chunk)) Events.Add((e, DateTime.UtcNow)); }
    }

    [Fact]
    public async Task Live_stream_reports_events_as_they_arrive()
    {
        using var engine = new HttpEngine();
        var rec = new Recorder();
        var p = RequestPreparer.Prepare(new ApiRequest { Url = _base + "tokens" }, AuthSpec.None(), new VariableScope());
        var res = await engine.SendAsync(p, new HttpOptions(TimeoutMs: 5000), CancellationToken.None, rec);
        Assert.Null(res.Error);
        Assert.Null(res.Interrupted);
        Assert.Equal(200, rec.Head!.Status);
        Assert.Equal(new[] { "start", "message", "message", "done" }, rec.Events.Select(e => e.Event.Event));
        Assert.Equal("2", rec.Events[2].Event.Id);
        Assert.Contains("ñ2", rec.Events[2].Event.Data);
        // They came one by one, not all at the end.
        Assert.True((rec.Events[^1].At - rec.Events[0].At).TotalMilliseconds > 200);
        Assert.Contains(ProtocolInsight.Describe(ApiKind.Rest, res), i => i.Text.Contains('4') && i.Text.Contains("done"));
    }

    [Fact]
    public async Task Quiet_live_stream_is_closed_by_the_idle_timeout_and_keeps_what_came()
    {
        using var engine = new HttpEngine();
        var p = RequestPreparer.Prepare(new ApiRequest { Url = _base + "forever" }, AuthSpec.None(), new VariableScope());
        // The timeout also bounds the wait for the headers: roomy enough for a slow CI machine.
        var res = await engine.SendAsync(p, new HttpOptions(TimeoutMs: 1500), CancellationToken.None, new Recorder());
        Assert.Null(res.Error);
        Assert.Equal("idle", res.Interrupted);
        Assert.Contains("\"n\": 0", res.Text);

        // A user's cancel once the stream is live keeps it too (a cancel before the headers throws, by design).
        using var cts = new CancellationTokenSource();
        var stopped = await engine.SendAsync(p, new HttpOptions(TimeoutMs: 5000), cts.Token, new Recorder { OnStarted = () => cts.CancelAfter(200) });
        Assert.Equal("cancelled", stopped.Interrupted);
    }
}
