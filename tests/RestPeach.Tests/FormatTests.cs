using RestPeach.Core.Model;
using RestPeach.Core.Storage;
using RestPeach.Core.Text;

namespace RestPeach.Tests;

public class FormatTests
{
    private static ApiRequest Sample() => new()
    {
        Name = "Create User",
        Method = "POST",
        Url = "{{BASE_URL}}/v1/users",
        Headers = { KeyValue.Of("Content-Type", "application/json") },
        Auth = AuthSpec.BearerOf("{{BEARER_TOKEN}}"),
        Body = new BodySpec { Type = BodyTypes.Json, Data = "{\n  \"email\": \"user@example.com\"\n}" },
    };

    [Fact]
    public void Request_json_has_sorted_keys_lf_and_matches_the_spec_example()
    {
        var text = Workspace.Serialize(Sample(), "Users/Create User.req.json");
        Assert.DoesNotContain("\r", text);
        Assert.EndsWith("}\n", text);
        var keys = System.Text.RegularExpressions.Regex.Matches(text, "^  \"(\\w+)\"", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(new[] { "auth", "body", "headers", "method", "name", "url" }, keys);
        Assert.Contains("\"token\": \"{{BEARER_TOKEN}}\"", text);
        Assert.Contains("\"active\": true", text);
    }

    [Fact]
    public void Saving_twice_gives_the_same_bytes_and_round_trips()
    {
        var a = Workspace.Serialize(Sample(), "x.req.json");
        var back = Workspace.ParseRequest(a, "x.req.json");
        var b = Workspace.Serialize(back, "x.req.json");
        Assert.Equal(a, b);
        Assert.Equal("Create User", back.Name);
        Assert.Equal(AuthTypes.Bearer, back.Auth.Type);
        Assert.Equal(BodyTypes.Json, back.Body.Type);
    }

    [Fact]
    public void Unknown_keys_survive_a_save()
    {
        var text = "{ \"name\": \"A\", \"method\": \"GET\", \"url\": \"/a\", \"zz-owner\": { \"team\": \"api\" } }";
        var r = Workspace.ParseRequest(text, "a.req.json");
        var saved = Workspace.Serialize(r, "a.req.json");
        Assert.Contains("\"zz-owner\"", saved);
        Assert.Contains("\"team\": \"api\"", saved);
    }

    [Fact]
    public void Auth_writes_only_the_fields_of_its_type()
    {
        var r = Sample();
        r.Auth.Username = "leftover";
        var text = Workspace.Serialize(r, "x.req.json");
        Assert.DoesNotContain("leftover", text);
    }

    [Fact]
    public void Body_data_written_as_object_by_hand_is_accepted()
    {
        var r = Workspace.ParseRequest("{ \"name\": \"A\", \"url\": \"/\", \"body\": { \"type\": \"json\", \"data\": { \"a\": 1 } } }", "a.req.json");
        Assert.Contains("\"a\": 1", r.Body.Data);
    }

    [Fact]
    public void Yaml_round_trips_multiline_bodies_and_tricky_strings()
    {
        var r = Sample();
        r.Tests = new List<string> { "status == 201", "$.id exists" };
        r.Description = "yes";
        r.Headers.Add(KeyValue.Of("X-Note", "a: b # c"));
        var yaml = Workspace.Serialize(r, "x.req.yaml");
        Assert.Contains("data: |-", yaml);
        Assert.Contains("description: \"yes\"", yaml);
        var back = Workspace.ParseRequest(yaml, "x.req.yaml");
        Assert.Equal(r.Body.Data, back.Body.Data);
        Assert.Equal("a: b # c", back.Headers[1].Value);
        Assert.Equal("yes", back.Description);
        Assert.Equal(r.Tests, back.Tests);
        Assert.Equal(yaml, Workspace.Serialize(back, "x.req.yaml"));
    }

    [Fact]
    public void Pretty_json_keeps_key_order()
    {
        Assert.Equal("{\n  \"b\": 1,\n  \"a\": [\n    2\n  ]\n}", CanonicalJson.Pretty("{\"b\":1,\"a\":[2]}"));
        Assert.Null(CanonicalJson.Pretty("not json"));
    }

    [Fact]
    public void Names_and_paths()
    {
        Assert.Equal("Get User", Workspace.NameOf("Users/Get User.req.json"));
        Assert.Equal("Login", Workspace.NameOf("Auth/Login.req.yaml"));
        Assert.Equal("a-b-c", Workspace.SafeFileName("a/b:c"));
        Assert.Equal("Users", Workspace.ParentOf("Users/Get.req.json"));
        Assert.Equal("", Workspace.ParentOf("Get.req.json"));
    }
}
