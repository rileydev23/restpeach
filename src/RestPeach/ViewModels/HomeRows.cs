using System.Windows.Input;
using RestPeach.Core;
using RestPeach.Services;

namespace RestPeach.ViewModels;

/// <summary>A row of the project list: a group header or a project.</summary>
public abstract class HomeRow;

public sealed class GroupHeaderRow : HomeRow
{
    /// <summary>Null for the "Sin grupo" section.</summary>
    public required ProjectGroup? Group { get; init; }
    public required int Count { get; init; }
    public required bool IsCollapsed { get; init; }
    public required ICommand ToggleCommand { get; init; }
    public required ICommand OpenAllCommand { get; init; }

    public string Name => Group?.Name ?? Loc.T("home.ungrouped");
    public string Chevron => IsCollapsed ? "▸" : "▾";
    public string CountText => Loc.Plural("common.projects", Count);
    public bool CanOpenAll => Count > 0;
}

public sealed class ProjectRow : HomeRow
{
    public required ProjectEntry Entry { get; init; }
    /// <summary>Projects inside a group are indented under its header.</summary>
    public required bool Indented { get; init; }
    public Avalonia.Thickness Pad => new(Indented ? 26 : 10, 8, 10, 8);
}
