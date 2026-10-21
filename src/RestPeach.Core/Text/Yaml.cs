using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace RestPeach.Core.Text;

/// <summary>
/// YAML through the same JSON tree the rest of the code uses: <see cref="Parse"/> for <c>.req.yaml</c>, Insomnia and
/// OpenAPI files; <see cref="Write"/> emits deterministic YAML (sorted keys, block strings for multi-line text).
/// </summary>
public static class Yaml
{
    public static JsonNode? Parse(string text)
    {
        var stream = new YamlStream();
        using (var reader = new StringReader(text)) stream.Load(reader);
        return stream.Documents.Count == 0 ? null : Convert(stream.Documents[0].RootNode, 0);
    }

    private static JsonNode? Convert(YamlNode node, int depth)
    {
        if (depth > 200) throw new InvalidDataException("YAML nested too deeply");
        switch (node)
        {
            case YamlMappingNode map:
                var o = new JsonObject();
                foreach (var (k, v) in map.Children)
                {
                    var key = (k as YamlScalarNode)?.Value ?? k.ToString();
                    if (key == "<<" && v is YamlMappingNode merge)
                    {
                        // Merge keys (anchors in OpenAPI files): copy what is not set explicitly.
                        if (Convert(merge, depth + 1) is JsonObject m)
                            foreach (var (mk, mv) in m.ToList())
                                if (!o.ContainsKey(mk)) { m.Remove(mk); o[mk] = mv; }
                        continue;
                    }
                    o[key] = Convert(v, depth + 1);
                }
                return o;
            case YamlSequenceNode seq:
                var a = new JsonArray();
                foreach (var item in seq.Children) a.Add(Convert(item, depth + 1));
                return a;
            case YamlScalarNode s:
                return ScalarToNode(s);
            default:
                return null;
        }
    }

    private static JsonNode? ScalarToNode(YamlScalarNode s)
    {
        var v = s.Value ?? "";
        if (s.Style != ScalarStyle.Plain) return JsonValue.Create(v);
        switch (v)
        {
            case "" or "~" or "null" or "Null" or "NULL": return null;
            case "true" or "True" or "TRUE": return JsonValue.Create(true);
            case "false" or "False" or "FALSE": return JsonValue.Create(false);
        }
        if (long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
        if (LooksLikeFloat(v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d);
        return JsonValue.Create(v);
    }

    private static bool LooksLikeFloat(string v) =>
        v.Length > 0 && (char.IsDigit(v[0]) || v[0] is '-' or '+' or '.') && v.Any(char.IsDigit) && v.All(c => char.IsDigit(c) || c is '.' or 'e' or 'E' or '-' or '+');

    // ------------------------------------------------------------ writing

    public static string Write(JsonNode? node)
    {
        var sb = new StringBuilder();
        switch (node)
        {
            case JsonObject o when o.Count > 0: WriteObject(sb, o, 0); break;
            case JsonArray a when a.Count > 0: WriteArray(sb, a, 0); break;
            default: sb.Append(Inline(node)).Append('\n'); break;
        }
        return sb.ToString();
    }

    private static void WriteObject(StringBuilder sb, JsonObject o, int indent)
    {
        foreach (var (key, value) in o.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.Append(' ', indent).Append(Key(key)).Append(':');
            WriteValue(sb, value, indent);
        }
    }

    private static void WriteArray(StringBuilder sb, JsonArray a, int indent)
    {
        foreach (var item in a)
        {
            sb.Append(' ', indent).Append('-');
            if (item is JsonObject o && o.Count > 0)
            {
                // "- key: value" with the rest of the object aligned under the first key.
                var first = true;
                foreach (var (key, value) in o.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (first) { sb.Append(' '); first = false; }
                    else sb.Append(' ', indent + 2);
                    sb.Append(Key(key)).Append(':');
                    WriteValue(sb, value, indent + 2);
                }
            }
            else if (item is JsonArray inner && inner.Count > 0)
            {
                sb.Append('\n');
                WriteArray(sb, inner, indent + 2);
            }
            else WriteValue(sb, item, indent);
        }
    }

    private static void WriteValue(StringBuilder sb, JsonNode? value, int indent)
    {
        switch (value)
        {
            case JsonObject o when o.Count > 0:
                sb.Append('\n');
                WriteObject(sb, o, indent + 2);
                break;
            case JsonArray a when a.Count > 0:
                sb.Append('\n');
                WriteArray(sb, a, indent + 2);
                break;
            case JsonValue v when v.TryGetValue<string>(out var s) && IsBlockCandidate(s):
                sb.Append(s.EndsWith('\n') ? " |\n" : " |-\n");
                foreach (var line in s.TrimEnd('\n').Split('\n'))
                {
                    if (line.Length > 0) sb.Append(' ', indent + 2).Append(line);
                    sb.Append('\n');
                }
                break;
            default:
                sb.Append(' ').Append(Inline(value)).Append('\n');
                break;
        }
    }

    /// <summary>Multi-line text reads best as a literal block, unless its shape would not survive one.</summary>
    private static bool IsBlockCandidate(string s) =>
        s.Contains('\n') && !s.Contains('\r') && !s.Contains('\t') && !s.StartsWith(' ') && !s.EndsWith("\n\n")
        && !s.Split('\n').Any(l => l.EndsWith(' '));

    private static string Inline(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "{}",
        JsonArray => "[]",
        JsonValue v when v.TryGetValue<string>(out var s) => Str(s),
        _ => node.ToJsonString(),
    };

    private static string Key(string key) => NeedsQuotes(key) ? Quote(key) : key;

    private static string Str(string s) => NeedsQuotes(s) ? Quote(s) : s;

    private static bool NeedsQuotes(string s)
    {
        if (s.Length == 0 || s != s.Trim()) return true;
        if (s is "~" or "null" or "Null" or "NULL" or "true" or "True" or "TRUE" or "false" or "False" or "FALSE"
            or "yes" or "no" or "on" or "off" or "Yes" or "No" or "On" or "Off") return true;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return true;
        if ("-?:,[]{}#&*!|>'\"%@`".Contains(s[0])) return true;
        if (s.Contains(": ") || s.Contains(" #") || s.EndsWith(':')) return true;
        return s.Any(c => c < ' ' || c == '\u007f');
    }

    private static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ') sb.Append("\\x").Append(((int)c).ToString("x2"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
