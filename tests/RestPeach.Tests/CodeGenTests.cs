using RestPeach.Core.Formats;
using RestPeach.Core.Http;
using RestPeach.Core.Model;

namespace RestPeach.Tests;

/// <summary>Code generation. fetch, requests and HttpClient were also run for real against an echo server.</summary>
public class CodeGenTests
{
    private static PreparedRequest P(BodySpec body, AuthSpec? auth = null, string method = "POST") =>
        RequestPreparer.Prepare(new ApiRequest { Method = method, Url = "https://api.test/items?x=1", Headers = { KeyValue.Of("X-Note", "it's \"ok\"") }, Body = body },
            auth ?? AuthSpec.None(), new VariableScope());

    private static readonly BodySpec Json = new() { Type = BodyTypes.Json, Data = "{\"a\": \"ñ\"}" };

    [Theory]
    [InlineData("fetch", "await fetch(\"https://api.test/items?x=1\"", "\"X-Note\": \"it's \\\"ok\\\"\"")]
    [InlineData("axios", "url: \"https://api.test/items?x=1\"", "data: \"{\\\"a\\\": \\\"ñ\\\"}\"")]
    [InlineData("python", "requests.request(\n    \"POST\"", "data=\"{\\\"a\\\": \\\"ñ\\\"}\".encode(\"utf-8\")")]
    [InlineData("csharp", "new HttpMethod(\"POST\")", "MediaTypeHeaderValue.Parse(\"application/json\")")]
    [InlineData("go", "http.NewRequest(\"POST\", \"https://api.test/items?x=1\", strings.NewReader(", "req.Header.Set(\"X-Note\", \"it's \\\"ok\\\"\")")]
    [InlineData("java", ".method(\"POST\", HttpRequest.BodyPublishers.ofString(", ".header(\"Content-Type\", \"application/json\")")]
    [InlineData("powershell", "-Uri 'https://api.test/items?x=1'", "'X-Note' = 'it''s \"ok\"'")]
    [InlineData("httpie", "http POST 'https://api.test/items?x=1'", "--raw '{\"a\": \"ñ\"}'")]
    [InlineData("curl", "curl 'https://api.test/items?x=1'", "--data-raw")]
    public void Each_language_writes_method_url_headers_and_body(string lang, string a, string b)
    {
        var code = CodeGen.Generate(lang, P(Json));
        Assert.Contains(a, code);
        Assert.Contains(b, code);
    }

    [Fact]
    public void Forms_files_and_binary_bodies()
    {
        var form = new BodySpec { Type = BodyTypes.FormData, Fields = { KeyValue.Of("t", "hi"), new KeyValue { Key = "doc", Value = "C:/f/a.pdf", Type = "file" } } };
        Assert.Contains("fs.openAsBlob(\"C:/f/a.pdf\"), \"a.pdf\"", CodeGen.Generate("fetch", P(form)));
        Assert.Contains("files=[(\"doc\", (\"a.pdf\", open(\"C:/f/a.pdf\", \"rb\")))]", CodeGen.Generate("python", P(form)));
        Assert.Contains("w.CreateFormFile(\"doc\", \"a.pdf\")", CodeGen.Generate("go", P(form)));
        Assert.Contains("'doc' = Get-Item 'C:/f/a.pdf'", CodeGen.Generate("powershell", P(form)));
        Assert.Contains("doc@C:/f/a.pdf", CodeGen.Generate("httpie", P(form)));
        // multipart's Content-Type carries the boundary: the library sets it.
        Assert.DoesNotContain("multipart/form-data", CodeGen.Generate("csharp", P(form)));

        var url = new BodySpec { Type = BodyTypes.UrlEncoded, Fields = { KeyValue.Of("a", "1 2") } };
        Assert.Contains("new URLSearchParams([[\"a\", \"1 2\"]])", CodeGen.Generate("fetch", P(url)));
        Assert.Contains("form.Add(\"a\", \"1 2\")", CodeGen.Generate("go", P(url)));

        var bin = new BodySpec { Type = BodyTypes.Binary, Data = "C:/f/a.png" };
        Assert.Contains("ofFile(Path.of(\"C:/f/a.png\"))", CodeGen.Generate("java", P(bin, method: "PUT")));
        Assert.Contains("-InFile 'C:/f/a.png'", CodeGen.Generate("powershell", P(bin, method: "PUT")));
        Assert.Contains("< C:/f/a.png", CodeGen.Generate("httpie", P(bin, method: "PUT")));
    }

    [Fact]
    public void Auth_the_code_cannot_do_is_explained()
    {
        var digest = new AuthSpec { Type = AuthTypes.Digest, Username = "ana", Password = "pw" };
        Assert.Contains("auth=HTTPDigestAuth(\"ana\", \"pw\")", CodeGen.Generate("python", P(Json, digest)));
        Assert.StartsWith("// Digest auth (user ana)", CodeGen.Generate("fetch", P(Json, digest)));
        var oauth = new AuthSpec { Type = AuthTypes.OAuth2, TokenUrl = "https://login.test/token", ClientId = "c" };
        var code = CodeGen.Generate("go", P(Json, oauth));
        Assert.Contains("https://login.test/token", code);
        Assert.Contains("\"Authorization\", \"Bearer <oauth2-token>\"", code);
    }
}
