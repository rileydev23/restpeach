using RestPeach.Core.Formats;
using RestPeach.Core.Http;
using RestPeach.Core.Model;

namespace RestPeach.Tests;

public class CurlTests
{
    [Fact]
    public void Parses_a_bash_command_with_continuations()
    {
        var r = Curl.Parse("curl -X POST 'https://api.test/v1/users?x=1' \\\n  -H 'Content-Type: application/json' \\\n  -H \"Authorization: Bearer abc\" \\\n  --data-raw '{\"email\":\"a@b.c\"}'")!;
        Assert.Equal("POST", r.Method);
        Assert.Equal("https://api.test/v1/users?x=1", r.Url);
        Assert.Equal(AuthTypes.Bearer, r.Auth.Type);
        Assert.Equal("abc", r.Auth.Token);
        Assert.Equal(BodyTypes.Json, r.Body.Type);
        Assert.Contains("\"email\": \"a@b.c\"", r.Body.Data);
        Assert.Empty(r.Headers); // Content-Type is implied by the body type, Authorization became auth
        Assert.Equal("POST /v1/users", r.Name);
    }

    [Fact]
    public void Data_binary_from_a_file_is_a_binary_body()
    {
        var r = Curl.Parse("curl -X PUT https://h.test/upload -H 'Content-Type: image/png' --data-binary @img/logo.png")!;
        Assert.Equal(BodyTypes.Binary, r.Body.Type);
        Assert.Equal("img/logo.png", r.Body.Data);
        Assert.Equal("image/png", r.Headers.Single().Value);
        // Without '@' it is still plain data.
        Assert.Equal(BodyTypes.Text, Curl.Parse("curl https://h.test --data-binary 'hello there'")!.Body.Type);
    }

    [Fact]
    public void Parses_chrome_cmd_and_ansi_c_strings()
    {
        var cmd = Curl.Parse("curl ^\"https://h.test/a^\" ^\n  -H ^\"accept: */*^\" ^\n  --compressed")!;
        Assert.Equal("https://h.test/a", cmd.Url);
        Assert.Equal("accept", cmd.Headers.Single().Key);

        var ansi = Curl.Parse("curl 'https://h.test' --data-raw $'line1\\nit\\'s'")!;
        Assert.Equal("line1\nit's", ansi.Body.Data);
        Assert.Equal(BodyTypes.Text, ansi.Body.Type);
    }

    [Fact]
    public void Grouped_flags_user_forms_and_get()
    {
        var r = Curl.Parse("curl -sSL -XPUT -u ana:secreto https://h.test/x -F 'name=Ana' -F 'file=@/tmp/a.png;type=image/png'")!;
        Assert.Equal("PUT", r.Method);
        Assert.Equal(AuthTypes.Basic, r.Auth.Type);
        Assert.Equal(("ana", "secreto"), (r.Auth.Username, r.Auth.Password));
        Assert.Equal(BodyTypes.FormData, r.Body.Type);
        Assert.True(r.Body.Fields[1].IsFile);
        Assert.Equal("/tmp/a.png", r.Body.Fields[1].Value);

        var g = Curl.Parse("curl -G https://h.test/s -d q=peach -d n=2")!;
        Assert.Equal("GET", g.Method);
        Assert.Equal("https://h.test/s?q=peach&n=2", g.Url);

        var form = Curl.Parse("curl https://h.test -d 'a=1&b=two%20words'")!;
        Assert.Equal(BodyTypes.UrlEncoded, form.Body.Type);
        Assert.Equal("two words", form.Body.Fields[1].Value);
    }

    [Fact]
    public void Not_a_curl_command()
    {
        Assert.False(Curl.LooksLikeCurl("https://h.test"));
        Assert.Null(Curl.Parse("curl -v"));
    }

    [Fact]
    public void Writes_a_command_that_parses_back_to_the_same_request()
    {
        var req = new ApiRequest
        {
            Method = "PATCH", Url = "https://h.test/u/1", Auth = AuthSpec.BearerOf("t0k'en"),
            Body = new BodySpec { Type = BodyTypes.Json, Data = "{\n  \"a\": \"it's\"\n}" },
        };
        var p = RequestPreparer.Prepare(req, req.Auth, new VariableScope());
        var text = Curl.Write(p);
        Assert.StartsWith("curl -X PATCH https://h.test/u/1 \\\n  -H 'Authorization: Bearer t0k'\\''en'", text);
        var back = Curl.Parse(text)!;
        Assert.Equal("PATCH", back.Method);
        Assert.Equal("t0k'en", back.Auth.Token);
        Assert.Equal(req.Body.Data, back.Body.Data);
    }

    [Fact]
    public void Shell_variables_are_double_quoted_and_escaped()
    {
        var req = new ApiRequest { Url = "{{BASE_URL}}/a?q=$x", Headers = { KeyValue.Of("X-Key", "{{api key}}") } };
        var p = RequestPreparer.Prepare(req, AuthSpec.None(), new VariableScope());
        var text = Curl.Write(p, shellVariables: true);
        Assert.Contains("\"${BASE_URL}/a?q=\\$x\"", text);
        Assert.Contains("\"X-Key: ${api_key}\"", text);
    }
}
