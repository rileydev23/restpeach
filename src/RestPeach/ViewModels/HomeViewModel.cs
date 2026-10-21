using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RestPeach.Core;
using RestPeach.Services;
using RestPeach.Views;

namespace RestPeach.ViewModels;

/// <summary>The "Proyectos" tab: saved project folders, organised in collapsible groups.</summary>
public partial class HomeViewModel : TabViewModel
{
    private readonly ProjectStore _store;
    private readonly Action<ProjectEntry> _open;

    public override bool Closable => false;
    public ObservableCollection<HomeRow> Rows { get; } = new();

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _onlyFavorites;
    [ObservableProperty] private HomeRow? _selectedRow;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _isEmpty;

    public ProjectEntry? Selected => (SelectedRow as ProjectRow)?.Entry;
    private AppState State => _store.State;
    private bool IsFiltering => OnlyFavorites || Filter.Trim().Length > 0;

    public HomeViewModel(ProjectStore store, Action<ProjectEntry> open)
    {
        _store = store;
        _open = open;
        Title = Loc.T("main.home");
        Reload();
        _ = RefreshKindsAsync();
    }

    partial void OnFilterChanged(string value) => Reload();
    partial void OnOnlyFavoritesChanged(bool value) => Reload();

    // ---------------------------------------------------------------- list

    public void Reload()
    {
        var keep = Selected;
        var q = Filter.Trim();
        bool filtering = IsFiltering;
        var sections = ProjectGrouping.Build(State,
            p => (!OnlyFavorites || p.Favorite) && Dialogs.Fuzzy(p.Name + " " + p.Path, q),
            hideEmpty: filtering);

        Rows.Clear();
        // With no groups at all the list stays flat.
        bool headers = State.Groups.Count > 0;
        foreach (var section in sections)
        {
            // While filtering every section is expanded, so matches are never hidden.
            bool collapsed = !filtering && (section.Group?.Collapsed ?? State.UngroupedCollapsed);
            if (headers)
            {
                var s = section;
                Rows.Add(new GroupHeaderRow
                {
                    Group = s.Group,
                    Count = s.Projects.Count,
                    IsCollapsed = collapsed,
                    ToggleCommand = new RelayCommand(() => ToggleGroup(s.Group)),
                    OpenAllCommand = new RelayCommand(() => OpenAll(s.Projects)),
                });
            }
            if (collapsed) continue;
            foreach (var p in section.Projects) Rows.Add(new ProjectRow { Entry = p, Indented = headers });
        }

        SelectedRow = keep is null ? null : Rows.OfType<ProjectRow>().FirstOrDefault(r => r.Entry == keep);
        IsEmpty = !Rows.OfType<ProjectRow>().Any() && !Rows.OfType<GroupHeaderRow>().Any(h => h.Count > 0);
    }

    public async Task RefreshKindsAsync()
    {
        var snapshot = State.Projects.ToList();
        await Task.Run(() =>
        {
            foreach (var p in snapshot)
            {
                p.Missing = !Directory.Exists(p.Path);
                p.Kind = p.Missing ? null : ProjectScanner.DetectKind(p.Path);
                p.Collection = p.Missing ? null : ProjectScanner.DescribeCollection(p.Path);
            }
        });
        Reload();
    }

    [RelayCommand]
    private async Task RefreshAsync() => await RefreshKindsAsync();

    // ---------------------------------------------------------------- groups

    public void ToggleGroup(ProjectGroup? group)
    {
        if (IsFiltering) return; // everything is expanded while filtering
        if (group is null) State.UngroupedCollapsed = !State.UngroupedCollapsed;
        else group.Collapsed = !group.Collapsed;
        _store.Save();
        Reload();
    }

    [RelayCommand]
    private void CollapseAll() => SetAllCollapsed(true);

    [RelayCommand]
    private void ExpandAll() => SetAllCollapsed(false);

    private void SetAllCollapsed(bool collapsed)
    {
        foreach (var g in State.Groups) g.Collapsed = collapsed;
        State.UngroupedCollapsed = collapsed;
        _store.Save();
        Reload();
    }

    private void OpenAll(IReadOnlyList<ProjectEntry> projects)
    {
        var existing = projects.Where(p => Directory.Exists(p.Path)).ToList();
        foreach (var p in existing) _open(p);
        Status = existing.Count == projects.Count
            ? Loc.F("home.opened", Plural(existing.Count))
            : Loc.F("home.openedSome", existing.Count, projects.Count);
    }

    [RelayCommand]
    private Task NewGroup() => MoveToNewGroup(Array.Empty<ProjectEntry>());

    private async Task MoveToNewGroup(IReadOnlyList<ProjectEntry> projects)
    {
        var name = await Dialogs.Prompt(Loc.T("home.newGroup.title"), Loc.T("home.newGroup.label"), ProjectGrouping.UniqueName(State, Loc.T("home.group.default")));
        if (string.IsNullOrWhiteSpace(name)) return;
        var group = ProjectGrouping.Ensure(State, name);
        group.Collapsed = false;
        MoveTo(projects, group);
    }

    private void MoveTo(IReadOnlyList<ProjectEntry> projects, ProjectGroup? group)
    {
        ProjectGrouping.Move(State, projects, group);
        _store.Save();
        Reload();
        if (projects.Count > 0)
            Status = group is null
                ? Loc.F("home.movedOut", Plural(projects.Count))
                : Loc.F("home.movedTo", Plural(projects.Count), group.Name);
    }

    private async Task RenameGroup(ProjectGroup group)
    {
        var name = await Dialogs.Prompt(Loc.T("home.renameGroup.title"), Loc.T("home.renameGroup.label"), group.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == group.Name) return;
        if (!ProjectGrouping.Rename(State, group, name))
        {
            await Dialogs.Message(Loc.T("home.renameGroup.title"), Loc.F("home.renameGroup.exists", name.Trim()));
            return;
        }
        _store.Save();
        Reload();
    }

    private async Task DeleteGroup(ProjectGroup group, bool removeProjects)
    {
        var count = ProjectGrouping.Members(State, group).Count();
        var message = removeProjects
            ? Loc.F("home.deleteGroup.withProjects", group.Name, Plural(count))
            : Loc.F("home.deleteGroup.keepProjects", group.Name, Plural(count));
        if (count > 0 && !await Dialogs.Confirm(Loc.T("home.deleteGroup.title"), message, Loc.T("home.deleteGroup.ok"))) return;
        ProjectGrouping.Delete(State, group, removeProjects);
        _store.Save();
        Reload();
    }

    private static string Plural(int n) => Loc.Plural("common.projects", n);

    // ---------------------------------------------------------------- menus

    public IReadOnlyList<MenuAction> MenuForGroup(GroupHeaderRow header)
    {
        var g = header.Group;
        var projects = ProjectGrouping.Members(State, g).ToList();
        var items = new List<MenuAction>
        {
            MenuAction.Sync(Loc.F("home.menu.openAll", projects.Count), () => OpenAll(projects)),
            MenuAction.Sync(header.IsCollapsed ? Loc.T("home.menu.expand") : Loc.T("home.menu.collapse"), () => ToggleGroup(g)),
        };
        if (g is not null)
        {
            items.Add(MenuAction.Separator);
            items.Add(new MenuAction(Loc.T("home.menu.renameGroup"), () => RenameGroup(g)));
            items.Add(new MenuAction(Loc.T("home.menu.deleteGroup"), () => DeleteGroup(g, removeProjects: false)));
            items.Add(new MenuAction(Loc.T("home.menu.removeGroup"), () => DeleteGroup(g, removeProjects: true)));
        }
        return MenuList.Clean(items);
    }

    public IReadOnlyList<MenuAction> MenuForProjects(IReadOnlyList<ProjectEntry> projects)
    {
        if (projects.Count == 0) return Array.Empty<MenuAction>();
        var single = projects.Count == 1 ? projects[0] : null;
        var items = new List<MenuAction>
        {
            single is not null
                ? MenuAction.Sync(Loc.T("common.open"), () => Open(single))
                : MenuAction.Sync(Loc.F("home.menu.openN", projects.Count), () => OpenAll(projects)),
        };
        if (single is not null)
        {
            items.Add(MenuAction.Sync(Loc.T("home.menu.favorite"), () => ToggleFavorite(single)));
            items.Add(new MenuAction(Loc.T("home.menu.rename"), () => RenameAsync(single)));
        }

        items.Add(MenuAction.Separator);
        var targets = new List<MenuAction>();
        foreach (var g in State.Groups)
        {
            var group = g;
            if (projects.All(p => ProjectGrouping.Find(State, p.Group) == group)) continue;
            targets.Add(MenuAction.Sync(group.Name, () => MoveTo(projects, group)));
        }
        if (targets.Count > 0) targets.Add(MenuAction.Separator);
        targets.Add(new MenuAction(Loc.T("home.menu.newGroup"), () => MoveToNewGroup(projects)));
        items.Add(MenuAction.Submenu(projects.Count == 1 ? Loc.T("home.menu.moveTo") : Loc.F("home.menu.moveNTo", projects.Count), targets));
        if (projects.Any(p => ProjectGrouping.Find(State, p.Group) is not null))
            items.Add(MenuAction.Sync(Loc.T("home.menu.ungroup"), () => MoveTo(projects, null)));

        if (single is not null)
        {
            items.Add(MenuAction.Separator);
            items.Add(new MenuAction(Loc.T("peach.openTextPeach"), () => PeachLink.OpenAsync(PeachLocator.TextPeach, single.Path, single.Path)));
            items.Add(new MenuAction(Loc.T("peach.openGitPeach"), () => PeachLink.OpenAsync(PeachLocator.GitPeach, single.Path, GitStatus.FindRepoRoot(single.Path) ?? single.Path)));
            items.Add(MenuAction.Sync(Loc.T("common.openDefault"), () => OpenWithDefault(single)));
        }
        items.Add(MenuAction.Separator);
        items.Add(MenuAction.Sync(single is not null ? Loc.T("home.menu.remove") : Loc.F("home.menu.removeN", projects.Count), () => RemoveMany(projects)));
        return MenuList.Clean(items);
    }

    // ---------------------------------------------------------------- projects

    [RelayCommand]
    private void Open(ProjectEntry? entry)
    {
        entry ??= Selected;
        if (entry is null) return;
        if (!Directory.Exists(entry.Path))
        {
            Status = Loc.F("home.folderGone", entry.Path);
            return;
        }
        _open(entry);
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var dir = await Dialogs.PickFolder(Loc.T("home.pickFolder"));
        if (dir is null) return;
        AddPath(dir, open: true);
    }

    public void AddPath(string dir, bool open)
    {
        if (!Directory.Exists(dir))
        {
            Status = Loc.F("home.notFolder", dir);
            return;
        }
        var entry = _store.Add(dir);
        Reload();
        SelectedRow = Rows.OfType<ProjectRow>().FirstOrDefault(r => r.Entry == entry);
        Status = Loc.F("home.added", entry.Name);
        _ = RefreshKindsAsync();
        if (open) _open(entry);
    }

    /// <summary>Dropped folder: a project is added as is; a folder that only holds projects becomes a group.</summary>
    public async Task AddOrImportAsync(string dir)
    {
        if (ProjectScanner.DetectKind(dir) is null && Core.Storage.Workspace.Find(dir) is null)
        {
            var inside = await Task.Run(() => ProjectScanner.FindProjects(dir, 4));
            if (inside.Count > 1) { await ImportFolderAsGroupAsync(dir); return; }
        }
        AddPath(dir, open: false);
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        var dir = await Dialogs.PickFolder(Loc.T("home.pickScan"));
        if (dir is not null) await ImportFolderAsGroupAsync(dir);
    }

    /// <summary>
    /// Finds the projects under <paramref name="dir"/> and puts them in a group named after the
    /// folder. Projects already in another group stay where they are.
    /// </summary>
    public async Task ImportFolderAsGroupAsync(string dir)
    {
        Busy = true;
        Status = Loc.T("home.scanning");
        try
        {
            var found = await Task.Run(() => ProjectScanner.FindProjects(dir, 4));
            if (found.Count == 0)
            {
                Status = Loc.F("home.noneFound", dir);
                return;
            }
            var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
            var group = ProjectGrouping.Ensure(State, string.IsNullOrEmpty(name) ? dir : name);
            group.Collapsed = false;

            int added = 0, kept = 0;
            foreach (var f in found)
            {
                var entry = _store.Find(f);
                if (entry is null) { entry = _store.Add(f); added++; }
                if (ProjectGrouping.Find(State, entry.Group) is { } other && other != group) { kept++; continue; }
                entry.Group = group.Name;
            }
            _store.Save();
            Reload();
            _ = RefreshKindsAsync();
            Status = Loc.F("home.scanned", group.Name, Plural(found.Count), added) +
                     (kept > 0 ? Loc.F("home.scanned.kept", kept) : "") + ".";
        }
        finally { Busy = false; }
    }

    public void RemoveMany(IReadOnlyList<ProjectEntry> projects)
    {
        foreach (var p in projects) _store.Remove(p);
        Reload();
        Status = projects.Count == 1
            ? Loc.F("home.removedOne", projects[0].Name)
            : Loc.F("home.removedMany", projects.Count);
    }

    [RelayCommand]
    private void ToggleFavorite(ProjectEntry? entry)
    {
        entry ??= Selected;
        if (entry is null) return;
        entry.Favorite = !entry.Favorite;
        _store.Save();
        Reload();
    }

    private async Task RenameAsync(ProjectEntry entry)
    {
        var name = await Dialogs.Prompt(Loc.T("home.rename.title"), Loc.T("home.rename.label"), entry.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        entry.Name = name.Trim();
        _store.Save();
        Reload();
    }

    private void OpenWithDefault(ProjectEntry entry)
    {
        try { Process.Start(new ProcessStartInfo(entry.Path) { UseShellExecute = true }); }
        catch (Exception ex) { Status = ex.Message; }
    }
}
