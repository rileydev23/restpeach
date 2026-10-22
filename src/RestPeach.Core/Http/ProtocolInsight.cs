using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using RestPeach.Core.Model;
using RestPeach.Core.Text;

namespace RestPeach.Core.Http;

public enum InsightLevel { Info, Ok, Warning, Error }

/// <summary>One line about the response in the terms of its API style ("GraphQL: 1 error — Not authorized").</summary>
public sealed record Insight(string Text, InsightLevel Level);

/// <summary>
/// Reads a response the way its protocol means it. A GraphQL error, a JSON-RPC error or a SOAP Fault often comes
/// with a 200, so the HTTP status alone says "all good" when it is not; a stream cut by a timeout looks like a
/// short body. These lines say so.
/// </summary>
public static class ProtocolInsight
{
    public static List<Insight> Describe(ApiKind kind, ApiResponse r)
    {
        var list = new List<Insight>();
        if (r.Error is not null) return list;
        foreach (var hop in r.Redirects)
            list.Add(new Insight(Loc.F("insight.redirect", hop.Status, hop.To, ResponseFormatter.Duration(hop.ElapsedMs)), InsightLevel.Info));
        try
        {
            var ct = (r.ContentType ?? "").ToLowerInvariant();
            var insight = kind switch
            {
                ApiKind.GraphQL => GraphQL(r),
                ApiKind.JsonRpc => JsonRpc(r),
                ApiKind.Soap => Soap(r),
                ApiKind.WebSocket => WebSocket(r),
                _ => null,
            };
            insight ??= ct.Contains("event-stream") ? Sse(r.Text)
                : ct.Contains("ndjson") || ct.Contains("jsonl") || ct.Contains("json-seq") ? Ndjson(r.Text)
                : ct.Contains("problem+json") ? Problem(r)
                : null;
            if (insight is not null) list.Add(insight);
        }
        catch (Exception e) when (e is JsonException or XmlException or InvalidOperationException or FormatException) { }

        if (r.Interrupted is { } why)
            list.Add(new Insight(Loc.F("insight.interrupted." + why, ResponseFormatter.Size(r.Size)), InsightLevel.Warning));
        return list;
    }

    // ------------------------------------------------------------ WebSocket

    /// <summary>A WebSocket of a collection run: how many messages came back and whether the server hung up.</summary>
    private static Insight WebSocket(ApiResponse r)
    {
        if (r.Status != 101) return new Insight(Loc.F("insight.ws.refused", r.Status), InsightLevel.Error);
        using var doc = TryJson(r.Text);
        int n = doc?.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
        var text = Loc.Plural("insight.ws.received", n);
        if (r.WebSocketClose is { } close)
            return new Insight(text + " · " + Loc.F("insight.ws.closedByServer", close), close.StartsWith("1000") ? InsightLevel.Info : InsightLevel.Warning);
        return new Insight(text, n > 0 ? InsightLevel.Ok : InsightLevel.Info);
    }

    // ------------------------------------------------------------ GraphQL

    private static Insight? GraphQL(ApiResponse r)
    {
        if (r.Body.Length == 0) return new Insight(Loc.T("insight.gql.empty"), InsightLevel.Warning);
        using var doc = TryJson(r.Text);
        if (doc is null) return new Insight(Loc.T("insight.gql.notJson"), InsightLevel.Warning);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            int n = root.GetArrayLength(), failed = root.EnumerateArray().Count(e => Errors(e).Count > 0);
            return new Insight(Loc.F("insight.gql.batch", n, failed), failed > 0 ? InsightLevel.Error : InsightLevel.Ok);
        }
        if (root.ValueKind != JsonValueKind.Object) return null;
        var errors = Errors(root);
        bool hasData = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object;
        if (errors.Count > 0)
        {
            var first = errors[0];
            var text = Loc.Plural("insight.gql.errors", errors.Count) + " — " + first.Message + (first.Code is { } c ? $" [{c}]" : "")
                       + (first.Path is { } p ? " · " + p : "")
                       + (hasData && data.EnumerateObject().Any(f => f.Value.ValueKind != JsonValueKind.Null) ? " · " + Loc.T("insight.gql.partial") : "");
            return new Insight(text, InsightLevel.Error);
        }
        if (hasData)
        {
            var fields = data.EnumerateObject().Select(f => f.Name).ToList();
            return new Insight(Loc.F("insight.gql.ok", fields.Count == 0 ? "—" : string.Join(", ", fields.Take(6)) + (fields.Count > 6 ? "…" : "")), InsightLevel.Ok);
        }
        return new Insight(Loc.T("insight.gql.noData"), InsightLevel.Warning);
    }

    private sealed record GqlError(string Message, string? Code, string? Path);

    private static List<GqlError> Errors(JsonElement e)
    {
        var list = new List<GqlError>();
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array) return list;
        foreach (var err in errors.EnumerateArray())
        {
            var message = err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m) ? m.ToString() : err.ToString();
            string? code = err.ValueKind == JsonValueKind.Object && err.TryGetProperty("extensions", out var ext) && ext.ValueKind == JsonValueKind.Object
                           && ext.TryGetProperty("code", out var c) ? c.ToString() : null;
            string? path = err.ValueKind == JsonValueKind.Object && err.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.Array
                ? string.Join('.', p.EnumerateArray().Select(x => x.ToString())) : null;
            list.Add(new GqlError(message, code, path));
        }
        return list;
    }

    // ------------------------------------------------------------ JSON-RPC

    private static Insight? JsonRpc(ApiResponse r)
    {
        if (r.Body.Length == 0)
            return new Insight(Loc.T(r.Status == 204 ? "insight.rpc.notification" : "insight.rpc.empty"), r.Status == 204 ? InsightLevel.Ok : InsightLevel.Warning);
        using var doc = TryJson(r.Text);
        if (doc is null) return new Insight(Loc.T("insight.rpc.notJson"), InsightLevel.Warning);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            int n = root.GetArrayLength(), failed = root.EnumerateArray().Count(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("error", out _));
            return new Insight(Loc.F("insight.rpc.batch", n, failed), failed > 0 ? InsightLevel.Error : InsightLevel.Ok);
        }
        if (root.ValueKind != JsonValueKind.Object) return null;
        var id = !root.TryGetProperty("id", out var idEl) ? "—" : idEl.ValueKind == JsonValueKind.Null ? "null" : idEl.ToString();
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : (int?)null;
            var message = error.TryGetProperty("message", out var m) ? m.ToString() : "";
            var meaning = code is { } k ? RpcMeaning(k) : null;
            return new Insight(Loc.F("insight.rpc.error", code?.ToString() ?? "?", message) + (meaning is not null && !message.Contains(meaning, StringComparison.OrdinalIgnoreCase) ? $" ({meaning})" : "")
                               + " · id " + id, InsightLevel.Error);
        }
        if (root.TryGetProperty("result", out _)) return new Insight(Loc.F("insight.rpc.ok", id), InsightLevel.Ok);
        return new Insight(Loc.T("insight.rpc.invalid"), InsightLevel.Warning);
    }

    /// <summary>The standard codes of JSON-RPC 2.0, section 5.1.</summary>
    private static string? RpcMeaning(int code) => code switch
    {
        -32700 => "Parse error",
        -32600 => "Invalid Request",
        -32601 => "Method not found",
        -32602 => "Invalid params",
        -32603 => "Internal error",
        <= -32000 and >= -32099 => "Server error",
        _ => null,
    };

    // ------------------------------------------------------------ SOAP

    private static Insight? Soap(ApiResponse r)
    {
        if (r.Body.Length == 0) return new Insight(Loc.T("insight.soap.empty"), InsightLevel.Warning);
        var doc = SafeXml(r.Text);
        if (doc?.Root is not { } root) return new Insight(Loc.T("insight.soap.notXml"), InsightLevel.Warning);
        if (root.Name.LocalName == "definitions")
        {
            var ops = root.Descendants().Where(e => e.Name.LocalName == "operation").Select(e => (string?)e.Attribute("name")).OfType<string>().Distinct().ToList();
            return new Insight(Loc.F("insight.soap.wsdl", ops.Count, string.Join(", ", ops.Take(6))), InsightLevel.Info);
        }
        var body = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Body");
        if (body is null) return new Insight(Loc.T("insight.soap.noEnvelope"), InsightLevel.Warning);
        var fault = body.Elements().FirstOrDefault(e => e.Name.LocalName == "Fault");
        if (fault is not null)
        {
            string Child(params string[] path)
            {
                XElement? cur = fault;
                foreach (var name in path) cur = cur?.Elements().FirstOrDefault(e => e.Name.LocalName == name);
                return cur?.Value.Trim() ?? "";
            }
            var code = Child("faultcode") is { Length: > 0 } c11 ? c11 : Child("Code", "Value");
            var reason = Child("faultstring") is { Length: > 0 } s11 ? s11 : Child("Reason", "Text");
            return new Insight(Loc.F("insight.soap.fault", code, reason), InsightLevel.Error);
        }
        var first = body.Elements().FirstOrDefault();
        return new Insight(Loc.F("insight.soap.ok", first?.Name.LocalName ?? "—"), InsightLevel.Ok);
    }

    /// <summary>No DTDs, no external entities: a response must never make the client read files.</summary>
    private static XDocument? SafeXml(string text)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(text), settings);
        return XDocument.Load(reader);
    }

    // ------------------------------------------------------------ streams

    /// <summary>Server-Sent Events: blocks separated by a blank line, with "event:" and "data:" fields.</summary>
    private static Insight Sse(string text)
    {
        var events = SseParser.Parse(text);
        var types = events.Select(e => e.Event).Distinct().Take(6).ToList();
        return new Insight(Loc.Plural("insight.sse", events.Count) + (types.Count > 0 ? " · " + string.Join(", ", types) : ""), InsightLevel.Info);
    }

    private static Insight Ndjson(string text)
    {
        var lines = text.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        int bad = 0;
        foreach (var line in lines)
        {
            using var doc = TryJson(line);
            if (doc is null) bad++;
        }
        return new Insight(Loc.Plural("insight.ndjson", lines.Count) + (bad > 0 ? " · " + Loc.Plural("insight.ndjson.bad", bad) : ""),
            bad > 0 ? InsightLevel.Warning : InsightLevel.Info);
    }

    // ------------------------------------------------------------ REST errors (RFC 7807)

    private static Insight? Problem(ApiResponse r)
    {
        using var doc = TryJson(r.Text);
        if (doc?.RootElement is not { ValueKind: JsonValueKind.Object } root) return null;
        string S(string name) => root.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "";
        var title = S("title");
        var detail = S("detail");
        var text = "Problem: " + (title.Length > 0 ? title : S("type")) + (detail.Length > 0 && detail != title ? " — " + detail : "");
        if (root.TryGetProperty("errors", out var errs) && errs.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            text += " · " + Loc.Plural("insight.problem.errors", errs.ValueKind == JsonValueKind.Array ? errs.GetArrayLength() : errs.EnumerateObject().Count());
        return new Insight(text, r.Status >= 500 ? InsightLevel.Error : r.Status >= 400 ? InsightLevel.Warning : InsightLevel.Info);
    }

    private static JsonDocument? TryJson(string text)
    {
        try { return JsonDocument.Parse(text, CanonicalJson.ReadOptions); }
        catch (JsonException) { return null; }
    }
}
