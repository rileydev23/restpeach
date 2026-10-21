using System.Text.Json.Nodes;
using RestPeach.Core.Http;
using RestPeach.Core.Model;
using RestPeach.Core.Text;

namespace RestPeach.Core.Formats;

/// <summary>Insomnia exports: v4 (JSON/YAML list of resources) and v5 (YAML tree).</summary>
public static class InsomniaImporter
{
    // ------------------------------------------------------------ v4

    public static ImportResult ImportV4(JsonObject doc)
    {
        var result = new ImportResult { Format = "Insomnia 4" };
        var resources = (doc.Arr("resources") ?? new JsonArray()).OfType<JsonObject>().ToList();
        var byParent = resources.GroupBy(r => r.Str("parentId")).ToDictionary(g => g.Key, g => g.ToList());
        var workspace = resources.FirstOrDefault(r => r.Str("_type") == "workspace");
        result.Root.Name = workspace?.Str("name") is { Length: > 0 } n ? n : "Insomnia";
        var rootId = workspace?.Str("_id") ?? "";

        void Fill(string parentId, ImportedFolder folder)
        {
            if (!byParent.TryGetValue(parentId, out var children)) return;
            foreach (var c in children.OrderBy(c => c.Int("metaSortKey")))
            {
                switch (c.Str("_type"))
                {
                    case "request_group":
                        var sub = new ImportedFolder { Name = c.Str("name", "Folder") };
                        foreach (var (k, v) in Flatten(c.Obj("environment"))) sub.Config.Variables[k] = v;
                        sub.Config.Auth = ReadAuth(c.Obj("authentication"));
                        Fill(c.Str("_id"), sub);
                        folder.Add(sub);
                        break;
                    case "request":
                        folder.Add(ReadRequestV4(c, result.Warnings));
                        break;
                    case "grpc_request" or "websocket_request":
                        result.Warnings.Add(Loc.F("imp.unsupportedKind", c.Str("name"), c.Str("_type")));
                        break;
                }
            }
        }
        if (workspace is not null) Fill(rootId, result.Root);
        else
        {
            // Partial exports may have no workspace: take everything whose parent is not exported.
            var ids = resources.Select(r => r.Str("_id")).ToHashSet();
            foreach (var parent in byParent.Keys.Where(p => !ids.Contains(p))) Fill(parent, result.Root);
        }

        // Base environment → collection variables; its children → environments.
        var baseEnv = resources.FirstOrDefault(r => r.Str("_type") == "environment" && r.Str("parentId") == rootId)
                      ?? resources.FirstOrDefault(r => r.Str("_type") == "environment" && r.Str("parentId").StartsWith("wrk_"));
        if (baseEnv is not null)
        {
            foreach (var (k, v) in Flatten(baseEnv.Obj("data"))) result.Root.Config.Variables[k] = v;
            foreach (var env in resources.Where(r => r.Str("_type") == "environment" && r.Str("parentId") == baseEnv.Str("_id")))
                result.Environments.Add(new ApiEnvironment { Name = env.Str("name"), Stem = env.Str("name"), Variables = Flatten(env.Obj("data")) });
        }
        return result;
    }

    private static ApiRequest ReadRequestV4(JsonObject c, List<string> warnings)
    {
        var r = new ApiRequest
        {
            Name = c.Str("name", "Request"),
            Method = c.Str("method", "GET").ToUpperInvariant(),
            Description = c.Str("description"),
        };
        var url = Importer.Vars(c.Str("url"));
        var active = new List<KeyValue>();
        foreach (var p in (c.Arr("parameters") ?? new JsonArray()).OfType<JsonObject>())
        {
            var row = new KeyValue { Key = Importer.Vars(p.Str("name")), Value = Importer.Vars(p.Str("value")), Active = !p.Bool("disabled") };
            if (row.Key.Length == 0) continue;
            if (row.Active) active.Add(row);
            else r.DisabledParams.Add(row);
        }
        r.Url = active.Count > 0 ? QueryString.WithParams(url, QueryString.Parse(url).Concat(active)) : url;
        foreach (var h in (c.Arr("headers") ?? new JsonArray()).OfType<JsonObject>())
            if (h.Str("name").Length > 0)
                r.Headers.Add(new KeyValue { Key = h.Str("name"), Value = Importer.Vars(h.Str("value")), Active = !h.Bool("disabled") });
        r.Auth = ReadAuth(c.Obj("authentication"));
        r.Body = ReadBody(c.Obj("body"));
        if ((r.Url + string.Concat(r.Headers.Select(h => h.Value)) + r.Body.Data).Contains("{%"))
            warnings.Add(Loc.F("imp.insomniaTags", r.Name));
        return r;
    }

    internal static AuthSpec ReadAuth(JsonObject? a)
    {
        if (a is null || a.Count == 0) return new AuthSpec();
        if (a.Bool("disabled")) return AuthSpec.None();
        return a.Str("type").ToLowerInvariant() switch
        {
            "bearer" => AuthSpec.BearerOf(Importer.Vars(a.Str("token"))),
            "basic" => new AuthSpec { Type = AuthTypes.Basic, Username = Importer.Vars(a.Str("username")), Password = Importer.Vars(a.Str("password")) },
            "apikey" => new AuthSpec
            {
                Type = AuthTypes.ApiKey, Key = Importer.Vars(a.Str("key")), Value = Importer.Vars(a.Str("value")),
                In = a.Str("addTo").Contains("query", StringComparison.OrdinalIgnoreCase) ? "query" : "header",
            },
            "none" => AuthSpec.None(),
            _ => new AuthSpec(),
        };
    }

    internal static BodySpec ReadBody(JsonObject? b)
    {
        if (b is null || b.Count == 0) return new BodySpec();
        var mime = b.Str("mimeType").ToLowerInvariant();
        if (mime.Contains("form-urlencoded") || mime.Contains("multipart"))
        {
            var fields = (b.Arr("params") ?? new JsonArray()).OfType<JsonObject>().Select(p => new KeyValue
            {
                Key = Importer.Vars(p.Str("name")),
                Value = p.Str("type") == "file" ? p.Str("fileName") : Importer.Vars(p.Str("value")),
                Active = !p.Bool("disabled"),
                Type = p.Str("type") == "file" ? "file" : null,
            }).ToList();
            return new BodySpec { Type = mime.Contains("multipart") ? BodyTypes.FormData : BodyTypes.UrlEncoded, Fields = fields };
        }
        var text = Importer.Vars(b.Str("text"));
        // Insomnia keeps a GraphQL body as the JSON text {"query", "variables"}.
        if (mime.Contains("graphql"))
        {
            try
            {
                if (CanonicalJson.Parse(text) is JsonObject gql && gql.ContainsKey("query"))
                {
                    var vars = gql["variables"] is JsonObject { Count: > 0 } v ? CanonicalJson.Pretty(v.ToJsonString()) ?? "" : "";
                    return new BodySpec { Type = BodyTypes.GraphQL, Data = gql.Str("query"), Variables = vars };
                }
            }
            catch (System.Text.Json.JsonException) { }
        }
        var type = mime.Contains("json") || mime.Contains("graphql") ? BodyTypes.Json
            : mime.Contains("xml") ? BodyTypes.Xml
            : mime.Contains("html") ? BodyTypes.Html
            : text.Length > 0 ? BodyTypes.Text : BodyTypes.None;
        return new BodySpec { Type = type, Data = text };
    }

    /// <summary>Nested environment objects ({"api": {"url": …}}) become dotted names ("api.url"), as Insomnia resolves them.</summary>
    internal static SortedDictionary<string, string> Flatten(JsonObject? data)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Walk(JsonNode? n, string prefix)
        {
            if (n is JsonObject o)
            {
                foreach (var (k, v) in o) Walk(v, prefix.Length == 0 ? k : prefix + "." + k);
            }
            else if (prefix.Length > 0) map[prefix] = Importer.Vars(n.Scalar() ?? n?.ToJsonString() ?? "");
        }
        Walk(data, "");
        return map;
    }

    // ------------------------------------------------------------ v5

    public static ImportResult ImportV5(JsonObject doc)
    {
        var result = new ImportResult { Format = "Insomnia 5", Root = { Name = doc.Str("name", "Insomnia") } };
        void Fill(JsonArray? items, ImportedFolder folder)
        {
            foreach (var item in (items ?? new JsonArray()).OfType<JsonObject>())
            {
                if (item.Arr("children") is { } children)
                {
                    var sub = new ImportedFolder { Name = item.Str("name", "Folder") };
                    foreach (var (k, v) in Flatten(item.Obj("environment"))) sub.Config.Variables[k] = v;
                    sub.Config.Auth = ReadAuth(item.Obj("authentication"));
                    Fill(children, sub);
                    folder.Add(sub);
                }
                else if (item.ContainsKey("url") || item.ContainsKey("method"))
                {
                    // Same request shape as v4 except where documented; reuse its reader.
                    var copy = (JsonObject)item.DeepClone();
                    if (copy["headers"] is JsonArray hs)
                        foreach (var h in hs.OfType<JsonObject>()) h["disabled"] = h.Bool("disabled");
                    folder.Add(ReadRequestV4(copy, result.Warnings));
                }
            }
        }
        Fill(doc.Arr("collection"), result.Root);
        if (doc.Obj("environments") is { } envs)
        {
            foreach (var (k, v) in Flatten(envs.Obj("data"))) result.Root.Config.Variables[k] = v;
            foreach (var sub in (envs.Arr("subEnvironments") ?? new JsonArray()).OfType<JsonObject>())
                result.Environments.Add(new ApiEnvironment { Name = sub.Str("name"), Stem = sub.Str("name"), Variables = Flatten(sub.Obj("data")) });
        }
        return result;
    }
}
