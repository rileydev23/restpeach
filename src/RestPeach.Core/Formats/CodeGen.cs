using System.Text;
using System.Text.Json;
using RestPeach.Core.Http;
using RestPeach.Core.Model;
using RestPeach.Core.Text;

namespace RestPeach.Core.Formats;

/// <summary>
/// A prepared request written as code to paste into a program: cURL, JavaScript (fetch, axios), Python (requests),
/// C# (HttpClient), Go, Java, PowerShell and HTTPie. Only the standard library of each, except axios, requests and
/// HTTPie, which are the tools themselves.
/// </summary>
public static class CodeGen
{
    public static readonly IReadOnlyList<(string Id, string Label)> Languages = new[]
    {
        ("curl", "cURL"), ("fetch", "JavaScript · fetch"), ("axios", "JavaScript · axios"), ("python", "Python · requests"),
        ("csharp", "C# · HttpClient"), ("go", "Go · net/http"), ("java", "Java · HttpClient"), ("powershell", "PowerShell"), ("httpie", "HTTPie"),
    };

    public static string Generate(string language, PreparedRequest p) => language switch
    {
        "fetch" => Fetch(p),
        "axios" => Axios(p),
        "python" => Python(p),
        "csharp" => CSharp(p),
        "go" => Go(p),
        "java" => Java(p),
        "powershell" => PowerShell(p),
        "httpie" => Httpie(p),
        _ => Curl.Write(p),
    };

    // ------------------------------------------------------------ shared

    /// <summary>A double-quoted literal valid in JavaScript, Python, C#, Go and Java.</summary>
    private static string S(string s) => "\"" + JsonEncodedText.Encode(s, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping) + "\"";

    private static bool IsForm(PreparedRequest p) => p.BodyType == BodyTypes.FormData;
    private static bool IsUrlEncoded(PreparedRequest p) => p.BodyType == BodyTypes.UrlEncoded;
    private static bool IsBinary(PreparedRequest p) => p.BodyType == BodyTypes.Binary && p.RawBody.Length > 0;
    private static bool HasRaw(PreparedRequest p) => BodyTypes.IsRaw(p.BodyType) && p.RawBody.Length > 0;

    /// <summary>The headers to write: multipart's Content-Type is left to the library (it carries the boundary).</summary>
    private static List<KeyValuePair<string, string>> Headers(PreparedRequest p)
    {
        var list = p.Headers.Where(h => !(IsForm(p) && h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))).ToList();
        if (p.Deferred?.Type == AuthTypes.OAuth2) list.Add(new("Authorization", "Bearer <oauth2-token>"));
        return list;
    }

    /// <summary>What the code cannot do by itself (Digest's challenge, AWS's signature), said in a comment.</summary>
    private static string? Note(PreparedRequest p) => p.Deferred?.Type switch
    {
        AuthTypes.Digest => $"Digest auth (user {p.Deferred.Username}): answer the server's 401 challenge with your HTTP library's Digest support.",
        AuthTypes.AwsSigV4 => $"AWS Signature V4 ({p.Deferred.Region}/{p.Deferred.Service}): sign the request with the AWS SDK or a SigV4 library.",
        AuthTypes.OAuth2 => "OAuth 2.0: replace <oauth2-token> with an access token from " + (p.Deferred.TokenUrl.Length > 0 ? p.Deferred.TokenUrl : "the token URL") + ".",
        _ => null,
    };

    private static void Comment(StringBuilder sb, PreparedRequest p, string prefix)
    {
        if (Note(p) is { } n) sb.Append(prefix).Append(' ').Append(n).Append('\n');
    }

    private static string FileName(string path) => Path.GetFileName(path.Replace('\\', '/'));

    // ------------------------------------------------------------ JavaScript

    private static string JsHeaders(PreparedRequest p, string indent)
    {
        var h = Headers(p);
        if (h.Count == 0) return "";
        var sb = new StringBuilder(indent + "headers: {\n");
        foreach (var (k, v) in h) sb.Append(indent).Append("  ").Append(S(k)).Append(": ").Append(S(v)).Append(",\n");
        return sb.Append(indent).Append("},\n").ToString();
    }

    /// <summary>The statements that build a FormData / URLSearchParams body, and the expression to send.</summary>
    private static (string Setup, string? Body, bool UsesFs) JsBody(PreparedRequest p)
    {
        if (IsForm(p))
        {
            var sb = new StringBuilder("const form = new FormData();\n");
            foreach (var f in p.Fields)
                sb.Append(f.IsFile
                    ? $"form.append({S(f.Key)}, await fs.openAsBlob({S(f.Value)}), {S(FileName(f.Value))});\n"
                    : $"form.append({S(f.Key)}, {S(f.Value)});\n");
            return (sb.Append('\n').ToString(), "form", p.Fields.Any(f => f.IsFile));
        }
        if (IsUrlEncoded(p))
            return ("", "new URLSearchParams([" + string.Join(", ", p.Fields.Select(f => $"[{S(f.Key)}, {S(f.Value)}]")) + "])", false);
        if (IsBinary(p)) return ("", $"await fs.openAsBlob({S(p.RawBody)})", true);
        return ("", HasRaw(p) ? S(p.RawBody) : null, false);
    }

    private static string Fetch(PreparedRequest p)
    {
        var sb = new StringBuilder();
        Comment(sb, p, "//");
        var (setup, body, fs) = JsBody(p);
        if (fs) sb.Append("import fs from \"node:fs\";\n\n");
        sb.Append(setup);
        sb.Append($"const response = await fetch({S(p.Url)}, {{\n  method: {S(p.Method)},\n");
        sb.Append(JsHeaders(p, "  "));
        if (body is not null) sb.Append("  body: ").Append(body).Append(",\n");
        sb.Append("});\n\nconsole.log(response.status, await response.text());\n");
        return sb.ToString();
    }

    private static string Axios(PreparedRequest p)
    {
        var sb = new StringBuilder();
        Comment(sb, p, "//");
        var (setup, body, fs) = IsBinary(p) ? ("", $"fs.readFileSync({S(p.RawBody)})", true) : JsBody(p);
        sb.Append("import axios from \"axios\";\n");
        if (fs) sb.Append("import fs from \"node:fs\";\n");
        sb.Append('\n').Append(setup);
        sb.Append($"const response = await axios({{\n  method: {S(p.Method.ToLowerInvariant())},\n  url: {S(p.Url)},\n");
        sb.Append(JsHeaders(p, "  "));
        if (body is not null) sb.Append("  data: ").Append(body).Append(",\n");
        sb.Append("  validateStatus: () => true,\n});\n\nconsole.log(response.status, response.data);\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------ Python

    private static string Python(PreparedRequest p)
    {
        var sb = new StringBuilder();
        if (p.Deferred?.Type != AuthTypes.Digest) Comment(sb, p, "#");
        sb.Append("import requests\n");
        if (p.Deferred?.Type == AuthTypes.Digest) sb.Append("from requests.auth import HTTPDigestAuth\n");
        sb.Append($"\nresponse = requests.request(\n    {S(p.Method)},\n    {S(p.Url)},\n");
        var h = Headers(p);
        if (h.Count > 0)
        {
            sb.Append("    headers={\n");
            foreach (var (k, v) in h) sb.Append($"        {S(k)}: {S(v)},\n");
            sb.Append("    },\n");
        }
        if (IsForm(p))
        {
            var text = p.Fields.Where(f => !f.IsFile).ToList();
            var files = p.Fields.Where(f => f.IsFile).ToList();
            if (text.Count > 0) sb.Append("    data=[" + string.Join(", ", text.Select(f => $"({S(f.Key)}, {S(f.Value)})")) + "],\n");
            if (files.Count > 0) sb.Append("    files=[" + string.Join(", ", files.Select(f => $"({S(f.Key)}, ({S(FileName(f.Value))}, open({S(f.Value)}, \"rb\")))")) + "],\n");
        }
        else if (IsUrlEncoded(p)) sb.Append("    data=[" + string.Join(", ", p.Fields.Select(f => $"({S(f.Key)}, {S(f.Value)})")) + "],\n");
        else if (IsBinary(p)) sb.Append($"    data=open({S(p.RawBody)}, \"rb\"),\n");
        else if (HasRaw(p)) sb.Append($"    data={S(p.RawBody)}.encode(\"utf-8\"),\n");
        if (p.Deferred?.Type == AuthTypes.Digest) sb.Append($"    auth=HTTPDigestAuth({S(p.Deferred.Username)}, {S(p.Deferred.Password)}),\n");
        sb.Append(")\n\nprint(response.status_code)\nprint(response.text)\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------ C#

    private static string CSharp(PreparedRequest p)
    {
        var sb = new StringBuilder();
        Comment(sb, p, "//");
        sb.Append("using System.Net.Http.Headers;\nusing System.Text;\n\n");
        sb.Append("using var client = new HttpClient();\n");
        sb.Append($"using var request = new HttpRequestMessage(new HttpMethod({S(p.Method)}), {S(p.Url)});\n");
        string? contentType = null;
        foreach (var (k, v) in Headers(p))
        {
            if (k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) { contentType = v; continue; }
            sb.Append($"request.Headers.TryAddWithoutValidation({S(k)}, {S(v)});\n");
        }
        bool content = true;
        if (IsForm(p))
        {
            sb.Append("var form = new MultipartFormDataContent();\n");
            foreach (var f in p.Fields)
                sb.Append(f.IsFile
                    ? $"form.Add(new ByteArrayContent(File.ReadAllBytes({S(f.Value)})), {S(f.Key)}, {S(FileName(f.Value))});\n"
                    : $"form.Add(new StringContent({S(f.Value)}), {S(f.Key)});\n");
            sb.Append("request.Content = form;\n");
            content = false;
        }
        else if (IsUrlEncoded(p))
            sb.Append("request.Content = new FormUrlEncodedContent(new[]\n{\n" + string.Concat(p.Fields.Select(f => $"    KeyValuePair.Create({S(f.Key)}, {S(f.Value)}),\n")) + "});\n");
        else if (IsBinary(p)) sb.Append($"request.Content = new ByteArrayContent(File.ReadAllBytes({S(p.RawBody)}));\n");
        else if (HasRaw(p)) sb.Append($"request.Content = new StringContent({S(p.RawBody)}, Encoding.UTF8);\n");
        else content = false;
        if (content && contentType is not null) sb.Append($"request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse({S(contentType)});\n");
        sb.Append("\nusing var response = await client.SendAsync(request);\n");
        sb.Append("Console.WriteLine((int)response.StatusCode);\nConsole.WriteLine(await response.Content.ReadAsStringAsync());\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------ Go

    private static string Go(PreparedRequest p)
    {
        var imports = new SortedSet<string>(StringComparer.Ordinal) { "fmt", "io", "net/http" };
        var body = new StringBuilder();
        string reader = "nil";
        string? multipartType = null;
        if (IsForm(p))
        {
            imports.UnionWith(new[] { "bytes", "mime/multipart" });
            body.Append("\tvar buf bytes.Buffer\n\tw := multipart.NewWriter(&buf)\n");
            foreach (var f in p.Fields)
            {
                if (f.IsFile)
                {
                    imports.Add("os");
                    body.Append($"\tif f, err := os.Open({S(f.Value)}); err == nil {{\n\t\tpart, _ := w.CreateFormFile({S(f.Key)}, {S(FileName(f.Value))})\n\t\tio.Copy(part, f)\n\t\tf.Close()\n\t}}\n");
                }
                else body.Append($"\tw.WriteField({S(f.Key)}, {S(f.Value)})\n");
            }
            body.Append("\tw.Close()\n\n");
            reader = "&buf";
            multipartType = "w.FormDataContentType()";
        }
        else if (IsUrlEncoded(p))
        {
            imports.UnionWith(new[] { "net/url", "strings" });
            body.Append("\tform := url.Values{}\n");
            foreach (var f in p.Fields) body.Append($"\tform.Add({S(f.Key)}, {S(f.Value)})\n");
            body.Append('\n');
            reader = "strings.NewReader(form.Encode())";
        }
        else if (IsBinary(p))
        {
            imports.Add("os");
            body.Append($"\tfile, err := os.Open({S(p.RawBody)})\n\tif err != nil {{\n\t\tpanic(err)\n\t}}\n\tdefer file.Close()\n\n");
            reader = "file";
        }
        else if (HasRaw(p))
        {
            imports.Add("strings");
            reader = $"strings.NewReader({S(p.RawBody)})";
        }

        var sb = new StringBuilder();
        Comment(sb, p, "//");
        sb.Append("package main\n\nimport (\n");
        foreach (var i in imports) sb.Append("\t\"").Append(i).Append("\"\n");
        sb.Append(")\n\nfunc main() {\n").Append(body);
        sb.Append($"\treq, err := http.NewRequest({S(p.Method)}, {S(p.Url)}, {reader})\n\tif err != nil {{\n\t\tpanic(err)\n\t}}\n");
        foreach (var (k, v) in Headers(p)) sb.Append($"\treq.Header.Set({S(k)}, {S(v)})\n");
        if (multipartType is not null) sb.Append($"\treq.Header.Set(\"Content-Type\", {multipartType})\n");
        sb.Append("\n\tres, err := http.DefaultClient.Do(req)\n\tif err != nil {\n\t\tpanic(err)\n\t}\n\tdefer res.Body.Close()\n");
        sb.Append("\tdata, _ := io.ReadAll(res.Body)\n\tfmt.Println(res.StatusCode)\n\tfmt.Println(string(data))\n}\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------ Java

    /// <summary>Headers java.net.http refuses to set (it sets them itself).</summary>
    private static readonly HashSet<string> JavaRestricted = new(StringComparer.OrdinalIgnoreCase) { "Host", "Connection", "Content-Length", "Expect", "Upgrade" };

    private static string Java(PreparedRequest p)
    {
        var sb = new StringBuilder();
        Comment(sb, p, "//");
        sb.Append("import java.net.URI;\nimport java.net.http.HttpClient;\nimport java.net.http.HttpRequest;\nimport java.net.http.HttpResponse;\n");
        if (IsBinary(p)) sb.Append("import java.nio.file.Path;\n");
        sb.Append("\nHttpClient client = HttpClient.newHttpClient();\nHttpRequest request = HttpRequest.newBuilder()\n");
        sb.Append($"    .uri(URI.create({S(p.Url)}))\n");
        foreach (var (k, v) in Headers(p).Where(h => !JavaRestricted.Contains(h.Key))) sb.Append($"    .header({S(k)}, {S(v)})\n");
        string publisher;
        if (IsForm(p))
        {
            sb.Insert(0, "// multipart/form-data: java.net.http has no builder for it; use a library (or send the fields as below).\n");
            publisher = "HttpRequest.BodyPublishers.noBody()";
        }
        else if (IsUrlEncoded(p)) publisher = $"HttpRequest.BodyPublishers.ofString({S(HttpEngine.FormEncode(p.Fields))})";
        else if (IsBinary(p)) publisher = $"HttpRequest.BodyPublishers.ofFile(Path.of({S(p.RawBody)}))";
        else if (HasRaw(p)) publisher = $"HttpRequest.BodyPublishers.ofString({S(p.RawBody)})";
        else publisher = "HttpRequest.BodyPublishers.noBody()";
        sb.Append($"    .method({S(p.Method)}, {publisher})\n    .build();\n\n");
        sb.Append("HttpResponse<String> response = client.send(request, HttpResponse.BodyHandlers.ofString());\n");
        sb.Append("System.out.println(response.statusCode());\nSystem.out.println(response.body());\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------ PowerShell

    private static string Ps(string s) => "'" + s.Replace("'", "''") + "'";

    private static string PowerShell(PreparedRequest p)
    {
        var sb = new StringBuilder();
        Comment(sb, p, "#");
        string? contentType = null;
        var headers = Headers(p).Where(h =>
        {
            if (!h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) return true;
            contentType = h.Value;
            return false;
        }).ToList();
        var args = new List<string> { "-Uri " + Ps(p.Url), "-Method " + Ps(p.Method) };
        if (headers.Count > 0)
        {
            sb.Append("$headers = @{\n");
            foreach (var (k, v) in headers) sb.Append($"    {Ps(k)} = {Ps(v)}\n");
            sb.Append("}\n");
            args.Add("-Headers $headers");
        }
        if (IsForm(p))
        {
            sb.Append("$form = @{\n");
            foreach (var f in p.Fields) sb.Append($"    {Ps(f.Key)} = {(f.IsFile ? "Get-Item " + Ps(f.Value) : Ps(f.Value))}\n");
            sb.Append("}\n");
            args.Add("-Form $form");
        }
        else
        {
            if (contentType is not null) args.Add("-ContentType " + Ps(contentType));
            if (IsUrlEncoded(p)) args.Add("-Body " + Ps(HttpEngine.FormEncode(p.Fields)));
            else if (IsBinary(p)) args.Add("-InFile " + Ps(p.RawBody));
            else if (HasRaw(p)) args.Add("-Body " + Ps(p.RawBody));
        }
        args.Add("-SkipHttpErrorCheck");
        if (sb.Length > 0) sb.Append('\n');
        sb.Append("$response = Invoke-WebRequest " + string.Join(" `\n    ", args) + "\n");
        sb.Append("$response.StatusCode\n$response.Content\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------ HTTPie

    private static string Httpie(PreparedRequest p)
    {
        var sb = new StringBuilder();
        Comment(sb, p, "#");
        var parts = new List<string> { "http" };
        if (IsForm(p)) parts.Add("--multipart");
        else if (IsUrlEncoded(p)) parts.Add("--form");
        if (p.Deferred?.Type == AuthTypes.Digest) parts.Add("--auth-type=digest --auth " + Curl.Quote(p.Deferred.Username + ":" + p.Deferred.Password));
        parts.Add(p.Method + " " + Curl.Quote(p.Url));
        parts = new List<string> { string.Join(' ', parts) };
        foreach (var (k, v) in Headers(p))
        {
            if (IsUrlEncoded(p) && k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            parts.Add(Curl.Quote(k + ":" + v));
        }
        if (IsForm(p) || IsUrlEncoded(p))
            foreach (var f in p.Fields) parts.Add(Curl.Quote(f.Key + (f.IsFile ? "@" : "=") + f.Value));
        else if (HasRaw(p)) parts.Add("--raw " + Curl.Quote(p.RawBody));
        var line = string.Join(" \\\n  ", parts);
        if (IsBinary(p)) line += " \\\n  < " + Curl.Quote(p.RawBody);
        return sb.Append(line).Append('\n').ToString();
    }
}
