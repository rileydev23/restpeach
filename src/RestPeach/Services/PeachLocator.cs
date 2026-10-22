using System.Diagnostics;

namespace RestPeach.Services;

/// <summary>
/// Finds the other apps of the family so RestPeach can hand them work: GitPeach for everything git (the branch in
/// the status bar), TextPeach to read a request file or the project. Same rules as GitPeach's TextPeachLocator and
/// TextPeach's GitPeachLocator, for any app name; keep them in sync.
/// </summary>
public static class PeachLocator
{
    public const string GitPeach = "GitPeach";
    public const string TextPeach = "TextPeach";

    public static string RepoUrl(string app) => "https://github.com/rileydev23/" + app.ToLowerInvariant();

    /// <summary>
    /// Fallback locations (when the app has never announced itself) in priority order: next to RestPeach, a sibling
    /// folder named after the app, the usual per-user and per-machine install folders, then every folder in PATH.
    /// </summary>
    public static IEnumerable<string> Candidates(string app, string baseDir, string? localAppData, string? programFiles, string? pathVariable)
    {
        var exe = OperatingSystem.IsWindows() ? app + ".exe" : app;
        yield return Path.Combine(baseDir, exe);
        var parent = Path.GetDirectoryName(baseDir.TrimEnd('\\', '/'));
        if (parent is not null) yield return Path.Combine(parent, app, exe);
        if (!string.IsNullOrEmpty(localAppData)) yield return Path.Combine(localAppData, "Programs", app, exe);
        if (!string.IsNullOrEmpty(programFiles)) yield return Path.Combine(programFiles, app, exe);
        foreach (var dir in (pathVariable ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return Path.Combine(dir.Trim('"'), exe);
    }

    public static string? Find(string app, Func<string, bool> exists, string baseDir, string? localAppData, string? programFiles, string? pathVariable)
    {
        foreach (var c in Candidates(app, baseDir, localAppData, programFiles, pathVariable))
        {
            try { if (exists(c)) return c; } catch { /* malformed PATH entry */ }
        }
        return null;
    }

    /// <summary>Where the app last said it runs from (peachapps/&lt;App&gt;/location.json), else the usual folders.</summary>
    public static string? FindOnThisMachine(string app) => PeachApps.Locate(app) ?? Find(app, File.Exists,
        AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetEnvironmentVariable("PATH"));

    /// <summary>Launches <paramref name="app"/> with absolute paths. False when it is not on this machine.</summary>
    public static bool Open(string app, string workingDir, params string[] paths)
    {
        var exe = FindOnThisMachine(app);
        if (exe is null) return false;
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = workingDir };
            foreach (var p in paths) psi.ArgumentList.Add(p);
            Process.Start(psi);
            return true;
        }
        catch { return false; }
    }
}
