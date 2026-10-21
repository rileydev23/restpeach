using Avalonia.Media;
using RestPeach.Core;
using RestPeach.Services;

namespace RestPeach.Controls;

/// <summary>Brushes shared by code-built UI. Values from docs/DISENO.md.</summary>
public static class Palette
{
    private static SolidColorBrush B(string hex) => new(Color.Parse(hex));

    public static readonly IBrush Bg = B("#1e1f22");
    public static readonly IBrush Chrome = B("#18191c");
    public static readonly IBrush Sidebar = B("#222327");
    public static readonly IBrush Panel = B("#26272b");
    public static readonly IBrush Panel2 = B("#2d2e33");
    public static readonly IBrush Border = B("#3a3c42");
    public static readonly IBrush Code = B("#1b1c1f");
    public static readonly IBrush Text = B("#d4d4d4");
    public static readonly IBrush Dim = B("#8a8d93");
    public static readonly IBrush Accent = B("#4a90d9");
    public static readonly IBrush Selection = B("#33507a");
    public static readonly IBrush Transparent = Brushes.Transparent;

    public static readonly IBrush Blue = B("#4a90d9");
    public static readonly IBrush Amber = B("#e0a24a");
    public static readonly IBrush Green = B("#6cc070");
    public static readonly IBrush Pink = B("#d96a9b");
    public static readonly IBrush Purple = B("#9a7ae0");
    public static readonly IBrush Teal = B("#4ac7c0");
    public static readonly IBrush Red = B("#e0645a");
    public static readonly IBrush Olive = B("#c8c84a");

    public static readonly IBrush MatchBg = B("#5a4318");
    public static readonly IBrush MatchFg = B("#ffd27a");
    public static readonly IBrush FileBg = B("#34363c");

    // Filled badges (white text on top): darker steps of the semantic colors, like the ref badges of GitPeach.
    public static readonly IBrush OkBadge = B("#2f6f3e");
    public static readonly IBrush RedirectBadge = B("#2d5c8a");
    public static readonly IBrush ClientErrorBadge = B("#8a6d1f");
    public static readonly IBrush ServerErrorBadge = B("#8a2f2a");

    public static readonly Color[] Series =
    {
        Color.Parse("#4a90d9"), Color.Parse("#e0a24a"), Color.Parse("#6cc070"), Color.Parse("#d96a9b"),
        Color.Parse("#9a7ae0"), Color.Parse("#4ac7c0"), Color.Parse("#e0645a"), Color.Parse("#c8c84a"),
    };

    private static readonly Dictionary<string, IBrush> Cache = new();

    public static IBrush FromHex(string hex)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(hex, out var b)) Cache[hex] = b = B(hex);
            return b;
        }
    }
}

/// <summary>Colors with a fixed meaning in RestPeach (docs/DISENO.md, annex): methods, statuses, variable sources.</summary>
public static class Look
{
    /// <summary>HTTP methods are categories, so they take colors of the series; DELETE keeps red ("removes").</summary>
    public static IBrush Method(string method) => method.ToUpperInvariant() switch
    {
        "GET" => Palette.Green,
        "POST" => Palette.Amber,
        "PUT" => Palette.Blue,
        "PATCH" => Palette.Purple,
        "DELETE" => Palette.Red,
        "HEAD" or "OPTIONS" => Palette.Teal,
        _ => Palette.Dim,
    };

    /// <summary>"DEL" and "OPT" keep the tag column narrow in the tree.</summary>
    public static string MethodTag(string method) => method.ToUpperInvariant() switch
    {
        "DELETE" => "DEL",
        "OPTIONS" => "OPT",
        var m => m.Length > 5 ? m[..5] : m,
    };

    /// <summary>
    /// The tag of a request in the tree and the tabs: its API style when it has one ("GQL" says more than "POST",
    /// since every GraphQL operation is a POST), else its method.
    /// </summary>
    public static string Tag(string method, Core.Model.ApiKind kind) => Core.Model.ApiKinds.Tag(kind) ?? MethodTag(method);

    public static IBrush TagBrush(string method, Core.Model.ApiKind kind) => kind switch
    {
        Core.Model.ApiKind.GraphQL => Palette.Pink,
        Core.Model.ApiKind.JsonRpc => Palette.Olive,
        Core.Model.ApiKind.Soap => Palette.Teal,
        _ => Method(method),
    };

    public static IBrush Insight(Core.Http.InsightLevel level) => level switch
    {
        Core.Http.InsightLevel.Ok => Palette.Green,
        Core.Http.InsightLevel.Warning => Palette.Amber,
        Core.Http.InsightLevel.Error => Palette.Red,
        _ => Palette.Dim,
    };

    public static IBrush StatusBadge(int status, bool error) => error ? Palette.ServerErrorBadge : status switch
    {
        < 300 => Palette.OkBadge,
        < 400 => Palette.RedirectBadge,
        < 500 => Palette.ClientErrorBadge,
        _ => Palette.ServerErrorBadge,
    };

    public static IBrush StatusText(int status, bool error) => error ? Palette.Red : status switch
    {
        < 300 => Palette.Green,
        < 400 => Palette.Blue,
        < 500 => Palette.Amber,
        _ => Palette.Red,
    };

    public static IBrush Source(Core.Http.VariableSource s) => s switch
    {
        Core.Http.VariableSource.Collection => Palette.Dim,
        Core.Http.VariableSource.Folder => Palette.Amber,
        Core.Http.VariableSource.Environment => Palette.Blue,
        Core.Http.VariableSource.Secret => Palette.Purple,
        _ => Palette.Teal,
    };

    public static string SourceName(Core.Http.VariableSource s) => s switch
    {
        Core.Http.VariableSource.Collection => Loc.T("var.collection"),
        Core.Http.VariableSource.Folder => Loc.T("var.folder"),
        Core.Http.VariableSource.Environment => Loc.T("var.environment"),
        Core.Http.VariableSource.Secret => Loc.T("var.secret"),
        _ => Loc.T("var.runtime"),
    };
}

/// <summary>Colors and letters of git states, as most editors show them (same as TextPeach).</summary>
public static class GitLook
{
    public static IBrush Brush(GitChange c) => c switch
    {
        GitChange.Modified => Palette.Amber,
        GitChange.Untracked or GitChange.Added => Palette.Green,
        GitChange.Renamed => Palette.Teal,
        GitChange.Deleted => Palette.Red,
        GitChange.Conflict => Palette.Pink,
        GitChange.Ignored => Palette.Dim,
        _ => Palette.Text,
    };

    public static IBrush NameBrush(GitChange c) => c == GitChange.None ? Palette.Text : Brush(c);

    /// <summary>Files get the letter; folders a dot that says "something changed in here".</summary>
    public static string Letter(GitChange c, bool isDirectory) => c switch
    {
        GitChange.None or GitChange.Ignored => "",
        _ when isDirectory => "•",
        GitChange.Modified => "M",
        GitChange.Untracked => "U",
        GitChange.Added => "A",
        GitChange.Renamed => "R",
        GitChange.Deleted => "D",
        GitChange.Conflict => "!",
        _ => "",
    };

    public static string Describe(GitChange c) => c switch
    {
        GitChange.Modified => Loc.T("git.modified"),
        GitChange.Untracked => Loc.T("git.untracked"),
        GitChange.Added => Loc.T("git.added"),
        GitChange.Renamed => Loc.T("git.renamed"),
        GitChange.Deleted => Loc.T("git.deleted"),
        GitChange.Conflict => Loc.T("git.conflict"),
        GitChange.Ignored => Loc.T("git.ignored"),
        _ => "",
    };
}
