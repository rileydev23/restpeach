using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RestPeach.Core.Http;
using RestPeach.Core.Model;
using RestPeach.Core.Text;

namespace RestPeach.Core.Formats;

/// <summary>Postman Collection v2.0/v2.1 (<c>.postman_collection.json</c>) and Postman environments.</summary>
public static class PostmanImporter
{
    public static ImportResult Import(JsonObject doc)
    {
        var schema = doc.Obj("info")?.Str("schema") ?? "";
        var result = new ImportResult { Format = schema.Contains("v2.0") ? "Postman 2.0" : "Postman 2.1" };
        var root = result.Root;
        root.Name = doc.Obj("info")?.Str("name") is { Length: > 0 } n ? n : "Postman";
        root.Config.Auth = ReadAuth(doc.Obj("auth"), result.Warnings, root.Name);
        foreach (var v in doc.Arr("variable") ?? new JsonArray())
            if (v.Str("key") is { Length: > 0 } key && !v.Bool("disabled")) root.Config.Variables[key] = v.Str("value");
        ReadItems(doc.Arr("item"), root, result.Warnings);
        return result;
    }

    private static void ReadItems(JsonArray? items, ImportedFolder into, List<string> warnings)
    {
        if (items is null) return;
        foreach (var node in items)
        {
            if (node is not JsonObject item) continue;
            var name = item.Str("name", "Request");
            if (item.Arr("item") is { } children)
            {
                var folder = new ImportedFolder { Name = name };
                folder.Config.Auth = ReadAuth(item.Obj("auth"), warnings, name);
                foreach (var v in item.Arr("variable") ?? new JsonArray())
                    if (v.Str("key") is { Length: > 0 } key) folder.Config.Variables[key] = v.Str("value");
                ReadItems(children, folder, warnings);
                into.Folders.Add(folder);
            }
            else into.Requests.Add(ReadRequest(item, name, warnings));
        }
    }

    private static ApiRequest ReadRequest(JsonObject item, string name, List<string> warnings)
    {
        var r = new ApiRequest { Name = name };
        var reqNode = item["request"];
        if (reqNode is JsonValue) { r.Url = reqNode.Scalar() ?? ""; return r; }
        if (reqNode is not JsonObject req) return r;

        r.Method = req.Str("method", "GET").ToUpperInvariant();
        r.Description = Description(req["description"]);
        ReadUrl(req["url"], r);
        foreach (var h in req.Arr("header") ?? new JsonArray())
            if (h is JsonObject ho && ho.Str("key").Length > 0)
                r.Headers.Add(new KeyValue { Key = ho.Str("key"), Value = ho.Str("value"), Active = !ho.Bool("disabled") });
        r.Auth = req.Obj("auth") is { } a ? ReadAuth(a, warnings, name) : new AuthSpec();
        r.Body = ReadBody(req.Obj("body"), warnings, name);
        r.Tests = ConvertTests(item.Arr("event"), warnings, name);
        return r;
    }

    private static string Description(JsonNode? node) => node switch
    {
        JsonObject o => o.Str("content"),
        JsonValue v => v.ToString(),
        _ => "",
    };

    private static void ReadUrl(JsonNode? node, ApiRequest r)
    {
        if (node is JsonValue) { r.Url = node.Scalar() ?? ""; return; }
        if (node is not JsonObject u) return;
        var raw = u.Str("raw");
        if (u.Arr("query") is { } query)
        {
            // "query" is the full list (with disabled entries); "raw" only has the enabled ones.
            var rows = new List<KeyValue>();
            foreach (var q in query)
            {
                if (q is not JsonObject qo || qo.Str("key").Length == 0) continue;
                var row = new KeyValue { Key = qo.Str("key"), Value = qo.Str("value"), Active = !qo.Bool("disabled") };
                if (row.Active) rows.Add(row);
                else r.DisabledParams.Add(row);
            }
            if (raw.Length == 0) raw = BuildRaw(u);
            r.Url = QueryString.WithParams(raw, rows);
        }
        else r.Url = raw.Length > 0 ? raw : BuildRaw(u);
    }

    private static string BuildRaw(JsonObject u)
    {
        string Join(JsonNode? n, string sep) => n switch
        {
            JsonArray a => string.Join(sep, a.Select(x => x.Scalar() ?? x.Str("value"))),
            JsonValue v => v.ToString(),
            _ => "",
        };
        var protocol = u.Str("protocol");
        var host = Join(u["host"], ".");
        var port = u.Str("port");
        var path = Join(u["path"], "/");
        return (protocol.Length > 0 ? protocol + "://" : "") + host + (port.Length > 0 ? ":" + port : "") + (path.Length > 0 ? "/" + path : "");
    }

    /// <summary>Postman stores auth parameters as lists of {key, value}.</summary>
    internal static AuthSpec ReadAuth(JsonObject? auth, List<string> warnings, string where)
    {
        if (auth is null) return new AuthSpec();
        var type = auth.Str("type").ToLowerInvariant();
        string P(string list, string key) =>
            (auth.Arr(list) ?? new JsonArray()).OfType<JsonObject>().FirstOrDefault(x => x.Str("key") == key).Str("value");
        switch (type)
        {
            case "noauth": return AuthSpec.None();
            case "bearer": return AuthSpec.BearerOf(P("bearer", "token"));
            case "basic": return new AuthSpec { Type = AuthTypes.Basic, Username = P("basic", "username"), Password = P("basic", "password") };
            case "apikey":
                return new AuthSpec { Type = AuthTypes.ApiKey, Key = P("apikey", "key"), Value = P("apikey", "value"), In = P("apikey", "in") == "query" ? "query" : "header" };
            case "oauth2":
                var token = P("oauth2", "accessToken");
                warnings.Add($"{where}: OAuth 2.0 → Bearer ({(token.Length > 0 ? "token kept" : "set the token")})");
                return AuthSpec.BearerOf(token.Length > 0 ? token : "{{ACCESS_TOKEN}}");
            case "":
                return new AuthSpec();
            default:
                warnings.Add($"{where}: auth \"{type}\" is not supported, it inherits instead");
                return new AuthSpec();
        }
    }

    private static BodySpec ReadBody(JsonObject? body, List<string> warnings, string where)
    {
        if (body is null || body.Bool("disabled")) return new BodySpec();
        switch (body.Str("mode"))
        {
            case "raw":
                var raw = body.Str("raw");
                var lang = body.Obj("options")?.Obj("raw")?.Str("language") ?? "";
                var type = lang switch
                {
                    "json" => BodyTypes.Json,
                    "xml" => BodyTypes.Xml,
                    "html" => BodyTypes.Html,
                    "text" or "javascript" => BodyTypes.Text,
                    _ => raw.TrimStart().StartsWith('{') || raw.TrimStart().StartsWith('[') ? BodyTypes.Json : BodyTypes.Text,
                };
                return new BodySpec { Type = type, Data = raw };
            case "urlencoded":
                return new BodySpec { Type = BodyTypes.UrlEncoded, Fields = Rows(body.Arr("urlencoded")) };
            case "formdata":
                var fields = new List<KeyValue>();
                foreach (var f in (body.Arr("formdata") ?? new JsonArray()).OfType<JsonObject>())
                {
                    bool file = f.Str("type") == "file";
                    var src = f["src"] is JsonArray arr ? arr.FirstOrDefault().Scalar() ?? "" : f.Str("src");
                    fields.Add(new KeyValue { Key = f.Str("key"), Value = file ? src : f.Str("value"), Active = !f.Bool("disabled"), Type = file ? "file" : null });
                }
                return new BodySpec { Type = BodyTypes.FormData, Fields = fields };
            case "graphql":
                var gql = new JsonObject { ["query"] = body.Obj("graphql")?.Str("query") ?? "" };
                var vars = body.Obj("graphql")?.Str("variables") ?? "";
                if (vars.Trim().Length > 0)
                {
                    try { gql["variables"] = CanonicalJson.Parse(vars); }
                    catch (System.Text.Json.JsonException) { gql["variables"] = vars; }
                }
                return new BodySpec { Type = BodyTypes.Json, Data = CanonicalJson.Pretty(gql.ToJsonString()) ?? "" };
            case "file":
                warnings.Add($"{where}: binary file body not imported");
                return new BodySpec();
            default:
                return new BodySpec();
        }
    }

    private static List<KeyValue> Rows(JsonArray? a) =>
        (a ?? new JsonArray()).OfType<JsonObject>().Where(o => o.Str("key").Length > 0)
            .Select(o => new KeyValue { Key = o.Str("key"), Value = o.Str("value"), Active = !o.Bool("disabled") }).ToList();

    private static readonly (Regex Rx, Func<Match, string> Line)[] TestPatterns =
    {
        (new Regex(@"to\.have\.status\((\d{3})\)"), m => "status == " + m.Groups[1].Value),
        (new Regex(@"response\.code\)\.to\.(?:eql|equal|be)\((\d{3})\)"), m => "status == " + m.Groups[1].Value),
        (new Regex(@"responseTime\)\.to\.be\.below\((\d+)\)"), m => "time < " + m.Groups[1].Value + "ms"),
        (new Regex(@"to\.have\.header\(['""]([^'""]+)['""]\)"), m => "header " + m.Groups[1].Value + " exists"),
        (new Regex(@"pm\.environment\.set\(['""]([\w.\-]+)['""]\s*,\s*(?:pm\.response\.json\(\)|jsonData|json|data)((?:\.[A-Za-z_]\w*|\[\d+\])+)\)"),
            m => "set " + m.Groups[1].Value + " = $" + m.Groups[2].Value),
    };

    /// <summary>The few test idioms that have a declarative equivalent; the rest of the script is reported, not run.</summary>
    private static List<string> ConvertTests(JsonArray? events, List<string> warnings, string where)
    {
        var lines = new List<string>();
        if (events is null) return lines;
        bool dropped = false;
        foreach (var e in events.OfType<JsonObject>())
        {
            var exec = e.Obj("script")?["exec"];
            var code = exec switch
            {
                JsonArray a => string.Join("\n", a.Select(x => x.Scalar() ?? "")),
                JsonValue v => v.ToString(),
                _ => "",
            };
            if (code.Trim().Length == 0) continue;
            if (e.Str("listen") != "test") { dropped = true; continue; }
            int before = lines.Count;
            foreach (var (rx, line) in TestPatterns)
                foreach (Match m in rx.Matches(code)) lines.Add(line(m));
            if (lines.Count == before || code.Split('\n').Count(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("//")) > (lines.Count - before) * 3 + 2)
                dropped = true;
        }
        if (dropped) warnings.Add($"{where}: scripts are not run; only simple checks were converted to tests");
        return lines.Distinct().ToList();
    }

    /// <summary>A Postman environment export (<c>.postman_environment.json</c>).</summary>
    public static ApiEnvironment? ReadEnvironment(JsonObject doc)
    {
        if (doc.Arr("values") is not { } values || doc.Str("name").Length == 0) return null;
        var env = new ApiEnvironment { Name = doc.Str("name"), Stem = doc.Str("name") };
        foreach (var v in values.OfType<JsonObject>())
        {
            if (v.Str("key").Length == 0 || !v.Bool("enabled", true)) continue;
            if (v.Str("type") == "secret") env.Secrets[v.Str("key")] = v.Str("value");
            else env.Variables[v.Str("key")] = v.Str("value");
        }
        return env;
    }
}
