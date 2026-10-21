using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RestPeach.Core.Model;

namespace RestPeach.Core.Http;

/// <summary>Where a variable's value came from, from lowest to highest priority.</summary>
public enum VariableSource { Collection, Folder, Environment, Secret, Runtime }

public sealed record VariableValue(string Name, string Value, VariableSource Source);

/// <summary>
/// The variables visible to one request, layered: collection &lt; folders (outer to inner) &lt; environment &lt;
/// secrets &lt; values captured by earlier requests in this session. <c>{{NAME}}</c> is replaced by the highest one.
/// </summary>
public sealed class VariableScope
{
    private readonly Dictionary<string, VariableValue> _values = new(StringComparer.Ordinal);

    public IReadOnlyCollection<VariableValue> All => _values.Values;

    public void Set(string name, string value, VariableSource source) => _values[name] = new VariableValue(name, value, source);

    public void SetAll(IEnumerable<KeyValuePair<string, string>> values, VariableSource source)
    {
        foreach (var (k, v) in values) if (k.Length > 0) Set(k, v, source);
    }

    public VariableValue? Get(string name) => _values.TryGetValue(name, out var v) ? v : null;

    public bool IsSecret(string name) => Get(name)?.Source == VariableSource.Secret;

    public static VariableScope Build(WorkspaceConfig config, IEnumerable<FolderConfig> folders, ApiEnvironment? env,
        IReadOnlyDictionary<string, string>? runtime)
    {
        var s = new VariableScope();
        s.SetAll(config.Variables, VariableSource.Collection);
        foreach (var f in folders) s.SetAll(f.Variables, VariableSource.Folder);
        if (env is not null)
        {
            s.SetAll(env.Variables, VariableSource.Environment);
            s.SetAll(env.Secrets, VariableSource.Secret);
        }
        if (runtime is not null) s.SetAll(runtime, VariableSource.Runtime);
        return s;
    }
}

/// <summary>Replaces <c>{{NAME}}</c> and the dynamic <c>{{$guid}}</c>, <c>{{$timestamp}}</c>… placeholders.</summary>
public static partial class Interpolator
{
    [GeneratedRegex(@"\{\{\s*([^{}\s][^{}]*?)\s*\}\}")]
    private static partial Regex Placeholder();

    public static readonly string[] Dynamic = { "$guid", "$timestamp", "$isoTimestamp", "$randomInt" };

    /// <summary>
    /// Expands <paramref name="text"/>. Values may reference other variables (up to a few levels). Names that do not
    /// exist are left as written and reported in <paramref name="missing"/>.
    /// </summary>
    public static string Expand(string text, VariableScope scope, ISet<string>? missing = null, Func<string, string>? secretMask = null)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("{{")) return text;
        return Expand(text, scope, missing, secretMask, 0);
    }

    private static string Expand(string text, VariableScope scope, ISet<string>? missing, Func<string, string>? mask, int depth)
    {
        return Placeholder().Replace(text, m =>
        {
            var name = m.Groups[1].Value;
            if (name.StartsWith('$') && DynamicValue(name) is { } dyn) return dyn;
            var v = scope.Get(name);
            if (v is null)
            {
                missing?.Add(name);
                return m.Value;
            }
            if (mask is not null && v.Source == VariableSource.Secret) return mask(v.Value);
            return depth < 5 && v.Value.Contains("{{") ? Expand(v.Value, scope, missing, mask, depth + 1) : v.Value;
        });
    }

    private static string? DynamicValue(string name) => name switch
    {
        "$guid" or "$uuid" or "$randomUUID" => Guid.NewGuid().ToString(),
        "$timestamp" => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        "$isoTimestamp" => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        "$randomInt" => Random.Shared.Next(0, 1001).ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>The variable names <paramref name="text"/> refers to, in order of first appearance.</summary>
    public static IEnumerable<string> Names(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Placeholder().Matches(text))
        {
            var n = m.Groups[1].Value;
            if (!n.StartsWith('$') && seen.Add(n)) yield return n;
        }
    }

    /// <summary><c>{{NAME}}</c> → <c>${NAME}</c>, for shell scripts that read the variables from the environment.</summary>
    public static string ToShell(string text) =>
        Placeholder().Replace(text, m => m.Groups[1].Value.StartsWith('$') ? m.Value : "${" + ShellName(m.Groups[1].Value) + "}");

    public static string ShellName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name) sb.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');
        if (sb.Length == 0 || char.IsAsciiDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }
}
