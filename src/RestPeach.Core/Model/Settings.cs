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
    /// <summary>"" (automatic: HTTP/1.1, or what the server offers), "1.1", "2" or "3".</summary>
    public string HttpVersion { get; set; } = "";
    /// <summary>"" (the system's, and HTTP(S)_PROXY), "none", or a URL such as <c>http://user:pass@proxy:8080</c>; may hold <c>{{variables}}</c>.</summary>
    public string Proxy { get; set; } = "";
    public ClientCertSpec ClientCert { get; set; } = new();
    /// <summary>Order of the items at the top of the collection (see <see cref="FolderConfig.Order"/>).</summary>
    public List<string> Order { get; set; } = new();
    public JsonObject Extra { get; set; } = new();
}

/// <summary>
/// A client certificate for mutual TLS: a .pfx/.p12, or a PEM certificate with its key. Paths are relative to the
/// collection; every field may hold <c>{{variables}}</c>, so the password can live in the environment's secrets.
/// </summary>
public sealed class ClientCertSpec
{
    public string Path { get; set; } = "";
    /// <summary>PEM only: the private key file (empty when it is in the same file as the certificate).</summary>
    public string Key { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>Only offered to this host (and its subdomains); empty = to every server that asks for one.</summary>
    public string Host { get; set; } = "";

    public bool IsEmpty => Path.Trim().Length == 0;
    public ClientCertSpec Clone() => (ClientCertSpec)MemberwiseClone();
}

/// <summary><c>folder.json</c> inside a folder of requests: auth and variables for everything below it.</summary>
public sealed class FolderConfig
{
    public AuthSpec Auth { get; set; } = new();
    public SortedDictionary<string, string> Variables { get; set; } = new(StringComparer.Ordinal);
    /// <summary>
    /// File and folder names in the order to show and run them ("Login.req.json", "Users"). Imports keep the order of
    /// the original collection this way (log in first, then use the token); names not listed follow, sorted.
    /// </summary>
    public List<string> Order { get; set; } = new();
    public JsonObject Extra { get; set; } = new();

    public bool IsEmpty => Auth.IsInherit && Variables.Count == 0 && Order.Count == 0 && Extra.Count == 0;
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
