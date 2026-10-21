using System.Text.Json.Nodes;

namespace RestPeach.Core.Model;

/// <summary>A key/value row of a table (headers, query parameters, form fields). Inactive rows are kept but not sent.</summary>
public sealed class KeyValue
{
    public bool Active { get; set; } = true;
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    /// <summary>Form-data only: "file" sends <see cref="Value"/> as the path of a file; null/"text" sends it as text.</summary>
    public string? Type { get; set; }

    public bool IsFile => string.Equals(Type, "file", StringComparison.OrdinalIgnoreCase);
    public bool IsEmpty => Key.Length == 0 && Value.Length == 0;

    public KeyValue Clone() => new() { Active = Active, Key = Key, Value = Value, Type = Type };

    public static KeyValue Of(string key, string value, bool active = true) => new() { Key = key, Value = value, Active = active };
}

public static class AuthTypes
{
    /// <summary>Use the auth of the parent folder (or of the collection).</summary>
    public const string Inherit = "inherit";
    public const string None = "none";
    public const string Bearer = "bearer";
    public const string Basic = "basic";
    public const string ApiKey = "apikey";

    public static readonly string[] All = { Inherit, None, Bearer, Basic, ApiKey };
}

/// <summary>How a request (or a folder, for its children) authenticates.</summary>
public sealed class AuthSpec
{
    public string Type { get; set; } = AuthTypes.Inherit;
    public string Token { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>API key: header or query parameter name.</summary>
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    /// <summary>API key: "header" (default) or "query".</summary>
    public string In { get; set; } = "header";

    public bool IsInherit => Type == AuthTypes.Inherit;
    public AuthSpec Clone() => (AuthSpec)MemberwiseClone();

    public static AuthSpec None() => new() { Type = AuthTypes.None };
    public static AuthSpec BearerOf(string token) => new() { Type = AuthTypes.Bearer, Token = token };
}

public static class BodyTypes
{
    public const string None = "none";
    public const string Json = "json";
    public const string Text = "text";
    public const string Xml = "xml";
    public const string Html = "html";
    public const string FormData = "form-data";
    public const string UrlEncoded = "urlencoded";
    /// <summary>A GraphQL operation: <see cref="BodySpec.Data"/> is the query, <see cref="BodySpec.Variables"/> its variables (JSON).</summary>
    public const string GraphQL = "graphql";

    public static readonly string[] All = { None, Json, Text, Xml, Html, FormData, UrlEncoded, GraphQL };

    /// <summary>Bodies sent as the text of <see cref="BodySpec.Data"/> as is (GraphQL is wrapped in JSON first).</summary>
    public static bool IsRaw(string type) => type is Json or Text or Xml or Html;
    public static bool IsForm(string type) => type is FormData or UrlEncoded;

    /// <summary>Content-Type sent when the request does not set one itself.</summary>
    public static string? DefaultContentType(string type) => type switch
    {
        Json => "application/json",
        Text => "text/plain",
        Xml => "application/xml",
        Html => "text/html",
        UrlEncoded => "application/x-www-form-urlencoded",
        GraphQL => "application/json",
        _ => null,
    };
}

public sealed class BodySpec
{
    public string Type { get; set; } = BodyTypes.None;
    /// <summary>Raw bodies (json, text, xml, html); the query of a GraphQL body.</summary>
    public string Data { get; set; } = "";
    /// <summary>GraphQL only: the variables, as JSON text (may hold <c>{{placeholders}}</c>).</summary>
    public string Variables { get; set; } = "";
    /// <summary>Form bodies (form-data, urlencoded).</summary>
    public List<KeyValue> Fields { get; set; } = new();

    public BodySpec Clone() => new() { Type = Type, Data = Data, Variables = Variables, Fields = Fields.Select(f => f.Clone()).ToList() };
}

/// <summary>A WebSocket message kept with the request, ready to send again (the chat's "ping", "join"…).</summary>
public sealed class WsMessage
{
    public string Name { get; set; } = "";
    public string Data { get; set; } = "";

    public WsMessage Clone() => new() { Name = Name, Data = Data };
}

/// <summary>
/// One request, as stored in a <c>.req.json</c> / <c>.req.yaml</c> file. The URL holds the active query
/// parameters; parameters switched off in the Params table live in <see cref="DisabledParams"/> so they survive.
/// Keys this version does not know are kept in <see cref="Extra"/> and written back untouched.
/// </summary>
public sealed class ApiRequest
{
    public string Name { get; set; } = "";
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";
    public string Description { get; set; } = "";
    public List<KeyValue> Headers { get; set; } = new();
    public List<KeyValue> DisabledParams { get; set; } = new();
    public AuthSpec Auth { get; set; } = new();
    public BodySpec Body { get; set; } = new();
    /// <summary>Declarative assertions and captures, one per line (see <see cref="Testing.Assertions"/>).</summary>
    public List<string> Tests { get; set; } = new();
    /// <summary>Overrides the collection's "follow redirects" for this request only; null = use the collection's.</summary>
    public bool? FollowRedirects { get; set; }
    /// <summary>WebSocket only: saved messages. The message being written is the <see cref="Body"/>.</summary>
    public List<WsMessage> Messages { get; set; } = new();
    public JsonObject Extra { get; set; } = new();

    /// <summary>Not an HTTP method: a WebSocket connection (the handshake is a GET).</summary>
    public const string WebSocket = "WS";

    public static readonly string[] Methods = { "GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "HEAD", WebSocket };

    public ApiRequest Clone() => new()
    {
        Name = Name,
        Method = Method,
        Url = Url,
        Description = Description,
        Headers = Headers.Select(h => h.Clone()).ToList(),
        DisabledParams = DisabledParams.Select(h => h.Clone()).ToList(),
        Auth = Auth.Clone(),
        Body = Body.Clone(),
        Tests = Tests.ToList(),
        FollowRedirects = FollowRedirects,
        Messages = Messages.Select(m => m.Clone()).ToList(),
        Extra = (JsonObject)Extra.DeepClone(),
    };
}
