using System.Text;
using RestPeach.Core.Model;

namespace RestPeach.Core.Http;

/// <summary>A request with every variable expanded and auth applied: exactly what goes on the wire.</summary>
public sealed class PreparedRequest
{
    public string Method { get; init; } = "GET";
    public string Url { get; set; } = "";
    public List<KeyValuePair<string, string>> Headers { get; } = new();
    /// <summary><see cref="BodyTypes"/> value.</summary>
    public string BodyType { get; init; } = BodyTypes.None;
    public string RawBody { get; init; } = "";
    /// <summary>The style of API (a GraphQL body has already become JSON here).</summary>
    public ApiKind Kind { get; init; } = ApiKind.Rest;
    /// <summary>Form fields (active only); file fields carry an absolute path.</summary>
    public List<KeyValue> Fields { get; } = new();
    /// <summary>Variables referenced but not defined anywhere.</summary>
    public SortedSet<string> Missing { get; } = new(StringComparer.Ordinal);

    public string? Header(string name) =>
        Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Key: not null } h ? h.Value : null;

    public bool HasHeader(string name) => Headers.Any(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Turns a stored request into a <see cref="PreparedRequest"/>. Pure, so cURL export and tests share it.</summary>
public static class RequestPreparer
{
    /// <summary>
    /// <paramref name="auth"/>: the effective auth (see <see cref="AuthResolver"/>). <paramref name="filesBase"/>: folder
    /// relative form-data file paths start from (the project). <paramref name="secretMask"/>: when set, secret values
    /// are replaced by its result (previews that should not show them).
    /// </summary>
    public static PreparedRequest Prepare(ApiRequest req, AuthSpec auth, VariableScope scope, string filesBase = "",
        Func<string, string>? secretMask = null)
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        string X(string s) => Interpolator.Expand(s, scope, missing, secretMask);

        var method = string.IsNullOrWhiteSpace(req.Method) ? "GET" : req.Method.Trim().ToUpperInvariant();
        var url = NormalizeUrl(X(req.Url.Trim()));
        var bodyType = req.Body.Type;
        var raw = BodyTypes.IsRaw(bodyType) ? X(req.Body.Data) : "";
        if (bodyType == BodyTypes.GraphQL)
        {
            // GraphQL over HTTP: POST {"query", "variables"} as JSON, or the same two as query parameters for GET.
            var query = X(req.Body.Data);
            var variables = X(req.Body.Variables).Trim();
            if (method is "GET" or "HEAD")
            {
                url = QueryString.Append(url, "query", Uri.EscapeDataString(query));
                if (variables.Length > 0) url = QueryString.Append(url, "variables", Uri.EscapeDataString(variables));
                bodyType = BodyTypes.None;
            }
            else
            {
                bodyType = BodyTypes.Json;
                raw = GraphQLEnvelope(query, variables);
            }
        }

        var p = new PreparedRequest
        {
            Method = method,
            Url = url,
            BodyType = bodyType,
            RawBody = raw,
            Kind = ApiKinds.Of(req),
        };
        foreach (var h in req.Headers.Where(h => h.Active && h.Key.Trim().Length > 0))
            p.Headers.Add(new(X(h.Key.Trim()), X(h.Value)));

        switch (auth.Type)
        {
            case AuthTypes.Bearer when !p.HasHeader("Authorization"):
                p.Headers.Add(new("Authorization", "Bearer " + X(auth.Token).Trim()));
                break;
            case AuthTypes.Basic when !p.HasHeader("Authorization"):
                var pair = X(auth.Username) + ":" + X(auth.Password);
                // A masked preview keeps the mask readable instead of base64-encoding it.
                p.Headers.Add(new("Authorization", "Basic " + (secretMask is not null && pair.Contains('•') ? pair : Convert.ToBase64String(Encoding.UTF8.GetBytes(pair)))));
                break;
            case AuthTypes.ApiKey when auth.Key.Trim().Length > 0:
                var key = X(auth.Key.Trim());
                var value = X(auth.Value);
                if (auth.In == "query") p.Url = QueryString.Append(p.Url, Uri.EscapeDataString(key), Uri.EscapeDataString(value));
                else if (!p.HasHeader(key)) p.Headers.Add(new(key, value));
                break;
        }

        if (BodyTypes.IsForm(req.Body.Type))
        {
            foreach (var f in req.Body.Fields.Where(f => f.Active && f.Key.Trim().Length > 0))
            {
                var v = X(f.Value);
                if (f.IsFile && v.Length > 0 && !Path.IsPathRooted(v) && filesBase.Length > 0) v = Path.GetFullPath(Path.Combine(filesBase, v));
                p.Fields.Add(new KeyValue { Key = X(f.Key.Trim()), Value = v, Type = f.Type });
            }
        }
        // multipart sets its own boundary, so only the other bodies get a default Content-Type.
        if (p.BodyType != BodyTypes.FormData && BodyTypes.DefaultContentType(p.BodyType) is { } ct && !p.HasHeader("Content-Type"))
            p.Headers.Add(new("Content-Type", ct));

        foreach (var m in missing) p.Missing.Add(m);
        return p;
    }

    /// <summary>
    /// <c>{"query": …, "variables": …}</c>. Variables that are not valid JSON go in as typed, so the server's error
    /// (not a silent rewrite) tells what is wrong with them.
    /// </summary>
    public static string GraphQLEnvelope(string query, string variables)
    {
        var sb = new StringBuilder("{\n  \"query\": ");
        sb.Append(System.Text.Json.JsonSerializer.Serialize(query, Text.CanonicalJson.StringOptions));
        if (variables.Length > 0)
        {
            var vars = Text.CanonicalJson.Pretty(variables)?.Replace("\n", "\n  ") ?? variables;
            sb.Append(",\n  \"variables\": ").Append(vars);
        }
        return sb.Append("\n}").ToString();
    }

    /// <summary>"localhost:3000/x" → "http://localhost:3000/x": a scheme is needed and nobody types it.</summary>
    public static string NormalizeUrl(string url)
    {
        if (url.Length == 0 || url.Contains("://") || url.StartsWith("{{")) return url;
        return "http://" + url;
    }
}

/// <summary>Auth inheritance: the request's own, else the nearest folder that sets one, else the collection's.</summary>
public static class AuthResolver
{
    /// <param name="folders">Folder settings from the collection root down to the request's folder.</param>
    public static AuthSpec Resolve(AuthSpec own, IReadOnlyList<FolderConfig> folders, WorkspaceConfig config)
    {
        if (!own.IsInherit) return own;
        for (int i = folders.Count - 1; i >= 0; i--)
            if (!folders[i].Auth.IsInherit) return folders[i].Auth;
        return config.Auth.IsInherit ? AuthSpec.None() : config.Auth;
    }
}

/// <summary>Query strings kept exactly as typed (no decoding), so the URL and the Params table never fight.</summary>
public static class QueryString
{
    /// <summary>"https://h/p?a=1&amp;b#frag" → ("https://h/p", "a=1&amp;b", "#frag").</summary>
    public static (string Base, string Query, string Fragment) Split(string url)
    {
        var fragment = "";
        var hash = url.IndexOf('#');
        // "{{...}}" never contains '#', but be careful not to cut inside a placeholder anyway.
        if (hash >= 0 && url.LastIndexOf("}}", StringComparison.Ordinal) < hash) { fragment = url[hash..]; url = url[..hash]; }
        var q = url.IndexOf('?');
        return q < 0 ? (url, "", fragment) : (url[..q], url[(q + 1)..], fragment);
    }

    public static List<KeyValue> Parse(string url)
    {
        var (_, query, _) = Split(url);
        var list = new List<KeyValue>();
        if (query.Length == 0) return list;
        foreach (var part in query.Split('&'))
        {
            if (part.Length == 0) continue;
            var eq = part.IndexOf('=');
            list.Add(eq < 0 ? KeyValue.Of(part, "") : KeyValue.Of(part[..eq], part[(eq + 1)..]));
        }
        return list;
    }

    /// <summary>The URL with its query replaced by the active rows.</summary>
    public static string WithParams(string url, IEnumerable<KeyValue> rows)
    {
        var (b, _, fragment) = Split(url);
        var parts = rows.Where(r => r.Active && r.Key.Length > 0).Select(r => r.Value.Length > 0 ? r.Key + "=" + r.Value : r.Key + "=").ToList();
        return b + (parts.Count > 0 ? "?" + string.Join('&', parts) : "") + fragment;
    }

    public static string Append(string url, string key, string value)
    {
        var (b, q, fragment) = Split(url);
        var pair = key + "=" + value;
        return b + "?" + (q.Length > 0 ? q + "&" + pair : pair) + fragment;
    }
}
