using System.Text.Json;
using System.Text.Json.Serialization;

namespace RestPeach.Services;

public sealed class ProjectEntry
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Favorite { get; set; }
    public DateTimeOffset LastOpened { get; set; }
    /// <summary>Name of the <see cref="ProjectGroup"/> it belongs to; null = ungrouped.</summary>
    public string? Group { get; set; }
    /// <summary>Project type (".NET", "Node"…), detected on refresh.</summary>
    [JsonIgnore] public string? Kind { get; set; }
    /// <summary>"12 peticiones" when the project has a collection; null when it has none yet.</summary>
    [JsonIgnore] public string? Collection { get; set; }
    [JsonIgnore] public bool Missing { get; set; }
    [JsonIgnore] public bool HasCollection => Collection is not null;
}

public sealed class ProjectGroup
{
    public string Name { get; set; } = "";
    public bool Collapsed { get; set; }
}

/// <summary>What was open inside a project tab, restored with it.</summary>
public sealed class ProjectSession
{
    /// <summary>Open request files, relative to the collection root.</summary>
    public List<string> Files { get; set; } = new();
    public string? Active { get; set; }
    /// <summary>Selected environment (file stem); null = the collection's default.</summary>
    public string? Environment { get; set; }
    /// <summary>Selected environment was explicitly "none".</summary>
    public bool NoEnvironment { get; set; }
    public List<string> Expanded { get; set; } = new();
}

public sealed class AppState
{
    public List<ProjectEntry> Projects { get; set; } = new();
    /// <summary>Groups in display order.</summary>
    public List<ProjectGroup> Groups { get; set; } = new();
    public bool UngroupedCollapsed { get; set; }
    public List<string> OpenTabs { get; set; } = new();
    public string? ActiveTab { get; set; }
    public Dictionary<string, ProjectSession> Sessions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public double Width { get; set; }
    public double Height { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    public bool Maximized { get; set; }
    public double FontSize { get; set; } = 13;
    public bool WordWrap { get; set; }
    /// <summary>Re-indent JSON/XML responses (off = show the raw body).</summary>
    public bool PrettyResponses { get; set; } = true;
    /// <summary>Interface language: "en", "es" or null to follow Windows.</summary>
    public string? Language { get; set; }
}

[JsonSerializable(typeof(AppState))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class AppStateContext : JsonSerializerContext { }

/// <summary>
/// The app's own settings (<c>state.json</c>), which are about this machine: the project list, open tabs, window
/// placement. Everything about a collection lives in the project's <c>.api</c> folder instead.
/// </summary>
public sealed class ProjectStore
{
    private readonly string _file;
    private readonly object _lock = new();

    public AppState State { get; private set; } = new();
    public string DataFolder => System.IO.Path.GetDirectoryName(_file)!;

    public ProjectStore()
    {
        var dir = PeachApps.SettingsFolder("RestPeach");
        Directory.CreateDirectory(dir);
        _file = System.IO.Path.Combine(dir, "state.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_file))
                State = JsonSerializer.Deserialize(File.ReadAllText(_file), AppStateContext.Default.AppState) ?? new AppState();
        }
        catch
        {
            State = new AppState();
        }
        // The deserializer builds a case-sensitive dictionary; paths on Windows are not.
        State.Sessions = new Dictionary<string, ProjectSession>(State.Sessions, StringComparer.OrdinalIgnoreCase);
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                var tmp = _file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(State, AppStateContext.Default.AppState));
                File.Move(tmp, _file, true);
            }
            catch { /* best effort */ }
        }
    }

    public static string Normalize(string p) => System.IO.Path.GetFullPath(p).TrimEnd('\\', '/');

    public ProjectEntry? Find(string path)
        => State.Projects.FirstOrDefault(r => string.Equals(Normalize(r.Path), Normalize(path), StringComparison.OrdinalIgnoreCase));

    public ProjectEntry Add(string path)
    {
        var existing = Find(path);
        if (existing is not null) return existing;
        var norm = Normalize(path);
        var name = System.IO.Path.GetFileName(norm);
        var e = new ProjectEntry { Path = norm, Name = name.Length > 0 ? name : norm, LastOpened = DateTimeOffset.Now };
        State.Projects.Add(e);
        Save();
        return e;
    }

    public void Remove(ProjectEntry e)
    {
        State.Projects.Remove(e);
        State.OpenTabs.RemoveAll(t => string.Equals(t, e.Path, StringComparison.OrdinalIgnoreCase));
        State.Sessions.Remove(e.Path);
        Save();
    }

    public void Touch(ProjectEntry e)
    {
        e.LastOpened = DateTimeOffset.Now;
        Save();
    }

    public ProjectSession SessionFor(string path)
    {
        if (!State.Sessions.TryGetValue(path, out var s)) State.Sessions[path] = s = new ProjectSession();
        return s;
    }
}
