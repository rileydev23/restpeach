using System.Diagnostics;
using System.Text;
using RestPeach.Core;
using RestPeach.Core.Formats;
using RestPeach.Core.Http;
using RestPeach.Core.Storage;

namespace RestPeach.Cli;

/// <summary>
/// <c>restpeach-cli run [ruta] [--env nombre] [--bail]</c> and friends. Reads exactly the files the app writes, so a
/// collection that passes on your machine passes in the pipeline. Exit codes: 0 all passed, 1 something failed,
/// 2 bad usage or no collection.
/// </summary>
internal static class Program
{
    private static bool _color;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Loc.Init(Environment.GetEnvironmentVariable("RESTPEACH_LANG"));
        _color = !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;
        if (args.Contains("--no-color")) { _color = false; args = args.Where(a => a != "--no-color").ToArray(); }

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Loc.T("cli.help")); return args.Length == 0 ? 2 : 0; }
        if (args[0] is "--version" or "-v") { Console.WriteLine("restpeach-cli " + typeof(Program).Assembly.GetName().Version?.ToString(3)); return 0; }

        try
        {
            return args[0] switch
            {
                "run" => await RunAsync(args[1..]),
                "import" => Import(args[1..]),
                "export" => Export(args[1..]),
                "envs" => Envs(args[1..]),
                "init" => Init(args[1..]),
                _ => Usage(Loc.F("cli.unknownCommand", args[0])),
            };
        }
        catch (UsageException e) { return Usage(e.Message); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or YamlDotNet.Core.YamlException)
        {
            Console.Error.WriteLine(Paint(Loc.T("cli.error") + ": " + e.Message, Red));
            return 2;
        }
    }

    private sealed class UsageException(string message) : Exception(message);

    private static int Usage(string message)
    {
        Console.Error.WriteLine(Paint(message, Red));
        Console.Error.WriteLine(Loc.T("cli.seeHelp"));
        return 2;
    }

    // ------------------------------------------------------------ arguments

    private sealed class Args
    {
        public List<string> Positional { get; } = new();
        public Dictionary<string, string?> Options { get; } = new(StringComparer.Ordinal);

        public static Args Parse(string[] args, params string[] withValue)
        {
            var a = new Args();
            for (int i = 0; i < args.Length; i++)
            {
                var s = args[i];
                if (!s.StartsWith("--") && !(s.StartsWith('-') && s.Length == 2)) { a.Positional.Add(s); continue; }
                var eq = s.IndexOf('=');
                if (eq > 0) { a.Options[s[..eq]] = s[(eq + 1)..]; continue; }
                if (withValue.Contains(s))
                {
                    if (i + 1 >= args.Length) throw new UsageException(Loc.F("cli.missingValue", s));
                    a.Options[s] = args[++i];
                }
                else a.Options[s] = null;
            }
            return a;
        }

        public string? Get(params string[] names) => names.Select(n => Options.TryGetValue(n, out var v) ? v : null).FirstOrDefault(v => v is not null);
        public bool Has(params string[] names) => names.Any(Options.ContainsKey);
    }

    /// <summary>
    /// A path given on the command line: a project, its .api folder, a folder inside it or one request file.
    /// Returns the collection and what to run inside it ("" = everything).
    /// </summary>
    private static (Workspace Ws, string Rel, bool IsFile) Locate(string? path)
    {
        var full = Path.GetFullPath(path ?? Directory.GetCurrentDirectory());
        bool isFile = File.Exists(full);
        if (!isFile && !Directory.Exists(full)) throw new UsageException(Loc.F("cli.notFound", full));
        for (var dir = isFile ? Path.GetDirectoryName(full) : full; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (Workspace.Find(dir) is not { } ws) continue;
            var inside = full.StartsWith(ws.Root, StringComparison.OrdinalIgnoreCase);
            var rel = inside ? ws.RelPath(full) : "";
            return (ws, rel, isFile && inside);
        }
        throw new UsageException(Loc.F("cli.noCollection", full));
    }

    // ------------------------------------------------------------ run

    private static async Task<int> RunAsync(string[] raw)
    {
        var a = Args.Parse(raw, "--env", "-e", "--timeout", "--filter", "--var");
        var (ws, rel, isFile) = Locate(a.Positional.FirstOrDefault());
        var env = a.Get("--env", "-e");
        if (env is not null && !ws.ListEnvironments().Contains(env, StringComparer.OrdinalIgnoreCase))
            throw new UsageException(Loc.F("cli.noEnv", env, string.Join(", ", ws.ListEnvironments())));

        var runtime = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in raw.Select((s, i) => (s, i)).Where(x => x.s == "--var" && x.i + 1 < raw.Length).Select(x => raw[x.i + 1]))
        {
            var eq = v.IndexOf('=');
            if (eq <= 0) throw new UsageException(Loc.F("cli.badVar", v));
            runtime[v[..eq]] = v[(eq + 1)..];
        }

        var requests = isFile ? new List<string> { rel } : ws.ListRequests(rel);
        if (a.Get("--filter") is { } filter)
            requests = requests.Where(r => r.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (requests.Count == 0) { Console.WriteLine(Loc.T("cli.nothingToRun")); return 0; }

        var config = ws.LoadConfig();
        if (a.Get("--timeout") is { } t && int.TryParse(t, out var ms)) { config.TimeoutMs = ms; }
        using var engine = new HttpEngine();
        var runner = new Runner(ws, engine, env, runtime, config);
        bool verbose = a.Has("--verbose", "-V");

        var name = config.Name.Length > 0 ? config.Name : Path.GetFileName(ws.ProjectRoot);
        Console.WriteLine(Paint("RestPeach", Bold) + " · " + name + (runner.Environment is { } e2 ? " · " + Loc.F("cli.env", e2.DisplayName) : ""));
        Console.WriteLine();

        var sw = Stopwatch.StartNew();
        int passed = 0, failed = 0;
        int width = Math.Min(48, requests.Max(r => Display(r).Length) + 2);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, ev) => { ev.Cancel = true; cts.Cancel(); };
        try
        {
            await foreach (var r in runner.RunAllAsync(requests, a.Has("--bail"), cts.Token))
            {
                if (r.Passed) passed++; else failed++;
                PrintResult(r, width, verbose);
            }
        }
        catch (OperationCanceledException) { Console.WriteLine(Paint(Loc.T("cli.cancelled"), Amber)); failed++; }

        Console.WriteLine();
        var total = passed + failed;
        var summary = Loc.Plural("cli.requests", total) + " · " + Paint(Loc.Plural("cli.passed", passed), Green)
                      + (failed > 0 ? " · " + Paint(Loc.Plural("cli.failed", failed), Red) : "")
                      + " · " + ResponseFormatter.Duration(sw.Elapsed.TotalMilliseconds);
        Console.WriteLine(summary);
        return failed == 0 ? 0 : 1;
    }

    private static string Display(string rel) => Workspace.Join(Workspace.ParentOf(rel), Workspace.NameOf(rel));

    private static void PrintResult(RunResult r, int width, bool verbose)
    {
        var mark = r.Passed ? Paint("✓", Green) : Paint("✗", Red);
        var name = Display(r.RelPath).PadRight(width);
        if (r.Error is not null || r.Response?.Error is { } _)
        {
            Console.WriteLine($"  {mark} {name}{Paint(Loc.T("cli.error") + ": " + (r.Error ?? r.Response!.Error), Red)}");
            return;
        }
        var res = r.Response!;
        var status = $"{res.Status} {res.Reason}";
        var statusColor = res.Status < 300 ? Green : res.Status < 400 ? Amber : Red;
        Console.WriteLine($"  {mark} {name}{Paint(status.PadRight(22), statusColor)}{ResponseFormatter.Duration(res.ElapsedMs),9}  {Paint(ResponseFormatter.Size(res.Size), Dim)}");
        foreach (var x in r.Assertions)
        {
            if (x.Passed && !verbose) continue;
            var line = x.IsCapture
                ? Loc.F("cli.captured", x.CapturedName, Shorten(x.Actual ?? ""))
                : x.Error is not null ? x.Line + "  (" + x.Error + ")"
                : x.Passed ? x.Line : x.Line + "  " + Loc.F("cli.got", x.Actual ?? Loc.T("cli.nothing"));
            Console.WriteLine("      " + (x.Passed ? Paint("✓ " + line, Dim) : Paint("✗ " + line, Red)));
        }
        if (r.Assertions.Count == 0 && !r.Passed)
            Console.WriteLine("      " + Paint(Loc.T("cli.httpError"), Red));
    }

    private static string Shorten(string s) => s.Length <= 40 ? s : s[..37] + "...";

    // ------------------------------------------------------------ other commands

    private static int Import(string[] raw)
    {
        var a = Args.Parse(raw, "--project", "-p", "--into");
        var file = a.Positional.FirstOrDefault() ?? throw new UsageException(Loc.T("cli.importWhat"));
        if (!File.Exists(file)) throw new UsageException(Loc.F("cli.notFound", file));
        var project = a.Get("--project", "-p") ?? Directory.GetCurrentDirectory();
        var ws = Workspace.Open(project, create: true)!;
        var result = Importer.Import(File.ReadAllText(file), Path.GetFileName(file));
        var summary = Importer.Write(ws, result, a.Get("--into") ?? "");
        Console.WriteLine(Loc.F("cli.imported", result.Format, Loc.Plural("cli.requests", summary.Requests), ws.FullPath(summary.FolderRel)));
        foreach (var w in result.Warnings) Console.WriteLine(Paint("  ! " + w, Amber));
        return 0;
    }

    private static int Export(string[] raw)
    {
        var a = Args.Parse(raw, "-o", "--output");
        if (a.Positional.Count == 0) throw new UsageException(Loc.T("cli.exportWhat"));
        var format = a.Positional[0].ToLowerInvariant();
        var (ws, rel, isFile) = Locate(a.Positional.ElementAtOrDefault(1));
        if (isFile) rel = Workspace.ParentOf(rel);
        var text = format switch
        {
            "postman" => Exporter.ToPostman(ws, rel),
            "openapi" => Exporter.ToOpenApi(ws, rel),
            "bash" or "curl" or "sh" => Exporter.ToBash(ws, rel),
            _ => throw new UsageException(Loc.F("cli.badFormat", format)),
        };
        if (a.Get("-o", "--output") is { } output)
        {
            Workspace.WriteText(output, text);
            Console.WriteLine(Loc.F("cli.exported", Path.GetFullPath(output)));
        }
        else Console.Write(text);
        return 0;
    }

    private static int Envs(string[] raw)
    {
        var (ws, _, _) = Locate(Args.Parse(raw).Positional.FirstOrDefault());
        var def = ws.LoadConfig().DefaultEnvironment;
        foreach (var stem in ws.ListEnvironments())
        {
            var env = ws.LoadEnvironment(stem);
            Console.WriteLine($"{(stem == def ? "*" : " ")} {stem,-16} {env.DisplayName,-16} {Paint(Loc.F("cli.envCounts", env.Variables.Count, env.Secrets.Count), Dim)}");
        }
        return 0;
    }

    private static int Init(string[] raw)
    {
        var dir = Path.GetFullPath(Args.Parse(raw).Positional.FirstOrDefault() ?? Directory.GetCurrentDirectory());
        var existed = Workspace.Find(dir) is not null;
        var ws = Workspace.Init(dir, Path.GetFileName(dir));
        Console.WriteLine(existed ? Loc.F("cli.initExisted", ws.Root) : Loc.F("cli.initDone", ws.Root));
        return 0;
    }

    // ------------------------------------------------------------ color

    private const string Red = "31", Green = "32", Amber = "33", Dim = "90", Bold = "1";

    private static string Paint(string s, string code) => _color ? $"\u001b[{code}m{s}\u001b[0m" : s;
}
