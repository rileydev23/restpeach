using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RestPeach.Controls;
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
    public ObservableCollection<TreeNode> Children { get; } = new();

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Foreground), nameof(StatusText), nameof(StatusBrush), nameof(HasStatus), nameof(StatusTip))]
    private GitChange _change;

    public bool IsRequest => !IsFolder;
    public string Glyph => IsFolder ? "■" : "";
    public string MethodTag => IsFolder ? "" : Look.MethodTag(Method);
    public IBrush MethodBrush => Look.Method(Method);
    public IBrush Foreground => GitLook.NameBrush(Change);
    public string StatusText => GitLook.Letter(Change, IsFolder);
    public IBrush StatusBrush => GitLook.Brush(Change);
    public bool HasStatus => StatusText.Length > 0;
    public string StatusTip => GitLook.Describe(Change) is { Length: > 0 } d ? $"{RelPath} · {d}" : RelPath;

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

    /// <summary>The method without parsing the whole file (the tree lists hundreds of them).</summary>
    public static string PeekMethod(string fullPath)
    {
        try
        {
            using var reader = new StreamReader(fullPath);
            var buffer = new char[8192];
            int n = reader.ReadBlock(buffer, 0, buffer.Length);
            var head = new string(buffer, 0, n);
            var m = (Workspace.IsYaml(fullPath) ? YamlMethod : JsonMethod).Match(head);
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : "GET";
        }
        catch { return ""; }
    }

    /// <summary>Builds the whole tree (collections are small; this runs off the UI thread).</summary>
    public static List<TreeNode> Build(Workspace ws, string relDir = "")
    {
        var list = new List<TreeNode>();
        foreach (var item in ws.ListChildren(relDir))
        {
            var node = new TreeNode
            {
                Name = item.Name,
                RelPath = item.RelPath,
                IsFolder = item.IsFolder,
                Method = item.IsFolder ? "" : PeekMethod(ws.FullPath(item.RelPath)),
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
