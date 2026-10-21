using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RestPeach.Core.Http;
using RestPeach.Core.Model;
using RestPeach.Core.Storage;
using RestPeach.Core.Text;

namespace RestPeach.Core.Formats;

/// <summary>
/// Exports a folder of the collection (or all of it) for people and tools outside RestPeach: a Postman collection,
/// an OpenAPI document, or a bash script of cURL calls that reads its variables from the environment.
/// Secrets are never written: variables stay as <c>{{NAME}}</c> (or <c>${NAME}</c> in the script).
/// </summary>
public static partial class Exporter
{
    private sealed record Entry(string RelPath, ApiRequest Request, AuthSpec Auth);

    private static List<Entry> Collect(Workspace ws, string relDir)
    {
        var config = ws.LoadConfig();
        var list = new List<Entry>();
        foreach (var rel in ws.ListRequests(relDir))
        {
            ApiRequest r;
            try { r = ws.LoadRequest(rel); } catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or YamlDotNet.Core.YamlException) { continue; }
            var auth = AuthResolver.Resolve(r.Auth, ws.FolderChain(Workspace.ParentOf(rel)), config);
            list.Add(new Entry(rel, r, auth));
        }
        return list;
    }

    private static string FolderName(Workspace ws, string relDir) =>
        relDir.Length > 0 ? relDir[(relDir.LastIndexOf('/') + 1)..] : ws.LoadConfig().Name is { Length: > 0 } n ? n : Path.GetFileName(ws.ProjectRoot);

    /// <summary>Collection and folder variables visible from <paramref name="relDir"/> (not environments, not secrets).</summary>
    private static SortedDictionary<string, string> SharedVariables(Workspace ws, string relDir)
    {
        var vars = new SortedDictionary<string, string>(ws.LoadConfig().Variables, StringComparer.Ordinal);
        foreach (var f in ws.FolderChain(relDir)) foreach (var (k, v) in f.Variables) vars[k] = v;
        return vars;
    }

    // ------------------------------------------------------------ Postman

    public static string ToPostman(Workspace ws, string relDir)
    {
        var config = ws.LoadConfig();
        var items = new JsonArray();
        var folders = new Dictionary<string, JsonArray> { [relDir] = items };
        JsonArray FolderItems(string dir)
        {
            if (folders.TryGetValue(dir, out var a)) return a;
            var parent = FolderItems(Workspace.ParentOf(dir));
            var f = new JsonObject { ["name"] = dir[(dir.LastIndexOf('/') + 1)..], ["item"] = new JsonArray() };
            var fc = ws.LoadFolder(dir);
            if (!fc.Auth.IsInherit) f["auth"] = PostmanAuth(fc.Auth);
            if (fc.Variables.Count > 0) f["variable"] = PostmanVariables(fc.Variables);
            parent.Add((JsonNode)f);
            return folders[dir] = (JsonArray)f["item"]!;
        }

        foreach (var e in Collect(ws, relDir))
        {
            var r = e.Request;
            var query = new JsonArray();
            foreach (var q in QueryString.Parse(r.Url)) query.Add((JsonNode)new JsonObject { ["key"] = q.Key, ["value"] = q.Value });
            foreach (var q in r.DisabledParams) query.Add((JsonNode)new JsonObject { ["key"] = q.Key, ["value"] = q.Value, ["disabled"] = true });
            var url = new JsonObject { ["raw"] = r.Url };
            if (query.Count > 0) url["query"] = query;

            var req = new JsonObject
            {
                ["method"] = r.Method,
                ["header"] = new JsonArray(r.Headers.Where(h => !h.IsEmpty).Select(h =>
                {
                    var o = new JsonObject { ["key"] = h.Key, ["value"] = h.Value };
                    if (!h.Active) o["disabled"] = true;
                    return (JsonNode)o;
                }).ToArray()),
                ["url"] = url,
            };
            if (r.Description.Length > 0) req["description"] = r.Description;
            if (!r.Auth.IsInherit) req["auth"] = PostmanAuth(r.Auth);
            if (PostmanBody(r.Body) is { } body) req["body"] = body;

            var item = new JsonObject { ["name"] = r.Name, ["request"] = req };
            if (PostmanTests(r.Tests) is { Count: > 0 } exec)
                item["event"] = new JsonArray(new JsonObject { ["listen"] = "test", ["script"] = new JsonObject { ["type"] = "text/javascript", ["exec"] = exec } });
            FolderItems(Workspace.ParentOf(e.RelPath)).Add((JsonNode)item);
        }

        var name = FolderName(ws, relDir);
        var doc = new JsonObject
        {
            ["info"] = new JsonObject
            {
                ["name"] = name,
                ["_postman_id"] = StableGuid(name).ToString(),
                ["schema"] = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json",
            },
            ["item"] = items,
        };
        var rootAuth = AuthResolver.Resolve(new AuthSpec(), ws.FolderChain(relDir), config);
        if (rootAuth.Type != AuthTypes.None) doc["auth"] = PostmanAuth(rootAuth);
        var vars = SharedVariables(ws, relDir);
        if (vars.Count > 0) doc["variable"] = PostmanVariables(vars);
        // Postman keeps its own key order; the canonical writer is still deterministic.
        return CanonicalJson.Write(doc);
    }

    private static JsonArray PostmanVariables(IEnumerable<KeyValuePair<string, string>> vars) =>
        new(vars.Select(kv => (JsonNode)new JsonObject { ["key"] = kv.Key, ["value"] = kv.Value }).ToArray());

    private static JsonObject PostmanAuth(AuthSpec a)
    {
        JsonArray P(params (string K, string V)[] kv) =>
            new(kv.Select(x => (JsonNode)new JsonObject { ["key"] = x.K, ["value"] = x.V, ["type"] = "string" }).ToArray());
        return a.Type switch
        {
            AuthTypes.Bearer => new JsonObject { ["type"] = "bearer", ["bearer"] = P(("token", a.Token)) },
            AuthTypes.Basic => new JsonObject { ["type"] = "basic", ["basic"] = P(("username", a.Username), ("password", a.Password)) },
            AuthTypes.ApiKey => new JsonObject { ["type"] = "apikey", ["apikey"] = P(("key", a.Key), ("value", a.Value), ("in", a.In)) },
            _ => new JsonObject { ["type"] = "noauth" },
        };
    }

    private static JsonObject? PostmanBody(BodySpec b)
    {
        JsonArray Rows(bool file) => new(b.Fields.Where(f => !f.IsEmpty).Select(f =>
        {
            var o = new JsonObject { ["key"] = f.Key };
            if (file && f.IsFile) { o["type"] = "file"; o["src"] = f.Value; }
            else { o["value"] = f.Value; if (file) o["type"] = "text"; }
            if (!f.Active) o["disabled"] = true;
            return (JsonNode)o;
        }).ToArray());

        return b.Type switch
        {
            BodyTypes.None => null,
            BodyTypes.FormData => new JsonObject { ["mode"] = "formdata", ["formdata"] = Rows(true) },
            BodyTypes.UrlEncoded => new JsonObject { ["mode"] = "urlencoded", ["urlencoded"] = Rows(false) },
            _ => new JsonObject
            {
                ["mode"] = "raw",
                ["raw"] = b.Data,
                ["options"] = new JsonObject { ["raw"] = new JsonObject { ["language"] = b.Type == BodyTypes.Json ? "json" : b.Type == BodyTypes.Xml ? "xml" : b.Type == BodyTypes.Html ? "html" : "text" } },
            },
        };
    }

    [GeneratedRegex(@"^status\s*==\s*(\d{3})$", RegexOptions.IgnoreCase)]
    private static partial Regex StatusTest();

    [GeneratedRegex(@"^(?:response\s+)?time\s*<\s*(\d+)\s*(?:ms)?$", RegexOptions.IgnoreCase)]
    private static partial Regex TimeTest();

    /// <summary>The checks Postman can express directly; the rest stay as comments so nothing is silently lost.</summary>
    private static JsonArray PostmanTests(List<string> tests)
    {
        var exec = new JsonArray();
        foreach (var t in tests.Where(t => !Testing.Assertions.IsComment(t)))
        {
            if (StatusTest().Match(t.Trim()) is { Success: true } s)
                exec.Add(JsonValue.Create($"pm.test(\"{t.Trim()}\", () => pm.response.to.have.status({s.Groups[1].Value}));"));
            else if (TimeTest().Match(t.Trim()) is { Success: true } m)
                exec.Add(JsonValue.Create($"pm.test(\"{t.Trim()}\", () => pm.expect(pm.response.responseTime).to.be.below({m.Groups[1].Value}));"));
            else exec.Add(JsonValue.Create("// RestPeach: " + t.Trim()));
        }
        return exec;
    }

    private static Guid StableGuid(string s)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("restpeach:" + s));
        return new Guid(hash.AsSpan(0, 16));
    }

    // ------------------------------------------------------------ OpenAPI

    [GeneratedRegex(@"\{\{\s*([\w.\-]+)\s*\}\}")]
    private static partial Regex Var();

    public static string ToOpenApi(Workspace ws, string relDir)
    {
        var vars = SharedVariables(ws, relDir);
        var paths = new JsonObject();
        var schemes = new JsonObject();
        string? globalScheme = null;

        foreach (var e in Collect(ws, relDir))
        {
            var r = e.Request;
            var (baseUrl, _, _) = QueryString.Split(r.Url);
            var path = PathOf(baseUrl);
            var parameters = new JsonArray();
            path = Var().Replace(path, m =>
            {
                parameters.Add((JsonNode)new JsonObject { ["name"] = m.Groups[1].Value, ["in"] = "path", ["required"] = true, ["schema"] = new JsonObject { ["type"] = "string" } });
                return "{" + m.Groups[1].Value + "}";
            });
            foreach (var q in QueryString.Parse(r.Url).Select(q => (q, true)).Concat(r.DisabledParams.Select(q => (q, false))))
                parameters.Add((JsonNode)Param(q.q.Key, "query", q.Item2, q.q.Value));
            foreach (var h in r.Headers.Where(h => h.Key.Length > 0 && !IsTransportHeader(h.Key)))
                parameters.Add((JsonNode)Param(h.Key, "header", h.Active, h.Value));

            var op = new JsonObject
            {
                ["summary"] = r.Name,
                ["operationId"] = OperationId(e.RelPath),
                ["responses"] = Responses(r.Tests),
            };
            var tag = TopFolder(e.RelPath, relDir);
            if (tag.Length > 0) op["tags"] = new JsonArray(tag);
            if (r.Description.Length > 0) op["description"] = r.Description;
            if (parameters.Count > 0) op["parameters"] = parameters;
            if (OpenApiBody(r.Body) is { } body) op["requestBody"] = body;

            var scheme = SchemeFor(e.Auth, schemes);
            if (scheme is null) op["security"] = new JsonArray();
            else
            {
                globalScheme ??= scheme;
                if (scheme != globalScheme) op["security"] = new JsonArray(new JsonObject { [scheme] = new JsonArray() });
            }

            var item = paths[path] as JsonObject ?? new JsonObject();
            paths[path] = item;
            var method = r.Method.ToLowerInvariant();
            if (item.ContainsKey(method)) op["operationId"] = op.Str("operationId") + "_" + item.Count;
            item[method] = op;
        }

        var server = vars.TryGetValue("BASE_URL", out var b) && b.Length > 0 ? b : "{{BASE_URL}}";
        var doc = new JsonObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = new JsonObject { ["title"] = FolderName(ws, relDir), ["version"] = "1.0.0" },
            ["servers"] = new JsonArray(new JsonObject { ["url"] = server }),
            ["paths"] = paths,
        };
        if (schemes.Count > 0) doc["components"] = new JsonObject { ["securitySchemes"] = schemes };
        if (globalScheme is not null) doc["security"] = new JsonArray(new JsonObject { [globalScheme] = new JsonArray() });
        return CanonicalJson.Write(doc);
    }

    private static string PathOf(string url)
    {
        var u = url;
        if (u.StartsWith("{{"))
        {
            var close = u.IndexOf("}}", StringComparison.Ordinal);
            u = close < 0 ? "" : u[(close + 2)..];
        }
        else
        {
            var scheme = u.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) u = u[(scheme + 3)..];
            var slash = u.IndexOf('/');
            u = slash < 0 ? "" : u[slash..];
        }
        if (!u.StartsWith('/')) u = "/" + u;
        return u.Length > 1 ? u.TrimEnd('/') : u;
    }

    private static JsonObject Param(string name, string location, bool required, string example)
    {
        var p = new JsonObject { ["name"] = name, ["in"] = location, ["required"] = required, ["schema"] = new JsonObject { ["type"] = "string" } };
        if (example.Length > 0 && !example.Contains("{{")) p["example"] = example;
        return p;
    }

    private static bool IsTransportHeader(string h) =>
        h.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) || h.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
        || h.Equals("Accept", StringComparison.OrdinalIgnoreCase) || h.Equals("Content-Length", StringComparison.OrdinalIgnoreCase);

    private static string TopFolder(string rel, string relDir)
    {
        var inside = relDir.Length == 0 ? rel : rel[(relDir.Length + 1)..];
        var slash = inside.IndexOf('/');
        return slash < 0 ? "" : inside[..slash];
    }

    private static string OperationId(string rel)
    {
        var words = Regex.Split(Workspace.ParentOf(rel) + " " + Workspace.NameOf(rel), @"[^A-Za-z0-9]+").Where(w => w.Length > 0).ToList();
        if (words.Count == 0) return "operation";
        var sb = new StringBuilder(words[0].ToLowerInvariant());
        foreach (var w in words.Skip(1)) sb.Append(char.ToUpperInvariant(w[0])).Append(w[1..]);
        return sb.ToString();
    }

    private static JsonObject Responses(List<string> tests)
    {
        var responses = new JsonObject();
        foreach (var t in tests)
            if (StatusTest().Match(t.Trim()) is { Success: true } m)
                responses[m.Groups[1].Value] = new JsonObject { ["description"] = "Expected response" };
        if (responses.Count == 0) responses["200"] = new JsonObject { ["description"] = "OK" };
        return responses;
    }

    private static JsonObject? OpenApiBody(BodySpec b)
    {
        JsonObject Content(string mime, JsonObject media) => new() { ["content"] = new JsonObject { [mime] = media } };
        JsonObject FormSchema() => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(b.Fields.Where(f => f.Key.Length > 0).GroupBy(f => f.Key).Select(g => new KeyValuePair<string, JsonNode?>(g.Key,
                g.First().IsFile ? new JsonObject { ["type"] = "string", ["format"] = "binary" } : new JsonObject { ["type"] = "string", ["example"] = g.First().Value }))),
        };
        switch (b.Type)
        {
            case BodyTypes.None: return null;
            case BodyTypes.Json:
                JsonNode? example;
                try { example = CanonicalJson.Parse(b.Data); }
                catch (System.Text.Json.JsonException) { example = b.Data; } // placeholders outside strings are not JSON
                return Content("application/json", new JsonObject { ["example"] = example });
            case BodyTypes.FormData: return Content("multipart/form-data", new JsonObject { ["schema"] = FormSchema() });
            case BodyTypes.UrlEncoded: return Content("application/x-www-form-urlencoded", new JsonObject { ["schema"] = FormSchema() });
            default: return Content(BodyTypes.DefaultContentType(b.Type) ?? "text/plain", new JsonObject { ["example"] = b.Data });
        }
    }

    private static string? SchemeFor(AuthSpec a, JsonObject schemes)
    {
        string Add(string name, JsonObject scheme)
        {
            schemes[name] ??= scheme;
            return name;
        }
        return a.Type switch
        {
            AuthTypes.Bearer => Add("bearerAuth", new JsonObject { ["type"] = "http", ["scheme"] = "bearer" }),
            AuthTypes.Basic => Add("basicAuth", new JsonObject { ["type"] = "http", ["scheme"] = "basic" }),
            AuthTypes.ApiKey => Add("apiKey_" + Regex.Replace(a.Key, @"[^A-Za-z0-9]", "_"), new JsonObject { ["type"] = "apiKey", ["name"] = a.Key, ["in"] = a.In }),
            _ => null,
        };
    }

    // ------------------------------------------------------------ bash

    public static string ToBash(Workspace ws, string relDir)
    {
        var entries = Collect(ws, relDir);
        var defaults = SharedVariables(ws, relDir);
        var used = new SortedSet<string>(StringComparer.Ordinal);
        var blocks = new List<string>();
        foreach (var e in entries)
        {
            var auth = e.Auth.Type == AuthTypes.Basic ? AuthSpec.None() : e.Auth;
            // An empty scope leaves every {{NAME}} in place; the shell fills them in.
            var p = RequestPreparer.Prepare(e.Request, auth, new VariableScope(), "");
            string? user = e.Auth.Type == AuthTypes.Basic ? e.Auth.Username + ":" + e.Auth.Password : null;
            foreach (var n in p.Missing) used.Add(n);
            if (user is not null) foreach (var n in Interpolator.Names(user)) used.Add(n);
            blocks.Add($"# {e.RelPath}\n" + Curl.Write(p, shellVariables: true, basicUser: user) + "\necho");
        }

        var sb = new StringBuilder();
        sb.Append("#!/usr/bin/env bash\n");
        sb.Append($"# {FolderName(ws, relDir)}: {entries.Count} requests, exported by RestPeach.\n");
        sb.Append("# Variables are read from the environment, e.g.  BASE_URL=http://localhost:8080 ./script.sh\n");
        sb.Append("set -euo pipefail\n\n");
        foreach (var n in used)
        {
            var shell = Interpolator.ShellName(n);
            if (defaults.TryGetValue(n, out var d) && !d.Contains("{{")) sb.Append($": \"${{{shell}:={Curl.DoubleQuoteBody(d)}}}\"\n");
            else sb.Append($": \"${{{shell}:?set {shell}}}\"\n");
        }
        if (used.Count > 0) sb.Append('\n');
        sb.Append(string.Join("\n\n", blocks)).Append('\n');
        return sb.ToString();
    }
}
