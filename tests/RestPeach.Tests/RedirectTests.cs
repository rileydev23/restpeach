using System.Net;
using System.Net.Sockets;
using System.Text;
using RestPeach.Core.Http;
using RestPeach.Core.Model;

namespace RestPeach.Tests;

/// <summary>Redirects are followed by the engine itself: every hop is kept, methods change like curl's, credentials stay home.</summary>
public sealed class RedirectTests : IDisposable
{
    private readonly HttpListener _a = new(), _b = new();
    private readonly string _baseA, _baseB;

    public RedirectTests()
    {
        _baseA = Start(_a);
        _baseB = Start(_b);
    }

    private string Start(HttpListener l)
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
        var b = $"http://localhost:{port}/";
        l.Prefixes.Add(b);
        l.Start();
        _ = Task.Run(() => Serve(l));
        return b;
    }

    private async Task Serve(HttpListener l)
    {
        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = await l.GetContextAsync(); } catch { return; }
            var req = ctx.Request;
            var res = ctx.Response;
            var body = await new StreamReader(req.InputStream).ReadToEndAsync();
            string reply = "";
            void Go(int code, string to) { res.StatusCode = code; res.RedirectLocation = to; }
            switch (req.Url!.AbsolutePath)
            {
                case "/r1": res.AppendCookie(new Cookie("hop", "1", "/")); Go(302, "/r2"); break;
                case "/r2": Go(301, _baseA + "final"); break;
                case "/loop": Go(302, "/loop"); break;
                case "/s303": Go(303, "/final"); break;
                case "/s307": Go(307, "/final"); break;
                case "/away": Go(302, _baseB + "final"); break;
                default:
                    reply = $"{req.HttpMethod} body={body} auth={req.Headers["Authorization"]} cookie={req.Headers["Cookie"]} ct={req.ContentType}";
                    break;
            }
            var bytes = Encoding.UTF8.GetBytes(reply);
            await res.OutputStream.WriteAsync(bytes);
            res.Close();
        }
    }

    public void Dispose()
    {
        foreach (var l in new[] { _a, _b }) { l.Stop(); l.Close(); }
    }

    private static PreparedRequest Prepare(string method, string url, string body = "", AuthSpec? auth = null) =>
        RequestPreparer.Prepare(new ApiRequest
        {
            Method = method, Url = url,
            Body = body.Length > 0 ? new BodySpec { Type = BodyTypes.Json, Data = body } : new BodySpec(),
        }, auth ?? AuthSpec.None(), new VariableScope());

    [Fact]
    public async Task Every_hop_is_kept_and_cookies_set_on_the_way_are_sent()
    {
        using var engine = new HttpEngine();
        var res = await engine.SendAsync(Prepare("GET", _baseA + "r1"), new HttpOptions(), CancellationToken.None);
        Assert.Equal(200, res.Status);
        Assert.Equal(_baseA + "final", res.FinalUrl);
        Assert.Equal(new[] { 302, 301 }, res.Redirects.Select(h => h.Status));
        Assert.Equal(_baseA + "r2", res.Redirects[0].To);
        Assert.Contains("cookie=hop=1", res.Text);
        Assert.Equal(2, ProtocolInsight.Describe(ApiKind.Rest, res).Count(i => i.Text.Contains("→")));

        var stay = await engine.SendAsync(Prepare("GET", _baseA + "r1"), new HttpOptions(FollowRedirects: false), CancellationToken.None);
        Assert.Equal(302, stay.Status);
        Assert.Empty(stay.Redirects);
    }

    [Fact]
    public async Task Methods_change_like_curl_and_browsers()
    {
        using var engine = new HttpEngine();
        var see = await engine.SendAsync(Prepare("POST", _baseA + "s303", "{\"a\":1}"), new HttpOptions(), CancellationToken.None);
        Assert.StartsWith("GET body= ", see.Text);
        Assert.EndsWith("ct=", see.Text);
        var keep = await engine.SendAsync(Prepare("PUT", _baseA + "s307", "{\"a\":1}"), new HttpOptions(), CancellationToken.None);
        Assert.StartsWith("PUT body={\"a\":1}", keep.Text);
        Assert.EndsWith("ct=application/json", keep.Text);
    }

    [Fact]
    public async Task Credentials_do_not_follow_to_another_origin()
    {
        using var engine = new HttpEngine();
        var home = await engine.SendAsync(Prepare("GET", _baseA + "r2", auth: AuthSpec.BearerOf("secret")), new HttpOptions(), CancellationToken.None);
        Assert.Contains("auth=Bearer secret", home.Text);
        var away = await engine.SendAsync(Prepare("GET", _baseA + "away", auth: AuthSpec.BearerOf("secret")), new HttpOptions(), CancellationToken.None);
        Assert.Equal(_baseB + "final", away.FinalUrl);
        Assert.Contains("auth= ", away.Text);
    }

    [Fact]
    public async Task A_loop_stops_with_an_error()
    {
        using var engine = new HttpEngine();
        var res = await engine.SendAsync(Prepare("GET", _baseA + "loop"), new HttpOptions(), CancellationToken.None);
        Assert.NotNull(res.Error);
        Assert.Contains("10", res.Error);
    }
}
