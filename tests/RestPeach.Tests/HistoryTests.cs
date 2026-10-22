using System.Text;
using RestPeach.Core;
using RestPeach.Core.Http;
using RestPeach.Core.Model;
using RestPeach.Core.Storage;
using RestPeach.Core.Testing;

namespace RestPeach.Tests;

public class HistoryTests
{
    [Fact]
    public void An_entry_round_trips_with_secrets_masked_and_big_bodies_cut()
    {
        var req = new ApiRequest { Name = "Login", Method = "POST", Url = "{{BASE}}/login", Body = new BodySpec { Type = BodyTypes.Json, Data = "{\"pw\":\"{{PW}}\"}" } };
        var sent = new PreparedRequest { Method = "POST", Url = "https://h.test/login?key=hunter22", BodyType = BodyTypes.Json, RawBody = "{\"pw\":\"hunter22\"}" };
        sent.Headers.Add(new("Authorization", "Bearer hunter22"));
        var res = new ApiResponse
        {
            Status = 200, Reason = "OK", FinalUrl = "https://h.test/home", ElapsedMs = 12.34, At = new DateTimeOffset(2026, 10, 21, 9, 30, 0, TimeSpan.Zero),
            Headers = { new("Content-Type", "text/plain") }, Body = Encoding.UTF8.GetBytes(new string('x', History.MaxBodyBytes + 10)),
            Redirects = { new RedirectHop(302, "https://h.test/login", "https://h.test/home", 3) },
        };
        var tests = new List<AssertionResult> { new("status == 200", true, "200"), new("set T = $.t", false, null, "no value", "T") };
        var line = History.Serialize(new RunResult(".api/a/Login.req.json", req, sent, res, tests, null), new[] { "hunter22" });
        Assert.DoesNotContain("hunter22", line);

        var back = History.Deserialize(line)!;
        Assert.Equal("Login", back.Request!.Name);
        Assert.Equal("{{BASE}}/login", back.Request.Url); // the request as written, variables and all
        Assert.Equal("https://h.test/login?key=••••", back.Sent!.Url);
        Assert.Equal("Bearer ••••", back.Sent.Header("Authorization"));
        Assert.Equal(History.MaxBodyBytes, back.Response!.Body.Length);
        Assert.True(back.Response.Truncated);
        Assert.Equal(res.At, back.Response.At);
        Assert.Equal(302, back.Response.Redirects.Single().Status);
        Assert.Equal(2, back.Assertions.Count);
        Assert.Equal("T", back.Assertions[1].CapturedName);
        Assert.Null(History.Deserialize("{not json"));
    }
}
