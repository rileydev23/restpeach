using System.Text.Json;

namespace RestPeach.Core.Http;

/// <summary>A field of a GraphQL type: its name, its type as written in SDL (<c>[User!]!</c>) and its arguments.</summary>
public sealed record GqlField(string Name, string Type, string? Description, IReadOnlyList<GqlField> Args, bool Deprecated)
{
    /// <summary>The named type under the list and non-null wrappers: "User" for <c>[User!]!</c>.</summary>
    public string NamedType => Type.Trim('[', ']', '!');
    public string Signature => (Args.Count > 0 ? "(" + string.Join(", ", Args.Select(a => a.Name + ": " + a.Type)) + ")" : "") + ": " + Type;
}

public sealed record GqlType(string Name, string Kind, string? Description, IReadOnlyList<GqlField> Fields, IReadOnlyList<string> EnumValues, IReadOnlyList<string> PossibleTypes);

/// <summary>
/// A GraphQL schema read from the server's introspection, and what the editor needs from it: which type the cursor is
/// in (by walking the query's braces) and what can be written there.
/// </summary>
public sealed class GraphQLSchema
{
    public const string IntrospectionQuery =
        "query IntrospectionQuery {\n  __schema {\n    queryType { name }\n    mutationType { name }\n    subscriptionType { name }\n" +
        "    types {\n      kind\n      name\n      description\n      fields(includeDeprecated: true) {\n        name\n        description\n        isDeprecated\n" +
        "        args { name description type { ...TypeRef } }\n        type { ...TypeRef }\n      }\n      inputFields { name description type { ...TypeRef } }\n" +
        "      enumValues(includeDeprecated: true) { name }\n      possibleTypes { name }\n    }\n  }\n}\n\n" +
        "fragment TypeRef on __Type {\n  kind\n  name\n  ofType { kind name ofType { kind name ofType { kind name ofType { kind name ofType { kind name } } } } }\n}\n";

    public string? QueryType { get; private init; }
    public string? MutationType { get; private init; }
    public string? SubscriptionType { get; private init; }
    public IReadOnlyDictionary<string, GqlType> Types { get; private init; } = new Dictionary<string, GqlType>();

    /// <summary>"12 types · 5 queries · 3 mutations".</summary>
    public string Summary => Loc.F("gql.schema.summary", Types.Count(t => !t.Key.StartsWith("__")),
        Fields(QueryType).Count, Fields(MutationType).Count);

    public IReadOnlyList<GqlField> Fields(string? type) =>
        type is not null && Types.TryGetValue(type, out var t) ? t.Fields : Array.Empty<GqlField>();

    /// <summary>Reads <c>{"data": {"__schema": …}}</c>; throws <see cref="FormatException"/> with the server's error otherwise.</summary>
    public static GraphQLSchema Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("__schema", out var schema))
        {
            var why = root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0
                && errors[0].TryGetProperty("message", out var m) ? m.GetString() : null;
            throw new FormatException(why ?? Loc.T("gql.schema.notSchema"));
        }
        string? Root(string key) => schema.TryGetProperty(key, out var t) && t.ValueKind == JsonValueKind.Object ? Str(t, "name") : null;
        var types = new Dictionary<string, GqlType>(StringComparer.Ordinal);
        foreach (var t in schema.GetProperty("types").EnumerateArray())
        {
            var name = Str(t, "name");
            if (name is null) continue;
            var fields = new List<GqlField>();
            foreach (var key in new[] { "fields", "inputFields" })
                if (t.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array)
                    fields.AddRange(list.EnumerateArray().Select(Field));
            var enums = t.TryGetProperty("enumValues", out var ev) && ev.ValueKind == JsonValueKind.Array
                ? ev.EnumerateArray().Select(e => Str(e, "name") ?? "").ToList() : new List<string>();
            var possible = t.TryGetProperty("possibleTypes", out var pt) && pt.ValueKind == JsonValueKind.Array
                ? pt.EnumerateArray().Select(e => Str(e, "name") ?? "").ToList() : new List<string>();
            types[name] = new GqlType(name, Str(t, "kind") ?? "", Str(t, "description"), fields, enums, possible);
        }
        return new GraphQLSchema { QueryType = Root("queryType"), MutationType = Root("mutationType"), SubscriptionType = Root("subscriptionType"), Types = types };
    }

    private static GqlField Field(JsonElement f)
    {
        var args = f.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(Field).ToList() : new List<GqlField>();
        return new GqlField(Str(f, "name") ?? "", TypeRef(f.GetProperty("type")), Str(f, "description"), args,
            f.TryGetProperty("isDeprecated", out var d) && d.ValueKind == JsonValueKind.True);
    }

    private static string TypeRef(JsonElement t) => Str(t, "kind") switch
    {
        "NON_NULL" => TypeRef(t.GetProperty("ofType")) + "!",
        "LIST" => "[" + TypeRef(t.GetProperty("ofType")) + "]",
        _ => Str(t, "name") ?? "?",
    };

    private static string? Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ------------------------------------------------------------ where the cursor is

    /// <summary>
    /// The type whose selection set holds <paramref name="caret"/>: null at the top level (outside any braces), where
    /// operations and fragments are written. Strings, comments and arguments are skipped.
    /// </summary>
    public string? TypeAt(string text, int caret)
    {
        var stack = new Stack<string?>();
        string? operation = null, condition = null, lastField = null;
        bool expectCondition = false;
        int i = 0, end = Math.Min(caret, text.Length);
        while (i < end)
        {
            char c = text[i];
            if (c == '#') { while (i < end && text[i] != '\n') i++; continue; }
            if (c == '"')
            {
                bool block = i + 2 < text.Length && text[i + 1] == '"' && text[i + 2] == '"';
                i += block ? 3 : 1;
                while (i < end && !(block ? i + 2 < text.Length && text[i] == '"' && text[i + 1] == '"' && text[i + 2] == '"' : text[i] == '"' || text[i] == '\n'))
                    i += text[i] == '\\' ? 2 : 1;
                i += block ? 3 : 1;
                continue;
            }
            if (c == '(')
            {
                int depth = 0;
                for (; i < end; i++)
                {
                    if (text[i] == '(') depth++;
                    else if (text[i] == ')' && --depth == 0) { i++; break; }
                }
                if (depth > 0) return ""; // inside arguments: no fields to offer
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < end && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                var word = text[start..i];
                int next = i;
                while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
                if (expectCondition) { condition = word; expectCondition = false; }
                else if (word == "on") expectCondition = true;
                else if (stack.Count == 0 && word is "query" or "mutation" or "subscription") operation = word;
                else if (stack.Count == 0 && word == "fragment") operation = "fragment";
                else if (next < text.Length && text[next] == ':' && stack.Count > 0) { } // an alias: the field follows
                else if (stack.Count > 0) lastField = word;
                continue;
            }
            if (c == '{')
            {
                string? type;
                if (condition is not null) type = condition;
                else if (stack.Count == 0) type = operation switch { "mutation" => MutationType, "subscription" => SubscriptionType, _ => QueryType };
                else type = stack.Peek() is { } parent && lastField is not null
                    ? Fields(parent).FirstOrDefault(f => f.Name == lastField)?.NamedType
                    : null;
                stack.Push(type);
                condition = null;
                lastField = null;
                if (stack.Count == 1) operation = null;
            }
            else if (c == '}')
            {
                if (stack.Count > 0) stack.Pop();
                lastField = null;
            }
            i++;
        }
        return stack.Count > 0 ? stack.Peek() ?? "" : null;
    }
}
