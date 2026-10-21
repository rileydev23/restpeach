using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RestPeach.Controls;
using RestPeach.Core.Model;
using RestPeach.Core.Storage;
using RestPeach.Services;

namespace RestPeach.ViewModels;

/// <summary>A folder or request of the collection tree. Mirrors the disk: a folder here is a directory there.</summary>
public sealed partial class TreeNode : ObservableObject
{
    public required string Name { get; init; }
    /// <summary>Relative to the collection root, '/'-separated.</summary>
    public required string RelPath { get; init; }
    public required bool IsFolder { get; init; }
    public string Method { get; init; } = "";
    public ApiKind Kind { get; init; } = ApiKind.Rest;
    public ObservableCollection<TreeNode> Children { get; } = new();

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Foreground), nameof(StatusText), nameof(StatusBrush), nameof(HasStatus), nameof(StatusTip))]
    private GitChange _change;

    public bool IsRequest => !IsFolder;
    public string Glyph => IsFolder ? "■" : "";
    public string MethodTag => IsFolder ? "" : Look.Tag(Method, Kind);
    public IBrush MethodBrush => Look.TagBrush(Method, Kind);
    public IBrush Foreground => GitLook.NameBrush(Change);
    public string StatusText => GitLook.Letter(Change, IsFolder);
    public IBrush StatusBrush => GitLook.Brush(Change);
    public bool HasStatus => StatusText.Length > 0;
    public string StatusTip => (IsFolder || Kind == ApiKind.Rest ? RelPath : $"{ApiKinds.Name(Kind)} · {Method} · {RelPath}")
                               + (GitLook.Describe(Change) is { Length: > 0 } d ? " · " + d : "");

    public IEnumerable<TreeNode> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.Descendants()) yield return d;
        }
    }

    private static readonly Regex JsonMethod = new("\"method\"\\s*:\\s*\"([A-Za-z]+)\"", RegexOptions.Compiled);
    private static readonly Regex YamlMethod = new(@"^method:\s*['""]?([A-Za-z]+)", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Method and API style of a request file. The whole file is read: keys are sorted, so "method" comes after
    /// "body", and a big body would hide it from a peek at the first bytes.
    /// </summary>
    public static (string Method, ApiKind Kind) Peek(string fullPath)
    {
        string text;
        try { text = File.ReadAllText(fullPath); }
        catch { return ("", ApiKind.Rest); }
        try
        {
            var r = Workspace.ParseRequest(text, fullPath);
            return (r.Method, ApiKinds.Of(r));
        }
        catch
        {
            // Broken file (it opens with an error banner): still show its method if it can be found.
            var m = (Workspace.IsYaml(fullPath) ? YamlMethod : JsonMethod).Match(text);
            return (m.Success ? m.Groups[1].Value.ToUpperInvariant() : "GET", ApiKind.Rest);
        }
    }

    /// <summary>Builds the whole tree (collections are small; this runs off the UI thread).</summary>
    public static List<TreeNode> Build(Workspace ws, string relDir = "")
    {
        var list = new List<TreeNode>();
        foreach (var item in ws.ListChildren(relDir))
        {
            var (method, kind) = item.IsFolder ? ("", ApiKind.Rest) : Peek(ws.FullPath(item.RelPath));
            var node = new TreeNode
            {
                Name = item.Name,
                RelPath = item.RelPath,
                IsFolder = item.IsFolder,
                Method = method,
                Kind = kind,
            };
            if (item.IsFolder) foreach (var c in Build(ws, item.RelPath)) node.Children.Add(c);
            list.Add(node);
        }
        return list;
    }
}

/// <summary>Flat result row shown while the tree filter has text.</summary>
public sealed class RequestHit
{
    public required TreeNode Node { get; init; }
    public string Name => Node.Name;
    public string Directory => Workspace.ParentOf(Node.RelPath);
    public string MethodTag => Node.MethodTag;
    public IBrush MethodBrush => Node.MethodBrush;
}

/// <summary>A variable of the right panel: where its value comes from and whether the current request uses it.</summary>
public sealed record VariableRow(string Name, string Value, string Source, IBrush SourceBrush, bool Used, bool Missing)
{
    public IBrush NameBrush => Missing ? Palette.Red : Used ? Palette.Text : Palette.Dim;
    public FontWeight Weight => Used ? FontWeight.SemiBold : FontWeight.Normal;
}

/// <summary>An environment in the picker; <see cref="Stem"/> is null for "no environment".</summary>
public sealed record EnvironmentChoice(string? Stem, string Name)
{
    public override string ToString() => Name;
}
