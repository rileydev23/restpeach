using System.Text;
using System.Text.Json.Nodes;
using RestPeach.Core.Model;
using RestPeach.Core.Text;

namespace RestPeach.Core.Storage;

/// <summary>A folder or a request inside a collection, with its path relative to the collection root ('/'-separated).</summary>
public sealed record WorkspaceItem(string RelPath, string Name, bool IsFolder);

/// <summary>
/// A collection on disk: the <c>.api/</c> (or <c>api-client/</c>) folder of a project. The file system is the only
/// source of truth: folders are real directories, every request is one plain-text file, environments are JSON files
/// and secrets live in a file that git ignores. Nothing is cached here; callers re-read when the disk changes.
/// </summary>
public sealed class Workspace
{
    public const string ApiFolder = ".api";
    public const string AltApiFolder = "api-client";
    public const string ConfigFile = "config.json";
    public const string FolderFile = "folder.json";
    public const string EnvironmentsDir = "environments";
    public const string SecretsFile = ".env.secrets.json";
    public const string EnvSuffix = ".env.json";
    public const string JsonSuffix = ".req.json";
    public const string YamlSuffix = ".req.yaml";
    private const string YmlSuffix = ".req.yml";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The project folder (the one under git).</summary>
    public string ProjectRoot { get; }
    /// <summary>The collection folder: <c>&lt;project&gt;/.api</c>.</summary>
    public string Root { get; }

    private Workspace(string projectRoot, string root)
    {
        ProjectRoot = projectRoot;
        Root = root;
    }

    // ------------------------------------------------------------ locating

    /// <summary>
    /// The collection of <paramref name="dir"/>: its <c>.api</c> or <c>api-client</c> subfolder, or the folder itself
    /// when it already is one (has a config.json or an environments folder). Null when there is none yet.
    /// </summary>
    public static Workspace? Find(string dir)
    {
        dir = Path.GetFullPath(dir).TrimEnd('\\', '/');
        foreach (var name in new[] { ApiFolder, AltApiFolder })
        {
            var candidate = Path.Combine(dir, name);
            if (Directory.Exists(candidate)) return new Workspace(dir, candidate);
        }
        var self = Path.GetFileName(dir);
        if (self is ApiFolder or AltApiFolder || File.Exists(Path.Combine(dir, ConfigFile)) && Directory.Exists(Path.Combine(dir, EnvironmentsDir)))
            return new Workspace(Path.GetDirectoryName(dir) ?? dir, dir);
        return null;
    }

    /// <summary>Creates <c>.api/</c> with a config, a "local" environment and the ignore rule for secrets.</summary>
    public static Workspace Init(string projectDir, string name)
    {
        projectDir = Path.GetFullPath(projectDir).TrimEnd('\\', '/');
        var ws = Find(projectDir) ?? new Workspace(projectDir, Path.Combine(projectDir, ApiFolder));
        Directory.CreateDirectory(ws.Root);
        Directory.CreateDirectory(ws.EnvironmentsPath);
        if (!File.Exists(ws.ConfigPath))
            ws.SaveConfig(new WorkspaceConfig { Name = name, DefaultEnvironment = "local" });
        if (!ws.ListEnvironments().Any())
            ws.SaveEnvironment(new ApiEnvironment { Stem = "local", Name = "Local", Variables = { ["BASE_URL"] = "http://localhost:8080" } });
        ws.EnsureGitignore();
        ws.EnsureGitattributes();
        return ws;
    }

    /// <summary>
    /// The files are written with LF; this keeps git from converting them on Windows checkouts (core.autocrlf), so
    /// a diff never shows every line changed just because someone else's machine saved it.
    /// </summary>
    public void EnsureGitattributes()
    {
        var path = Path.Combine(Root, ".gitattributes");
        if (File.Exists(path)) return;
        try { WriteText(path, "# RestPeach writes these files with LF and sorted keys; keep them byte-for-byte in git.\n* text=auto eol=lf\n"); }
        catch (IOException) { }
    }

    /// <summary>Opens the collection of <paramref name="dir"/>, creating it when <paramref name="create"/>.</summary>
    public static Workspace? Open(string dir, bool create = false) =>
        Find(dir) ?? (create ? Init(dir, Path.GetFileName(Path.GetFullPath(dir).TrimEnd('\\', '/'))) : null);

    public string ConfigPath => Path.Combine(Root, ConfigFile);
    public string EnvironmentsPath => Path.Combine(Root, EnvironmentsDir);
    public string SecretsPath => Path.Combine(EnvironmentsPath, SecretsFile);

    public string FullPath(string rel) =>
        rel.Length == 0 ? Root : Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

    public string RelPath(string full)
    {
        var rel = Path.GetRelativePath(Root, full).Replace('\\', '/');
        return rel == "." ? "" : rel;
    }

    // ------------------------------------------------------------ names

    public static bool IsRequestFile(string fileName) =>
        fileName.EndsWith(JsonSuffix, StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(YamlSuffix, StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(YmlSuffix, StringComparison.OrdinalIgnoreCase);

    public static bool IsYaml(string path) =>
        path.EndsWith(YamlSuffix, StringComparison.OrdinalIgnoreCase) || path.EndsWith(YmlSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>"Users/Get User.req.json" → "Get User".</summary>
    public static string NameOf(string path)
    {
        var file = path[(path.LastIndexOfAny(new[] { '/', '\\' }) + 1)..];
        foreach (var suffix in new[] { JsonSuffix, YamlSuffix, YmlSuffix })
            if (file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return file[..^suffix.Length];
        return file;
    }

    public static string SuffixOf(string path) =>
        path.EndsWith(YamlSuffix, StringComparison.OrdinalIgnoreCase) ? YamlSuffix
        : path.EndsWith(YmlSuffix, StringComparison.OrdinalIgnoreCase) ? YmlSuffix
        : JsonSuffix;

    /// <summary>A name usable as a file or folder name on every OS (request names may contain "/" or ":").</summary>
    public static string SafeFileName(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
        var sb = new StringBuilder();
        foreach (var c in name.Trim()) sb.Append(invalid.Contains(c) || c < ' ' ? '-' : c);
        var s = sb.ToString().Trim().TrimEnd('.');
        if (s.Length > 120) s = s[..120].Trim();
        return s.Length == 0 ? "Request" : s;
    }

    public static string ParentOf(string rel)
    {
        var i = rel.LastIndexOf('/');
        return i < 0 ? "" : rel[..i];
    }

    public static string Join(string dir, string name) => dir.Length == 0 ? name : dir + "/" + name;

    // ------------------------------------------------------------ tree

    /// <summary>
    /// The items listed in the folder's <c>order</c> first, in that order (imports keep the order of the original
    /// collection); then the rest: folders before requests, each in natural order ("2." before "10.") ignoring case.
    /// Tool folders and settings are hidden.
    /// </summary>
    public List<WorkspaceItem> ListChildren(string relDir)
    {
        var list = new List<WorkspaceItem>();
        var dir = FullPath(relDir);
        if (!Directory.Exists(dir)) return list;
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(Path.GetFileName, NaturalComparer.Instance))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || (relDir.Length == 0 && name.Equals(EnvironmentsDir, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(new WorkspaceItem(Join(relDir, name), name, true));
            }
            foreach (var file in Directory.EnumerateFiles(dir).Where(f => IsRequestFile(f)).OrderBy(NameOf, NaturalComparer.Instance))
                list.Add(new WorkspaceItem(Join(relDir, Path.GetFileName(file)), NameOf(file), false));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var order = OrderOf(relDir);
        if (order.Count == 0) return list;
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < order.Count; i++) rank.TryAdd(order[i], i);
        int Rank(WorkspaceItem item) => rank.TryGetValue(item.RelPath[(item.RelPath.LastIndexOf('/') + 1)..], out var r) ? r : int.MaxValue;
        // OrderBy is stable: unlisted items keep their sorted place after the listed ones.
        return list.OrderBy(Rank).ToList();
    }

    /// <summary>The <c>order</c> of a folder (or, for the root, of config.json).</summary>
    public List<string> OrderOf(string relDir) => relDir.Length == 0 ? LoadConfig().Order : LoadFolder(relDir).Order;

    /// <summary>Writes the <c>order</c> of a folder; an empty list removes it.</summary>
    public void SaveOrder(string relDir, List<string> order)
    {
        if (relDir.Length == 0)
        {
            var c = LoadConfig();
            c.Order = order;
            SaveConfig(c);
        }
        else
        {
            var f = LoadFolder(relDir);
            f.Order = order;
            SaveFolder(relDir, f);
        }
    }

    /// <summary>
    /// Keeps the parent's <c>order</c> pointing at an item that was renamed (<paramref name="newName"/>) or moved out
    /// (null). Folders without an order are left alone.
    /// </summary>
    private void Reorder(string relDir, string oldName, string? newName)
    {
        try
        {
            var order = OrderOf(relDir);
            int i = order.FindIndex(n => n.Equals(oldName, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return;
            if (newName is null) order.RemoveAt(i);
            else order[i] = newName;
            SaveOrder(relDir, order);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string LeafOf(string rel) => rel[(rel.LastIndexOf('/') + 1)..];

    /// <summary>Every request under <paramref name="relDir"/> (depth-first, in tree order).</summary>
    public List<string> ListRequests(string relDir = "")
    {
        var result = new List<string>();
        void Walk(string d)
        {
            var children = ListChildren(d);
            foreach (var c in children.Where(c => !c.IsFolder)) result.Add(c.RelPath);
            foreach (var c in children.Where(c => c.IsFolder)) Walk(c.RelPath);
        }
        Walk(relDir);
        return result;
    }

    public int CountRequests() => ListRequests().Count;

    // ------------------------------------------------------------ requests

    public ApiRequest LoadRequest(string rel) => ParseRequest(File.ReadAllText(FullPath(rel)), rel);

    public static ApiRequest ParseRequest(string text, string relPath)
    {
        var node = IsYaml(relPath) ? Yaml.Parse(text) : CanonicalJson.Parse(text);
        return Codec.ReadRequest(node, NameOf(relPath));
    }

    public static string Serialize(ApiRequest r, string relPath)
    {
        var node = Codec.WriteRequest(r);
        return IsYaml(relPath) ? Yaml.Write(node) : CanonicalJson.Write(node);
    }

    public void SaveRequest(string rel, ApiRequest r) => WriteText(FullPath(rel), Serialize(r, rel));

    /// <summary>Creates a new request file in <paramref name="relDir"/> and returns its relative path.</summary>
    public string CreateRequest(string relDir, ApiRequest r, string suffix = JsonSuffix)
    {
        Directory.CreateDirectory(FullPath(relDir));
        var rel = UniqueFile(relDir, SafeFileName(r.Name), suffix);
        SaveRequest(rel, r);
        return rel;
    }

    public string CreateFolder(string relDir, string name)
    {
        var baseName = SafeFileName(name);
        var rel = Join(relDir, baseName);
        for (int i = 2; Directory.Exists(FullPath(rel)) || File.Exists(FullPath(rel)); i++) rel = Join(relDir, $"{baseName} ({i})");
        Directory.CreateDirectory(FullPath(rel));
        return rel;
    }

    /// <summary>"Login.req.json", or "Login (2).req.json"… when taken.</summary>
    public string UniqueFile(string relDir, string baseName, string suffix)
    {
        var rel = Join(relDir, baseName + suffix);
        for (int i = 2; File.Exists(FullPath(rel)); i++) rel = Join(relDir, $"{baseName} ({i}){suffix}");
        return rel;
    }

    /// <summary>Renames the file of a request (and its "name" field). Returns the new relative path.</summary>
    public string RenameRequest(string rel, string newName)
    {
        var r = LoadRequest(rel);
        r.Name = newName.Trim();
        var dir = ParentOf(rel);
        var suffix = SuffixOf(rel);
        var target = Join(dir, SafeFileName(newName) + suffix);
        if (!string.Equals(target, rel, StringComparison.OrdinalIgnoreCase) && File.Exists(FullPath(target)))
            target = UniqueFile(dir, SafeFileName(newName), suffix);
        SaveRequest(rel, r);
        if (!string.Equals(target, rel, StringComparison.Ordinal))
        {
            File.Move(FullPath(rel), FullPath(target));
            Reorder(dir, LeafOf(rel), LeafOf(target));
        }
        return target;
    }

    public string RenameFolder(string rel, string newName)
    {
        var target = Join(ParentOf(rel), SafeFileName(newName));
        if (string.Equals(target, rel, StringComparison.Ordinal)) return rel;
        if (!string.Equals(target, rel, StringComparison.OrdinalIgnoreCase) && Directory.Exists(FullPath(target)))
            throw new IOException(target);
        Directory.Move(FullPath(rel), FullPath(target));
        Reorder(ParentOf(rel), LeafOf(rel), LeafOf(target));
        return target;
    }

    public string DuplicateRequest(string rel)
    {
        var r = LoadRequest(rel);
        r.Name = r.Name + " (copy)";
        return CreateRequest(ParentOf(rel), r, SuffixOf(rel));
    }

    /// <summary>Moves a request or folder into <paramref name="targetDir"/>; returns its new relative path.</summary>
    public string Move(string rel, string targetDir, bool isFolder)
    {
        var name = rel[(rel.LastIndexOf('/') + 1)..];
        var target = Join(targetDir, name);
        if (string.Equals(target, rel, StringComparison.OrdinalIgnoreCase)) return rel;
        if (isFolder)
        {
            if ((targetDir + "/").StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase)) throw new IOException(targetDir);
            for (int i = 2; Directory.Exists(FullPath(target)); i++) target = Join(targetDir, $"{name} ({i})");
            Directory.Move(FullPath(rel), FullPath(target));
        }
        else
        {
            if (File.Exists(FullPath(target))) target = UniqueFile(targetDir, NameOf(name), SuffixOf(name));
            File.Move(FullPath(rel), FullPath(target));
        }
        Reorder(ParentOf(rel), name, null);
        return target;
    }

    // ------------------------------------------------------------ settings

    public WorkspaceConfig LoadConfig()
    {
        try { return File.Exists(ConfigPath) ? Codec.ReadConfig(CanonicalJson.Parse(File.ReadAllText(ConfigPath))) : new WorkspaceConfig(); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { return new WorkspaceConfig(); }
    }

    public void SaveConfig(WorkspaceConfig c) => WriteText(ConfigPath, CanonicalJson.Write(Codec.WriteConfig(c)));

    public FolderConfig LoadFolder(string relDir)
    {
        var path = Path.Combine(FullPath(relDir), FolderFile);
        try { return File.Exists(path) ? Codec.ReadFolder(CanonicalJson.Parse(File.ReadAllText(path))) : new FolderConfig(); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { return new FolderConfig(); }
    }

    public void SaveFolder(string relDir, FolderConfig f)
    {
        var path = Path.Combine(FullPath(relDir), FolderFile);
        if (f.IsEmpty) { if (File.Exists(path)) File.Delete(path); return; }
        WriteText(path, CanonicalJson.Write(Codec.WriteFolder(f)));
    }

    /// <summary>Folder settings from the collection root down to <paramref name="relDir"/> (outermost first).</summary>
    public List<FolderConfig> FolderChain(string relDir)
    {
        var chain = new List<FolderConfig>();
        var parts = relDir.Length == 0 ? Array.Empty<string>() : relDir.Split('/');
        var dir = "";
        for (int i = 0; i < parts.Length; i++)
        {
            dir = Join(dir, parts[i]);
            chain.Add(LoadFolder(dir));
        }
        return chain;
    }

    // ------------------------------------------------------------ environments

    public IEnumerable<string> ListEnvironments()
    {
        if (!Directory.Exists(EnvironmentsPath)) return Array.Empty<string>();
        return Directory.EnumerateFiles(EnvironmentsPath, "*" + EnvSuffix)
            .Select(f => Path.GetFileName(f)[..^EnvSuffix.Length])
            .Where(s => s.Length > 0 && !s.StartsWith('.'))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public ApiEnvironment LoadEnvironment(string stem)
    {
        var path = Path.Combine(EnvironmentsPath, stem + EnvSuffix);
        ApiEnvironment env;
        try { env = Codec.ReadEnvironment(File.Exists(path) ? CanonicalJson.Parse(File.ReadAllText(path)) : null, stem); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { env = new ApiEnvironment { Stem = stem, Name = stem }; }
        if (LoadSecrets().TryGetValue(stem, out var secrets)) env.Secrets = secrets;
        return env;
    }

    /// <summary>Writes the public file and, separately, this environment's entry of the secrets file.</summary>
    public void SaveEnvironment(ApiEnvironment env)
    {
        Directory.CreateDirectory(EnvironmentsPath);
        WriteText(Path.Combine(EnvironmentsPath, env.Stem + EnvSuffix), CanonicalJson.Write(Codec.WriteEnvironment(env)));
        var all = LoadSecrets();
        if (env.Secrets.Count == 0) all.Remove(env.Stem);
        else all[env.Stem] = env.Secrets;
        if (all.Count > 0 || File.Exists(SecretsPath)) SaveSecrets(all);
        EnsureGitignore();
    }

    public string CreateEnvironment(string name)
    {
        var baseStem = SafeFileName(name).ToLowerInvariant().Replace(' ', '-');
        var stem = baseStem;
        for (int i = 2; File.Exists(Path.Combine(EnvironmentsPath, stem + EnvSuffix)); i++) stem = $"{baseStem}-{i}";
        SaveEnvironment(new ApiEnvironment { Stem = stem, Name = name.Trim() });
        return stem;
    }

    public void DeleteEnvironment(string stem)
    {
        var path = Path.Combine(EnvironmentsPath, stem + EnvSuffix);
        if (File.Exists(path)) File.Delete(path);
        var all = LoadSecrets();
        if (all.Remove(stem)) SaveSecrets(all);
    }

    private Dictionary<string, SortedDictionary<string, string>> LoadSecrets()
    {
        var result = new Dictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(SecretsPath)) return result;
            if (CanonicalJson.Parse(File.ReadAllText(SecretsPath)) is JsonObject o)
                foreach (var (stem, vars) in o) result[stem] = Codec.ReadMap(vars as JsonObject);
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { }
        return result;
    }

    private void SaveSecrets(Dictionary<string, SortedDictionary<string, string>> all)
    {
        var o = new JsonObject();
        foreach (var (stem, vars) in all) o[stem] = Codec.WriteMap(vars);
        WriteText(SecretsPath, CanonicalJson.Write(o));
    }

    /// <summary>
    /// The collection keeps its own <c>.gitignore</c> (so the project root is never touched) with the secrets file in it.
    /// Returns true when the rule had to be added.
    /// </summary>
    public bool EnsureGitignore()
    {
        var path = Path.Combine(Root, ".gitignore");
        var rule = EnvironmentsDir + "/" + SecretsFile;
        try
        {
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim());
            if (lines.Any(l => l == rule || l == "/" + rule || l == SecretsFile || l == "**/" + SecretsFile)) return false;
            var sb = new StringBuilder(text);
            if (sb.Length > 0 && !text.EndsWith('\n')) sb.Append('\n');
            if (sb.Length == 0) sb.Append("# Secrets stay on this machine (see environments/*.env.json for the shared values)\n");
            sb.Append(rule).Append('\n');
            WriteText(path, sb.ToString());
            return true;
        }
        catch (IOException) { return false; }
    }

    // ------------------------------------------------------------ io

    /// <summary>UTF-8 without BOM, written to a temporary file first so a crash never leaves half a file.</summary>
    public static void WriteText(string path, string text)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, Utf8);
        File.Move(tmp, path, true);
    }
}
