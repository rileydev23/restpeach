using System.Text.Json.Nodes;

namespace RestPeach.Core.Model;

/// <summary><c>.api/config.json</c>: settings of the whole collection.</summary>
public sealed class WorkspaceConfig
{
    public string Name { get; set; } = "";
    /// <summary>File stem of the environment selected the first time ("local" for <c>local.env.json</c>).</summary>
    public string DefaultEnvironment { get; set; } = "";
    /// <summary>Auth inherited by every folder and request that says "inherit".</summary>
    public AuthSpec Auth { get; set; } = AuthSpec.None();
    /// <summary>Collection-wide variables (the lowest layer, below environments).</summary>
    public SortedDictionary<string, string> Variables { get; set; } = new(StringComparer.Ordinal);
    public int TimeoutMs { get; set; } = 30000;
    public bool FollowRedirects { get; set; } = true;
    public bool VerifyTls { get; set; } = true;
    public JsonObject Extra { get; set; } = new();
}

/// <summary><c>folder.json</c> inside a folder of requests: auth and variables for everything below it.</summary>
public sealed class FolderConfig
{
    public AuthSpec Auth { get; set; } = new();
    public SortedDictionary<string, string> Variables { get; set; } = new(StringComparer.Ordinal);
    public JsonObject Extra { get; set; } = new();

    public bool IsEmpty => Auth.IsInherit && Variables.Count == 0 && Extra.Count == 0;
}

/// <summary>An environment: public variables in <c>environments/&lt;stem&gt;.env.json</c>, secrets apart.</summary>
public sealed class ApiEnvironment
{
    /// <summary>"local" for <c>local.env.json</c>; also the key of its secrets.</summary>
    public string Stem { get; set; } = "";
    /// <summary>Display name; defaults to the stem.</summary>
    public string Name { get; set; } = "";
    public SortedDictionary<string, string> Variables { get; set; } = new(StringComparer.Ordinal);
    /// <summary>From <c>.env.secrets.json</c>, which git ignores.</summary>
    public SortedDictionary<string, string> Secrets { get; set; } = new(StringComparer.Ordinal);
    public JsonObject Extra { get; set; } = new();

    public string DisplayName => Name.Length > 0 ? Name : Stem;
}
