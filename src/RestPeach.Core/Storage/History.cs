using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RestPeach.Core.Http;
using RestPeach.Core.Model;
using RestPeach.Core.Testing;

namespace RestPeach.Core.Storage;

/// <summary>
/// The history of sent requests, one JSON object per line: what was sent and what came back. It lives in the app's
/// own data folder (never in the collection, never in git). Secret values are masked before they are written, and a
/// big body is cut, so the file stays small and safe to keep.
/// </summary>
public static class History
{
    public const int MaxEntries = 200;
    public const int MaxBodyBytes = 256 * 1024;
    private const string Mask = "••••";

    public static string Serialize(RunResult r, IReadOnlyCollection<string> secrets)
    {
        string M(string s)
        {
            foreach (var secret in secrets) if (secret.Length >= 3) s = s.Replace(secret, Mask, StringComparison.Ordinal);
            return s;
        }
        var o = new JsonObject { ["path"] = r.RelPath };
        if (r.Request is { } req) o["request"] = Codec.WriteRequest(req);
        if (r.Sent is { } p)
        {
            var sent = new JsonObject
            {
                ["method"] = p.Method, ["url"] = M(p.Url), ["kind"] = p.Kind.ToString(), ["bodyType"] = p.BodyType, ["body"] = M(p.RawBody),
                ["headers"] = Pairs(p.Headers.Select(h => new KeyValuePair<string, string>(h.Key, M(h.Value)))),
            };
            if (p.Fields.Count > 0)
                sent["fields"] = new JsonArray(p.Fields.Select(f => (JsonNode)new JsonObject { ["key"] = f.Key, ["value"] = M(f.Value), ["file"] = f.IsFile }).ToArray());
            o["sent"] = sent;
        }
        if (r.Response is { } res)
        {
            var body = res.Body.Length > MaxBodyBytes ? res.Body[..MaxBodyBytes] : res.Body;
            var resp = new JsonObject
            {
                ["status"] = res.Status, ["reason"] = res.Reason, ["version"] = res.HttpVersion, ["url"] = M(res.FinalUrl),
                ["ms"] = Math.Round(res.ElapsedMs, 1), ["at"] = res.At.ToString("O"), ["truncated"] = res.Truncated || body.Length < res.Body.Length,
                ["headers"] = Pairs(res.Headers), ["body"] = Convert.ToBase64String(body),
            };
            if (res.Error is not null) resp["error"] = res.Error;
            if (res.Interrupted is not null) resp["interrupted"] = res.Interrupted;
            if (res.WebSocketClose is not null) resp["close"] = res.WebSocketClose;
            if (res.Redirects.Count > 0)
                resp["redirects"] = new JsonArray(res.Redirects.Select(h => (JsonNode)new JsonObject { ["status"] = h.Status, ["from"] = M(h.From), ["to"] = M(h.To), ["ms"] = Math.Round(h.ElapsedMs, 1) }).ToArray());
            o["response"] = resp;
        }
        if (r.Assertions.Count > 0)
            o["tests"] = new JsonArray(r.Assertions.Select(a => (JsonNode)new JsonObject
            {
                ["line"] = a.Line, ["passed"] = a.Passed, ["actual"] = a.Actual is null ? null : M(a.Actual), ["error"] = a.Error,
                ["capture"] = a.CapturedName,
            }).ToArray());
        if (r.Error is not null) o["error"] = r.Error;
        return o.ToJsonString();
    }

    public static RunResult? Deserialize(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is not JsonObject o) return null;
            var path = o["path"]?.GetValue<string>() ?? "";
            ApiRequest? request = o["request"] is JsonObject rq ? Codec.ReadRequest(rq, Workspace.NameOf(path)) : null;
            PreparedRequest? sent = null;
            if (o["sent"] is JsonObject s)
            {
                sent = new PreparedRequest
                {
                    Method = s.Str("method"), Url = s.Str("url"), BodyType = s.Str("bodyType", BodyTypes.None), RawBody = s.Str("body"),
                    Kind = Enum.TryParse<ApiKind>(s.Str("kind"), out var k) ? k : ApiKind.Rest,
                };
                foreach (var (key, value) in ReadPairs(s["headers"])) sent.Headers.Add(new(key, value));
                foreach (var f in (s["fields"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    sent.Fields.Add(new KeyValue { Key = f.Str("key"), Value = f.Str("value"), Type = f["file"]?.GetValue<bool>() == true ? "file" : null });
            }
            ApiResponse? response = null;
            if (o["response"] is JsonObject r)
            {
                response = new ApiResponse
                {
                    Status = r["status"]?.GetValue<int>() ?? 0, Reason = r.Str("reason"), HttpVersion = r.Str("version"), FinalUrl = r.Str("url"),
                    ElapsedMs = r["ms"]?.GetValue<double>() ?? 0, At = DateTimeOffset.TryParse(r.Str("at"), out var at) ? at : DateTimeOffset.MinValue,
                    Truncated = r["truncated"]?.GetValue<bool>() == true, Headers = ReadPairs(r["headers"]),
                    Body = Convert.FromBase64String(r.Str("body")), Error = r["error"]?.GetValue<string>(),
                    Interrupted = r["interrupted"]?.GetValue<string>(), WebSocketClose = r["close"]?.GetValue<string>(),
                    Redirects = (r["redirects"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                        .Select(h => new RedirectHop(h["status"]?.GetValue<int>() ?? 0, h.Str("from"), h.Str("to"), h["ms"]?.GetValue<double>() ?? 0)).ToList(),
                };
            }
            var tests = (o["tests"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Select(t => new AssertionResult(t.Str("line"), t["passed"]?.GetValue<bool>() == true, t["actual"]?.GetValue<string>(), t["error"]?.GetValue<string>(), t["capture"]?.GetValue<string>()))
                .ToList();
            return new RunResult(path, request, sent, response, tests, o["error"]?.GetValue<string>());
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or InvalidDataException) { return null; }
    }

    private static JsonArray Pairs(IEnumerable<KeyValuePair<string, string>> pairs) =>
        new(pairs.Select(h => (JsonNode)new JsonArray(h.Key, h.Value)).ToArray());

    private static List<KeyValuePair<string, string>> ReadPairs(JsonNode? node) =>
        (node as JsonArray ?? new JsonArray()).OfType<JsonArray>().Where(a => a.Count == 2)
            .Select(a => new KeyValuePair<string, string>(a[0]?.GetValue<string>() ?? "", a[1]?.GetValue<string>() ?? "")).ToList();

    private static string Str(this JsonObject o, string key, string fallback = "") => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;

    /// <summary>Reads a history file (newest last), skipping lines that do not parse.</summary>
    public static List<RunResult> Load(string file)
    {
        var list = new List<RunResult>();
        if (!File.Exists(file)) return list;
        foreach (var line in File.ReadLines(file, Encoding.UTF8))
            if (line.Length > 0 && Deserialize(line) is { } r) list.Add(r);
        return list;
    }
}
