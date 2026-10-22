using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using RestPeach.Core.Http;
using RestPeach.Core.Text;

namespace RestPeach.Core.Testing;

/// <summary>
/// Outcome of one line of a request's tests. <see cref="Error"/> means the line itself could not be understood or
/// evaluated (a bad path, a regex that does not compile); <see cref="Actual"/> is what the response had.
/// </summary>
public sealed record AssertionResult(string Line, bool Passed, string? Actual = null, string? Error = null,
    string? CapturedName = null)
{
    public bool IsCapture => CapturedName is not null;
}

/// <summary>
/// Declarative checks, one per line, readable in a diff and with no script runtime:
/// <code>
/// status == 200            status in 200, 201         status == 2xx
/// time &lt; 500ms            size &lt; 10000
/// header Content-Type contains json                   header X-Request-Id exists
/// body contains "ok"       body matches ^\{
/// $.data.id exists         $.items.length &gt; 0         $.user.name == "Ana"
/// set TOKEN = $.data.token                            set SESSION = header X-Session
/// </code>
/// The phrasings of the roadmap also work: <c>Status == 200</c>, <c>Response Time &lt; 500ms</c>,
/// <c>JSON Path $.data.id exists</c>. A line starting with <c>#</c> or <c>//</c> is a comment.
/// </summary>
public static class Assertions
{
    private enum Subject { Status, Time, Size, Header, Body, Json }

    private sealed record Parsed(Subject Subject, string? Arg, string Op, string? Value);

    private static readonly string[] Ops =
    {
        "not contains", "!contains", "not exists", "!exists", "not matches", "!matches",
        "==", "!=", "<=", ">=", "<", ">", "=",
        "contains", "exists", "matches", "startsWith", "endsWith", "in", "is",
    };

    public static bool IsComment(string line)
    {
        var t = line.Trim();
        return t.Length == 0 || t.StartsWith('#') || t.StartsWith("//");
    }

    /// <summary>
    /// Runs every line against <paramref name="r"/>. Captured values are written to <paramref name="captures"/>
    /// (so the next request of a run, or of the session, can use them as <c>{{NAME}}</c>).
    /// </summary>
    public static List<AssertionResult> Evaluate(IEnumerable<string> lines, ApiResponse r, IDictionary<string, string>? captures = null)
    {
        var results = new List<AssertionResult>();
        JsonDocument? json = null;
        bool jsonTried = false;
        JsonElement? Json()
        {
            if (!jsonTried)
            {
                jsonTried = true;
                // An event stream is tested as the array of its events: $[0].data.id, $.length.
                var text = Http.SseParser.IsEventStream(r.ContentType) ? Http.SseParser.ToJson(Http.SseParser.Parse(r.Text)) : r.Text;
                try { json = JsonDocument.Parse(text, CanonicalJson.ReadOptions); } catch (JsonException) { }
            }
            return json?.RootElement;
        }

        try
        {
            foreach (var raw in lines)
            {
                if (IsComment(raw)) continue;
                var line = raw.Trim();
                try { results.Add(EvaluateLine(line, r, Json, captures)); }
                catch (Exception e) when (e is FormatException or ArgumentException or RegexMatchTimeoutException)
                {
                    results.Add(new AssertionResult(line, false, Error: e.Message));
                }
            }
        }
        finally { json?.Dispose(); }
        return results;
    }

    private static AssertionResult EvaluateLine(string line, ApiResponse r, Func<JsonElement?> json, IDictionary<string, string>? captures)
    {
        if (TryCapture(line, out var name, out var source))
        {
            var (found, value) = Read(source, r, json);
            if (!found) return new AssertionResult(line, false, Error: "not found: " + source, CapturedName: name);
            if (captures is not null) captures[name] = value;
            return new AssertionResult(line, true, value, CapturedName: name);
        }

        var p = Parse(line);
        if (r.Error is not null) return new AssertionResult(line, false, Error: r.Error);
        return p.Subject switch
        {
            Subject.Status => Number(line, p, r.Status),
            Subject.Time => Number(line, p, Math.Round(r.ElapsedMs)),
            Subject.Size => Number(line, p, r.Size),
            Subject.Header => Text(line, p, r.Header(p.Arg!), ignoreCase: true),
            Subject.Body => Text(line, p, r.Text.Length > 0 ? r.Text : null, ignoreCase: false),
            _ => JsonCheck(line, p, json()),
        };
    }

    // ------------------------------------------------------------ parsing

    /// <summary>"set NAME = source" / "capture NAME = source".</summary>
    private static bool TryCapture(string line, out string name, out string source)
    {
        name = source = "";
        var m = Regex.Match(line, @"^(?:set|capture|let)\s+([A-Za-z_][\w.\-]*)\s*=\s*(.+)$", RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        name = m.Groups[1].Value;
        source = m.Groups[2].Value.Trim();
        return true;
    }

    private static (bool Found, string Value) Read(string source, ApiResponse r, Func<JsonElement?> json)
    {
        var p = ParseSubject(source, out var rest);
        if (rest.Trim().Length > 0) throw new FormatException("unexpected: " + rest.Trim());
        switch (p.Subject)
        {
            case Subject.Status: return (r.Error is null, r.Status.ToString(CultureInfo.InvariantCulture));
            case Subject.Time: return (true, Math.Round(r.ElapsedMs).ToString(CultureInfo.InvariantCulture));
            case Subject.Size: return (true, r.Size.ToString(CultureInfo.InvariantCulture));
            case Subject.Header: return r.Header(p.Arg!) is { } h ? (true, h) : (false, "");
            case Subject.Body: return (true, r.Text);
        }
        if (json() is not { } root) return (false, "");
        if (!JsonPath.TrySelect(root, p.Arg!, out var v, out var len, out var err))
        {
            if (err is not null) throw new FormatException(err);
            return (false, "");
        }
        return (true, len?.ToString(CultureInfo.InvariantCulture) ?? JsonPath.Text(v));
    }

    private static Parsed Parse(string line)
    {
        var p = ParseSubject(line, out var rest);
        rest = rest.Trim();
        foreach (var op in Ops)
        {
            if (!rest.StartsWith(op, StringComparison.OrdinalIgnoreCase)) continue;
            // Word operators must end at a word boundary ("is" vs "isActive").
            if (char.IsLetter(op[^1]) && rest.Length > op.Length && char.IsLetterOrDigit(rest[op.Length])) continue;
            var value = rest[op.Length..].Trim();
            var normalized = op.ToLowerInvariant() switch
            {
                "=" or "is" => "==",
                "!contains" => "not contains",
                "!exists" => "not exists",
                "!matches" => "not matches",
                "startswith" => "startsWith",
                "endswith" => "endsWith",
                var o => o,
            };
            if (normalized is "exists" or "not exists")
            {
                if (value.Length > 0) throw new FormatException("exists takes no value");
                return p with { Op = normalized };
            }
            if (value.Length == 0) throw new FormatException("missing value after " + op);
            return p with { Op = normalized, Value = Unquote(value) };
        }
        throw new FormatException(rest.Length == 0 ? "missing operator" : "unknown operator: " + rest);
    }

    private static Parsed ParseSubject(string line, out string rest)
    {
        var s = line.Trim();
        string? Take(string prefix)
        {
            if (!s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            if (s.Length > prefix.Length && char.IsLetterOrDigit(s[prefix.Length]) && char.IsLetter(prefix[^1])) return null;
            return s[prefix.Length..];
        }

        if ((Take("status code") ?? Take("status")) is { } a) { rest = a; return new Parsed(Subject.Status, null, "", null); }
        if ((Take("response time") ?? Take("time")) is { } b) { rest = b; return new Parsed(Subject.Time, null, "", null); }
        if ((Take("response size") ?? Take("size")) is { } c) { rest = c; return new Parsed(Subject.Size, null, "", null); }
        if (Take("header") is { } h)
        {
            h = h.TrimStart();
            string name;
            int consumed;
            if (h.Length > 0 && h[0] is '"' or '\'')
            {
                var close = h.IndexOf(h[0], 1);
                if (close < 0) throw new FormatException("unclosed quote in header name");
                name = h[1..close];
                consumed = close + 1;
            }
            else
            {
                consumed = h.TakeWhile(ch => !char.IsWhiteSpace(ch)).Count();
                name = h[..consumed];
            }
            if (name.Length == 0) throw new FormatException("header needs a name");
            rest = h[consumed..];
            return new Parsed(Subject.Header, name, "", null);
        }
        if (Take("body") is { } d) { rest = d; return new Parsed(Subject.Body, null, "", null); }
        var j = Take("json path") ?? Take("jsonpath") ?? Take("json") ?? s;
        j = j.TrimStart();
        if (!j.StartsWith('$')) throw new FormatException("unknown subject: " + line);
        int end = 0;
        int depth = 0;
        for (; end < j.Length; end++)
        {
            var ch = j[end];
            if (ch == '[') depth++;
            else if (ch == ']') depth--;
            else if (char.IsWhiteSpace(ch) && depth == 0) break;
            else if (depth == 0 && end > 0 && ch is '=' or '!' or '<' or '>') break;
        }
        rest = j[end..];
        return new Parsed(Subject.Json, j[..end], "", null);
    }

    private static string Unquote(string v)
    {
        v = v.Trim();
        if (v.Length >= 2 && (v[0] is '"' or '\'') && v[^1] == v[0]) return v[1..^1].Replace("\\" + v[0], v[0].ToString());
        return v;
    }

    // ------------------------------------------------------------ evaluation

    private static AssertionResult Number(string line, Parsed p, double actual)
    {
        var a = actual.ToString(CultureInfo.InvariantCulture);
        if (p.Op is "exists") return new AssertionResult(line, true, a);
        if (p.Op is "not exists") return new AssertionResult(line, false, a);
        if (p.Op is "in")
        {
            var options = p.Value!.Trim('[', ']', '(', ')').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new AssertionResult(line, options.Any(o => Matches(actual, o)), a);
        }
        if (p.Op is "==" or "!=" && IsClass(p.Value!))
        {
            bool eq = Matches(actual, p.Value!);
            return new AssertionResult(line, p.Op == "==" ? eq : !eq, a);
        }
        if (p.Op is "contains" or "not contains" or "matches" or "not matches" or "startsWith" or "endsWith")
            return Text(line, p, a, ignoreCase: false);
        var expected = ParseNumber(p.Value!) ?? throw new FormatException("not a number: " + p.Value);
        return new AssertionResult(line, Compare(actual, p.Op, expected), a);
    }

    /// <summary>"2xx", "4XX".</summary>
    private static bool IsClass(string v) => v.Length == 3 && char.IsDigit(v[0]) && v[1..].Equals("xx", StringComparison.OrdinalIgnoreCase);

    private static bool Matches(double actual, string option) =>
        IsClass(option) ? (int)actual / 100 == option[0] - '0' : ParseNumber(option) is { } n && Math.Abs(actual - n) < 1e-9;

    private static double? ParseNumber(string v)
    {
        v = v.Trim();
        double factor = 1;
        if (v.EndsWith("ms", StringComparison.OrdinalIgnoreCase)) v = v[..^2];
        else if (v.EndsWith("kb", StringComparison.OrdinalIgnoreCase)) { v = v[..^2]; factor = 1024; }
        else if (v.EndsWith("mb", StringComparison.OrdinalIgnoreCase)) { v = v[..^2]; factor = 1024 * 1024; }
        else if (v.EndsWith('s') && !v.EndsWith("ss")) { v = v[..^1]; factor = 1000; }
        return double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d * factor : null;
    }

    private static bool Compare(double a, string op, double b) => op switch
    {
        "==" => Math.Abs(a - b) < 1e-9,
        "!=" => Math.Abs(a - b) >= 1e-9,
        "<" => a < b,
        "<=" => a <= b,
        ">" => a > b,
        ">=" => a >= b,
        _ => throw new FormatException("operator not valid here: " + op),
    };

    private static AssertionResult Text(string line, Parsed p, string? actual, bool ignoreCase)
    {
        var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        switch (p.Op)
        {
            case "exists": return new AssertionResult(line, actual is not null, actual);
            case "not exists": return new AssertionResult(line, actual is null, actual);
        }
        if (actual is null) return new AssertionResult(line, false, null);
        var v = p.Value!;
        bool ok = p.Op switch
        {
            "==" => string.Equals(actual, v, cmp),
            "!=" => !string.Equals(actual, v, cmp),
            "contains" => actual.Contains(v, cmp),
            "not contains" => !actual.Contains(v, cmp),
            "startsWith" => actual.StartsWith(v, cmp),
            "endsWith" => actual.EndsWith(v, cmp),
            "matches" => Rx(v, ignoreCase).IsMatch(actual),
            "not matches" => !Rx(v, ignoreCase).IsMatch(actual),
            "in" => v.Trim('[', ']', '(', ')').Split(',', StringSplitOptions.TrimEntries).Select(Unquote).Any(o => string.Equals(o, actual, cmp)),
            _ => ParseNumber(actual) is { } n && ParseNumber(v) is { } m ? Compare(n, p.Op, m) : throw new FormatException("not a number: " + Short(actual)),
        };
        return new AssertionResult(line, ok, Short(actual));
    }

    private static Regex Rx(string pattern, bool ignoreCase) =>
        new(pattern, (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(500));

    private static AssertionResult JsonCheck(string line, Parsed p, JsonElement? root)
    {
        if (root is null) return new AssertionResult(line, p.Op == "not exists", Error: p.Op == "not exists" ? null : "body is not JSON");
        if (!JsonPath.TrySelect(root.Value, p.Arg!, out var value, out var length, out var error))
        {
            if (error is not null) throw new FormatException(error);
            return new AssertionResult(line, p.Op == "not exists", null);
        }
        if (length is int n) return Number(line, p, n);
        switch (p.Op)
        {
            case "exists": return new AssertionResult(line, true, JsonPath.Text(value));
            case "not exists": return new AssertionResult(line, false, JsonPath.Text(value));
        }
        var actual = JsonPath.Text(value);
        if (value.ValueKind == JsonValueKind.Number && p.Op is "==" or "!=" or "<" or "<=" or ">" or ">=" && ParseNumber(p.Value!) is { } expected)
            return new AssertionResult(line, Compare(value.GetDouble(), p.Op, expected), actual);
        return Text(line, p, actual, ignoreCase: false);
    }

    private static string Short(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
