using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RestPeach.Core.Text;

/// <summary>
/// Deterministic JSON for files that live in git: object keys sorted (ordinal), two-space indent, LF line endings,
/// readable accents and a final newline. Saving the same data twice always produces the same bytes, so a diff only
/// shows real changes.
/// </summary>
public static class CanonicalJson
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>For one value (a JSON string literal) with the same readable escaping as the files.</summary>
    public static readonly JsonSerializerOptions StringOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static JsonNode? Parse(string text) => JsonNode.Parse(text, documentOptions: ReadOptions);

    public static string Write(JsonNode? node)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, WriterOptions)) WriteNode(w, node);
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    private static void WriteNode(Utf8JsonWriter w, JsonNode? node)
    {
        switch (node)
        {
            case null:
                w.WriteNullValue();
                break;
            case JsonObject o:
                w.WriteStartObject();
                foreach (var (key, value) in o.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    w.WritePropertyName(key);
                    WriteNode(w, value);
                }
                w.WriteEndObject();
                break;
            case JsonArray a:
                w.WriteStartArray();
                foreach (var item in a) WriteNode(w, item);
                w.WriteEndArray();
                break;
            default:
                node.WriteTo(w);
                break;
        }
    }

    /// <summary>Re-indents arbitrary JSON keeping its key order (response viewer). Null when it is not JSON.</summary>
    public static string? Pretty(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, ReadOptions);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, WriterOptions)) doc.WriteTo(w);
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch (JsonException) { return null; }
    }

    // ---- small helpers for reading loosely-typed documents (imports, settings)

    public static string Str(this JsonNode? node, string key, string fallback = "")
    {
        if (node is not JsonObject o || !o.TryGetPropertyValue(key, out var v) || v is null) return fallback;
        return v is JsonValue jv ? jv.ToString() : v.ToJsonString();
    }

    public static bool Bool(this JsonNode? node, string key, bool fallback = false)
    {
        if (node is not JsonObject o || !o.TryGetPropertyValue(key, out var v) || v is not JsonValue jv) return fallback;
        if (jv.TryGetValue<bool>(out var b)) return b;
        return jv.TryGetValue<string>(out var s) && bool.TryParse(s, out b) ? b : fallback;
    }

    public static int Int(this JsonNode? node, string key, int fallback = 0)
    {
        if (node is not JsonObject o || !o.TryGetPropertyValue(key, out var v) || v is not JsonValue jv) return fallback;
        if (jv.TryGetValue<int>(out var i)) return i;
        if (jv.TryGetValue<double>(out var d)) return (int)d;
        return jv.TryGetValue<string>(out var s) && int.TryParse(s, out i) ? i : fallback;
    }

    public static JsonObject? Obj(this JsonNode? node, string key) => (node as JsonObject)?[key] as JsonObject;
    public static JsonArray? Arr(this JsonNode? node, string key) => (node as JsonObject)?[key] as JsonArray;

    /// <summary>Text of a scalar node (numbers and booleans as written), or null.</summary>
    public static string? Scalar(this JsonNode? node) => node is JsonValue v ? v.ToString() : null;
}
