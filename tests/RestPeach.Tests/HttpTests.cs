using System.Net;
using System.Net.Sockets;
using System.Text;
using RestPeach.Core;
using RestPeach.Core.Http;
using RestPeach.Core.Model;
using RestPeach.Core.Storage;

namespace RestPeach.Tests;

/// <summary>Real requests against a local HttpListener: sending, cookies, captures between requests and the runner.</summary>
public sealed class HttpTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _base;
    private readonly CancellationTokenSource _stop = new();

    public HttpTests()
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
            var req = ctx.Request;
            var res = ctx.Response;
            string body = await new StreamReader(req.InputStream).ReadToEndAsync();
            string reply;
            switch (req.Url!.AbsolutePath)
            {
                case "/login":
                    res.Headers.Add("Set-Cookie", "sid=abc; Path=/");
                    reply = "{\"token\":\"t-" + (body.Contains("ana") ? "ana" : "x") + "\"}";
                    break;
                case "/me":
                    var auth = req.Headers["Authorization"];
                    var cookie = req.Headers["Cookie"];
                    res.StatusCode = auth == "Bearer t-ana" ? 200 : 401;
                    reply = "{\"auth\":\"" + auth + "\",\"cookie\":\"" + cookie + "\",\"ct\":\"" + req.ContentType + "\"}";
                    break;
                case "/echo":
                    reply = "{\"method\":\"" + req.HttpMethod + "\",\"query\":\"" + req.Url.Query + "\",\"body\":" + System.Text.Json.JsonSerializer.Serialize(body) + ",\"ct\":\"" + req.ContentType + "\"}";
                    break;
                default:
                    res.StatusCode = 404;
                    reply = "nope";
                    break;
            }
            res.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(reply);
            await res.OutputStream.WriteAsync(bytes);
            res.Close();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _listener.Close();
    }

    [Fact]
    public async Task Sends_body_query_and_measures()
    {
        using var engine = new HttpEngine();
        var req = new ApiRequest
        {
            Method = "PUT", Url = _base + "echo?a=1",
            Body = new BodySpec { Type = BodyTypes.UrlEncoded, Fields = { KeyValue.Of("x", "a b"), KeyValue.Of("off", "1", active: false) } },
        };
        var p = RequestPreparer.Prepare(req, AuthSpec.None(), new VariableScope());
        var res = await engine.SendAsync(p, new HttpOptions(), CancellationToken.None);
        Assert.Null(res.Error);
        Assert.Equal(200, res.Status);
        Assert.Contains("\"method\":\"PUT\"", res.Text);
        Assert.Contains("\"body\":\"x=a%20b\"", res.Text);
        Assert.Contains("application/x-www-form-urlencoded", res.Text);
        Assert.True(res.ElapsedMs > 0);
    }

    [Fact]
    public async Task Binary_body_sends_the_file_relative_to_the_collection()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "files"));
        File.WriteAllText(Path.Combine(tmp.Path, "files", "data.csv"), "a,b;1,2");
        var req = new ApiRequest { Method = "POST", Url = _base + "echo", Body = new BodySpec { Type = BodyTypes.Binary, Data = "{{DIR}}/data.csv" } };
        var scope = new VariableScope();
        scope.Set("DIR", "files", VariableSource.Collection);
        var p = RequestPreparer.Prepare(req, AuthSpec.None(), scope, tmp.Path);
        Assert.Equal(Path.Combine(tmp.Path, "files", "data.csv"), p.RawBody);
        Assert.Equal("text/csv", p.Header("Content-Type"));
        Assert.Contains("--data-binary " + RestPeach.Core.Formats.Curl.Quote("@" + p.RawBody), RestPeach.Core.Formats.Curl.Write(p));

        using var engine = new HttpEngine();
        var res = await engine.SendAsync(p, new HttpOptions(), CancellationToken.None);
        Assert.Null(res.Error);
        Assert.Contains("\"body\":\"a,b;1,2\"", res.Text);
        Assert.Contains("\"ct\":\"text/csv\"", res.Text);

        var gone = RequestPreparer.Prepare(req, AuthSpec.None(), new VariableScope(), tmp.Path);
        Assert.NotNull((await engine.SendAsync(gone, new HttpOptions(), CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Form_file_field_without_a_file_says_which_field()
    {
        var req = new ApiRequest
        {
            Method = "POST", Url = _base + "echo",
            Body = new BodySpec { Type = BodyTypes.FormData, Fields = { new KeyValue { Key = "upload", Value = "", Type = "file" } } },
        };
        using var engine = new HttpEngine();
        var res = await engine.SendAsync(RequestPreparer.Prepare(req, AuthSpec.None(), new VariableScope()), new HttpOptions(), CancellationToken.None);
        Assert.Contains("\"upload\"", res.Error);
    }

    [Fact]
    public async Task Connection_errors_are_reported_not_thrown()
    {
        using var engine = new HttpEngine();
        var p = RequestPreparer.Prepare(new ApiRequest { Url = "http://127.0.0.1:1/x" }, AuthSpec.None(), new VariableScope());
        var res = await engine.SendAsync(p, new HttpOptions(TimeoutMs: 3000), CancellationToken.None);
        Assert.NotNull(res.Error);
        Assert.False(res.Ok);

        var missing = RequestPreparer.Prepare(new ApiRequest { Url = "{{NOPE}}/x" }, AuthSpec.None(), new VariableScope());
        Assert.Contains("NOPE", (await engine.SendAsync(missing, new HttpOptions(), CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Runner_chains_captures_and_keeps_cookies()
    {
        using var tmp = new TempDir();
        var ws = Workspace.Init(tmp.Path, "demo");
        var env = ws.LoadEnvironment("local");
        env.Variables["BASE_URL"] = _base.TrimEnd('/');
        ws.SaveEnvironment(env);
        var config = ws.LoadConfig();
        config.Auth = AuthSpec.BearerOf("{{TOKEN}}");
        ws.SaveConfig(config);

        var auth = ws.CreateFolder("", "1 Auth");
        ws.CreateRequest(auth, new ApiRequest
        {
            Name = "Login", Method = "POST", Url = "{{BASE_URL}}/login", Auth = AuthSpec.None(),
            Body = new BodySpec { Type = BodyTypes.Json, Data = "{\"user\":\"ana\"}" },
            Tests = { "status == 200", "set TOKEN = $.token" },
        });
        var users = ws.CreateFolder("", "2 Users");
        ws.CreateRequest(users, new ApiRequest { Name = "Me", Url = "{{BASE_URL}}/me", Tests = { "status == 200", "$.cookie contains sid=abc" } });
        ws.CreateRequest(users, new ApiRequest { Name = "Missing", Url = "{{BASE_URL}}/missing" });

        using var engine = new HttpEngine();
        var runner = new Runner(ws, engine, "local");
        var results = new List<RunResult>();
        await foreach (var r in runner.RunAllAsync(ws.ListRequests(), bail: false, CancellationToken.None)) results.Add(r);

        Assert.Equal(new[] { "Login", "Me", "Missing" }, results.Select(r => r.Name));
        Assert.True(results[0].Passed);
        Assert.Equal("t-ana", runner.Runtime["TOKEN"]);
        Assert.True(results[1].Passed, string.Join("; ", results[1].Assertions.Select(a => a.Line + "=" + a.Actual)));
        Assert.False(results[2].Passed); // 404 without tests
        Assert.Single(engine.AllCookies());

        var bailed = new List<RunResult>();
        await foreach (var r in new Runner(ws, new HttpEngine(), "local").RunAllAsync(ws.ListRequests(users), bail: true, CancellationToken.None)) bailed.Add(r);
        Assert.Single(bailed); // "Me" fails without the token and the run stops there
    }
}
