using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RestPeach.Core.Testing;

/// <summary>
/// The small JSONPath subset assertions need: <c>$</c>, <c>.name</c>, <c>['na me']</c>, <c>[0]</c>, <c>[-1]</c> and the
/// virtual <c>.length</c> (items of an array, properties of an object, characters of a string).
/// </summary>
public static class JsonPath
{
    /// <summary>One step: a property name, an index, or <c>length</c>.</summary>
    private readonly record struct Step(string? Name, int? Index);

    private static List<Step>? Parse(string path, out string? error)
    {
        error = null;
        var steps = new List<Step>();
        var p = path.Trim();
        if (!p.StartsWith('$')) { error = "must start with $"; return null; }
        int i = 1;
        while (i < p.Length)
        {
            if (p[i] == '.')
            {
                int start = ++i;
                while (i < p.Length && p[i] != '.' && p[i] != '[') i++;
                if (i == start) { error = "empty name"; return null; }
                steps.Add(new Step(p[start..i], null));
            }
            else if (p[i] == '[')
            {
                int end = p.IndexOf(']', i);
                if (end < 0) { error = "missing ]"; return null; }
                var inner = p[(i + 1)..end].Trim();
                if (inner.Length >= 2 && (inner[0] is '\'' or '"') && inner[^1] == inner[0]) steps.Add(new Step(inner[1..^1], null));
                else if (int.TryParse(inner, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var idx)) steps.Add(new Step(null, idx));
                else { error = $"bad index [{inner}]"; return null; }
                i = end + 1;
            }
            else { error = $"unexpected '{p[i]}'"; return null; }
        }
        return steps;
    }

    /// <summary>Evaluates <paramref name="path"/>; <paramref name="length"/> is set when the last step was <c>.length</c>.</summary>
    public static bool TrySelect(JsonElement root, string path, out JsonElement value, out int? length, out string? error)
    {
        value = default;
        length = null;
        var steps = Parse(path, out error);
        if (steps is null) return false;
        var cur = root;
        for (int s = 0; s < steps.Count; s++)
        {
            var step = steps[s];
            bool last = s == steps.Count - 1;
            if (step.Index is int idx)
            {
                if (cur.ValueKind != JsonValueKind.Array) return false;
                int n = cur.GetArrayLength();
                if (idx < 0) idx += n;
                if (idx < 0 || idx >= n) return false;
                cur = cur[idx];
                continue;
            }
            var name = step.Name!;
            if (cur.ValueKind == JsonValueKind.Object && cur.TryGetProperty(name, out var child))
            {
                cur = child;
                continue;
            }
            if (last && name is "length" or "count" or "size")
            {
                length = cur.ValueKind switch
                {
                    JsonValueKind.Array => cur.GetArrayLength(),
                    JsonValueKind.Object => cur.EnumerateObject().Count(),
                    JsonValueKind.String => cur.GetString()!.Length,
                    _ => null,
                };
                if (length is null) return false;
                value = cur;
                return true;
            }
            return false;
        }
        value = cur;
        return true;
    }

    /// <summary>Text of a value as an assertion compares it: strings unquoted, the rest as JSON.</summary>
    public static string Text(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Null => "null",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => e.GetRawText(),
        _ => Compact(e),
    };

    private static string Compact(JsonElement e)
    {
        var raw = e.GetRawText();
        if (raw.Length <= 200) return raw;
        var sb = new StringBuilder(raw, 0, 200, 203);
        return sb.Append('…').ToString();
    }
}
