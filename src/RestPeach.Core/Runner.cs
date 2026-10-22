using RestPeach.Core.Http;
using RestPeach.Core.Model;
using RestPeach.Core.Storage;
using RestPeach.Core.Testing;

namespace RestPeach.Core;

/// <summary>One request of a run: what was sent, what came back and how its tests went.</summary>
public sealed record RunResult(string RelPath, ApiRequest? Request, PreparedRequest? Sent, ApiResponse? Response,
    IReadOnlyList<AssertionResult> Assertions, string? Error)
{
    /// <summary>
    /// With tests: all of them pass. Without tests: a response arrived and it is not an HTTP error (status &lt; 400),
    /// which is what a CI pipeline expects from "run the collection"; a WebSocket must also not have been closed by
    /// the server with an error code (1011, 1008…).
    /// </summary>
    public bool Passed => Error is null && Response is { Error: null } r
                          && (Assertions.Count > 0 ? Assertions.All(a => a.Passed) : r.Status < 400 && !AbnormalClose(r.WebSocketClose));

    /// <summary>1000 (normal), 1001 (going away) and 1005 (no code) are clean; the rest say something went wrong.</summary>
    public static bool AbnormalClose(string? close) =>
        close is not null && !(close.StartsWith("1000") || close.StartsWith("1001") || close.StartsWith("1005"));

    public string Name => Request?.Name ?? Workspace.NameOf(RelPath);
}

/// <summary>
/// Runs requests of a collection exactly as the app sends them: same variables, same auth inheritance, same
/// assertions. Values captured with <c>set NAME = …</c> are kept in <see cref="Runtime"/> and seen by the requests
/// that follow (log in once, use the token everywhere).
/// </summary>
public sealed class Runner
{
    private readonly Workspace _ws;
    private readonly HttpEngine _engine;
    private readonly WorkspaceConfig _config;
    private readonly ApiEnvironment? _env;

    public Dictionary<string, string> Runtime { get; }

    /// <param name="config">Settings to use instead of the saved ones (the CLI's --timeout).</param>
    public Runner(Workspace ws, HttpEngine engine, string? environment, Dictionary<string, string>? runtime = null, WorkspaceConfig? config = null)
    {
        _ws = ws;
        _engine = engine;
        _config = config ?? ws.LoadConfig();
        var stem = environment ?? (_config.DefaultEnvironment.Length > 0 ? _config.DefaultEnvironment : null);
        _env = stem is not null && ws.ListEnvironments().Contains(stem, StringComparer.OrdinalIgnoreCase) ? ws.LoadEnvironment(stem) : null;
        Runtime = runtime ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public ApiEnvironment? Environment => _env;

    /// <summary>
    /// Expands a request (saved or still being edited) in the context of its folder. Used for sending and for the
    /// cURL preview (<paramref name="secretMask"/> hides secrets there).
    /// </summary>
    public static PreparedRequest Prepare(Workspace ws, WorkspaceConfig config, string relDir, ApiRequest req, ApiEnvironment? env,
        IReadOnlyDictionary<string, string>? runtime, Func<string, string>? secretMask = null)
    {
        var chain = ws.FolderChain(relDir);
        var scope = VariableScope.Build(config, chain, env, runtime);
        var auth = AuthResolver.Resolve(req.Auth, chain, config);
        return RequestPreparer.Prepare(req, auth, scope, ws.ProjectRoot, secretMask);
    }

    public static VariableScope Scope(Workspace ws, WorkspaceConfig config, string relDir, ApiEnvironment? env, IReadOnlyDictionary<string, string>? runtime) =>
        VariableScope.Build(config, ws.FolderChain(relDir), env, runtime);

    /// <summary>Sends <paramref name="req"/> and evaluates its tests (captures go to <see cref="Runtime"/>).</summary>
    public async Task<RunResult> SendAsync(string relPath, ApiRequest req, CancellationToken ct)
    {
        PreparedRequest prepared;
        try { prepared = Prepare(_ws, _config, Workspace.ParentOf(relPath), req, _env, Runtime); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new RunResult(relPath, req, null, null, Array.Empty<AssertionResult>(), e.Message); }
        var options = HttpOptions.For(_config, req, Scope(_ws, _config, Workspace.ParentOf(relPath), _env, Runtime), _ws.ProjectRoot);
        var response = prepared.Kind == ApiKind.WebSocket
            ? await WebSocketSession.ProbeAsync(prepared, options, _engine, ct)
            : await _engine.SendAsync(prepared, options, ct);
        var assertions = response.Error is null ? Assertions.Evaluate(req.Tests, response, Runtime) : new List<AssertionResult>();
        return new RunResult(relPath, req, prepared, response, assertions, null);
    }

    public async Task<RunResult> RunAsync(string relPath, CancellationToken ct)
    {
        ApiRequest req;
        try { req = _ws.LoadRequest(relPath); }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or YamlDotNet.Core.YamlException or UnauthorizedAccessException)
        {
            return new RunResult(relPath, null, null, null, Array.Empty<AssertionResult>(), e.Message);
        }
        return await SendAsync(relPath, req, ct);
    }

    /// <summary>Runs <paramref name="relPaths"/> in order; with <paramref name="bail"/>, stops after the first failure.</summary>
    public async IAsyncEnumerable<RunResult> RunAllAsync(IEnumerable<string> relPaths, bool bail,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var rel in relPaths)
        {
            ct.ThrowIfCancellationRequested();
            var r = await RunAsync(rel, ct);
            yield return r;
            if (bail && !r.Passed) yield break;
        }
    }
}
