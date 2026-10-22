using System.Text;
using RestPeach.Core.Http;
using RestPeach.Core.Model;

namespace RestPeach.Core.Formats;

/// <summary>
/// cURL both ways. <see cref="Parse"/> understands what browsers and docs paste (bash, cmd and PowerShell line
/// continuations, <c>$'…'</c> strings, grouped short flags); <see cref="Write"/> produces a bash command.
/// </summary>
public static class Curl
{
    public static bool LooksLikeCurl(string text)
    {
        var t = text.TrimStart();
        return t.StartsWith("curl ", StringComparison.OrdinalIgnoreCase) || t.StartsWith("curl.exe ", StringComparison.OrdinalIgnoreCase)
            || t.Equals("curl", StringComparison.OrdinalIgnoreCase);
    }

    // Options followed by a value that is not used here.
    private static readonly HashSet<string> SkipWithValue = new(StringComparer.Ordinal)
    {
        "-o", "--output", "-m", "--max-time", "--connect-timeout", "-x", "--proxy", "--retry", "-w", "--write-out",
        "--cacert", "--cert", "--key", "-E", "-c", "--cookie-jar", "-r", "--range", "-T", "--upload-file",
        "--resolve", "--limit-rate", "-U", "--proxy-user", "--retry-delay", "--retry-max-time", "-K", "--config",
    };

    // Short flags that take a value (for "-XPOST" and grouped flags like "-sSL").
    private const string ShortWithValue = "XHdFuAbeomxwcrTEUK";

    public static ApiRequest? Parse(string text)
    {
        var args = Tokenize(text);
        if (args.Count == 0 || !args[0].StartsWith("curl", StringComparison.OrdinalIgnoreCase)) return null;

        string? method = null, url = null;
        var headers = new List<KeyValue>();
        var data = new List<string>();
        var form = new List<KeyValue>();
        bool get = false, head = false, json = false;
        string? user = null, binaryFile = null, awsSigV4 = null;
        bool digest = false;

        for (int i = 1; i < args.Count; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Count ? args[++i] : "";
            string? Peek() => i + 1 < args.Count ? args[i + 1] : null;

            // "-XPOST", "-sSL", "-Hfoo: bar"
            if (a.Length > 2 && a[0] == '-' && a[1] != '-')
            {
                var expanded = new List<string>();
                for (int k = 1; k < a.Length; k++)
                {
                    var flag = "-" + a[k];
                    if (ShortWithValue.Contains(a[k]))
                    {
                        expanded.Add(flag);
                        if (k + 1 < a.Length) expanded.Add(a[(k + 1)..]);
                        break;
                    }
                    expanded.Add(flag);
                }
                args.RemoveAt(i);
                args.InsertRange(i, expanded);
                a = args[i];
            }

            // "--data=x" form
            if (a.StartsWith("--") && a.Contains('='))
            {
                var eq = a.IndexOf('=');
                args[i] = a[..eq];
                args.Insert(i + 1, a[(eq + 1)..]);
                a = args[i];
            }

            switch (a)
            {
                case "-X" or "--request": method = Next().ToUpperInvariant(); break;
                case "-H" or "--header":
                    var h = Next();
                    var colon = h.IndexOf(':');
                    if (colon > 0) headers.Add(KeyValue.Of(h[..colon].Trim(), h[(colon + 1)..].Trim()));
                    else if (h.EndsWith(';')) headers.Add(KeyValue.Of(h.TrimEnd(';').Trim(), ""));
                    break;
                case "--data-binary" when Peek() is { } fileArg && fileArg.StartsWith('@') && fileArg.Length > 1:
                    binaryFile = Next()[1..];
                    break;
                case "-d" or "--data" or "--data-ascii" or "--data-raw" or "--data-binary": data.Add(Next()); break;
                case "--data-urlencode":
                    var du = Next();
                    var deq = du.IndexOf('=');
                    data.Add(deq > 0 ? du[..deq] + "=" + Uri.EscapeDataString(du[(deq + 1)..]) : Uri.EscapeDataString(du));
                    break;
                case "--json": json = true; data.Add(Next()); break;
                case "-F" or "--form" or "--form-string":
                    var f = Next();
                    var feq = f.IndexOf('=');
                    if (feq <= 0) break;
                    var fv = f[(feq + 1)..];
                    bool isFile = a != "--form-string" && fv.StartsWith('@');
                    if (isFile) fv = fv[1..].Split(';')[0];
                    form.Add(new KeyValue { Key = f[..feq], Value = fv.Trim('"'), Type = isFile ? "file" : null });
                    break;
                case "-u" or "--user": user = Next(); break;
                case "--digest": digest = true; break;
                case "--aws-sigv4": awsSigV4 = Next(); break;
                case "-A" or "--user-agent": headers.Add(KeyValue.Of("User-Agent", Next())); break;
                case "-e" or "--referer": headers.Add(KeyValue.Of("Referer", Next())); break;
                case "-b" or "--cookie":
                    var cookie = Next();
                    if (cookie.Contains('=')) headers.Add(KeyValue.Of("Cookie", cookie));
                    break;
                case "-G" or "--get": get = true; break;
                case "-I" or "--head": head = true; break;
                case "--url": url = Next(); break;
                default:
                    if (SkipWithValue.Contains(a)) { Next(); break; }
                    if (a.StartsWith('-')) break; // -s, -L, -k, --compressed, -v, -i…
                    url ??= a;
                    break;
            }
        }
        if (url is null) return null;

        var r = new ApiRequest { Url = url.Trim() };
        if (get && data.Count > 0)
        {
            var (b, q, fragment) = QueryString.Split(r.Url);
            r.Url = b + "?" + (q.Length > 0 ? q + "&" : "") + string.Join('&', data) + fragment;
            data.Clear();
        }
        r.Method = method ?? (head ? "HEAD" : data.Count > 0 || form.Count > 0 ? "POST" : "GET");

        // Authorization headers become the request's auth, which is what the Auth tab is for.
        var auth = headers.FirstOrDefault(x => x.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
        if (auth is not null && auth.Value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            r.Auth = AuthSpec.BearerOf(auth.Value[7..].Trim());
            headers.Remove(auth);
        }
        else if (auth is not null && auth.Value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) && DecodeBasic(auth.Value[6..].Trim()) is { } basic)
        {
            r.Auth = new AuthSpec { Type = AuthTypes.Basic, Username = basic.User, Password = basic.Pass };
            headers.Remove(auth);
        }
        if (user is not null)
        {
            var c = user.IndexOf(':');
            string name = c < 0 ? user : user[..c], secret = c < 0 ? "" : user[(c + 1)..];
            if (awsSigV4 is not null)
            {
                // "aws:amz:us-east-1:execute-api"
                var provider = awsSigV4.Split(':');
                r.Auth = new AuthSpec
                {
                    Type = AuthTypes.AwsSigV4, AccessKey = name, SecretKey = secret,
                    Region = provider.Length > 2 ? provider[2] : "", Service = provider.Length > 3 ? provider[3] : "",
                };
                var token = headers.FirstOrDefault(x => x.Key.Equals("X-Amz-Security-Token", StringComparison.OrdinalIgnoreCase));
                if (token is not null) { r.Auth.SessionToken = token.Value; headers.Remove(token); }
            }
            else r.Auth = new AuthSpec { Type = digest ? AuthTypes.Digest : AuthTypes.Basic, Username = name, Password = secret };
        }
        if (r.Auth.IsInherit) r.Auth = AuthSpec.None();

        var contentType = headers.FirstOrDefault(x => x.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value.ToLowerInvariant() ?? "";
        if (form.Count > 0)
        {
            r.Body = new BodySpec { Type = BodyTypes.FormData, Fields = form };
        }
        else if (binaryFile is not null)
        {
            r.Body = new BodySpec { Type = BodyTypes.Binary, Data = binaryFile };
        }
        else if (data.Count > 0)
        {
            var body = string.Join('&', data);
            var trimmed = body.TrimStart();
            if ((json || contentType.Contains("json") || contentType.Length == 0) && GraphQLBody(body) is { } gql)
                r.Body = gql;
            else if (json || contentType.Contains("json") || (contentType.Length == 0 && (trimmed.StartsWith('{') || trimmed.StartsWith('['))))
                r.Body = new BodySpec { Type = BodyTypes.Json, Data = Text.CanonicalJson.Pretty(body) ?? body };
            else if (contentType.Contains("xml")) r.Body = new BodySpec { Type = BodyTypes.Xml, Data = body };
            else if (contentType.Contains("html")) r.Body = new BodySpec { Type = BodyTypes.Html, Data = body };
            else if ((contentType.Length == 0 || contentType.Contains("x-www-form-urlencoded")) && IsFormEncoded(body))
                r.Body = new BodySpec { Type = BodyTypes.UrlEncoded, Fields = ParseForm(body) };
            else r.Body = new BodySpec { Type = BodyTypes.Text, Data = body };
            if (json && !headers.Any(x => x.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase)))
                headers.Add(KeyValue.Of("Accept", "application/json"));
        }
        // The body type implies these; keeping them would only duplicate what the Body tab says.
        headers.RemoveAll(x => x.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) &&
                               (r.Body.Type == BodyTypes.FormData || BodyTypes.DefaultContentType(r.Body.Type) is { } d && x.Value.Split(';')[0].Trim().Equals(d, StringComparison.OrdinalIgnoreCase)));
        r.Headers = headers;
        r.Name = SuggestName(r.Method, r.Url);
        return r;
    }

    /// <summary>A JSON body that is one GraphQL operation ({"query": "…", "variables": {…}}), as browsers copy it.</summary>
    private static BodySpec? GraphQLBody(string body)
    {
        try
        {
            if (Text.CanonicalJson.Parse(body) is not System.Text.Json.Nodes.JsonObject o || o["query"] is not System.Text.Json.Nodes.JsonValue q
                || !q.TryGetValue<string>(out var query) || o.Any(p => p.Key is not ("query" or "variables" or "operationName")))
                return null;
            var vars = o["variables"] is System.Text.Json.Nodes.JsonObject { Count: > 0 } v ? Text.CanonicalJson.Pretty(v.ToJsonString()) ?? "" : "";
            return new BodySpec { Type = BodyTypes.GraphQL, Data = query, Variables = vars };
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static (string User, string Pass)? DecodeBasic(string b64)
    {
        try
        {
            var s = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            var c = s.IndexOf(':');
            return c < 0 ? null : (s[..c], s[(c + 1)..]);
        }
        catch (FormatException) { return null; }
    }

    private static bool IsFormEncoded(string body) =>
        body.Length > 0 && body.Split('&').All(p => p.Contains('=') && !p.Contains(' ') && !p.Contains('\n'));

    private static List<KeyValue> ParseForm(string body) =>
        body.Split('&').Where(p => p.Length > 0).Select(p =>
        {
            var eq = p.IndexOf('=');
            static string D(string s) { try { return Uri.UnescapeDataString(s.Replace('+', ' ')); } catch { return s; } }
            return eq < 0 ? KeyValue.Of(D(p), "") : KeyValue.Of(D(p[..eq]), D(p[(eq + 1)..]));
        }).ToList();

    /// <summary>"GET /v1/users/{{id}}" from the URL, without host or query.</summary>
    public static string SuggestName(string method, string url)
    {
        var (b, _, _) = QueryString.Split(url);
        var path = b;
        var scheme = path.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) path = path[(scheme + 3)..];
        var slash = path.IndexOf('/');
        path = slash < 0 ? "/" : path[slash..];
        if (path.StartsWith("{{")) path = "/";
        if (b.StartsWith("{{") && b.Contains("}}")) path = b[(b.IndexOf("}}", StringComparison.Ordinal) + 2)..];
        if (path.Length == 0) path = "/";
        var name = method + " " + path.TrimEnd('/');
        return name.Trim().Length <= method.Length ? method + " /" : name;
    }

    // ------------------------------------------------------------ tokenizer

    public static List<string> Tokenize(string text)
    {
        // Line continuations: bash "\", cmd "^" and PowerShell "`".
        var s = text.Replace("\r\n", "\n");
        bool cmd = s.Contains("^\"") || s.Contains(" ^\n");
        s = s.Replace("\\\n", " ").Replace(" ^\n", " ").Replace(" `\n", " ");
        if (cmd) s = UnescapeCmd(s);

        var args = new List<string>();
        var cur = new StringBuilder();
        bool inArg = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c))
            {
                if (inArg) { args.Add(cur.ToString()); cur.Clear(); inArg = false; }
                continue;
            }
            inArg = true;
            if (c == '\'')
            {
                int end = s.IndexOf('\'', i + 1);
                if (end < 0) end = s.Length;
                cur.Append(s, i + 1, Math.Max(0, end - i - 1));
                i = end;
            }
            else if (c == '$' && i + 1 < s.Length && s[i + 1] == '\'')
            {
                i = ReadAnsiC(s, i + 2, cur);
            }
            else if (c == '"')
            {
                i++;
                for (; i < s.Length && s[i] != '"'; i++)
                {
                    if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] is '"' or '\\' or '$' or '`') { cur.Append(s[++i]); }
                    else cur.Append(s[i]);
                }
            }
            else if (c == '\\' && i + 1 < s.Length) cur.Append(s[++i]);
            else cur.Append(c);
        }
        if (inArg) args.Add(cur.ToString());
        return args;
    }

    /// <summary>cmd.exe escapes: <c>^"</c> → <c>"</c>, <c>^^</c> → <c>^</c>, <c>^%</c> → <c>%</c>…</summary>
    private static string UnescapeCmd(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '^' && i + 1 < s.Length) { sb.Append(s[++i]); continue; }
            sb.Append(s[i]);
        }
        // Chrome writes literal quotes inside a cmd string as \" (after removing ^).
        return sb.ToString();
    }

    /// <summary>Bash <c>$'…'</c> strings: \n, \t, \', \\, \xHH, \uHHHH.</summary>
    private static int ReadAnsiC(string s, int i, StringBuilder cur)
    {
        for (; i < s.Length && s[i] != '\''; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length) { cur.Append(s[i]); continue; }
            char e = s[++i];
            switch (e)
            {
                case 'n': cur.Append('\n'); break;
                case 't': cur.Append('\t'); break;
                case 'r': cur.Append('\r'); break;
                case '0': cur.Append('\0'); break;
                case 'x' when i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]):
                    cur.Append((char)Convert.ToInt32(s.Substring(i + 1, 2), 16)); i += 2; break;
                case 'u' when i + 4 < s.Length:
                    try { cur.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; }
                    catch (FormatException) { cur.Append('u'); }
                    break;
                default: cur.Append(e); break;
            }
        }
        return i;
    }

    // ------------------------------------------------------------ writer

    /// <summary>
    /// A bash command that sends <paramref name="p"/>, one option per line. With <paramref name="shellVariables"/>,
    /// <c>{{NAME}}</c> becomes <c>${NAME}</c> (double-quoted so the shell expands it) instead of literal text.
    /// </summary>
    public static string Write(PreparedRequest p, bool shellVariables = false, string? basicUser = null)
    {
        Func<string, string> q = shellVariables ? ShellQuote : Quote;
        var parts = new List<string>();
        var first = "curl";
        if (p.Method == "HEAD") first += " -I";
        else if (p.Method != "GET" && !(p.Method == "POST" && HasBody(p))) first += " -X " + p.Method;
        parts.Add(first + " " + q(p.Url));
        if (basicUser is not null) parts.Add("-u " + q(basicUser));
        switch (p.Deferred?.Type)
        {
            case AuthTypes.Digest:
                parts.Add("--digest -u " + q(p.Deferred.Username + ":" + p.Deferred.Password));
                break;
            case AuthTypes.AwsSigV4:
                parts.Add("--aws-sigv4 " + q($"aws:amz:{p.Deferred.Region}:{p.Deferred.Service}") + " -u " + q(p.Deferred.AccessKey + ":" + p.Deferred.SecretKey));
                if (p.Deferred.SessionToken.Length > 0) parts.Add("-H " + q("X-Amz-Security-Token: " + p.Deferred.SessionToken));
                break;
            case AuthTypes.OAuth2:
                // The token is asked for when sending; cURL cannot do that by itself.
                parts.Add("-H " + q("Authorization: Bearer <oauth2-token>"));
                break;
        }
        foreach (var (k, v) in p.Headers)
        {
            if (p.BodyType == BodyTypes.FormData && k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            parts.Add("-H " + q(k + ": " + v));
        }
        switch (p.BodyType)
        {
            case BodyTypes.FormData:
                foreach (var f in p.Fields) parts.Add("-F " + q(f.Key + "=" + (f.IsFile ? "@" + f.Value : f.Value)));
                break;
            case BodyTypes.UrlEncoded:
                if (p.Fields.Count > 0) parts.Add("--data-raw " + q(HttpEngine.FormEncode(p.Fields)));
                break;
            case BodyTypes.Binary:
                if (p.RawBody.Length > 0) parts.Add("--data-binary " + q("@" + p.RawBody));
                break;
            default:
                if (BodyTypes.IsRaw(p.BodyType) && p.RawBody.Length > 0) parts.Add("--data-raw " + q(p.RawBody));
                break;
        }
        return string.Join(" \\\n  ", parts);
    }

    private static string ShellQuote(string s) => s.Contains("{{") ? "\"" + DoubleQuoteBody(s) + "\"" : Quote(s);

    /// <summary>Text for inside bash double quotes: <c>\ " $ `</c> escaped, <c>{{NAME}}</c> turned into <c>${NAME}</c>.</summary>
    public static string DoubleQuoteBody(string s)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] == '{' && i + 1 < s.Length && s[i + 1] == '{')
            {
                int close = s.IndexOf("}}", i + 2, StringComparison.Ordinal);
                var name = close < 0 ? "" : s[(i + 2)..close].Trim();
                if (name.Length > 0 && !name.StartsWith('$') && !name.Contains('{'))
                {
                    sb.Append("${").Append(Interpolator.ShellName(name)).Append('}');
                    i = close + 2;
                    continue;
                }
            }
            if (s[i] is '\\' or '"' or '$' or '`') sb.Append('\\');
            sb.Append(s[i]);
            i++;
        }
        return sb.ToString();
    }

    private static bool HasBody(PreparedRequest p) =>
        (BodyTypes.IsRaw(p.BodyType) || p.BodyType == BodyTypes.Binary) && p.RawBody.Length > 0 || (BodyTypes.IsForm(p.BodyType) && p.Fields.Count > 0);

    /// <summary>Single quotes for bash: nothing inside is special except the quote itself.</summary>
    public static string Quote(string s) =>
        s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || "-_./:=@%+,".Contains(c)) ? s : "'" + s.Replace("'", "'\\''") + "'";
}
