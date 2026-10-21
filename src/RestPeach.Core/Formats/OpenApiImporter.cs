using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RestPeach.Core.Model;
using RestPeach.Core.Text;

namespace RestPeach.Core.Formats;

/// <summary>
/// OpenAPI 3.0 / 3.1 (JSON or YAML) → one folder per tag (or first path segment), one request per operation.
/// The server becomes <c>{{BASE_URL}}</c>, path parameters become variables, bodies get their example (or one built
/// from the schema) and security schemes become the collection's auth.
/// </summary>
public static partial class OpenApiImporter
{
    private static readonly string[] Methods = { "get", "post", "put", "patch", "delete", "head", "options" };

    [GeneratedRegex(@"\{([^}/]+)\}")]
    private static partial Regex PathParam();

    public static ImportResult Import(JsonObject doc)
    {
        var result = new ImportResult { Format = "OpenAPI " + doc.Str("openapi") };
        var root = result.Root;
        root.Name = doc.Obj("info")?.Str("title") is { Length: > 0 } t ? t : "OpenAPI";
        var refs = new RefResolver(doc);

        // Server: first entry, with its variables at their defaults.
        var server = doc.Arr("servers")?.OfType<JsonObject>().FirstOrDefault();
        var serverUrl = server?.Str("url") ?? "";
        foreach (var (name, v) in server?.Obj("variables") ?? new JsonObject())
            serverUrl = serverUrl.Replace("{" + name + "}", v.Str("default"));
        root.Config.Variables["BASE_URL"] = serverUrl.TrimEnd('/');

        // Security applied to the whole API.
        var schemes = doc.Obj("components")?.Obj("securitySchemes");
        var globalAuth = AuthFor(doc.Arr("security"), schemes, refs, result.Warnings);
        if (globalAuth is not null) root.Config.Auth = globalAuth;

        var folders = new Dictionary<string, ImportedFolder>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, pathNode) in doc.Obj("paths") ?? new JsonObject())
        {
            if (refs.Resolve(pathNode) is not JsonObject item) continue;
            var shared = item.Arr("parameters");
            foreach (var method in Methods)
            {
                if (refs.Resolve(item[method]) is not JsonObject op) continue;
                var req = ReadOperation(path, method, op, shared, refs, schemes, globalAuth, root.Config.Variables, result.Warnings);
                var tag = op.Arr("tags")?.FirstOrDefault().Scalar() ?? FirstSegment(path);
                if (tag.Length == 0) { root.Requests.Add(req); continue; }
                if (!folders.TryGetValue(tag, out var folder))
                {
                    folder = new ImportedFolder { Name = tag };
                    folders[tag] = folder;
                    root.Folders.Add(folder);
                }
                folder.Requests.Add(req);
            }
        }
        return result;
    }

    private static string FirstSegment(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(s => !s.StartsWith('{')) ?? "";

    private static ApiRequest ReadOperation(string path, string method, JsonObject op, JsonArray? shared, RefResolver refs,
        JsonObject? schemes, AuthSpec? globalAuth, SortedDictionary<string, string> variables, List<string> warnings)
    {
        var name = op.Str("summary") is { Length: > 0 } s ? s : op.Str("operationId") is { Length: > 0 } id ? id : method.ToUpperInvariant() + " " + path;
        var r = new ApiRequest
        {
            Name = name.Length > 100 ? name[..100] : name,
            Method = method.ToUpperInvariant(),
            Description = op.Str("description"),
            Url = "{{BASE_URL}}" + PathParam().Replace(path, m => "{{" + m.Groups[1].Value + "}}"),
        };

        // Operation parameters override path-level ones with the same name and location.
        var parameters = new Dictionary<(string, string), JsonObject>();
        foreach (var p in (shared ?? new JsonArray()).Concat(op.Arr("parameters") ?? new JsonArray()))
            if (refs.Resolve(p) is JsonObject po) parameters[(po.Str("in"), po.Str("name"))] = po;

        var query = new List<KeyValue>();
        foreach (var ((location, pname), p) in parameters)
        {
            var example = ExampleOf(p, refs);
            bool required = p.Bool("required") || location == "path";
            switch (location)
            {
                case "path":
                    if (example.Length > 0) variables.TryAdd(pname, example);
                    break;
                case "query":
                    var row = KeyValue.Of(pname, example.Length > 0 ? example : "{{" + pname + "}}", required);
                    if (required) query.Add(row);
                    else r.DisabledParams.Add(row);
                    break;
                case "header":
                    r.Headers.Add(KeyValue.Of(pname, example, required));
                    break;
                case "cookie":
                    warnings.Add($"{r.Name}: cookie parameter \"{pname}\" not imported");
                    break;
            }
        }
        if (query.Count > 0) r.Url = Http.QueryString.WithParams(r.Url, query);

        if (refs.Resolve(op["requestBody"]) is JsonObject body && body.Obj("content") is { } content)
            r.Body = BodyFor(content, refs);

        // "security: []" on an operation means public; a different scheme than the API's overrides it.
        if (op.Arr("security") is { } sec)
        {
            var auth = sec.Count == 0 ? AuthSpec.None() : AuthFor(sec, schemes, refs, warnings);
            if (auth is not null && !SameAuth(auth, globalAuth)) r.Auth = auth;
        }

        var ok = (op.Obj("responses") ?? new JsonObject()).Select(x => x.Key).FirstOrDefault(k => k.StartsWith('2'));
        if (ok is not null) r.Tests.Add(ok.Length == 3 && ok.All(char.IsDigit) ? "status == " + ok : "status == 2xx");
        return r;
    }

    private static bool SameAuth(AuthSpec a, AuthSpec? b) =>
        b is not null && a.Type == b.Type && a.Token == b.Token && a.Key == b.Key && a.In == b.In && a.Username == b.Username;

    private static BodySpec BodyFor(JsonObject content, RefResolver refs)
    {
        JsonObject? Media(Func<string, bool> pick) =>
            content.FirstOrDefault(kv => pick(kv.Key.ToLowerInvariant())).Value as JsonObject;

        if (Media(k => k.Contains("json")) is { } json)
            return new BodySpec { Type = BodyTypes.Json, Data = CanonicalJson.Pretty(SampleOf(json, refs)?.ToJsonString() ?? "{}") ?? "{}" };
        if (Media(k => k.Contains("x-www-form-urlencoded")) is { } form)
            return new BodySpec { Type = BodyTypes.UrlEncoded, Fields = FieldsOf(form, refs) };
        if (Media(k => k.Contains("multipart")) is { } multi)
            return new BodySpec { Type = BodyTypes.FormData, Fields = FieldsOf(multi, refs) };
        if (Media(k => k.Contains("xml")) is { } xml)
            return new BodySpec { Type = BodyTypes.Xml, Data = SampleOf(xml, refs).Scalar() ?? "<root />" };
        if (Media(k => k.StartsWith("text/")) is { } text)
            return new BodySpec { Type = BodyTypes.Text, Data = SampleOf(text, refs).Scalar() ?? "" };
        return new BodySpec();
    }

    private static List<KeyValue> FieldsOf(JsonObject media, RefResolver refs)
    {
        var fields = new List<KeyValue>();
        if (refs.Resolve(media["schema"]) is not JsonObject schema) return fields;
        var required = (schema.Arr("required") ?? new JsonArray()).Select(x => x.Scalar()).ToHashSet();
        foreach (var (name, prop) in schema.Obj("properties") ?? new JsonObject())
        {
            var p = refs.Resolve(prop) as JsonObject;
            bool file = p?.Str("format") is "binary" or "base64" || p?.Str("contentMediaType").Length > 0;
            var sample = file ? "" : Sample(p, refs, 0).Scalar() ?? "";
            fields.Add(new KeyValue { Key = name, Value = sample, Active = required.Contains(name) || required.Count == 0, Type = file ? "file" : null });
        }
        return fields;
    }

    private static JsonNode? SampleOf(JsonObject media, RefResolver refs)
    {
        if (media["example"] is { } ex) return ex.DeepClone();
        if (media.Obj("examples") is { Count: > 0 } exs && refs.Resolve(exs.First().Value) is JsonObject first && first["value"] is { } v)
            return v.DeepClone();
        return Sample(refs.Resolve(media["schema"]) as JsonObject, refs, 0);
    }

    private static string ExampleOf(JsonObject p, RefResolver refs)
    {
        if (p["example"] is { } ex) return ex.Scalar() ?? ex.ToJsonString();
        var schema = refs.Resolve(p["schema"]) as JsonObject;
        if (schema?["example"] is { } se) return se.Scalar() ?? se.ToJsonString();
        if (schema?["default"] is { } d) return d.Scalar() ?? d.ToJsonString();
        if (schema?.Arr("enum") is { Count: > 0 } e) return e[0].Scalar() ?? "";
        return "";
    }

    /// <summary>A plausible value for a schema: its example/default/first enum, else a typed placeholder.</summary>
    internal static JsonNode? Sample(JsonObject? schema, RefResolver refs, int depth)
    {
        if (schema is null || depth > 8) return null;
        if (schema["example"] is { } ex) return ex.DeepClone();
        if (schema.Arr("examples") is { Count: > 0 } exs) return exs[0]?.DeepClone();
        if (schema["default"] is { } d) return d.DeepClone();
        if (schema.Arr("enum") is { Count: > 0 } e) return e[0]?.DeepClone();
        if (schema["const"] is { } c) return c.DeepClone();

        if (schema.Arr("allOf") is { } all)
        {
            var merged = new JsonObject();
            foreach (var part in all)
                if (Sample(refs.Resolve(part) as JsonObject, refs, depth + 1) is JsonObject o)
                    foreach (var (k, v) in o) merged[k] = v?.DeepClone();
            return merged;
        }
        foreach (var key in new[] { "oneOf", "anyOf" })
            if (schema.Arr(key) is { Count: > 0 } options)
                return Sample(refs.Resolve(options[0]) as JsonObject, refs, depth + 1);

        var type = schema["type"] switch
        {
            JsonArray types => types.Select(x => x.Scalar()).FirstOrDefault(x => x != "null") ?? "string",
            var t => t.Scalar() ?? (schema.ContainsKey("properties") ? "object" : schema.ContainsKey("items") ? "array" : "string"),
        };
        switch (type)
        {
            case "object":
                var obj = new JsonObject();
                foreach (var (name, prop) in schema.Obj("properties") ?? new JsonObject())
                {
                    var p = refs.Resolve(prop) as JsonObject;
                    if (p?.Bool("readOnly") == true) continue;
                    obj[name] = Sample(p, refs, depth + 1);
                }
                return obj;
            case "array":
                var item = Sample(refs.Resolve(schema["items"]) as JsonObject, refs, depth + 1);
                return item is null ? new JsonArray() : new JsonArray(item);
            case "integer": return JsonValue.Create(0);
            case "number": return JsonValue.Create(0.0);
            case "boolean": return JsonValue.Create(true);
            default:
                return JsonValue.Create(schema.Str("format") switch
                {
                    "date-time" => "2024-01-01T00:00:00Z",
                    "date" => "2024-01-01",
                    "email" => "user@example.com",
                    "uuid" => "00000000-0000-0000-0000-000000000000",
                    "uri" or "url" => "https://example.com",
                    "password" => "********",
                    _ => "string",
                });
        }
    }

    private static AuthSpec? AuthFor(JsonArray? security, JsonObject? schemes, RefResolver refs, List<string> warnings)
    {
        if (security is null || security.Count == 0 || schemes is null) return null;
        foreach (var requirement in security.OfType<JsonObject>())
        {
            foreach (var (name, _) in requirement)
            {
                if (refs.Resolve(schemes[name]) is not JsonObject s) continue;
                var type = s.Str("type").ToLowerInvariant();
                var scheme = s.Str("scheme").ToLowerInvariant();
                if (type == "http" && scheme == "bearer") return AuthSpec.BearerOf("{{TOKEN}}");
                if (type == "http" && scheme == "basic") return new AuthSpec { Type = AuthTypes.Basic, Username = "{{USERNAME}}", Password = "{{PASSWORD}}" };
                if (type == "apikey" && s.Str("in") is "header" or "query")
                    return new AuthSpec { Type = AuthTypes.ApiKey, Key = s.Str("name"), Value = "{{API_KEY}}", In = s.Str("in") };
                if (type is "oauth2" or "openidconnect")
                {
                    warnings.Add($"Security \"{name}\" ({type}): imported as Bearer {{{{TOKEN}}}}");
                    return AuthSpec.BearerOf("{{TOKEN}}");
                }
            }
        }
        return null;
    }

    /// <summary>Local <c>$ref</c>s ("#/components/schemas/User"), with a guard against cycles.</summary>
    internal sealed class RefResolver
    {
        private readonly JsonObject _doc;
        public RefResolver(JsonObject doc) => _doc = doc;

        public JsonNode? Resolve(JsonNode? node)
        {
            for (int hops = 0; hops < 16 && node is JsonObject o && o.Str("$ref") is { Length: > 0 } r; hops++)
            {
                if (!r.StartsWith("#/")) return null;
                JsonNode? cur = _doc;
                foreach (var part in r[2..].Split('/'))
                {
                    var key = part.Replace("~1", "/").Replace("~0", "~");
                    cur = (cur as JsonObject)?[key];
                    if (cur is null) return null;
                }
                node = cur;
            }
            return node;
        }
    }
}
