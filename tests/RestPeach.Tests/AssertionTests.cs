using System.Text;
using RestPeach.Core.Http;
using RestPeach.Core.Testing;

namespace RestPeach.Tests;

public class AssertionTests
{
    private static ApiResponse Res(int status = 200, string body = "{\"data\":{\"id\":7,\"name\":\"Ana\",\"tags\":[\"a\",\"b\"]},\"ok\":true}", double ms = 120) => new()
    {
        Status = status,
        Reason = "OK",
        Body = Encoding.UTF8.GetBytes(body),
        ElapsedMs = ms,
        Headers = { new("Content-Type", "application/json; charset=utf-8"), new("X-Session", "s-1") },
    };

    private static AssertionResult One(string line, ApiResponse? r = null) => Assertions.Evaluate(new[] { line }, r ?? Res()).Single();

    [Theory]
    [InlineData("status == 200")]
    [InlineData("Status == 200")]
    [InlineData("status in 200, 201")]
    [InlineData("status == 2xx")]
    [InlineData("status < 300")]
    [InlineData("Response Time < 500ms")]
    [InlineData("time <= 1s")]
    [InlineData("size > 10")]
    [InlineData("JSON Path $.data.id exists")]
    [InlineData("$.data.id == 7")]
    [InlineData("$.data.name == \"Ana\"")]
    [InlineData("$.data.name is Ana")]
    [InlineData("$.data.tags.length == 2")]
    [InlineData("$.data.tags[-1] == b")]
    [InlineData("$['data']['name'] startsWith A")]
    [InlineData("$.ok == true")]
    [InlineData("$.missing not exists")]
    [InlineData("$.missing !exists")]
    [InlineData("header Content-Type contains JSON")]
    [InlineData("header X-Session exists")]
    [InlineData("header X-Nope not exists")]
    [InlineData("body contains \"Ana\"")]
    [InlineData("body matches ^\\{")]
    [InlineData("body not contains error")]
    public void Passing_lines(string line)
    {
        var r = One(line);
        Assert.True(r.Passed, $"{line} → actual={r.Actual} error={r.Error}");
        Assert.Null(r.Error);
    }

    [Theory]
    [InlineData("status == 201", "200")]
    [InlineData("time < 100ms", "120")]
    [InlineData("$.data.id > 10", "7")]
    [InlineData("$.data.name == Bob", "Ana")]
    [InlineData("$.data.id not exists", "7")]
    public void Failing_lines_report_what_came_back(string line, string actual)
    {
        var r = One(line);
        Assert.False(r.Passed);
        Assert.Null(r.Error);
        Assert.Equal(actual, r.Actual);
    }

    [Theory]
    [InlineData("status ~ 200")]
    [InlineData("status ==")]
    [InlineData("whatever == 1")]
    [InlineData("$.a[ == 1")]
    [InlineData("body matches (")]
    public void Malformed_lines_are_errors_not_crashes(string line)
    {
        var r = One(line);
        Assert.False(r.Passed);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public void Comments_are_skipped_and_captures_fill_the_runtime()
    {
        var captures = new Dictionary<string, string>();
        var results = Assertions.Evaluate(new[] { "# a comment", "// another", "set USER_ID = $.data.id", "set SESSION = header X-Session", "set MISSING = $.nope" }, Res(), captures);
        Assert.Equal(3, results.Count);
        Assert.Equal("7", captures["USER_ID"]);
        Assert.Equal("s-1", captures["SESSION"]);
        Assert.False(results[2].Passed);
        Assert.True(results.All(r => r.IsCapture));
    }

    [Fact]
    public void Json_checks_on_a_non_json_body()
    {
        var html = Res(body: "<html></html>");
        Assert.False(One("$.a exists", html).Passed);
        Assert.True(One("$.a not exists", html).Passed);
    }

    [Fact]
    public void A_failed_request_fails_every_check()
    {
        var r = One("status == 200", new ApiResponse { Error = "No such host" });
        Assert.False(r.Passed);
        Assert.Equal("No such host", r.Error);
    }
}
