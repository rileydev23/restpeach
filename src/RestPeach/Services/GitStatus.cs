using System.Diagnostics;
using System.Text;

namespace RestPeach.Services;

/// <summary>How a path differs from the last commit, ordered by how much it should stand out in a folder.</summary>
public enum GitChange { None, Ignored, Untracked, Added, Renamed, Deleted, Modified, Conflict }

/// <summary>Branch and file states of the repository that contains a project, relative to the project folder.</summary>
public sealed class GitSnapshot
{
    public string Branch { get; init; } = "";
    public bool Detached { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public string RepoRoot { get; init; } = "";

    /// <summary>Files (project-relative, '/'-separated).</summary>
    public Dictionary<string, GitChange> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Folders reported as a whole ("dir/"), whose content shares the state: untracked or ignored.</summary>
    public List<(string Prefix, GitChange Change)> Folders { get; } = new();
    /// <summary>Strongest change below each folder, so collapsed folders still show there is something inside.</summary>
    public Dictionary<string, GitChange> Inside { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int ChangedCount => Files.Values.Count(c => c is not (GitChange.None or GitChange.Ignored))
                               + Folders.Count(f => f.Change == GitChange.Untracked);

    public GitChange For(string relPath, bool isDirectory)
    {
        if (Files.TryGetValue(relPath, out var c)) return c;
        foreach (var (prefix, change) in Folders)
        {
            if ((relPath + "/").StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return change;
        }
        return isDirectory && Inside.TryGetValue(relPath, out var inside) ? inside : GitChange.None;
    }
}

/// <summary>
/// Reads (never writes) the git state of a project: branch, ahead/behind and file states, like the
/// decorations of most editors. Uses <c>git --no-optional-locks status</c>, so not even the index is touched.
/// No git or no repository simply means no decorations.
/// </summary>
public static class GitStatus
{
    private static readonly Lazy<string?> GitExe = new(FindGit);

    public static bool IsAvailable => GitExe.Value is not null;

    /// <summary>The folder holding <c>.git</c> (a directory, or a file in worktrees and submodules) at or above <paramref name="dir"/>.</summary>
    public static string? FindRepoRoot(string dir)
    {
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var git = Path.Combine(d.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git)) return d.FullName;
        }
        return null;
    }

    public static async Task<GitSnapshot?> ReadAsync(string projectRoot, CancellationToken ct)
    {
        var exe = GitExe.Value;
        var repo = FindRepoRoot(projectRoot);
        if (exe is null || repo is null) return null;
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "--no-optional-locks", "-c", "core.quotepath=off", "status", "--porcelain=v1", "-z",
                     "--branch", "--untracked-files=normal", "--ignored=traditional" })
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return null;
            _ = p.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            string output;
            try
            {
                output = await p.StandardOutput.ReadToEndAsync(timeout.Token);
                await p.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(); } catch { }
                return null;
            }
            return p.ExitCode == 0 ? Parse(output, repo, projectRoot) : null;
        }
        catch { return null; }
    }

    /// <summary>Parses <c>git status --porcelain=v1 -z --branch</c> output; paths are made relative to <paramref name="projectRoot"/>.</summary>
    public static GitSnapshot Parse(string output, string repoRoot, string projectRoot)
    {
        var entries = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        string branch = "";
        bool detached = false;
        int ahead = 0, behind = 0;
        var prefix = Path.GetRelativePath(repoRoot, projectRoot).Replace('\\', '/');
        prefix = prefix == "." ? "" : prefix.TrimEnd('/') + "/";

        var files = new List<(string Path, GitChange Change)>();
        for (int i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            if (e.StartsWith("## "))
            {
                (branch, detached, ahead, behind) = ParseBranch(e[3..]);
                continue;
            }
            if (e.Length < 4) continue;
            char x = e[0], y = e[1];
            var path = e[3..];
            // Renames and copies are followed by the original path, which no longer matters here.
            if (x is 'R' or 'C') i++;
            files.Add((path, Classify(x, y)));
        }

        var snap = new GitSnapshot { Branch = branch, Detached = detached, Ahead = ahead, Behind = behind, RepoRoot = repoRoot };
        foreach (var (repoPath, change) in files)
        {
            if (!repoPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rel = repoPath[prefix.Length..];
            if (rel.Length == 0) continue;
            if (rel.EndsWith('/')) snap.Folders.Add((rel, change));
            else snap.Files[rel] = change;
            if (change == GitChange.Ignored) continue;
            // Every folder above the entry learns the strongest change it contains.
            var parts = rel.TrimEnd('/').Split('/');
            var dir = "";
            for (int k = 0; k < parts.Length - 1; k++)
            {
                dir = k == 0 ? parts[0] : dir + "/" + parts[k];
                if (!snap.Inside.TryGetValue(dir, out var old) || change > old) snap.Inside[dir] = change;
            }
        }
        return snap;
    }

    private static GitChange Classify(char x, char y)
    {
        if (x == '!' && y == '!') return GitChange.Ignored;
        if (x == '?' && y == '?') return GitChange.Untracked;
        if (x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D')) return GitChange.Conflict;
        if (x == 'D' || y == 'D') return GitChange.Deleted;
        if (x == 'R' || x == 'C') return y == 'M' ? GitChange.Modified : GitChange.Renamed;
        if (x == 'A') return y == 'M' ? GitChange.Modified : GitChange.Added;
        return GitChange.Modified;
    }

    /// <summary>"main...origin/main [ahead 1, behind 2]", "No commits yet on main", "HEAD (no branch)".</summary>
    private static (string Branch, bool Detached, int Ahead, int Behind) ParseBranch(string s)
    {
        int ahead = 0, behind = 0;
        var bracket = s.IndexOf(" [", StringComparison.Ordinal);
        if (bracket >= 0)
        {
            foreach (var part in s[(bracket + 2)..].TrimEnd(']').Split(", "))
            {
                var kv = part.Split(' ');
                if (kv.Length == 2 && int.TryParse(kv[1], out var n))
                {
                    if (kv[0] == "ahead") ahead = n;
                    else if (kv[0] == "behind") behind = n;
                }
            }
            s = s[..bracket];
        }
        foreach (var lead in new[] { "No commits yet on ", "Initial commit on " })
            if (s.StartsWith(lead, StringComparison.Ordinal)) s = s[lead.Length..];
        if (s.StartsWith("HEAD (no branch)", StringComparison.Ordinal)) return ("HEAD", true, ahead, behind);
        var dots = s.IndexOf("...", StringComparison.Ordinal);
        return (dots >= 0 ? s[..dots] : s, false, ahead, behind);
    }

    /// <summary>Short commit of a detached HEAD, read straight from the file (no process).</summary>
    public static string? ReadDetachedCommit(string repoRoot)
    {
        try
        {
            var git = Path.Combine(repoRoot, ".git");
            if (File.Exists(git))
            {
                var line = File.ReadAllText(git).Trim();
                if (!line.StartsWith("gitdir:")) return null;
                git = Path.GetFullPath(Path.Combine(repoRoot, line[7..].Trim()));
            }
            var head = File.ReadAllText(Path.Combine(git, "HEAD")).Trim();
            return head.Length >= 7 && !head.StartsWith("ref:") ? head[..7] : null;
        }
        catch { return null; }
    }

    private static string? FindGit()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "git.exe" } : new[] { "git" };
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var pf in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var root = Environment.GetFolderPath(pf);
            if (root.Length > 0) dirs.Add(Path.Combine(root, "Git", "cmd"));
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (local.Length > 0) dirs.Add(Path.Combine(local, "Programs", "Git", "cmd"));
        foreach (var dir in dirs)
            foreach (var n in names)
            {
                try
                {
                    var full = Path.Combine(dir.Trim('"'), n);
                    if (File.Exists(full)) return full;
                }
                catch { /* malformed PATH entry */ }
            }
        return null;
    }
}
