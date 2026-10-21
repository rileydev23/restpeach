using RestPeach.Core;
using RestPeach.Core.Storage;

namespace RestPeach.Services;

/// <summary>What a folder is: its project type and whether it already holds a collection.</summary>
public static class ProjectScanner
{
    public static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".vs", ".vscode", ".idea", ".gradle", ".next", ".nuxt", ".cache", ".parcel-cache",
        ".venv", "venv", "env", "__pycache__", ".pytest_cache", ".mypy_cache", ".tox",
        "node_modules", "bower_components", "bin", "obj", "dist", "build", "out", "target", "packages",
        "coverage", ".terraform", "$RECYCLE.BIN", "System Volume Information",
    };

    public static bool IsSkipped(string dirName) => SkipDirs.Contains(dirName);

    private static readonly (string Marker, string Kind)[] Markers =
    {
        ("*.sln", ".NET"), ("*.slnx", ".NET"), ("*.csproj", ".NET"), ("*.fsproj", ".NET"),
        ("package.json", "Node"), ("Cargo.toml", "Rust"), ("go.mod", "Go"),
        ("pyproject.toml", "Python"), ("requirements.txt", "Python"), ("setup.py", "Python"),
        ("pom.xml", "Java"), ("build.gradle", "Java"), ("build.gradle.kts", "Kotlin"),
        ("composer.json", "PHP"), ("Gemfile", "Ruby"), ("pubspec.yaml", "Flutter"),
        ("CMakeLists.txt", "C/C++"), ("Makefile", "Make"), ("index.html", "Web"),
    };

    /// <summary>A short label for the project type, from well-known marker files.</summary>
    public static string? DetectKind(string dir)
    {
        try
        {
            foreach (var (marker, kind) in Markers)
            {
                bool hit = marker.Contains('*')
                    ? Directory.EnumerateFiles(dir, marker).Any()
                    : File.Exists(Path.Combine(dir, marker));
                if (hit) return kind;
            }
            if (Directory.Exists(Path.Combine(dir, ".git"))) return "Git";
        }
        catch { }
        return null;
    }

    /// <summary>"12 requests" when <paramref name="dir"/> has a collection, else null.</summary>
    public static string? DescribeCollection(string dir)
    {
        try
        {
            var ws = Workspace.Find(dir);
            return ws is null ? null : Loc.Plural("common.requests", ws.CountRequests());
        }
        catch { return null; }
    }

    /// <summary>Folders under <paramref name="root"/> that have a collection or look like projects (not descending into them).</summary>
    public static List<string> FindProjects(string root, int maxDepth)
    {
        var result = new List<string>();
        void Walk(string dir, int depth)
        {
            if (depth > 0 && (Workspace.Find(dir) is not null || DetectKind(dir) is not null))
            {
                result.Add(dir);
                return;
            }
            if (depth >= maxDepth) return;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (IsSkipped(name) || name.StartsWith('.') || name.Equals(Workspace.AltApiFolder, StringComparison.OrdinalIgnoreCase)) continue;
                    Walk(sub, depth + 1);
                }
            }
            catch { /* inaccessible */ }
        }
        Walk(root, 0);
        return result;
    }
}
