using System.Text.Json.Nodes;
using RestPeach.Core.Model;
using RestPeach.Core.Text;

namespace RestPeach.Core.Storage;

/// <summary>
/// Model ↔ JSON tree. Defaults are left out (an inherited auth, an empty header list…) so files stay short, and
/// unknown keys are carried in <c>Extra</c> so hand-written additions survive a save.
/// </summary>
public static class Codec
{
    // ------------------------------------------------------------ requests

    private static readonly HashSet<string> RequestKeys = new(StringComparer.Ordinal)
        { "name", "method", "url", "description", "headers", "disabledParams", "auth", "body", "tests", "followRedirects" };

    public static ApiRequest ReadRequest(JsonNode? node, string fallbackName)
    {
        var o = node as JsonObject ?? throw new InvalidDataException("A request file must hold a JSON object.");
        var r = new ApiRequest
        {
            Name = o.Str("name", fallbackName),
            Method = o.Str("method", "GET").Trim().ToUpperInvariant(),
            Url = o.Str("url"),
            Description = o.Str("description"),
            Headers = ReadRows(o.Arr("headers")),
            DisabledParams = ReadRows(o.Arr("disabledParams")),
            Auth = ReadAuth(o.Obj("auth")),
            Body = ReadBody(o["body"]),
            Tests = (o.Arr("tests") ?? new JsonArray()).Select(t => t.Scalar() ?? "").Where(t => t.Length > 0).ToList(),
            FollowRedirects = o["followRedirects"] is JsonValue fr && fr.TryGetValue<bool>(out var follow) ? follow : null,
            Extra = Leftovers(o, RequestKeys),
        };
        if (r.Name.Length == 0) r.Name = fallbackName;
        if (r.Method.Length == 0) r.Method = "GET";
        return r;
    }

    public static JsonObject WriteRequest(ApiRequest r)
    {
        var o = (JsonObject)r.Extra.DeepClone();
        o["name"] = r.Name;
        o["method"] = r.Method;
        o["url"] = r.Url;
        if (r.Description.Length > 0) o["description"] = r.Description;
        if (r.Headers.Count > 0) o["headers"] = WriteRows(r.Headers);
        if (r.DisabledParams.Count > 0) o["disabledParams"] = WriteRows(r.DisabledParams);
        if (!r.Auth.IsInherit) o["auth"] = WriteAuth(r.Auth);
        if (r.Body.Type != BodyTypes.None) o["body"] = WriteBody(r.Body);
        if (r.Tests.Count > 0) o["tests"] = new JsonArray(r.Tests.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray());
        if (r.FollowRedirects is { } follow) o["followRedirects"] = follow;
        return o;
    }

    public static List<KeyValue> ReadRows(JsonArray? a)
    {
        var list = new List<KeyValue>();
        if (a is null) return list;
        foreach (var item in a)
        {
            if (item is not JsonObject h) continue;
            list.Add(new KeyValue
            {
                Active = h.Bool("active", true),
                Key = h.Str("key"),
                Value = h.Str("value"),
                Type = h["type"] is null ? null : h.Str("type"),
            });
        }
        return list;
    }

    public static JsonArray WriteRows(IEnumerable<KeyValue> rows)
    {
        var a = new JsonArray();
        foreach (var h in rows.Where(r => !r.IsEmpty))
        {
            var o = new JsonObject { ["active"] = h.Active, ["key"] = h.Key, ["value"] = h.Value };
            if (h.IsFile) o["type"] = "file";
            a.Add((JsonNode)o);
        }
        return a;
    }

    public static AuthSpec ReadAuth(JsonObject? o)
    {
        if (o is null) return new AuthSpec();
        var type = o.Str("type", AuthTypes.Inherit).Trim().ToLowerInvariant();
        if (type is "apiKey" or "api-key" or "api_key") type = AuthTypes.ApiKey;
        return new AuthSpec
        {
            Type = AuthTypes.All.Contains(type) ? type : AuthTypes.Inherit,
            Token = o.Str("token"),
            Username = o.Str("username"),
            Password = o.Str("password"),
            Key = o.Str("key"),
            Value = o.Str("value"),
            In = o.Str("in", "header").Equals("query", StringComparison.OrdinalIgnoreCase) ? "query" : "header",
        };
    }

    /// <summary>Only the fields the type uses, so switching type in the UI does not leave stale keys in git.</summary>
    public static JsonObject WriteAuth(AuthSpec a)
    {
        var o = new JsonObject { ["type"] = a.Type };
        switch (a.Type)
        {
            case AuthTypes.Bearer:
                o["token"] = a.Token;
                break;
            case AuthTypes.Basic:
                o["username"] = a.Username;
                o["password"] = a.Password;
                break;
            case AuthTypes.ApiKey:
                o["key"] = a.Key;
                o["value"] = a.Value;
                o["in"] = a.In;
                break;
        }
        return o;
    }

    public static BodySpec ReadBody(JsonNode? node)
    {
        if (node is not JsonObject o) return new BodySpec();
        var type = o.Str("type", BodyTypes.None).Trim().ToLowerInvariant();
        if (type is "formdata" or "multipart") type = BodyTypes.FormData;
        if (type is "form" or "x-www-form-urlencoded") type = BodyTypes.UrlEncoded;
        if (!BodyTypes.All.Contains(type)) type = BodyTypes.Text;
        if (type == BodyTypes.GraphQL)
            return new BodySpec { Type = type, Data = Text(o["query"] ?? o["data"]), Variables = Text(o["variables"]) };
        // "data" may be written by hand as a real JSON object instead of a string: keep it readable.
        return new BodySpec { Type = type, Data = Text(o["data"]), Fields = ReadRows(o.Arr("fields")) };
    }

    /// <summary>A string as is; a JSON object or array (written by hand, or GraphQL variables) re-indented.</summary>
    private static string Text(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => CanonicalJson.Pretty(node.ToJsonString()) ?? node.ToJsonString(),
    };

    public static JsonObject WriteBody(BodySpec b)
    {
        var o = new JsonObject { ["type"] = b.Type };
        if (b.Type == BodyTypes.GraphQL)
        {
            o["query"] = b.Data;
            if (GraphQLVariables(b.Variables) is { } vars) o["variables"] = vars;
        }
        else if (BodyTypes.IsForm(b.Type)) o["fields"] = WriteRows(b.Fields);
        else o["data"] = b.Data;
        return o;
    }

    /// <summary>
    /// GraphQL variables as stored: a real JSON object when they parse (readable in a diff), the text otherwise
    /// (<c>"id": {{ID}}</c> is not JSON until expanded), nothing when empty.
    /// </summary>
    private static JsonNode? GraphQLVariables(string text)
    {
        if (text.Trim().Length == 0) return null;
        try
        {
            var node = CanonicalJson.Parse(text);
            return node is JsonObject { Count: 0 } ? null : node;
        }
        catch (System.Text.Json.JsonException) { return text; }
    }

    // ------------------------------------------------------------ settings

    private static readonly HashSet<string> ConfigKeys = new(StringComparer.Ordinal)
        { "name", "defaultEnvironment", "auth", "variables", "timeoutMs", "followRedirects", "verifyTls", "order" };

    public static WorkspaceConfig ReadConfig(JsonNode? node)
    {
        var o = node as JsonObject ?? new JsonObject();
        var auth = ReadAuth(o.Obj("auth"));
        if (auth.IsInherit) auth = AuthSpec.None(); // the collection has nothing above it
        return new WorkspaceConfig
        {
            Name = o.Str("name"),
            DefaultEnvironment = o.Str("defaultEnvironment"),
            Auth = auth,
            Variables = ReadMap(o.Obj("variables")),
            TimeoutMs = Math.Clamp(o.Int("timeoutMs", 30000), 100, 3_600_000),
            FollowRedirects = o.Bool("followRedirects", true),
            VerifyTls = o.Bool("verifyTls", true),
            Order = ReadNames(o.Arr("order")),
            Extra = Leftovers(o, ConfigKeys),
        };
    }

    public static JsonObject WriteConfig(WorkspaceConfig c)
    {
        var o = (JsonObject)c.Extra.DeepClone();
        o["name"] = c.Name;
        if (c.DefaultEnvironment.Length > 0) o["defaultEnvironment"] = c.DefaultEnvironment;
        o["auth"] = WriteAuth(c.Auth);
        o["variables"] = WriteMap(c.Variables);
        o["timeoutMs"] = c.TimeoutMs;
        o["followRedirects"] = c.FollowRedirects;
        o["verifyTls"] = c.VerifyTls;
        if (c.Order.Count > 0) o["order"] = WriteNames(c.Order);
        return o;
    }

    private static readonly HashSet<string> FolderKeys = new(StringComparer.Ordinal) { "auth", "variables", "order" };

    public static FolderConfig ReadFolder(JsonNode? node)
    {
        var o = node as JsonObject ?? new JsonObject();
        return new FolderConfig { Auth = ReadAuth(o.Obj("auth")), Variables = ReadMap(o.Obj("variables")), Order = ReadNames(o.Arr("order")), Extra = Leftovers(o, FolderKeys) };
    }

    public static JsonObject WriteFolder(FolderConfig f)
    {
        var o = (JsonObject)f.Extra.DeepClone();
        if (!f.Auth.IsInherit) o["auth"] = WriteAuth(f.Auth);
        if (f.Variables.Count > 0) o["variables"] = WriteMap(f.Variables);
        if (f.Order.Count > 0) o["order"] = WriteNames(f.Order);
        return o;
    }

    private static readonly HashSet<string> EnvKeys = new(StringComparer.Ordinal) { "name", "variables" };

    public static ApiEnvironment ReadEnvironment(JsonNode? node, string stem)
    {
        var o = node as JsonObject ?? new JsonObject();
        return new ApiEnvironment { Stem = stem, Name = o.Str("name", stem), Variables = ReadMap(o.Obj("variables")), Extra = Leftovers(o, EnvKeys) };
    }

    public static JsonObject WriteEnvironment(ApiEnvironment e)
    {
        var o = (JsonObject)e.Extra.DeepClone();
        o["name"] = e.DisplayName;
        o["variables"] = WriteMap(e.Variables);
        return o;
    }

    public static SortedDictionary<string, string> ReadMap(JsonObject? o)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (o is null) return map;
        foreach (var (k, v) in o) map[k] = v.Scalar() ?? v?.ToJsonString() ?? "";
        return map;
    }

    public static JsonObject WriteMap(IEnumerable<KeyValuePair<string, string>> map)
    {
        var o = new JsonObject();
        foreach (var (k, v) in map) if (k.Length > 0) o[k] = v;
        return o;
    }

    private static List<string> ReadNames(JsonArray? a) =>
        (a ?? new JsonArray()).Select(n => n.Scalar() ?? "").Where(n => n.Length > 0).ToList();

    private static JsonArray WriteNames(IEnumerable<string> names) =>
        new(names.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());

    private static JsonObject Leftovers(JsonObject o, HashSet<string> known)
    {
        var extra = new JsonObject();
        foreach (var (k, v) in o)
            if (!known.Contains(k)) extra[k] = v?.DeepClone();
        return extra;
    }
}
