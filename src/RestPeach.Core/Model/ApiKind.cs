using System.Text.RegularExpressions;

namespace RestPeach.Core.Model;

/// <summary>
/// The style of API a request talks to. Everything but WebSocket goes over plain HTTP; the kind only changes how the
/// request is shown (a GraphQL query is not "a POST") and what the response is checked for (GraphQL errors arrive
/// with a 200). A WebSocket is a connection that stays open and exchanges messages both ways.
/// </summary>
public enum ApiKind { Rest, GraphQL, JsonRpc, Soap, WebSocket }

public static partial class ApiKinds
{
    /// <summary>From the request alone, without sending it.</summary>
    public static ApiKind Of(ApiRequest r)
    {
        if (IsWebSocket(r)) return ApiKind.WebSocket;
        if (r.Body.Type == BodyTypes.GraphQL) return ApiKind.GraphQL;
        if (r.Body.Type == BodyTypes.Json && JsonRpc().IsMatch(Head(r.Body.Data))) return ApiKind.JsonRpc;
        if (r.Body.Type is BodyTypes.Xml or BodyTypes.Text && SoapEnvelope().IsMatch(Head(r.Body.Data))) return ApiKind.Soap;
        if (r.Headers.Any(h => h.Active && h.Key.Equals("SOAPAction", StringComparison.OrdinalIgnoreCase))) return ApiKind.Soap;
        // "GET /graphql?query={…}": a GraphQL query without a body.
        if (r.Body.Type == BodyTypes.None && GraphQLPath().IsMatch(r.Url)) return ApiKind.GraphQL;
        return ApiKind.Rest;
    }

    /// <summary>Method "WS", or a ws:// / wss:// URL written by hand with another method.</summary>
    public static bool IsWebSocket(ApiRequest r) =>
        r.Method.Equals(ApiRequest.WebSocket, StringComparison.OrdinalIgnoreCase) || IsWebSocketUrl(r.Url);

    public static bool IsWebSocketUrl(string url) =>
        url.TrimStart().StartsWith("ws://", StringComparison.OrdinalIgnoreCase) || url.TrimStart().StartsWith("wss://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Short tag for the tree and the tabs ("GQL"); null for REST, which shows its method.</summary>
    public static string? Tag(ApiKind k) => k switch
    {
        ApiKind.GraphQL => "GQL",
        ApiKind.JsonRpc => "RPC",
        ApiKind.Soap => "SOAP",
        ApiKind.WebSocket => "WS",
        _ => null,
    };

    public static string Name(ApiKind k) => k switch
    {
        ApiKind.GraphQL => "GraphQL",
        ApiKind.JsonRpc => "JSON-RPC",
        ApiKind.Soap => "SOAP",
        ApiKind.WebSocket => "WebSocket",
        _ => "REST",
    };

    private static string Head(string s) => s.Length > 4096 ? s[..4096] : s;

    [GeneratedRegex(@"""jsonrpc""\s*:\s*""2\.0""")]
    private static partial Regex JsonRpc();

    [GeneratedRegex(@"<(?:[\w-]+:)?Envelope[\s>]", RegexOptions.IgnoreCase)]
    private static partial Regex SoapEnvelope();

    [GeneratedRegex(@"/graphql/?(?:\?|#|$)", RegexOptions.IgnoreCase)]
    private static partial Regex GraphQLPath();
}
