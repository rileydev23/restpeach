using RestPeach.Core;

namespace RestPeach.Services;

/// <summary>One section of the project list: a group (or the ungrouped projects, when <see cref="Group"/> is null).</summary>
public sealed record ProjectSection(ProjectGroup? Group, IReadOnlyList<ProjectEntry> Projects);

/// <summary>Group operations over <see cref="AppState"/>. Pure: the caller decides when to save.</summary>
public static class ProjectGrouping
{
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    public static ProjectGroup? Find(AppState s, string? name)
        => name is null ? null : s.Groups.FirstOrDefault(g => Cmp.Equals(g.Name, name));

    /// <summary>The group with that name (case-insensitive), created at the end if missing.</summary>
    public static ProjectGroup Ensure(AppState s, string name)
    {
        name = name.Trim();
        if (Find(s, name) is { } g) return g;
        g = new ProjectGroup { Name = name };
        s.Groups.Add(g);
        return g;
    }

    /// <summary>"name", or "name (2)", "name (3)"… when taken.</summary>
    public static string UniqueName(AppState s, string baseName)
    {
        baseName = string.IsNullOrWhiteSpace(baseName) ? Loc.T("home.group.default") : baseName.Trim();
        var name = baseName;
        for (int i = 2; Find(s, name) is not null; i++) name = $"{baseName} ({i})";
        return name;
    }

    public static void Move(AppState s, IEnumerable<ProjectEntry> projects, ProjectGroup? group)
    {
        foreach (var r in projects) r.Group = group?.Name;
    }

    /// <summary>Renames a group and its members. False when another group already has that name.</summary>
    public static bool Rename(AppState s, ProjectGroup group, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0) return false;
        if (Find(s, newName) is { } other && !ReferenceEquals(other, group)) return false;
        foreach (var r in Members(s, group)) r.Group = newName;
        group.Name = newName;
        return true;
    }

    /// <summary>Deletes the group; its projects become ungrouped (or are removed from the list).</summary>
    public static void Delete(AppState s, ProjectGroup group, bool removeProjects)
    {
        var members = Members(s, group).ToList();
        if (removeProjects)
        {
            foreach (var r in members)
            {
                s.Projects.Remove(r);
                s.OpenTabs.RemoveAll(t => Cmp.Equals(t, r.Path));
                s.Sessions.Remove(r.Path);
            }
        }
        else Move(s, members, null);
        s.Groups.Remove(group);
    }

    public static IEnumerable<ProjectEntry> Members(AppState s, ProjectGroup? group)
        => group is null
            ? s.Projects.Where(r => Find(s, r.Group) is null)   // unknown group names count as ungrouped
            : s.Projects.Where(r => Cmp.Equals(r.Group, group.Name));

    /// <summary>
    /// Sections in display order: groups as stored, then the ungrouped projects. Projects are sorted by
    /// favorite, then most recently opened. Empty groups are kept unless <paramref name="hideEmpty"/>.
    /// </summary>
    public static List<ProjectSection> Build(AppState s, Func<ProjectEntry, bool> include, bool hideEmpty)
    {
        IReadOnlyList<ProjectEntry> Sorted(IEnumerable<ProjectEntry> projects) => projects.Where(include)
            .OrderByDescending(r => r.Favorite).ThenByDescending(r => r.LastOpened).ToList();

        var sections = new List<ProjectSection>();
        foreach (var g in s.Groups)
        {
            var projects = Sorted(Members(s, g));
            if (projects.Count > 0 || !hideEmpty) sections.Add(new ProjectSection(g, projects));
        }
        var ungrouped = Sorted(Members(s, null));
        if (ungrouped.Count > 0) sections.Add(new ProjectSection(null, ungrouped));
        return sections;
    }
}
