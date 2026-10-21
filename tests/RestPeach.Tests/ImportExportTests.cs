using System.Text.Json.Nodes;
using RestPeach.Core.Formats;
using RestPeach.Core.Model;
using RestPeach.Core.Storage;
using RestPeach.Core.Text;

namespace RestPeach.Tests;

public class ImportExportTests
{
    private const string Postman = """
    {
      "info": { "name": "Shop", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
      "auth": { "type": "bearer", "bearer": [ { "key": "token", "value": "{{TOKEN}}" } ] },
      "variable": [ { "key": "BASE_URL", "value": "https://shop.test" } ],
      "item": [
        { "name": "Products", "item": [
          { "name": "List products",
            "request": { "method": "GET", "header": [ { "key": "Accept", "value": "application/json" } ],
              "url": { "raw": "{{BASE_URL}}/products?page=1", "query": [ { "key": "page", "value": "1" }, { "key": "debug", "value": "true", "disabled": true } ] } },
            "event": [ { "listen": "test", "script": { "exec": [ "pm.test('ok', () => pm.response.to.have.status(200));" ] } } ] },
          { "name": "Create product",
            "request": { "method": "POST", "url": "{{BASE_URL}}/products", "auth": { "type": "noauth" },
              "body": { "mode": "raw", "raw": "{\"name\":\"Peach\"}", "options": { "raw": { "language": "json" } } } } }
        ] },
        { "name": "Upload", "request": { "method": "POST", "url": "{{BASE_URL}}/upload",
          "body": { "mode": "formdata", "formdata": [ { "key": "file", "type": "file", "src": "a.png" }, { "key": "note", "value": "x" } ] } } }
      ]
    }
    """;

    [Fact]
    public void Postman_collection_imports_folders_auth_params_and_simple_tests()
    {
        var r = Importer.Import(Postman);
        Assert.Equal("Postman 2.1", r.Format);
        Assert.Equal("Shop", r.Root.Name);
        Assert.Equal(AuthTypes.Bearer, r.Root.Config.Auth.Type);
        Assert.Equal("https://shop.test", r.Root.Config.Variables["BASE_URL"]);
        var list = r.Root.Folders.Single().Requests[0];
        Assert.Equal("{{BASE_URL}}/products?page=1", list.Url);
        Assert.Equal("debug", list.DisabledParams.Single().Key);
        Assert.Equal(new[] { "status == 200" }, list.Tests);
        var create = r.Root.Folders[0].Requests[1];
        Assert.Equal(AuthTypes.None, create.Auth.Type);
        Assert.Equal(BodyTypes.Json, create.Body.Type);
        var upload = r.Root.Requests.Single();
        Assert.True(upload.Body.Fields[0].IsFile);
        Assert.Equal(4, r.Root.CountRequests() + 1);
    }

    [Fact]
    public void Import_writes_files_and_postman_export_round_trips()
    {
        using var tmp = new TempDir();
        var ws = Workspace.Init(tmp.Path, "demo");
        var summary = Importer.Write(ws, Importer.Import(Postman), "");
        Assert.Equal("Shop", summary.FolderRel);
        Assert.Equal(3, summary.Requests);
        Assert.True(File.Exists(ws.FullPath("Shop/Products/List products.req.json")));
        Assert.Equal(AuthTypes.Bearer, ws.LoadFolder("Shop").Auth.Type);

        // Importing again never overwrites: a second folder appears.
        Assert.Equal("Shop (2)", Importer.Write(ws, Importer.Import(Postman), "").FolderRel);

        var exported = Exporter.ToPostman(ws, "Shop");
        var again = Importer.Import(exported);
        Assert.Equal("Shop", again.Root.Name);
        Assert.Equal(3, again.Root.CountRequests());
        Assert.Equal("debug", again.Root.Folders.Single().Requests.First(x => x.Name == "List products").DisabledParams.Single().Key);
        Assert.Equal(AuthTypes.Bearer, again.Root.Config.Auth.Type);
        Assert.Equal(exported, Exporter.ToPostman(ws, "Shop")); // deterministic
    }

    [Fact]
    public void Insomnia_v4_resources_and_environments()
    {
        var doc = """
        { "_type": "export", "__export_format": 4, "resources": [
          { "_id": "wrk_1", "_type": "workspace", "name": "Billing" },
          { "_id": "env_base", "_type": "environment", "parentId": "wrk_1", "name": "Base", "data": { "base": "http://localhost" } },
          { "_id": "env_dev", "_type": "environment", "parentId": "env_base", "name": "Dev", "data": { "api": { "key": "k-dev" } } },
          { "_id": "fld_1", "_type": "request_group", "parentId": "wrk_1", "name": "Invoices" },
          { "_id": "req_1", "_type": "request", "parentId": "fld_1", "name": "List", "method": "get", "url": "{{ _.base }}/invoices",
            "parameters": [ { "name": "limit", "value": "10" }, { "name": "debug", "value": "1", "disabled": true } ],
            "headers": [ { "name": "X-Key", "value": "{{ _.api.key }}" } ],
            "authentication": { "type": "basic", "username": "u", "password": "p" }, "body": {} }
        ] }
        """;
        var r = Importer.Import(doc);
        Assert.Equal("Insomnia 4", r.Format);
        Assert.Equal("http://localhost", r.Root.Config.Variables["base"]);
        var req = r.Root.Folders.Single().Requests.Single();
        Assert.Equal("GET", req.Method);
        Assert.Equal("{{base}}/invoices?limit=10", req.Url);
        Assert.Equal("{{api.key}}", req.Headers[0].Value);
        Assert.Equal(AuthTypes.Basic, req.Auth.Type);
        Assert.Equal("k-dev", r.Environments.Single().Variables["api.key"]);
    }

    private const string OpenApi = """
    openapi: 3.1.0
    info:
      title: Pets
    servers:
      - url: https://{env}.pets.test/v1
        variables:
          env: { default: api }
    security:
      - bearer: []
    components:
      securitySchemes:
        bearer: { type: http, scheme: bearer }
      schemas:
        Pet:
          type: object
          required: [name]
          properties:
            id: { type: integer, readOnly: true }
            name: { type: string, example: Rex }
            tags: { type: array, items: { type: string } }
    paths:
      /pets/{petId}:
        parameters:
          - { name: petId, in: path, required: true, schema: { type: string }, example: "42" }
        get:
          tags: [pets]
          summary: Get a pet
          parameters:
            - { name: fields, in: query, schema: { type: string } }
          responses:
            "200": { description: ok }
      /pets:
        post:
          tags: [pets]
          operationId: createPet
          security: []
          requestBody:
            content:
              application/json:
                schema: { $ref: '#/components/schemas/Pet' }
          responses:
            "201": { description: created }
    """;

    [Fact]
    public void OpenApi_yaml_becomes_folders_requests_and_variables()
    {
        var r = Importer.Import(OpenApi);
        Assert.StartsWith("OpenAPI 3.1", r.Format);
        Assert.Equal("https://api.pets.test/v1", r.Root.Config.Variables["BASE_URL"]);
        Assert.Equal("42", r.Root.Config.Variables["petId"]);
        Assert.Equal(AuthTypes.Bearer, r.Root.Config.Auth.Type);
        var pets = r.Root.Folders.Single();
        Assert.Equal("pets", pets.Name);
        var get = pets.Requests.Single(x => x.Method == "GET");
        Assert.Equal("{{BASE_URL}}/pets/{{petId}}", get.Url);
        Assert.Equal("fields", get.DisabledParams.Single().Key);
        Assert.Equal(new[] { "status == 200" }, get.Tests);
        var post = pets.Requests.Single(x => x.Method == "POST");
        Assert.Equal("createPet", post.Name);
        Assert.Equal(AuthTypes.None, post.Auth.Type);
        var body = CanonicalJson.Parse(post.Body.Data)!.AsObject();
        Assert.Equal("Rex", body["name"]!.GetValue<string>());
        Assert.False(body.ContainsKey("id")); // read-only
    }

    [Fact]
    public void OpenApi_export_lists_paths_parameters_and_security()
    {
        using var tmp = new TempDir();
        var ws = Workspace.Init(tmp.Path, "demo");
        Importer.Write(ws, Importer.Import(OpenApi), "");
        var doc = CanonicalJson.Parse(Exporter.ToOpenApi(ws, "Pets"))!.AsObject();
        var paths = doc.Obj("paths")!;
        Assert.True(paths.ContainsKey("/pets/{petId}"));
        Assert.True(paths.Obj("/pets")!.ContainsKey("post"));
        Assert.Equal("bearerAuth", doc.Arr("security")![0]!.AsObject().First().Key);
        Assert.Empty(paths.Obj("/pets")!.Obj("post")!.Arr("security")!);
        Assert.Equal("https://api.pets.test/v1", doc.Arr("servers")![0].Str("url"));
    }

    [Fact]
    public void Bash_export_reads_variables_from_the_environment()
    {
        using var tmp = new TempDir();
        var ws = Workspace.Init(tmp.Path, "demo");
        Importer.Write(ws, Importer.Import(Postman), "");
        var script = Exporter.ToBash(ws, "Shop");
        Assert.StartsWith("#!/usr/bin/env bash", script);
        Assert.Contains(": \"${BASE_URL:=https://shop.test}\"", script);
        Assert.Contains(": \"${TOKEN:?set TOKEN}\"", script);
        Assert.Contains("\"Authorization: Bearer ${TOKEN}\"", script);
        Assert.DoesNotContain("{{", script);
    }

    [Fact]
    public void Curl_text_imports_a_single_request()
    {
        using var tmp = new TempDir();
        var ws = Workspace.Init(tmp.Path, "demo");
        var s = Importer.Write(ws, Importer.Import("curl https://h.test/ping"), "");
        Assert.Equal(1, s.Requests);
        Assert.Single(ws.ListRequests(""));
    }

    [Fact]
    public void Unknown_formats_are_explained()
    {
        Assert.Throws<InvalidDataException>(() => Importer.Import("{ \"swagger\": \"2.0\" }"));
        Assert.Throws<InvalidDataException>(() => Importer.Import("{ \"hello\": 1 }"));
    }
}
