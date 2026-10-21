using TextMateSharp.Grammars;
using TextMateSharp.Internal.Themes.Reader;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace RestPeach.Services;

/// <summary>Bundled TextMate grammars plus our own "Peach Dark" token theme.</summary>
public sealed class Highlighting : IRegistryOptions
{
    public static Highlighting Instance { get; } = new();

    private readonly RegistryOptions _inner = new(ThemeName.DarkPlus);
    private readonly Lazy<IRawTheme> _theme = new(LoadTheme);

    public IRawTheme Theme => _theme.Value;

    private static IRawTheme LoadTheme()
    {
        using var s = typeof(Highlighting).Assembly.GetManifestResourceStream("peach-dark.json")!;
        using var r = new StreamReader(s);
        return ThemeReader.ReadThemeSync(r);
    }

    /// <summary>Grammar scope for a file, or null for plain text.</summary>
    public string? ScopeFor(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        string? ext = name switch
        {
            "dockerfile" => ".dockerfile",
            "makefile" or "gnumakefile" => ".mk",
            _ => Path.GetExtension(path).ToLowerInvariant(),
        };
        ext = ext switch
        {
            ".axaml" or ".xaml" or ".csproj" or ".fsproj" or ".vbproj" or ".props" or ".targets" or ".config" or ".resx" or ".nuspec" or ".manifest" or ".svg" or ".plist" => ".xml",
            ".jsonc" or ".json5" or ".code-workspace" => ".json",
            ".mjs" or ".cjs" => ".js",
            ".mts" or ".cts" => ".ts",
            ".csx" => ".cs",
            ".razor" or ".cshtml" => ".cshtml",
            ".mdx" => ".md",
            ".gradle" => ".groovy",
            ".mk" => ".makefile",
            _ => ext,
        };
        if (string.IsNullOrEmpty(ext)) return null;
        try
        {
            var lang = _inner.GetLanguageByExtension(ext);
            return lang is null ? null : _inner.GetScopeByLanguageId(lang.Id);
        }
        catch { return null; }
    }

    public IRawTheme GetTheme(string scopeName) => _inner.GetTheme(scopeName);
    public IRawGrammar GetGrammar(string scopeName) => _inner.GetGrammar(scopeName);
    public ICollection<string> GetInjections(string scopeName) => _inner.GetInjections(scopeName);
    public IRawTheme GetDefaultTheme() => Theme;
}
