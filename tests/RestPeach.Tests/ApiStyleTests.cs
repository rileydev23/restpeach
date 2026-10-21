using System.Text;
using RestPeach.Core;
using RestPeach.Core.Formats;
using RestPeach.Core.Http;
using RestPeach.Core.Model;
using RestPeach.Core.Storage;
using RestPeach.Core.Text;

namespace RestPeach.Tests;

/// <summary>GraphQL, JSON-RPC, SOAP and streams: how they are stored, sent, imported and explained.</summary>
public class ApiStyleTests
{
    private static ApiRequest Gql(string method = "POST") => new()
    {
        Name = "Products",
        Method = method,
        Url = "{{BASE}}/graphql",
        Body = new BodySpec { Type = BodyTypes.GraphQL, Data = "query P($n: Int) { products(limit: $n) { id } }", Variables = "{ \"n\": {{LIMIT}} }" },
    };

    private static VariableScope Scope() => VariableScope.Build(
        new WorkspaceConfig { Variables = { ["BASE"] = "https://api.test", ["LIMIT"] = "3" } }, new List<FolderConfig>(), null, null);

    [Fact]
    public void GraphQL_body_is_stored_as_query_and_variables()
    {
        var r = Gql();
        r.Body.Variables = "{\"n\": 3}";
        var text = Workspace.Serialize(r, "p.req.json");
        Assert.Contains("\"type\": \"graphql\"", text);
        Assert.Contains("\"query\": \"query P", text);
        Assert.Contains("\"variables\": {\n      \"n\": 3\n    }", text); // a real object, readable in a diff
        var back = Workspace.ParseRequest(text, "p.req.json");
        Assert.Equal(BodyTypes.GraphQL, back.Body.Type);
        Assert.Equal(r.Body.Data, back.Body.Data);
        Assert.Equal(text, Workspace.Serialize(back, "p.req.json"));

        // Variables with a placeholder outside quotes are not JSON yet: they stay text.
        var withVar = Workspace.Serialize(Gql(), "p.req.json");
        Assert.Contains("\"variables\": \"{ \\\"n\\\": {{LIMIT}} }\"", withVar);
        Assert.Equal("{ \"n\": {{LIMIT}} }", Workspace.ParseRequest(withVar, "p.req.json").Body.Variables);
    }

    [Fact]
    public void GraphQL_is_sent_as_json_or_in_the_query_string_for_GET()
    {
        var post = RequestPreparer.Prepare(Gql(), AuthSpec.None(), Scope());
        Assert.Equal(BodyTypes.Json, post.BodyType);
        Assert.Equal("application/json", post.Header("Content-Type"));
        Assert.Equal(ApiKind.GraphQL, post.Kind);
        var sent = System.Text.Json.JsonDocument.Parse(post.RawBody).RootElement;
        Assert.StartsWith("query P", sent.GetProperty("query").GetString());
        Assert.Equal(3, sent.GetProperty("variables").GetProperty("n").GetInt32());

        var get = RequestPreparer.Prepare(Gql("GET"), AuthSpec.None(), Scope());
        Assert.Equal(BodyTypes.None, get.BodyType);
        Assert.StartsWith("https://api.test/graphql?query=query%20P", get.Url);
        Assert.Contains("&variables=%7B%20%22n%22%3A%203%20%7D", get.Url);
        Assert.Null(get.Header("Content-Type"));
    }

    [Fact]
    public void Api_style_is_recognized_from_the_request()
    {
        Assert.Equal(ApiKind.GraphQL, ApiKinds.Of(Gql()));
        Assert.Equal(ApiKind.GraphQL, ApiKinds.Of(new ApiRequest { Url = "{{baseUrl}}/graphql?query={ hello }" }));
        Assert.Equal(ApiKind.JsonRpc, ApiKinds.Of(new ApiRequest { Method = "POST", Body = new BodySpec { Type = BodyTypes.Json, Data = "{ \"jsonrpc\": \"2.0\", \"method\": \"a\", \"id\": 1 }" } }));
        Assert.Equal(ApiKind.Soap, ApiKinds.Of(new ApiRequest { Method = "POST", Body = new BodySpec { Type = BodyTypes.Xml, Data = "<soap:Envelope xmlns:soap=\"x\"><soap:Body/></soap:Envelope>" } }));
        Assert.Equal(ApiKind.Rest, ApiKinds.Of(new ApiRequest { Url = "{{baseUrl}}/graphql-docs/page" }));
        Assert.Equal(ApiKind.Rest, ApiKinds.Of(new ApiRequest { Method = "POST", Body = new BodySpec { Type = BodyTypes.Json, Data = "{ \"query\": 1 }" } }));
        Assert.Equal("GQL", ApiKinds.Tag(ApiKind.GraphQL));
        Assert.Null(ApiKinds.Tag(ApiKind.Rest));
    }

    private static ApiResponse Response(string body, string contentType, int status = 200, string? interrupted = null) => new()
    {
        Status = status,
        Reason = "OK",
        Headers = { new("Content-Type", contentType) },
        Body = Encoding.UTF8.GetBytes(body),
        Interrupted = interrupted,
    };

    [Fact]
    public void Protocol_errors_that_come_with_a_200_are_called_out()
    {
        Loc.Use("en");
        var gql = ProtocolInsight.Describe(ApiKind.GraphQL, Response(
            """{"data":{"me":{"id":1},"orders":null},"errors":[{"message":"Not authorized","path":["orders"],"extensions":{"code":"UNAUTHENTICATED"}}]}""", "application/json"));
        var line = Assert.Single(gql);
        Assert.Equal(InsightLevel.Error, line.Level);
        Assert.Equal("GraphQL: 1 error — Not authorized [UNAUTHENTICATED] · orders · partial data", line.Text);

        var ok = Assert.Single(ProtocolInsight.Describe(ApiKind.GraphQL, Response("""{"data":{"hello":"hi"}}""", "application/json")));
        Assert.Equal(InsightLevel.Ok, ok.Level);
        Assert.Contains("hello", ok.Text);

        var batch = Assert.Single(ProtocolInsight.Describe(ApiKind.GraphQL, Response("""[{"data":{}},{"errors":[{"message":"x"}]}]""", "application/json")));
        Assert.Equal("GraphQL batch: 2 results, 1 with errors", batch.Text);

        var rpc = Assert.Single(ProtocolInsight.Describe(ApiKind.JsonRpc, Response("""{"jsonrpc":"2.0","error":{"code":-32601,"message":"Unknown"},"id":7}""", "application/json")));
        Assert.Equal("JSON-RPC error -32601: Unknown (Method not found) · id 7", rpc.Text);
        Assert.Equal(InsightLevel.Ok, Assert.Single(ProtocolInsight.Describe(ApiKind.JsonRpc, Response("", "application/json", 204))).Level);

        var fault = Assert.Single(ProtocolInsight.Describe(ApiKind.Soap, Response(
            """<soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"><soap:Body><soap:Fault><faultcode>soap:Client</faultcode><faultstring>No such product</faultstring></soap:Fault></soap:Body></soap:Envelope>""",
            "text/xml", 500)));
        Assert.Equal("SOAP Fault soap:Client: No such product", fault.Text);

        var problem = Assert.Single(ProtocolInsight.Describe(ApiKind.Rest, Response("""{"title":"Validation","detail":"Bad body","errors":[1,2]}""", "application/problem+json", 422)));
        Assert.Equal("Problem: Validation — Bad body · 2 errors", problem.Text);
        Assert.Empty(ProtocolInsight.Describe(ApiKind.Rest, Response("{}", "application/json")));
    }

    [Fact]
    public void Streams_are_counted_and_a_cut_is_explained()
    {
        Loc.Use("en");
        var sse = ProtocolInsight.Describe(ApiKind.Rest, Response("event: tick\ndata: 1\n\nevent: tick\ndata: 2\n\ndata: bye\n\n", "text/event-stream", interrupted: "timeout"));
        Assert.Equal(2, sse.Count);
        Assert.Equal("Server-Sent Events: 3 events · tick, message", sse[0].Text);
        Assert.Equal(InsightLevel.Warning, sse[1].Level);
        Assert.StartsWith("The stream was cut by the timeout", sse[1].Text);

        var nd = Assert.Single(ProtocolInsight.Describe(ApiKind.Rest, Response("{\"a\":1}\n{\"a\":2}\nnope\n", "application/x-ndjson")));
        Assert.Equal("NDJSON stream: 3 lines · 1 is not JSON", nd.Text);
    }

    private const string PostmanGraphQL = """
    {
      "info": { "name": "Playground", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
      "item": [
        { "name": "Auth", "item": [
          { "name": "Login", "request": { "method": "POST", "url": "{{baseUrl}}/login" },
            "event": [ { "listen": "test", "script": { "exec": [
              "let json = {}; try { json = pm.response.json(); } catch (e) {}",
              "pm.test('Status 200', () => pm.expect(pm.response.code).to.be.oneOf([200]));",
              "pm.collectionVariables.set('token', json.access_token);",
              "pm.collectionVariables.set('etag', pm.response.headers.get('ETag'));" ] } } ] },
          { "name": "Me", "request": { "method": "GET", "url": "{{baseUrl}}/me" },
            "event": [ { "listen": "test", "script": { "exec": [
              "pm.test('ok', () => pm.expect(pm.response.code).to.be.oneOf([200, 201]));",
              "pm.test('role', () => pm.expect(pm.response.json().user.role).to.eql('admin'))",
              "pm.test('code', () => pm.expect(pm.response.json().errors[0].extensions.code).to.eql('FORBIDDEN'))",
              "pm.test('n', () => pm.expect(pm.response.json().error.code).to.eql(-32601))",
              "pm.test('len', () => pm.expect(pm.response.json()).to.have.length(2))",
              "pm.test('null', () => pm.expect(pm.response.json().data.product).to.be.null)",
              "pm.test('more', () => pm.expect(pm.response.json().errors.length).to.be.above(0))",
              "pm.test('h', () => pm.expect(pm.response.headers.get('Content-Type')).to.include('json'))",
              "pm.test('loop', () => pm.response.json().items.forEach(p => pm.expect(p.ok).to.eql(true)))" ] } } ] }
        ] },
        { "name": "Hello", "request": { "method": "POST", "url": "{{baseUrl}}/graphql",
          "body": { "mode": "graphql", "graphql": { "query": "{ hello }", "variables": "{}" } } },
          "protocolProfileBehavior": { "followRedirects": false },
          "event": [ { "listen": "prerequest", "script": { "exec": [ "pm.collectionVariables.set('stamp', Date.now());" ] } } ] },
        { "name": "A first", "request": { "method": "GET", "url": "{{baseUrl}}/a" } }
      ]
    }
    """;

    [Fact]
    public void Postman_graphql_tests_order_and_options_survive_the_import()
    {
        Loc.Use("en");
        var r = Importer.Import(PostmanGraphQL);
        var hello = r.Root.Requests.First(x => x.Name == "Hello");
        Assert.Equal(BodyTypes.GraphQL, hello.Body.Type);
        Assert.Equal("{ hello }", hello.Body.Data);
        Assert.Equal("", hello.Body.Variables);
        Assert.False(hello.FollowRedirects);

        var auth = r.Root.Folders.Single();
        Assert.Equal(new[] { "status == 200", "set token = $.access_token", "set etag = header ETag" }, auth.Requests[0].Tests);
        Assert.Equal(new[]
        {
            "status in 200, 201",
            "$.user.role == \"admin\"",
            "$.errors[0].extensions.code == \"FORBIDDEN\"",
            "$.error.code == -32601",
            "$.length == 2",
            "$.data.product == null",
            "$.errors.length > 0",
            "header Content-Type contains json",
        }, auth.Requests[1].Tests);
        // Only the forEach line is lost, and the warning shows it; the pre-request names what it set.
        Assert.Contains(r.Warnings, w => w.StartsWith("Me: part of the test script") && w.Contains("forEach"));
        Assert.Contains(r.Warnings, w => w.StartsWith("Hello: the pre-request script is not run") && w.Contains("stamp"));
        Assert.DoesNotContain(r.Warnings, w => w.StartsWith("Login"));

        // On disk the order of the collection is kept (Login before Me; Auth, Hello, A first), not the alphabet.
        using var tmp = new TempDir();
        var ws = Workspace.Init(tmp.Path, "demo");
        var summary = Importer.Write(ws, r, "");
        Assert.Equal(new[] { "Playground/Auth", "Playground/Hello.req.json", "Playground/A first.req.json" }, ws.ListChildren(summary.FolderRel).Select(c => c.RelPath));
        Assert.Equal(new[] { "Playground/Auth/Login.req.json", "Playground/Auth/Me.req.json" }, ws.ListRequests("Playground/Auth"));
        Assert.False(ws.LoadRequest("Playground/Hello.req.json").FollowRedirects);

        // Renaming keeps the place in the order.
        var renamed = ws.RenameRequest("Playground/Hello.req.json", "Hola");
        Assert.Equal("Playground/Hola.req.json", ws.ListChildren("Playground")[1].RelPath);
        Assert.Equal("Playground/Hola.req.json", renamed);

        // Export keeps GraphQL as GraphQL.
        var exported = Exporter.ToPostman(ws, "Playground");
        Assert.Contains("\"mode\": \"graphql\"", exported);
        Assert.Contains("\"followRedirects\": false", exported);
    }

    [Fact]
    public void Folders_sort_by_their_numbers()
    {
        var names = new[] { "10. Webhooks", "2. REST", "0. Inicio", "1. Auth", "11. Tiempo real", "b", "A" };
        Assert.Equal(new[] { "0. Inicio", "1. Auth", "2. REST", "10. Webhooks", "11. Tiempo real", "A", "b" },
            names.OrderBy(n => n, NaturalComparer.Instance));
        Assert.True(NaturalComparer.Instance.Compare("v02", "v2") != 0); // different names never compare equal
    }

    [Fact]
    public void Pasted_graphql_curl_becomes_a_graphql_request()
    {
        var r = Curl.Parse("""curl 'https://api.test/graphql' -H 'content-type: application/json' --data-raw '{"query":"{ hello }","variables":{"a":1}}'""")!;
        Assert.Equal(BodyTypes.GraphQL, r.Body.Type);
        Assert.Equal("{ hello }", r.Body.Data);
        Assert.Contains("\"a\": 1", r.Body.Variables);
        Assert.DoesNotContain(r.Headers, h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
    }
}
